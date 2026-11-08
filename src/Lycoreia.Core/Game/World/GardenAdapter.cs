// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Lycoreia.Game.Hooks;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.Owned;
using Lycoreia.World;
using UnityEngine;

namespace Lycoreia.Game.World
{
    /// <summary>
    /// Land plots and gardens over the live game. Plot type and upgrades are keyed latched states (host-authoritative, cached until the
    /// plot model exists). Crops follow <see cref="GardenSync"/> (host revision order); each garden's growth runs only on its lease owner
    /// (<see cref="KeyLeases"/>), who publishes its timer about every 5 s. Not synced yet: feeders, collectors, silo slots, produce attach.
    /// </summary>
    public sealed class GardenAdapter
    {
        public static GardenAdapter Instance;
        public readonly LatchedSystem<NewLandPlot> PlotTypes; public readonly LatchedSystem<LandPlotUpgrade> Upgrades;
        public readonly GardenSync Gardens; public readonly KeyLeases Leases;
        public const double LeaseEvery = 1.0, TimerEvery = 5.0;
        readonly Dictionary<string, double> _loadedSince = new Dictionary<string, double>(StringComparer.Ordinal), _claimedAt = new Dictionary<string, double>(StringComparer.Ordinal);
        readonly Dictionary<string, int> _cropNextFrame = new Dictionary<string, int>(StringComparer.Ordinal);
        double _nextLease, _nextTimer; bool _joinCheckArmed; readonly Dictionary<int, string> _plotOfSpawner = new Dictionary<int, string>();
        static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);
        static Dictionary<string, ResourceGrowerDefinition> _growers;

        public static void Install(Session s, Lycoreia.Snapshot.SnapshotHost snap) { if (Instance == null) Instance = new GardenAdapter(s, snap); }
        GardenAdapter(Session s, Lycoreia.Snapshot.SnapshotHost snap)
        {
            PlotTypes = new LatchedSystem<NewLandPlot>(s, new PlotTypeModel(), m => (m.PlotId, m.Type), (k, v) => new NewLandPlot { PlotId = k, Type = (int)v }, null, (k, v) => !string.IsNullOrWhiteSpace(k) && v >= 0 && v < 64);
            Upgrades = new LatchedSystem<LandPlotUpgrade>(s, new PlotUpgradeModel(), m => (UpgradeKey(m.PlotId, m.Upgrade), 1), (k, v) => { var (p, u) = SplitUpgrade(k); return new LandPlotUpgrade { PlotId = p, Upgrade = u }; }, Math.Max,
                (k, v) => v > 0 && SplitUpgrade(k).plot.Length > 0 && SplitUpgrade(k).upgrade > 0);
            Leases = new KeyLeases(s, snap);
            Gardens = new GardenSync(s) { Leases = Leases, OnCrop = ApplyCrop, OnTimer = ApplyTimer };
            snap.Register(Lycoreia.Snapshot.SnapStep.LandPlots, ctx => ctx.Send(BuildSnapshot()));
            s.Node.On<InitialLandPlots>(Side.Guest, (c, m) => ApplySnapshot(m));
            s.ServerStarted += () => { ClearAll(); Leases.HostClaimAll(GardenIds()); };
            s.ClientJoined += () => { ClearAll(); GardenJoinCheck.Reset(); _joinCheckArmed = true; };
            s.ClientDisconnected += _ => { ClearAll(); GardenJoinCheck.Reset(); };   // offline every hook passes: gardens run vanilla again
            Mod.Guest.SnapshotApplied += () => { if (_joinCheckArmed) { _joinCheckArmed = false; GardenJoinCheck.Schedule(); } };
        }
        void ClearAll() { PlotTypes.Clear(); Upgrades.Clear(); Gardens.Clear(); Leases.Clear(); _cropNextFrame.Clear(); _loadedSince.Clear(); _claimedAt.Clear(); }

        // ---------------------------------------------------------------- keys and lookups
        static string UpgradeKey(string plot, int upgrade) => (plot ?? "") + "|" + upgrade;
        static (string plot, int upgrade) SplitUpgrade(string k) { int i = k?.LastIndexOf('|') ?? -1; return i > 0 && int.TryParse(k.Substring(i + 1), out var u) ? (k.Substring(0, i), u) : ("", 0); }
        static LandPlotModel PlotModel(string id) { var t = GameState.LandPlots; return t != null && id != null && t.TryGetValue(id, out var m) ? m : null; }
        static LandPlot PlotObject(string id) { var m = PlotModel(id); var go = m?.gameObj; return go != null && go.activeInHierarchy ? (go.GetComponent<LandPlot>() ?? go.GetComponentInChildren<LandPlot>(true)) : null; }
        public static string PlotIdOf(Component c) { try { var loc = c != null ? c.GetComponentInParent<LandPlotLocation>() : null; return loc != null ? loc.Id : null; } catch { return null; } }
        static IEnumerable<string> GardenIds()
        {
            var t = GameState.LandPlots; var list = new List<string>(); if (t == null) return list;
            foreach (var kv in t) if (kv.Value != null && kv.Value.typeId == LandPlot.Id.GARDEN) list.Add(kv.Key);
            return list;
        }
        static ResourceGrowerDefinition GrowerFor(IdentifiableType crop)
        {
            if (crop == null) return null;
            if (_growers == null || !_growers.ContainsKey(crop.ReferenceId)) { _growers = new Dictionary<string, ResourceGrowerDefinition>(); foreach (var d in Resources.FindObjectsOfTypeAll<ResourceGrowerDefinition>()) if (d != null && d._primaryResourceType != null && !_growers.ContainsKey(d._primaryResourceType.ReferenceId)) _growers[d._primaryResourceType.ReferenceId] = d; }
            return _growers.TryGetValue(crop.ReferenceId, out var g) ? g : null;
        }
        static SpawnResource GardenSpawner(string plotId) { var m = PlotModel(plotId); var go = m?.gameObj; return go != null ? go.GetComponentInChildren<SpawnResource>(true) : null; }

        // ---------------------------------------------------------------- plot type and upgrades
        sealed class PlotTypeModel : ILatchedModel
        {
            public double Current(string key, double fallback) { var m = PlotModel(key); return m != null ? (int)m.typeId : fallback; }
            public bool TryApply(string key, double value, bool fromSnapshot)
            {
                try
                {
                    var g = GameState.Model; if (g == null) return false;
                    var m = PlotModel(key) ?? g.InitializeLandPlotModel(key);                          // the guest's save may be less advanced: create the missing model
                    if (m == null) return false; var type = (LandPlot.Id)(int)value; if (m.typeId == type) return true;
                    var plot = PlotObject(key); var loc = plot != null ? plot.GetComponentInParent<LandPlotLocation>() : null;
                    using (Applying.Enter())
                    {
                        if (plot != null && loc != null) { var prefab = GameContext.Instance?.LookupDirector?.GetPlotPrefab(type); if (prefab == null) return false; loc.Replace(plot, prefab); }
                        else { m.typeId = type; m.upgrades?.Clear(); m.resourceGrowerDefinition = null; }   // the location binds the type when the area loads
                    }
                    Instance?.Upgrades.Forget(k => k.StartsWith(key + "|", StringComparison.Ordinal));
                    if (fromSnapshot) Instance?.MarkReplaced(key);
                    return true;
                }
                catch (Exception e) { Rl.Warn("plot-type", $"plot {key} type apply failed: {e.Message}"); return false; }
            }
        }
        sealed class PlotUpgradeModel : ILatchedModel
        {
            public double Current(string key, double fallback) { var (p, u) = SplitUpgrade(key); var m = PlotModel(p); return m == null ? fallback : m.HasUpgrade((LandPlot.Upgrade)u) ? 1 : 0; }
            public bool TryApply(string key, double value, bool fromSnapshot)
            {
                try
                {
                    var (p, u) = SplitUpgrade(key); var m = PlotModel(p); if (m == null || value <= 0) return false; var up = (LandPlot.Upgrade)u;
                    if (m.HasUpgrade(up)) return true;
                    using (Applying.Enter()) { var plot = PlotObject(p); if (plot != null) plot.AddUpgrade(up); else m.upgrades.Add(up); }
                    return true;
                }
                catch (Exception e) { Rl.Warn("plot-upgrade", $"plot upgrade {key} failed: {e.Message}"); return false; }
            }
        }
        void MarkReplaced(string plotId) { if (Gardens.Crops.TryGetValue(plotId, out var c)) _cropNextFrame[plotId] = c; }

        public void OnLocalPlotReplaced(string plotId, GameObject replacement)
        {
            if (Applying.Active || string.IsNullOrEmpty(plotId) || replacement == null) return;
            var lp = replacement.GetComponent<LandPlot>() ?? replacement.GetComponentInChildren<LandPlot>(true); if (lp == null) return;
            Upgrades.Forget(k => k.StartsWith(plotId + "|", StringComparison.Ordinal));
            PlotTypes.Publish(plotId, (int)lp.TypeId);
            if (lp.TypeId == LandPlot.Id.GARDEN && Mod.IsHost) Leases.Claim(plotId);
        }
        public void OnLocalUpgrade(LandPlot plot, LandPlot.Upgrade up) { if (Applying.Active || plot == null) return; var id = PlotIdOf(plot); if (id != null) Upgrades.Publish(UpgradeKey(id, (int)up), 1); }

        // ---------------------------------------------------------------- crops
        public void OnLocalPlant(GardenCatcher catcher, IdentifiableType crop) { if (Applying.Active || crop == null) return; var id = PlotIdOf(catcher); int t = ActorTypes.IdOf(crop); if (id != null && t > 0) Gardens.LocalCrop(id, id, t); }
        public void OnLocalClear(LandPlot plot) { if (Applying.Active || plot == null || plot.TypeId != LandPlot.Id.GARDEN) return; var id = PlotIdOf(plot); if (id != null && Gardens.CropOf(id) > 0) Gardens.LocalCrop(id, id, 0); }
        void ApplyCrop(string plotId, int type)
        {
            try
            {
                IdentifiableType crop = null; if (type > 0 && !ActorTypes.TryGetType(type, out crop)) return;
                var m = PlotModel(plotId); if (m == null) return;
                var plot = PlotObject(plotId);
                using (Applying.Enter())
                {
                    if (plot == null) { m.resourceGrowerDefinition = crop != null ? GrowerFor(crop) : null; return; }   // planted from the model when the plot loads
                    var current = plot.GetAttachedCropId();
                    if (crop != null && current != null && current.ReferenceId == crop.ReferenceId) return;
                    if (current != null || plot.HasAttached()) plot.DestroyAttached();                      // the catcher rejects an occupied plot: clear any optimistic crop first
                    if (crop == null) return;
                    var catcher = plot.GetComponentInChildren<GardenCatcher>(true);
                    if (catcher == null || catcher.Plant(crop, false) == null) Rl.Warn("garden-plant-" + plotId, $"garden {plotId}: plant {crop.name} produced nothing");
                }
            }
            catch (Exception e) { Rl.Warn("garden-crop", $"garden {plotId} crop apply failed: {e.Message}"); }
        }

        // ---------------------------------------------------------------- timers (non-owners follow the owner)
        public static bool MayGrow(SpawnResource sr)
        {
            var a = Instance; if (a == null || !Mod.IsNetworkActive || sr == null) return true;
            int key = sr.GetInstanceID();
            if (!a._plotOfSpawner.TryGetValue(key, out var id)) { id = PlotIdOf(sr); if (a._plotOfSpawner.Count > 4096) a._plotOfSpawner.Clear(); a._plotOfSpawner[key] = id; }
            if (id == null) return ProduceLink.WildMayGrow(sr);                                            // wild plants: one grower per area (the spawn leader)
            return a.Leases.ShouldRun(id);
        }
        void ApplyTimer(GardenUpdate m)
        {
            try
            {
                var sr = GardenSpawner(m.GardenId); var model = sr != null ? sr._model : null; if (model == null) return;
                using (Applying.Enter()) { model.Push(m.StoredWater, m.NextSpawnTime); model.nextSpawnRipens = m.NextSpawnRipens; }
            }
            catch (Exception e) { Rl.Warn("garden-timer", $"garden {m.GardenId} timer apply failed: {e.Message}"); }
        }

        // ---------------------------------------------------------------- snapshot
        InitialLandPlots BuildSnapshot()
        {
            var msg = new InitialLandPlots(); var t = GameState.LandPlots; if (t == null) return msg;
            foreach (var kv in t)
            {
                var m = kv.Value; if (m == null || kv.Key == null) continue;
                var r = new PlotRecord { Id = kv.Key, Type = (int)m.typeId, Upgrades = new List<int>() };
                try { if (m.upgrades != null) foreach (var u in m.upgrades) r.Upgrades.Add((int)u); } catch { }
                if (m.typeId == LandPlot.Id.GARDEN) { r.Crop = ActorTypes.IdOf(m.resourceGrowerDefinition?._primaryResourceType); r.CropRevision = Gardens.SnapshotRevision(kv.Key); }
                msg.Items.Add(r);
            }
            return msg;
        }
        void ApplySnapshot(InitialLandPlots m)
        {
            PlotTypes.LoadSnapshot(m.Items.Select(r => (r.Id, (double)r.Type)));
            Upgrades.LoadSnapshot(m.Items.SelectMany(r => (r.Upgrades ?? new List<int>()).Select(u => (UpgradeKey(r.Id, u), 1.0))));
            foreach (var r in m.Items)
            {
                if (r.Type != (int)LandPlot.Id.GARDEN || r.CropRevision <= 0) continue;
                if (_cropNextFrame.ContainsKey(r.Id)) { _cropNextFrame[r.Id] = r.Crop; Gardens.ApplySnapshotCrop(r.Id, r.CropRevision, r.Crop); continue; }   // never plant in the replacement frame
                var sel = Gardens.ApplySnapshotCrop(r.Id, r.CropRevision, r.Crop);
                if (sel is int c && c > 0 && ActorTypes.TryGetType(c, out var crop)) GardenJoinCheck.ExpectCrop(r.Id, crop);
            }
        }

        // ---------------------------------------------------------------- frame pump
        public void Pump(double now)
        {
            if (_cropNextFrame.Count > 0)
            {
                foreach (var kv in _cropNextFrame.ToList()) { _cropNextFrame.Remove(kv.Key); int c = Gardens.CropOf(kv.Key); ApplyCrop(kv.Key, c); if (c > 0 && ActorTypes.TryGetType(c, out var crop)) GardenJoinCheck.ExpectCrop(kv.Key, crop); }
            }
            if (now >= _nextLease) { _nextLease = now + LeaseEvery; PumpLeases(now); }
            if (now >= _nextTimer) { _nextTimer = now + TimerEvery; PumpTimers(); }
        }
        void PumpLeases(double now)
        {
            var s = Mod.Session; if (s == null) return;
            foreach (var id in GardenIds())
            {
                var go = PlotModel(id)?.gameObj; bool loaded = go != null && go.activeInHierarchy;
                if (loaded) { if (!_loadedSince.ContainsKey(id)) _loadedSince[id] = now; } else _loadedSince.Remove(id);
                string owner = Leases.OwnerOf(id);
                if (owner.Length == 0 && !Leases.Known(id) && s.IsHost) continue;                       // never-claimed gardens already run on the host
                var a = LeasePlanner.Decide(owner, s.LocalId, loaded, Leases.OwnerConnected(id), loaded ? now - _loadedSince[id] : 0, LeasePlanner.GardenDelay(id, s.LocalId));
                if (a == LeaseAction.Release) Leases.Release(id);
                else if (a == LeaseAction.Claim && (!_claimedAt.TryGetValue(id, out var at) || now - at >= 5)) { _claimedAt[id] = now; Leases.Claim(id); }
            }
        }
        void PumpTimers()
        {
            foreach (var id in Leases.Keys.ToList())
            {
                if (Leases.OwnerOf(id) != Mod.LocalId) continue;
                var sr = GardenSpawner(id); var model = sr != null ? sr._model : null; if (model == null) continue;
                Gardens.LocalTimer(id, model.nextSpawnTime, model.storedWater, model.nextSpawnRipens);
            }
        }
    }

    // ---------------------------------------------------------------- hooks (never SpawnResource.FastForward: native access violation)
    [HarmonyPatch(typeof(LandPlotLocation), nameof(LandPlotLocation.Replace))]
    static class PlotReplaceHook { static void Postfix(LandPlotLocation __instance, GameObject __result) { try { if (WH.On) GardenAdapter.Instance?.OnLocalPlotReplaced(__instance.Id, __result); } catch (Exception e) { HookGuard.Fail("LandPlotLocation.Replace", e); } } }
    [HarmonyPatch(typeof(LandPlot), nameof(LandPlot.AddUpgrade))]
    static class PlotUpgradeHook { static void Postfix(LandPlot __instance, LandPlot.Upgrade upgrade) { try { if (WH.On) GardenAdapter.Instance?.OnLocalUpgrade(__instance, upgrade); } catch (Exception e) { HookGuard.Fail("LandPlot.AddUpgrade", e); } } }
    [HarmonyPatch(typeof(LandPlot), nameof(LandPlot.DestroyAttached))]
    static class PlotClearHook { static void Postfix(LandPlot __instance) { try { if (WH.On) GardenAdapter.Instance?.OnLocalClear(__instance); } catch (Exception e) { HookGuard.Fail("LandPlot.DestroyAttached", e); } } }
    [HarmonyPatch(typeof(GardenCatcher), nameof(GardenCatcher.Plant))]
    static class GardenPlantHook { static void Postfix(GardenCatcher __instance, IdentifiableType cropId, GameObject __result) { try { if (WH.On && __result != null) GardenAdapter.Instance?.OnLocalPlant(__instance, cropId); } catch (Exception e) { HookGuard.Fail("GardenCatcher.Plant", e); } } }
    [HarmonyPatch(typeof(SpawnResource), nameof(SpawnResource.Update))]
    static class GardenGrowthGateHook { static bool Prefix(SpawnResource __instance) { try { return GardenAdapter.MayGrow(__instance); } catch (Exception e) { HookGuard.Fail("SpawnResource.Update", e); return true; } } }
}

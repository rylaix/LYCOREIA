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
        void ClearAll() { PlotTypes.Clear(); Upgrades.Clear(); Gardens.Clear(); Leases.Clear(); _cropNextFrame.Clear(); _cropDeferredFrame.Clear(); _cropTries.Clear(); _loadedSince.Clear(); _claimedAt.Clear(); _convergeTries.Clear(); }

        // ---------------------------------------------------------------- keys and lookups
        static string UpgradeKey(string plot, int upgrade) => (plot ?? "") + "|" + upgrade;
        static (string plot, int upgrade) SplitUpgrade(string k) { int i = k?.LastIndexOf('|') ?? -1; return i > 0 && int.TryParse(k.Substring(i + 1), out var u) ? (k.Substring(0, i), u) : ("", 0); }
        static LandPlotModel PlotModel(string id) { var t = GameState.LandPlots; return t != null && id != null && t.TryGetValue(id, out var m) ? m : null; }
        static LandPlot PlotObject(string id) { var m = PlotModel(id); var go = m?.gameObj; return go != null && go.activeInHierarchy ? (go.GetComponent<LandPlot>() ?? go.GetComponentInChildren<LandPlot>(true)) : null; }
        /// <summary>The plot object registered for this location, active or not (a hibernating ranch region keeps it inactive). Pre-2026-11-12
        /// the type/upgrade apply used <see cref="PlotObject"/> (active only) and only rewrote the model under a registered inactive plot:
        /// paddocks already in the host's save never appeared on the guest. The game builds a plot from its model only when the location
        /// registers (LandPlotLocation.Awake -> GameModel.RegisterLandPlot).</summary>
        static LandPlot BoundPlot(string id) { var go = PlotModel(id)?.gameObj; return go != null ? (go.GetComponent<LandPlot>() ?? go.GetComponentInChildren<LandPlot>(true)) : null; }
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
                    if (m == null) return false; var type = (LandPlot.Id)(int)value;
                    var plot = BoundPlot(key); var loc = plot != null ? plot.GetComponentInParent<LandPlotLocation>(true) : null;
                    if (plot != null ? plot.TypeId == type : m.typeId == type) return true;                 // what the seat shows, not only the model
                    using (Applying.Enter())
                    {
                        if (plot != null && loc != null) { var prefab = GameContext.Instance?.LookupDirector?.GetPlotPrefab(type); if (prefab == null) { PlotLog(key, $"type {type}: no prefab"); return false; } loc.Replace(plot, prefab); PlotLog(key, $"type {type} applied (rebuilt)"); }
                        else { m.typeId = type; m.upgrades?.Clear(); m.resourceGrowerDefinition = null; PlotLog(key, $"type {type} applied to the model (area not loaded)"); }   // the location binds the type when the area loads
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
                    var plot = BoundPlot(p);
                    if (plot != null ? plot.HasUpgrade(up) && m.HasUpgrade(up) : m.HasUpgrade(up)) return true;
                    using (Applying.Enter()) { if (plot != null) plot.AddUpgrade(up); else m.upgrades.Add(up); PlotLog(p, $"upgrade {up} applied" + (plot != null ? "" : " to the model (area not loaded)")); }
                    return true;
                }
                catch (Exception e) { Rl.Warn("plot-upgrade", $"plot upgrade {key} failed: {e.Message}"); return false; }
            }
        }
        void MarkReplaced(string plotId) { if (Gardens.Crops.TryGetValue(plotId, out var c)) _cropNextFrame[plotId] = c; }
        const int MaxCropTries = 10;
        readonly Dictionary<string, int> _cropTries = new Dictionary<string, int>(StringComparer.Ordinal), _cropDeferredFrame = new Dictionary<string, int>(StringComparer.Ordinal);
        /// <summary>Plant the plot's current crop (read again at that time) on a frame after this one.</summary>
        void DeferCrop(string plotId) { _cropNextFrame[plotId] = Gardens.CropOf(plotId); _cropDeferredFrame[plotId] = Time.frameCount; }

        public void OnLocalPlotReplaced(string plotId, GameObject replacement)
        {
            if (Applying.Active || string.IsNullOrEmpty(plotId) || replacement == null) return;
            var lp = replacement.GetComponent<LandPlot>() ?? replacement.GetComponentInChildren<LandPlot>(true); if (lp == null) return;
            Upgrades.Forget(k => k.StartsWith(plotId + "|", StringComparison.Ordinal));
            PlotTypes.Publish(plotId, (int)lp.TypeId); PlotLog(plotId, $"local plot became {lp.TypeId}: published");
            if (lp.TypeId == LandPlot.Id.GARDEN && Mod.IsHost) Leases.Claim(plotId);
        }
        public void OnLocalUpgrade(LandPlot plot, LandPlot.Upgrade up) { if (Applying.Active || plot == null) return; var id = PlotIdOf(plot); if (id != null) { Upgrades.Publish(UpgradeKey(id, (int)up), 1); PlotLog(id, $"local upgrade {up}: published"); } else PlotLog("?", $"local upgrade {up} on a plot without location: not published"); }

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
                    if (current != null || plot.HasAttached())
                    {
                        // The catcher rejects an occupied plot, and Unity destroys the old crop only at the end of the frame: planting in
                        // this frame produced nothing and was never retried (issue 2026-11-09). Clear now, plant on a later frame.
                        plot.DestroyAttached();
                        if (crop != null) DeferCrop(plotId);
                        return;
                    }
                    if (crop == null) return;
                    var catcher = plot.GetComponentInChildren<GardenCatcher>(true);
                    if (catcher == null || catcher.Plant(crop, false) == null)
                    {
                        int tries = _cropTries.TryGetValue(plotId, out var n) ? n + 1 : 1;
                        if (tries <= MaxCropTries) { _cropTries[plotId] = tries; DeferCrop(plotId); }
                        else { _cropTries.Remove(plotId); Rl.Warn("garden-plant-" + plotId, $"garden {plotId}: plant {crop.name} produced nothing after {MaxCropTries} tries"); }
                    }
                    else _cropTries.Remove(plotId);
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
            Log.Info($"land plots snapshot sent: {DescribePlots(msg)}");
            return msg;
        }
        /// <summary>Plot sync events at info level (rare: purchases, upgrades, joins), issue4 "paddocks do not sync" had no evidence at all.</summary>
        static void PlotLog(string plotId, string what)
        {
            Log.Info($"plot {plotId}: {what}");
            Mod.Diagnostics?.Record("world", "plot", $"plot={plotId}; {what}");
        }
        static string DescribePlots(InitialLandPlots m)
            => $"{m.Items.Count} plots, " + string.Join(", ", m.Items.GroupBy(r => (LandPlot.Id)r.Type).Select(g => $"{g.Key} {g.Count()}")) + $", {m.Items.Sum(r => r.Upgrades?.Count ?? 0)} upgrades";
        void ApplySnapshot(InitialLandPlots m)
        {
            Log.Info($"land plots snapshot received: {DescribePlots(m)}");
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
                int frame = Time.frameCount;
                foreach (var kv in _cropNextFrame.ToList()) { if (_cropDeferredFrame.TryGetValue(kv.Key, out var f) && f >= frame) continue; _cropDeferredFrame.Remove(kv.Key); _cropNextFrame.Remove(kv.Key); int c = Gardens.CropOf(kv.Key); ApplyCrop(kv.Key, c); if (c > 0 && ActorTypes.TryGetType(c, out var crop)) GardenJoinCheck.ExpectCrop(kv.Key, crop); }
            }
            if (now >= _nextLease) { _nextLease = now + LeaseEvery; PumpLeases(now); }
            if (now >= _nextPlotCheck) { _nextPlotCheck = now + PlotCheckEvery; PlotTypes.RetryPending(); Upgrades.RetryPending(); if (Mod.IsGuest) ConvergePlots(); }
            if (now >= _nextTimer) { _nextTimer = now + TimerEvery; PumpTimers(); }
        }
        const double PlotCheckEvery = 5; const int MaxConvergeTries = 3; double _nextPlotCheck;
        readonly Dictionary<string, int> _convergeTries = new Dictionary<string, int>();
        /// <summary>At most a few rebuilds per plot and value; then one warning (never a rebuild every 5 s).</summary>
        bool MayConverge(string key, string what)
        {
            int n = _convergeTries.TryGetValue(key, out var t) ? t + 1 : 1; _convergeTries[key] = n;
            if (n <= MaxConvergeTries) return true;
            if (n == MaxConvergeTries + 1) Rl.Warn("plot-converge:" + key, $"plot {key} still {what} after {MaxConvergeTries} attempts; giving up until the next change");
            return false;
        }
        /// <summary>
        /// Guest: every plot the host reported (join snapshot or live) must show that type and those upgrades on this seat, whenever the
        /// plot was bought (before the mod, before this session or now). A plot whose object differs is applied again (rebuilds it).
        /// </summary>
        void ConvergePlots()
        {
            int fixedTypes = 0, fixedUpgrades = 0;
            foreach (var key in PlotTypes.Keys.ToList())
            {
                if (PlotTypes.Get(key) is not double v) continue; var plot = BoundPlot(key);
                if (plot == null || (int)plot.TypeId == (int)v) { _convergeTries.Remove(key); continue; }
                if (!MayConverge(key, $"shows {plot.TypeId}")) continue;
                PlotLog(key, $"shows {plot.TypeId}, host has {(LandPlot.Id)(int)v}: re-applying"); if (PlotTypes.Apply(key, v, false)) fixedTypes++;
            }
            foreach (var key in Upgrades.Keys.ToList())
            {
                var (p, u) = SplitUpgrade(key); var plot = BoundPlot(p); if (plot == null || Upgrades.Get(key) is not double v || v <= 0) continue;
                if ((int)plot.TypeId != (int)(PlotTypes.Get(p) ?? (int)plot.TypeId)) continue;                                  // the type goes first
                if (plot.HasUpgrade((LandPlot.Upgrade)u)) { _convergeTries.Remove(key); continue; }
                if (!MayConverge(key, "misses an upgrade")) continue;
                PlotLog(p, $"misses upgrade {(LandPlot.Upgrade)u}: re-applying"); if (Upgrades.Apply(key, v, false)) fixedUpgrades++;
            }
            if (fixedTypes + fixedUpgrades > 0) Mod.Diagnostics?.Record("world", "plots-converged", $"types={fixedTypes}; upgrades={fixedUpgrades}");
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

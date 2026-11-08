// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.Event;
using Il2CppMonomiPark.SlimeRancher.UI.Map;
using Lycoreia.Game.Hooks;
using Lycoreia.Net;
using Lycoreia.World;
using UnityEngine;

namespace Lycoreia.Game.World
{
    /// <summary>
    /// Connects the event table (<see cref="EventSync"/>) to the game's EventDirector: whitelisted local raises are published with their
    /// absolute count, received counts are raised by the missing increment, the host seeds its save's records for joiners.
    /// Map nodes ("fogRevealed") travel the same way; receivers also redraw the node hologram and tell the map director.
    /// </summary>
    public static class EventAdapter
    {
        public const string FogRevealed = "fogRevealed";
        static readonly HashSet<string> Synced = new HashSet<string>(StringComparer.Ordinal) { "zoneUnlocked", "objectDiscovered", "Gift", "endGameRewardGranted", FogRevealed };
        static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);
        static readonly List<(string key, string data, int count)> _pending = new List<(string, string, int)>();
        static Dictionary<(string, string), StaticGameEvent> _assets;
        static double _nextCheckpoint, _nextRetry;
        static EventSync _sync; static readonly HashSet<string> _seenKeys = new HashSet<string>();
        static bool _fogNeedsReconcile;
        static IntPtr _fogModel;
        static readonly HashSet<(string key, string data)> _mapRefresh = new();
        static MapNodeActivator[] _mapNodes = Array.Empty<MapNodeActivator>();
        static double _nextNodeScan, _nextMapRefresh;
        static Il2CppMonomiPark.SlimeRancher.Map.MapDirector _fallbackDirector;
        static MapNodeActivator[] LoadedMapNodes(double now) { if (_mapNodes == null || now >= _nextNodeScan) { _mapNodes = UnityEngine.Object.FindObjectsOfType<MapNodeActivator>(); _nextNodeScan = now + 5; } return _mapNodes; }

        public static bool IsSynced(string key) => key != null && Synced.Contains(key);
        static EventDirector Dir { get { try { return SceneContext.Instance != null ? SceneContext.Instance.eventDirector : null; } catch { return null; } } }
        static bool ModelReady(EventDirector d) { try { return d != null && d._model != null; } catch { return false; } }

        public static void Install(EventSync sync, Session s)
        {
            _sync = sync; sync.OnRaise = (k, d, n) => Apply(k, d, n);
            sync.OnHostSnapshot = () => _fogNeedsReconcile = true;
            sync.Trace = (name, details) => Mod.Diagnostics?.Record("world", name, details);
            s.ServerStarted += () => { _assets = null; _pending.Clear(); _seenKeys.Clear(); SeedFromSave(); };
            s.ClientJoined += () => { _assets = null; _pending.Clear(); _seenKeys.Clear(); _fogModel = IntPtr.Zero; _fogNeedsReconcile = true; _mapRefresh.Clear(); _mapNodes = Array.Empty<MapNodeActivator>(); _nextNodeScan = 0; _fallbackDirector = null; }; s.ClientDisconnected += _ => _pending.Clear();
        }

        /// <summary>Host start: every whitelisted record of the save goes into the table, so a joiner's snapshot carries it.</summary>
        static void SeedFromSave()
        {
            try
            {
                var d = Dir; if (!ModelReady(d) || _sync == null) return; int n = 0;
                foreach (var byKey in d._model.table)
                {
                    if (!IsSynced(byKey.Key) || byKey.Value == null) continue;
                    foreach (var e in byKey.Value) if (e.Value != null && e.Value.count > 0) { _sync.Seed(byKey.Key, e.Key, e.Value.count); n++; }
                }
                Mod.Diagnostics?.Record("world", "events-seeded", $"records={n}");
            }
            catch (Exception ex) { Log.Warn("event seed failed: " + ex.Message); }
        }

        /// <summary>Postfix of EventDirector.RaiseEvent: a local, whitelisted raise publishes the record's new absolute count.</summary>
        public static void OnLocalRaise(EventDirector d, IGameEvent ev)
        {
            if (!Mod.IsNetworkActive || Applying.Active || ev == null || _sync == null) return;
            string key = ev.EventKey; if (key != null && _seenKeys.Add(key)) Mod.Diagnostics?.Record("world", "event-key-seen", $"key={key}; data={ev.DataKey}; synced={IsSynced(key)}");
            // The native burst may finish after DoEat's postfix; publish that terminal
            // state through GordoSync rather than treating it as a generic event.
            if (key == "gordoBurst" && Mod.IsHost)
                WorldRuntime.OnNativeGordoBurst(ev.DataKey);
            if (!IsSynced(key)) return;
            int count = 0; try { var entry = d.GetRecordEntryForEvent(ev); count = entry != null ? entry.count : 0; } catch { }
            if (count <= 0) return;
            Mod.Diagnostics?.Record("world", "event-raised", $"key={key}; data={ev.DataKey}; count={count}");
            _sync.Raise(key, ev.DataKey ?? "", count);
        }

        /// <summary>A received absolute count: raise the missing increment (queued until the event model exists).</summary>
        static void Apply(string key, string data, int target)
        {
            var d = Dir;
            if (!ModelReady(d))
            {
                if (!_pending.Exists(p => p.key == key && p.data == data && p.count >= target))
                {
                    _pending.Add((key, data, target));
                    Mod.Diagnostics?.Record("world", "event-queued", $"key={key}; data={data}; target={target}; waiting=model");
                }
                return;
            }
            try
            {
                var ev = EventFor(key, data); int cur = 0;
                try { var entry = d.GetRecordEntryForEvent(ev.Cast<IGameEvent>()); cur = entry != null ? entry.count : 0; } catch { }
                int inc = Lycoreia.Owned.EventSyncPolicy.GetIncrement(cur, target);
                Mod.Diagnostics?.Record("world", "event-received", $"key={key}; data={data}; current={cur}; target={target}; increment={inc}");
                if (inc <= 0)
                {
                    if (key == FogRevealed) RefreshMapNodes(key, data);
                    return;
                }
                bool map = key == FogRevealed;
                using (Applying.Enter())
                {
                    if (map) TryNotifyMap(ev, key, data, Time.unscaledTimeAsDouble);
                    d.RaiseEvent(ev.Cast<IGameEvent>(), inc);
                }
                if (map) RefreshMapNodes(key, data);
                Mod.Diagnostics?.Record("world", "event-applied", $"key={key}; data={data}; from={cur}; to={cur + inc}");
            }
            catch (Exception ex) { if (!_pending.Exists(p => p.key == key && p.data == data && p.count >= target)) _pending.Add((key, data, target)); Rl.Warn("event-apply:" + key, $"event {key}/{data} apply failed: {ex.Message}"); }
        }

        /// <summary>Retries counts that arrived before the event model existed (joiner snapshot during load).</summary>
        public static void Pump()
        {
            double now = Time.unscaledTimeAsDouble;
            if (Mod.IsHost && now >= _nextCheckpoint)
            {
                _nextCheckpoint = now + 5;
                SeedFromSave(); _sync?.RepublishFog();
            }
            if (_pending.Count > 0 && ModelReady(Dir) && now >= _nextRetry)
            {
                _nextRetry = now + 0.5;
                var l = _pending.ToArray(); _pending.Clear(); foreach (var (k, d, n) in l) Apply(k, d, n);
            }
            ReconcileGuestFog();
            FlushMapNodes(now);
        }

        // Rendering alone cannot replace a guest save's exploration: native map nodes
        // also read this table. Reconcile only fog after host snapshot/model readiness.
        static void ReconcileGuestFog()
        {
            var director = Dir;
            if (!Mod.IsGuest || _sync?.HasHostSnapshot != true || !ModelReady(director)) return;
            var model = director._model;
            if (!_fogNeedsReconcile && _fogModel == model.Pointer) return;
            try
            {
                int changed = 0;
                // A new native model may not have any entries for host-revealed areas.
                // Reuse the normal missing-increment path before normalizing old entries.
                foreach (var record in _sync.FogRecords) Apply(FogRevealed, record.Data, record.Count);
                if (model.table != null && model.table.ContainsKey(FogRevealed))
                    foreach (var entry in model.table[FogRevealed])
                    {
                        if (entry.Value == null) continue;
                        int target = Lycoreia.World.MapFogRules.NativeCount(true, _sync.Count(FogRevealed, entry.Key), entry.Value.count);
                        if (entry.Value.count == target) continue;
                        entry.Value.count = target; changed++;
                    }
                if (changed > 0)
                {
                    using (Applying.Enter()) model.NotifyParticipants();
                    foreach (var node in UnityEngine.Object.FindObjectsOfType<MapNodeActivator>())
                        if (node != null) node.UpdateHologramState();
                    Mod.Diagnostics?.Record("world", "map-host-records-replaced", $"changed={changed}; mode=host-fog-only");
                }
                _fogModel = model.Pointer; _fogNeedsReconcile = false;
            }
            catch (Exception e) { Rl.Warn("map-record-replace", "host fog record reconciliation: " + e.Message); }
        }

        /// <summary>The game's own event asset (listeners are registered on it), or a new one with the same keys.</summary>
        static StaticGameEvent EventFor(string key, string data)
        {
            if (_assets == null)
            {
                _assets = new Dictionary<(string, string), StaticGameEvent>();
                foreach (var a in Resources.FindObjectsOfTypeAll<StaticGameEvent>()) { try { if (a != null && a.EventKey != null) _assets[(a.EventKey, a.DataKey ?? "")] = a; } catch { } }
            }
            if (_assets.TryGetValue((key, data ?? ""), out var found) && found != null) return found;
            var made = StaticGameEvent.CreateStaticGameEvent(key, data ?? ""); _assets[(key, data ?? "")] = made; return made;
        }

        static void TryNotifyMap(StaticGameEvent ev, string key, string data, double now)
        {
            try
            {
                var directors = new HashSet<Il2CppMonomiPark.SlimeRancher.Map.MapDirector>();
                int matchingNodes = 0;
                foreach (var node in LoadedMapNodes(now))
                {
                    if (!node) continue;
                    var nodeEvent = node._fogRevealEvent;
                    if (nodeEvent == null || nodeEvent.EventKey != key || (nodeEvent.DataKey ?? "") != (data ?? "")) continue;
                    matchingNodes++;
                    var director = node._mapDirector;
                    if (director != null) directors.Add(director);
                }
                // Match the native map-node path when that physical node is loaded. If it is not loaded here,
                // notify an active scene director so the popup still reaches players elsewhere.
                if (directors.Count == 0)
                {
                    if (_fallbackDirector == null)
                        foreach (var director in Resources.FindObjectsOfTypeAll<Il2CppMonomiPark.SlimeRancher.Map.MapDirector>())
                            if (director != null) { _fallbackDirector = director; break; }
                    if (_fallbackDirector != null) directors.Add(_fallbackDirector);
                }
                foreach (var director in directors) director.NotifyZoneUnlocked(ev, false, 0f);
                Mod.Diagnostics?.Record("world", "map-notify", $"key={key}; data={data}; directors={directors.Count}; matching-nodes={matchingNodes}");
            }
            catch (Exception ex) { Rl.Warn("map-notify", "map director notify failed: " + ex.Message); }
        }

        /// <summary>Loaded map nodes of that zone redraw from the record (otherwise the icon stays visible and inert).</summary>
        static void RefreshMapNodes(string key, string data) => _mapRefresh.Add((key, data ?? ""));

        static void FlushMapNodes(double now)
        {
            if (_mapRefresh.Count == 0 || now < _nextMapRefresh) return;
            _nextMapRefresh = now + 0.5;
            try
            {
                // Once per batch, cache the loaded nodes. Displayed fog is independently
                // repaired by MapFog's page/open and LateUpdate paths.
                foreach (var n in LoadedMapNodes(now))
                {
                    var e = n != null ? n._fogRevealEvent : null;
                    if (e == null || !_mapRefresh.Contains((e.EventKey, e.DataKey ?? ""))) continue;
                    n.UpdateHologramState();
                }
                _mapRefresh.Clear();
            }
            catch (Exception ex) { Rl.Warn("map-refresh", "map node refresh failed: " + ex.Message); }
        }
    }
}

namespace Lycoreia.Game.Hooks
{
    [HarmonyPatch(typeof(MapNodeActivator), nameof(MapNodeActivator.Activate))]
    static class MapNodeActivateTrace
    {
        static void Postfix(MapNodeActivator __instance)
        {
            try
            {
                var ev = __instance != null ? __instance._fogRevealEvent : null;
                if (ev == null) return;
                Mod.Diagnostics?.Record("world", "map-node-activated", $"key={ev.EventKey}; data={ev.DataKey}; node={__instance.name}; locked={__instance.IsZoneLocked()}; active={__instance.gameObject.activeInHierarchy}");
            }
            catch (Exception e) { HookGuard.Fail("MapNodeActivator.Activate", e); }
        }
    }

    [HarmonyPatch(typeof(EventDirector), nameof(EventDirector.RaiseEvent))]
    static class EventRaiseHook
    {
        static void Postfix(EventDirector __instance, IGameEvent gameEvent)
        { try { Lycoreia.Game.World.EventAdapter.OnLocalRaise(__instance, gameEvent); } catch (Exception e) { HookGuard.Fail("EventDirector.RaiseEvent", e); } }
    }
}

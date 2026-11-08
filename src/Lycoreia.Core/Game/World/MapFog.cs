// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Text;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.Event;
using Il2CppMonomiPark.SlimeRancher.UI.Map;
using Lycoreia.Game.Hooks;
using Lycoreia.World;
using UnityEngine;

namespace Lycoreia.Game.World
{
    /// <summary>
    /// Map fog check. Every time the map UI applies the fog (MapUIFogReveal.RevealKnownZones, run on open and on a map page change) this
    /// records, on each seat, how many fog elements exist, how many the event record says are revealed, how many are drawn revealed, and
    /// repairs an element whose record is present but which still draws fog.  15:30 run: the guest raised fogRevealed/fields1 natively and the host applied it, yet only the
    /// host's map looked explored; the host save had no fogRevealed record at all before.
    /// </summary>
    public static class MapFog
    {
        static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);
        static readonly Dictionary<int, double> NextView = new Dictionary<int, double>();

        // The native reveal controller caches its elements at Start/page selection. A pooled
        // map can retain the pre-join director/list. Render the displayed map from the host
        // event table after the snapshot, including fog absent from the guest's own save.
        public static void RefreshDisplayedMap(MapUI ui)
        {
            if (!Mod.IsNetworkActive || Mod.IsHost || ui == null || ui._map == null || WorldRuntime.Events?.HasHostSnapshot != true) return;
            int id = ui.GetInstanceID(); double now = Time.unscaledTimeAsDouble;
            if (NextView.TryGetValue(id, out var next) && now < next) return;
            NextView[id] = now + 0.25;
            try
            {
                int total = 0, revealed = 0, repaired = 0;
                foreach (var element in ui._map.GetComponentsInChildren<FogMapElement>(true))
                {
                    var ev = element != null ? element._fogRevealEvent : null;
                    if (ev == null || ev.EventKey != EventAdapter.FogRevealed || element._canvasGroup == null) continue;
                    total++; bool known = WorldRuntime.Events.Count(ev.EventKey, ev.DataKey) > 0;
                    if (known) revealed++;
                    if (!MapFogRules.NeedsCanonicalRepair(known, element._canvasGroup.alpha)) continue;
                    element.SetRevealState(known); repaired++;
                }
                if (repaired > 0) Mod.Diagnostics?.Record("world", "map-host-fog-rendered", $"elements={total}; host-revealed={revealed}; repaired={repaired}");
            }
            catch (Exception e) { Rl.Warn("map-host-fog", "host map fog render: " + e.Message); }
        }
        public static void ForgetView(MapUI ui) { if (ui != null) NextView.Remove(ui.GetInstanceID()); }

        public static void AfterReveal(MapUIFogReveal ui)
        {
            if (ui == null) return;
            try
            {
                var dir = ui._eventDirector; if (dir == null && SceneContext.Instance != null) dir = SceneContext.Instance.eventDirector;
                var elements = ui._fogElements; if (elements == null || dir == null) return;
                int total = 0, should = 0, drawnRevealed = 0, animating = 0, repaired = 0; var fogged = new StringBuilder();
                foreach (var el in elements)
                {
                    if (el == null) continue; total++;
                    var ev = el._fogRevealEvent; bool s = false; try { s = el.ShouldBeRevealed(dir); } catch { }
                    bool anim = false; try { anim = ev != null && ui._animatedEvents != null && ui._animatedEvents.Contains(ev); } catch { }
                    float alpha = 1f; try { alpha = el._canvasGroup != null ? el._canvasGroup.alpha : 1f; } catch { }
                    if (s) should++; if (anim) animating++;
                    if (MapFogRules.NeedsRepair(s, alpha, anim))
                    {
                        el.SetRevealState(true); repaired++;
                        try { alpha = el._canvasGroup != null ? el._canvasGroup.alpha : alpha; } catch { }
                        if (fogged.Length < 300) fogged.Append(ev != null ? ev.DataKey : el.name).Append(',');
                    }
                    if (!MapFogRules.LooksFogged(alpha)) drawnRevealed++;
                }
                Mod.Diagnostics?.Record("world", "map-fog",
                    $"elements={total}; record-revealed={should}; drawn-revealed={drawnRevealed}; animating={animating}; repaired={repaired}" +
                    (repaired > 0 ? "; repaired-keys=" + fogged : "") + $"; fog-records={FogRecords(dir)}; fog-layer={FogLayer(ui)}");
            }
            catch (Exception e) { Rl.Warn("map-fog", "map fog check failed: " + e.Message); }
        }

        /// <summary>fogRevealed records with a positive count (what the fog elements are compared against).</summary>
        static string FogRecords(EventDirector d)
        {
            try
            {
                var t = d._model != null ? d._model.table : null; if (t == null || !t.ContainsKey(EventAdapter.FogRevealed)) return "0";
                var sb = new StringBuilder(); int n = 0;
                foreach (var e in t[EventAdapter.FogRevealed]) if (e.Value != null && e.Value.count > 0) { n++; if (sb.Length < 200) sb.Append(e.Key).Append(' '); }
                return n + (n > 0 ? " [" + sb.ToString().TrimEnd() + "]" : "");
            }
            catch { return "?"; }
        }

        /// <summary>Whether the map's fog layers are active.</summary>
        static string FogLayer(MapUIFogReveal ui)
        {
            try
            {
                var root = ui.transform.root; string S(string n) { var tr = Find(root, n); return tr == null ? "none" : tr.gameObject.activeInHierarchy ? "on" : "off"; }
                return $"fog_static:{S("fog_static")},zone_fog_areas:{S("zone_fog_areas")}";
            }
            catch { return "?"; }
        }
        static Transform Find(Transform t, string name)
        {
            if (t == null) return null; if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++) { var r = Find(t.GetChild(i), name); if (r != null) return r; }
            return null;
        }
    }
}

namespace Lycoreia.Game.Hooks
{
    [HarmonyPatch(typeof(MapUI), nameof(MapUI.LateUpdate))]
    static class CanonicalMapFogHook { static void Postfix(MapUI __instance) => Lycoreia.Game.World.MapFog.RefreshDisplayedMap(__instance); }
    [HarmonyPatch(typeof(MapUI), nameof(MapUI.OnDestroy))]
    static class CanonicalMapFogDestroyHook { static void Prefix(MapUI __instance) => Lycoreia.Game.World.MapFog.ForgetView(__instance); }
    [HarmonyPatch(typeof(MapUIFogReveal), nameof(MapUIFogReveal.RevealZonesInMap))]
    static class MapFogPageHook
    {
        static void Postfix(MapUIFogReveal __instance) { try { Lycoreia.Game.World.MapFog.AfterReveal(__instance); } catch (Exception e) { HookGuard.Fail("MapUIFogReveal.RevealZonesInMap", e); } }
    }
    [HarmonyPatch(typeof(MapUIFogReveal), nameof(MapUIFogReveal.RevealKnownZones))]
    static class MapFogRevealHook
    {
        static void Postfix(MapUIFogReveal __instance) { try { Lycoreia.Game.World.MapFog.AfterReveal(__instance); } catch (Exception e) { HookGuard.Fail("MapUIFogReveal.RevealKnownZones", e); } }
    }
}

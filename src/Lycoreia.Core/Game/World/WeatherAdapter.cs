// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Weather;
using Il2CppMonomiPark.SlimeRancher.World;
using Lycoreia.Game.Hooks;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.World;
using UnityEngine;

namespace Lycoreia.Game.World
{
    /// <summary>
    /// Weather over the live game: the host reads its forecast (all zones) every 2 s and publishes it when it changed; guests never
    /// forecast themselves (WeatherRegistry.Forecast and the culls are skipped) and rebuild each zone from the host's entries, running
    /// only the entries the host has started and stopping the ones it no longer runs. Not synced yet: lightning strikes, tornadoes.
    /// </summary>
    public static class WeatherAdapter
    {
        public const double PublishEvery = 2.0, NotReadyLogEvery = 5.0;
        static double _nextPublish, _nextNotReadyLog, _nextReconcile; static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);
        static WeatherRegistry Registry { get { try { return SceneContext.Instance != null ? SceneContext.Instance.WeatherRegistry : null; } catch { return null; } } }
        static Dictionary<string, WeatherPatternDefinition> _patterns;
        static readonly Dictionary<string, string> _zoneTrace = new();
        static void TraceZone(string zone, IEnumerable<WeatherEntry> entries, string native)
        {
            string signature = native + "; forecast=" + string.Join("|", entries.Select(e => $"{e.Pattern}/{e.State}/{e.Started}/{e.Start:R}/{e.End:R}/{e.EndKnown}"));
            string key = (Mod.IsHost ? "host:" : "guest:") + zone;
            if (_zoneTrace.TryGetValue(key, out var old) && old == signature) return;
            _zoneTrace[key] = signature;
            Mod.Diagnostics?.Record("world", "weather-zone-state", $"zone={zone}; {signature}");
        }

        public static void Install(WorldClockSync clock) { clock.ApplyWeather = Apply; }

        static WeatherPatternDefinition Pattern(string guid)
        {
            if (_patterns == null || !_patterns.ContainsKey(guid ?? "")) { _patterns = new Dictionary<string, WeatherPatternDefinition>(); foreach (var p in Resources.FindObjectsOfTypeAll<WeatherPatternDefinition>()) if (p != null && !string.IsNullOrEmpty(p.Guid)) _patterns[p.Guid] = p; }
            return guid != null && _patterns.TryGetValue(guid, out var d) ? d : null;
        }
        static IWeatherState State(WeatherPatternDefinition p, string name)
        {
            if (p?._stateList == null || name == null) return null;
            foreach (var d in p._stateList) { if (d == null) continue; var s = d.TryCast<IWeatherState>(); if (s != null && s.GetName() == name) return s; }
            return null;
        }

        // ---------------------------------------------------------------- host: read and publish
        static byte[] Capture()
        {
            var reg = Registry; var zones = reg?._model?._zoneDatas; if (zones == null) return null;
            var f = new WeatherForecast();
            foreach (var kv in zones)
            {
                if (kv.Key == null || kv.Value?.Forecast == null) continue;
                var z = new ZoneForecast { Zone = kv.Key.ReferenceId, Entries = new List<WeatherEntry>() };
                foreach (var e in kv.Value.Forecast)
                {
                    if (e?.Pattern == null || e.State == null) continue;
                    z.Entries.Add(new WeatherEntry { Pattern = e.Pattern.Guid, State = e.State.GetName(), Start = e.StartTime, End = e.EndTime, EndKnown = e.IsEndTimeKnown, Started = e.Started });
                }
                f.Zones.Add(z);
                TraceZone(z.Zone, z.Entries, "authority=host");
            }
            f.Zones.Sort((a, b) => string.CompareOrdinal(a.Zone, b.Zone));
            var w = new NetWriter(); f.Describe(new Codec(w)); return w.Span.ToArray();
        }

        // ---------------------------------------------------------------- guest: rebuild per zone
        static bool Apply(byte[] data)
        {
            var reg = Registry; var zones = reg?._model?._zoneDatas; if (reg == null || zones == null) return false;   // scene not ready: kept pending
            var f = new WeatherForecast();
            var rd = NetReader.Rent(data, 0, data.Length); try { f.Describe(new Codec(rd)); } catch (Exception e) { Rl.Warn("weather-decode", "weather decode failed: " + e.Message); return true; } finally { rd.Return(); }
            var byRef = new Dictionary<string, (ZoneDefinition def, WeatherModel.ZoneData data)>();
            foreach (var kv in zones) if (kv.Key != null && kv.Value != null) byRef[kv.Key.ReferenceId] = (kv.Key, kv.Value);
            bool complete = true;
            using (Applying.Enter())
            {
                foreach (var z in f.Zones)
                {
                    if (z.Zone == null || !byRef.TryGetValue(z.Zone, out var zone)) { complete = false; continue; }
                    try { if (!ApplyZone(reg, zone.def, zone.data, z.Entries ?? new List<WeatherEntry>())) complete = false; }
                    catch (Exception e) { complete = false; Rl.Warn("weather-zone-" + z.Zone, $"weather zone {z.Zone} apply failed: {e.Message}"); }
                }
            }
            return complete;
        }
        static bool ApplyZone(WeatherRegistry reg, ZoneDefinition def, WeatherModel.ZoneData data, List<WeatherEntry> entries)
        {
            if (reg._zones == null || !reg._zones.TryGetValue(def, out var live)) return false;
            // Resolve the whole zone before mutating; missing assets stay pending.
            var desired = new List<(WeatherEntry e, WeatherPatternDefinition p, IWeatherState s)>();
            foreach (var e in entries)
            {
                var p = Pattern(e.Pattern); var st = State(p, e.State);
                if (p == null || st == null || !double.IsFinite(e.Start)) return false;
                desired.Add((e, p, st));
            }
            // Compare actual running instances, never the save's Started flags. A saved
            // forecast can already say Started while this registry has no live pattern.
            var running = live.CurrentWeatherPatterns.ToArray();
            foreach (var inst in running)
            {
                if (inst == null) continue;
                var p = inst.GetDefinition(); var state = inst.CurrentState();
                if (desired.Any(x => x.e.Started && x.p.Pointer == p.Pointer && state != null && x.s.Pointer == state.Pointer)) continue;
                if (state != null) reg.StopPatternState(def, inst, state);
                reg.RemoveWeatherPatternInstance(def, p);
            }
            data.Forecast.Clear();
            int started = 0;
            foreach (var (e, p, st) in desired)
            {
                data.Forecast.Add(new WeatherModel.ForecastEntry { Pattern = p, State = st, StartTime = e.Start, EndTime = e.End, IsEndTimeKnown = e.EndKnown, Started = e.Started });
                if (!WeatherSyncPolicy.ShouldRunForecast(e.Started)) continue;
                var inst = reg.GetWeatherPatternInstance(def, p) ?? reg.InstantiateWeatherPattern(def, p);
                if (inst == null) return false;
                var current = inst.CurrentState();
                if (current == null || current.Pointer != st.Pointer)
                {
                    reg.RunPatternState(def, inst, st, true); started++;
                }
            }
            live.UpdateChangeTimestamp();
            var runningNames = new List<string>();
            foreach (var pattern in live.CurrentWeatherPatterns)
                if (pattern != null) runningNames.Add(pattern.GetDefinition().Guid + "/" + pattern.CurrentState()?.GetName());
            TraceZone(def.ReferenceId, entries, $"authority=host; registry={reg.Pointer}; director={live.Director?.Pointer}; running=" + string.Join("|", runningNames));
            if (started > 0) Mod.Diagnostics?.Record("world", "weather-zone-applied", $"zone={def.ReferenceId}; entries={entries.Count}; started={started}; director={live.Director != null}");
            return true;
        }

        // ---------------------------------------------------------------- pump
        public static void Pump(WorldClockSync clock, double now)
        {
            if (Mod.IsHost)
            {
                if (now < _nextPublish) return; _nextPublish = now + PublishEvery;
                try { var d = Capture(); if (d != null) clock.HostPublish(d); } catch (Exception e) { Rl.Warn("weather-capture", "weather capture failed: " + e.Message); }
                return;
            }
            clock.ModelReady = Registry?._model != null;
            if (clock.HasPending && !clock.ModelReady && now >= _nextNotReadyLog) { _nextNotReadyLog = now + NotReadyLogEvery; Log.Debug("weather waiting for the scene (pending revision kept)"); }
            if (clock.ModelReady)
            {
                if (now < _nextReconcile) return;
                _nextReconcile = now + PublishEvery;
                // Reconcile again after registry/model/island readiness even when the
                // host forecast has not changed. RegisterDirector replays running states.
                if (clock.HasPending) clock.Pump();
                else clock.ReapplyWeather();
            }
        }
        /// <summary>Guests never forecast on their own: the host's forecast is applied instead.</summary>
        public static bool MayForecast() => !Mod.IsNetworkActive || Mod.IsHost;
    }

    [HarmonyPatch]
    static class GuestForecastGate
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(WeatherRegistry), nameof(WeatherRegistry.Update));
            yield return AccessTools.Method(typeof(WeatherRegistry), nameof(WeatherRegistry.Forecast));
            yield return AccessTools.Method(typeof(WeatherRegistry), nameof(WeatherRegistry.CullSkippedForecasts));
            yield return AccessTools.Method(typeof(WeatherRegistry), nameof(WeatherRegistry.CullFutureForecasts));
        }
        static bool Prefix() { try { return WeatherAdapter.MayForecast(); } catch (Exception e) { HookGuard.Fail("WeatherRegistry.forecast", e); return true; } }
    }
}

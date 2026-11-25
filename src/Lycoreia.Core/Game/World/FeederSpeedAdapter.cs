// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using HarmonyLib;
using Il2Cpp;
using Lycoreia.Game.Hooks;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.World;

namespace Lycoreia.Game.World
{
    public sealed class FeederSpeedAdapter
    {
        public static FeederSpeedAdapter Instance;
        public readonly LatchedSystem<AutoFeederSpeed> Speeds;
        readonly Session _s;

        public static void Install(Session s, Lycoreia.Snapshot.SnapshotHost snap) { if (Instance == null) Instance = new FeederSpeedAdapter(s, snap); }

        FeederSpeedAdapter(Session s, Lycoreia.Snapshot.SnapshotHost snap)
        {
            _s = s;
            Speeds = new LatchedSystem<AutoFeederSpeed>(s, new SpeedModel(), m => (m.Id, m.Speed), (k, v) => new AutoFeederSpeed { Id = k, Speed = (int)v }, null,
                (k, v) => !string.IsNullOrWhiteSpace(k) && v >= 0 && v <= 2 && v == Math.Floor(v));
            snap.Register(Lycoreia.Snapshot.SnapStep.LandPlots, ctx =>
            {
                var plots = GameState.LandPlots; if (plots == null) return; int n = 0;
                foreach (var kv in plots)
                {
                    if (kv.Key == null || kv.Value == null || kv.Value.typeId != LandPlot.Id.CORRAL) continue;
                    ctx.Send(new AutoFeederSpeed { Id = kv.Key, Speed = (int)kv.Value.feederCycleSpeed }); n++;
                }
                Log.Info($"feeder speed snapshot sent: {n} corrals");
            });
            s.ServerStarted += Speeds.Clear; s.ServerStopped += Speeds.Clear; s.ClientJoined += Speeds.Clear; s.ClientDisconnected += _ => Speeds.Clear();
        }

        static Il2CppMonomiPark.SlimeRancher.DataModel.LandPlotModel Plot(string id) { var t = GameState.LandPlots; return t != null && id != null && t.TryGetValue(id, out var m) ? m : null; }

        sealed class SpeedModel : ILatchedModel
        {
            public double Current(string key, double fallback) { var m = Plot(key); return m != null ? (int)m.feederCycleSpeed : fallback; }
            public bool TryApply(string key, double value, bool fromSnapshot)
            {
                try
                {
                    var m = Plot(key); if (m == null) return false; var speed = (SlimeFeeder.FeedSpeed)(int)value;
                    var feeder = m.gameObj != null ? m.gameObj.GetComponentInChildren<SlimeFeeder>(true) : null;
                    if (m.feederCycleSpeed == speed && (feeder == null || feeder.GetFeedingCycleSpeed() == speed)) return true;
                    using (Applying.Enter()) { if (feeder != null) feeder.SetFeederSpeed(speed); else m.feederCycleSpeed = speed; }
                    Log.Info($"plot {key}: feeder speed {speed} applied" + (feeder != null ? "" : " to the model (area not loaded)"));
                    return true;
                }
                catch (Exception e) { Log.Debug($"feeder speed {key}: {e.Message}"); return false; }
            }
        }

        /// <summary>A local speed change (the button): the host broadcasts it, a guest sends it to the host. A guest only sends plots the
        /// host already told it about, so a plot loading from the guest's own save never overwrites the host's speed.</summary>
        public void OnLocalSpeed(SlimeFeeder feeder, SlimeFeeder.FeedSpeed speed)
        {
            if (Applying.Active || feeder == null || !Mod.IsNetworkActive) return;
            var id = GardenAdapter.PlotIdOf(feeder); if (id == null) return;
            var known = Speeds.Get(id); if (known == (int)speed) return;
            if (Mod.IsGuest && known == null) return;
            Speeds.Publish(id, (int)speed);
            Log.Info($"plot {id}: local feeder speed {speed}: {(Mod.IsHost ? "published" : "sent to the host")}");
        }
    }

    [HarmonyPatch(typeof(SlimeFeeder), nameof(SlimeFeeder.SetFeederSpeed))]
    static class FeederSpeedHook
    {
        static void Postfix(SlimeFeeder __instance, SlimeFeeder.FeedSpeed speed)
        {
            try { if (WH.On) FeederSpeedAdapter.Instance?.OnLocalSpeed(__instance, speed); }
            catch (Exception e) { HookGuard.Fail("SlimeFeeder.SetFeederSpeed", e); }
        }
    }
}

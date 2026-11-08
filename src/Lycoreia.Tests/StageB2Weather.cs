// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.Snapshot;
using Lycoreia.World;

namespace Lycoreia.Tests
{
    static partial class StageB2Tests
    {
        static T RoundTrip<T>(T m) where T : Msg, new()
        {
            var w = new NetWriter(); m.Describe(new Codec(w)); var b = w.Span.ToArray();
            var r = NetReader.Rent(b, 0, b.Length); try { var back = new T(); back.Describe(new Codec(r)); return back; } finally { r.Return(); }
        }

        static partial void RegisterWeatherImpl()
        {
            T.Test("weather/host publishes only changes with a new revision; guest applies the newest, keeps it pending while the scene is not ready", () =>
            {
                var (w, h, g) = Rig(1); var snap = new SnapshotHost(h); var ch = new WorldClockSync(h, snap); var cg = new WorldClockSync(g[0]);
                var applied = new List<byte>(); bool ready = false; cg.ApplyWeather = d => { if (!ready) return false; applied.Add(d[0]); return true; };
                T.True(ch.HostPublish(new byte[] { 1 })); T.False(ch.HostPublish(new byte[] { 1 }), "unchanged forecast not resent"); T.True(ch.HostPublish(new byte[] { 2 })); w.Net.Step(300);
                T.Eq(0, applied.Count, "scene not ready"); T.True(cg.HasPending); T.Eq(2L, cg.LatestReceived);
                ready = true; T.True(cg.Pump()); T.Eq(1, applied.Count); T.Eq((byte)2, applied[0], "only the newest applies"); T.False(cg.HasPending);
                h.Node.SendToAll(new WeatherUpdate { Revision = 1, Data = new byte[] { 9 } }); w.Net.Step(300); T.Eq(1, applied.Count, "older revision ignored");
                ch.HostPublish(new byte[] { 3 }); w.Net.Step(300); T.Eq((byte)3, applied.Last());
                h.Node.SendToAll(new WorldTime { Revision = 1, Time = 77 }); w.Net.Step(300); T.Eq(77.0, cg.Time, "time revisions are their own stream (weather revision 3 does not block time revision 1)");
                var late = Late(w); var cl = new WorldClockSync(late); var lateApplied = new List<byte>(); cl.ApplyWeather = d => { lateApplied.Add(d[0]); return true; }; JoinLate(w, late);
                T.Eq((byte)3, lateApplied.Single(), "joiners get the current forecast from the snapshot");
                g[0].Disconnect(); w.Net.Step(300); T.Eq(0L, cg.LatestReceived, "reset on disconnect");
            });
            T.Test("weather/island registry replacement reapplies canonical revision without new wire data", () =>
            {
                var (w, h, g) = Rig(1); var ch = new WorldClockSync(h, new SnapshotHost(h)); var cg = new WorldClockSync(g[0]);
                int applications = 0; bool ready = true;
                cg.ApplyWeather = d => { if (!ready) return false; applications++; return true; };
                ch.HostPublish(new byte[] { 7 }); w.Net.Step(300); T.Eq(1, applications);
                ready = false; T.False(cg.ReapplyWeather()); T.Eq(1L, cg.AppliedRevision);
                ready = true; T.True(cg.ReapplyWeather()); T.Eq(2, applications); T.Eq(1L, cg.AppliedRevision);
                cg.Reset(); T.False(cg.ReapplyWeather(), "old session forecast is never replayed");
            });
            T.Test("messages/extended stage B2 messages round-trip", () =>
            {
                var t = RoundTrip(new DroneTask { ActorId = 5, TargetIdent = 3, Target = 2, Sink = 1, Source = 4, Intent = true }); T.True(t.Intent && t.ActorId == 5 && t.Source == 4);
                var b = RoundTrip(new DroneBattery { ActorId = 5, Charge = 0.5f, DepletedPerHour = 2, Intent = true }); T.True(b.Intent && b.Charge == 0.5f);
                var c = RoundTrip(new DroneCloud { TypeId = 7, Amount = -3, IsDelta = true }); T.True(c.IsDelta && c.Amount == -3);
                var p = RoundTrip(new InitialLandPlots { Items = { new PlotRecord { Id = "plot1", Type = 4, Upgrades = new List<int> { 6, 7 }, Crop = 12, CropRevision = 9 } } });
                T.Eq(12, p.Items[0].Crop); T.Eq(9L, p.Items[0].CropRevision); T.Eq(2, p.Items[0].Upgrades.Count);
                var rewards = new PodRewards { Blueprints = new List<string> { "bp" }, Components = new List<string> { "core" }, Appearances = new List<PodAppearance> { new PodAppearance { SlimeDefinition = "pink", SaveSet = 2 } } };
                var o = RoundTrip(new PodOpen { PodId = 118409926, RequestId = 4, ClientActivated = true, Rewards = rewards });
                T.Eq(118409926, o.PodId); T.True(o.ClientActivated); T.Eq("bp", o.Rewards.Blueprints[0]); T.Eq("core", o.Rewards.Components[0]); T.Eq(2, o.Rewards.Appearances[0].SaveSet);
                var empty = RoundTrip(new PodOpen { PodId = 1, Rejected = true }); T.True(empty.Rejected); T.Eq(0, empty.Rewards.Blueprints.Count);
                var ip = RoundTrip(new InitialTreasurePods { Pods = { [3] = 1 }, Rewards = { [3] = rewards } }); T.Eq(1, ip.Pods[3]); T.Eq("pink", ip.Rewards[3].Appearances[0].SlimeDefinition);
                var wu = RoundTrip(new WeatherUpdate { Revision = 8, Data = new byte[] { 1, 2 } }); T.Eq(8L, wu.Revision); T.Eq(2, wu.Data.Length);
                var f = new WeatherForecast { Zones = { new ZoneForecast { Zone = "zone_fields", Entries = new List<WeatherEntry> { new WeatherEntry { Pattern = "g", State = "Rain", Start = 1, End = 2, EndKnown = true, Started = true } } } } };
                var fw = new NetWriter(); f.Describe(new Codec(fw)); var fb = fw.Span.ToArray(); var fr = NetReader.Rent(fb, 0, fb.Length); var f2 = new WeatherForecast(); f2.Describe(new Codec(fr)); fr.Return();
                T.Eq("Rain", f2.Zones[0].Entries[0].State); T.True(f2.Zones[0].Entries[0].Started && f2.Zones[0].Entries[0].EndKnown);
            });
        }
        static partial void RegisterMoreImpl();
    }
}

// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Lycoreia.Domain;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.Snapshot;
using Lycoreia.World;

namespace Lycoreia.Tests
{
    static partial class StageB2Tests
    {
        sealed class FakeDrones : IDroneWorld { public bool Exists = true, Hibernated; public bool DroneExists(long id) => Exists; public bool DroneHibernated(long id) => Hibernated; }

        static (World w, Session h, List<Session> g) Rig(int guests)
        {
            var w = new World(); var h = w.StartHost(); var list = new List<Session>();
            for (int i = 0; i < guests; i++) { var g = w.AddGuest("G" + i); w.Join(g); T.True(w.Net.RunUntil(() => g.Joined, 4000)); list.Add(g); }
            w.Net.Step(300); return (w, h, list);
        }
        static Session Late(World w) { var l = w.AddGuest("Late"); return l; }
        static void JoinLate(World w, Session l) { w.Join(l); T.True(w.Net.RunUntil(() => l.Joined, 4000)); w.Net.Step(600); }

        public static void Register()
        {
            T.Test("lease/planner: owner releases on hibernation, loaded peer claims after the delay only when nobody connected owns it", () =>
            {
                T.Eq(LeaseAction.None, LeasePlanner.Decide("me", "me", true, true, 50, 2));
                T.Eq(LeaseAction.Release, LeasePlanner.Decide("me", "me", false, true, 0, 2));
                T.Eq(LeaseAction.None, LeasePlanner.Decide("other", "me", true, true, 50, 2), "connected owner keeps it");
                T.Eq(LeaseAction.Claim, LeasePlanner.Decide("other", "me", true, false, 3, 2), "disconnected owner");
                T.Eq(LeaseAction.None, LeasePlanner.Decide("", "me", true, false, 1, 2), "not loaded long enough");
                T.Eq(LeaseAction.Claim, LeasePlanner.Decide("", "me", true, false, 2, 2));
                T.Eq(LeaseAction.None, LeasePlanner.Decide("", "me", false, false, 99, 2), "not loaded here");
                for (int i = 0; i < 50; i++) { double d = LeasePlanner.GardenDelay("garden" + i, "p"); T.True(d >= 12 && d <= 16, "12-16 s"); }
                T.Eq(LeasePlanner.GardenDelay("g", "p"), LeasePlanner.GardenDelay("g", "p"), "stable");
            });
            T.Test("drones/intent from a non-owner is routed to the owner, who republishes it as owner", () =>
            {
                var (w, h, g) = Rig(2); var dw = new FakeDrones();
                var lh = new DroneLeases(h, dw); var l0 = new DroneLeases(g[0], dw); var l1 = new DroneLeases(g[1], dw);
                var applied = new Dictionary<string, int>(); void Count(string who) => applied[who] = applied.TryGetValue(who, out var n) ? n + 1 : 1;
                lh.OnTask = m => Count("h" + (m.Intent ? "i" : "")); l0.OnTask = m => Count("0" + (m.Intent ? "i" : "")); l1.OnTask = m => Count("1" + (m.Intent ? "i" : ""));
                lh.HostClaimAll(new long[] { 7 }); T.True(lh.ShouldSimulate(7)); T.False(l0.ShouldSimulate(7));
                g[0].Node.SendToHost(new DroneTask { ActorId = 7, Target = 2, Intent = true }); w.Net.Step(300);
                T.Eq(1, applied.GetValueOrDefault("hi"), "host owner applies the intent"); T.Eq(1, applied.GetValueOrDefault("1"), "others get the canonical task"); T.Eq(1, applied.GetValueOrDefault("0"));
                // guest 1 takes the drone (host releases it as hibernated there), then guest 0's intent goes to guest 1
                dw.Hibernated = true; lh.Release(7); w.Net.Step(300); T.Eq("", lh.OwnerOf(7));
                l1.Claim(7); w.Net.Step(300); T.Eq(g[1].LocalId, lh.OwnerOf(7)); T.True(l1.ShouldSimulate(7)); T.False(lh.ShouldSimulate(7));
                applied.Clear(); g[0].Node.SendToHost(new DroneTask { ActorId = 7, Target = 3, Intent = true }); w.Net.Step(400);
                T.Eq(1, applied.GetValueOrDefault("1i"), "owner applies the forwarded intent"); T.Eq(1, applied.GetValueOrDefault("h"), "host sees the owner's canonical task"); T.Eq(1, applied.GetValueOrDefault("0"));
                T.Eq(0, applied.GetValueOrDefault("hi"), "the host never applies an intent for a guest-owned drone");
                applied.Clear(); g[0].Node.SendToHost(new DroneTask { ActorId = 7, Target = 4 }); w.Net.Step(300); T.Eq(0, applied.Count, "a non-owner's direct task is rejected");
            });
            T.Test("drones/owners reach late joiners through the snapshot; a leaving owner frees the drone", () =>
            {
                var (w, h, g) = Rig(1); var dw = new FakeDrones { Hibernated = true }; var snap = new SnapshotHost(h);
                var lh = new DroneLeases(h, dw, snap); var l0 = new DroneLeases(g[0], dw);
                lh.HostClaimAll(new long[] { 3 }); lh.Release(3); w.Net.Step(200); l0.Claim(3); w.Net.Step(300); T.Eq(g[0].LocalId, lh.OwnerOf(3));
                var late = Late(w); var ll = new DroneLeases(late, dw); JoinLate(w, late);
                T.Eq(g[0].LocalId, ll.OwnerOf(3), "late joiner learns the owner"); T.False(ll.ShouldSimulate(3));
                lh.ForgetPlayer(g[0].LocalId); T.True(lh.IsReleased(3)); T.Eq("", lh.OwnerOf(3)); T.True(lh.ShouldSimulate(3), "host simulates an ownerless drone");
            });
            T.Test("drones/battery intent: the owner adds the fuel and answers with its canonical charge", () =>
            {
                var (w, h, g) = Rig(1); var dw = new FakeDrones(); var lh = new DroneLeases(h, dw); var l0 = new DroneLeases(g[0], dw);
                float charge = 0; lh.OnBattery = m => { charge = m.Intent ? charge + m.Charge : m.Charge; if (m.Intent) h.Node.SendToAll(new DroneBattery { ActorId = m.ActorId, Charge = charge, DepletedPerHour = 1 }); };
                float guestSeen = -1; l0.OnBattery = m => guestSeen = m.Charge;
                lh.HostClaimAll(new long[] { 9 }); g[0].Node.SendToHost(new DroneBattery { ActorId = 9, Charge = 0.25f, DepletedPerHour = 1, Intent = true }); w.Net.Step(300);
                T.Eq(0.25f, charge); T.Eq(0.25f, guestSeen);
            });
            T.Test("drones/cloud: guest deltas clamp at 0 on the host, absolute amounts to all, applying never echoes, joiners get the snapshot", () =>
            {
                var (w, h, g) = Rig(1); var snap = new SnapshotHost(h); var ch = new DroneCloudSync(h, snap); var c0 = new DroneCloudSync(g[0], null);
                ch.Seed(5, 10); c0.LocalChanged(5, 4); w.Net.Step(300); T.Eq(14, ch.Get(5), "the guest's +4 lands on the host's 10"); T.Eq(14, c0.Get(5), "the guest takes the canonical amount");
                c0.LocalChanged(5, -1); T.Eq(14, ch.Get(5), "negative local amounts are ignored");
                g[0].Node.SendToHost(new DroneCloud { TypeId = 5, Amount = -100, IsDelta = true }); w.Net.Step(300); T.Eq(0, ch.Get(5), "clamped at 0"); T.Eq(0, c0.Get(5));
                g[0].Node.SendToHost(new DroneCloud { TypeId = 5, Amount = 50 }); w.Net.Step(300); T.Eq(0, ch.Get(5), "absolute amounts from a guest are rejected");
                ch.LocalChanged(6, 12); w.Net.Step(300); T.Eq(12, c0.Get(6));
                using (Applying.Enter()) ch.LocalChanged(6, 99); w.Net.Step(200); T.Eq(12, c0.Get(6), "applying a received message never re-broadcasts");
                var late = Late(w); var cl = new DroneCloudSync(late, null); JoinLate(w, late); T.Eq(12, cl.Get(6)); T.Eq(0, cl.Get(5));
            });
            RegisterGardens(); RegisterTeleporters(); RegisterWeather(); RegisterMore();
        }
        static partial void RegisterGardensImpl(); static partial void RegisterTeleportersImpl(); static partial void RegisterWeatherImpl();
        static void RegisterGardens() => RegisterGardensImpl(); static void RegisterTeleporters() => RegisterTeleportersImpl();
        static void RegisterWeather() => RegisterWeatherImpl();
    }
}

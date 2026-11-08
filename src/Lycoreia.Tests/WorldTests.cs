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
    sealed class FakeModel : ILatchedModel
    {
        public Dictionary<string, double> Values = new Dictionary<string, double>(); public HashSet<string> NotReady = new HashSet<string>(); public int Applies;
        public bool TryApply(string key, double value, bool snap) { if (NotReady.Contains(key)) return false; Values[key] = value; Applies++; return true; }
        public double Current(string key, double fallback) => Values.TryGetValue(key, out var v) ? v : fallback;
    }

    static class WorldTests
    {
        static (World w, Session h, List<Session> g) Rig(int guests)
        {
            var w = new World(); var h = w.StartHost(); var list = new List<Session>();
            for (int i = 0; i < guests; i++) { var g = w.AddGuest("G" + i); w.Join(g); T.True(w.Net.RunUntil(() => g.Joined, 4000)); list.Add(g); }
            w.Net.Step(300); return (w, h, list);
        }

        public static void Register()
        {
            T.Test("latched/guest change reaches host and the other guest; invalid input rejected", () =>
            {
                var (w, h, g) = Rig(2); var mh = new FakeModel(); var m0 = new FakeModel(); var m1 = new FakeModel();
                Func<SwitchActivate, (string, double)> read = m => (m.Id, m.State); Func<string, double, SwitchActivate> make = (k, v) => new SwitchActivate { Id = k, State = (int)v };
                var sh = new LatchedSystem<SwitchActivate>(h, mh, read, make); var s0 = new LatchedSystem<SwitchActivate>(g[0], m0, read, make); var s1 = new LatchedSystem<SwitchActivate>(g[1], m1, read, make);
                s0.Publish("doorA", 2); w.Net.Step(300); T.Eq(2.0, mh.Values["doorA"]); T.Eq(2.0, m1.Values["doorA"]);
                s0.Publish("", 1); s0.Publish("x", double.NaN); w.Net.Step(300); T.Eq(1, mh.Values.Count);
                g[1].Node.SendToHost(new SwitchActivate { Id = "", State = 1 }); w.Net.Step(300); T.Eq(1, mh.Values.Count, "empty id never reaches the model");
            });
            T.Test("latched/guest prediction cannot unlock a confirmed host progression gate", () =>
            {
                var (w, h, g) = Rig(1); var model = new FakeModel();
                var guest = new LatchedSystem<PuzzleSlot>(g[0], model, m => (m.Id, m.Filled ? 1 : 0), (key, value) => new PuzzleSlot { Id = key, Filled = value > 0 });
                guest.Apply("portal", 0, true); guest.Publish("portal", 1);
                T.Eq(0.0, guest.Confirmed("portal"), "prediction is not host confirmation");
                h.Node.SendToAll(new PuzzleSlot { Id = "portal", Filled = true }); w.Net.Step(300);
                T.Eq(1.0, guest.Confirmed("portal"));
                guest.Clear(); T.True(guest.Confirmed("portal") == null);
            });
            T.Test("latched/state before its model exists is cached and applied later, snapshot never overwrites newer", () =>
            {
                var (w, h, g) = Rig(1); var m0 = new FakeModel { NotReady = { "slot1" } };
                var s0 = new LatchedSystem<PuzzleSlot>(g[0], m0, m => (m.Id, m.Filled ? 1 : 0), (k, v) => new PuzzleSlot { Id = k, Filled = v > 0 });
                var sh = new LatchedSystem<PuzzleSlot>(h, new FakeModel(), m => (m.Id, m.Filled ? 1 : 0), (k, v) => new PuzzleSlot { Id = k, Filled = v > 0 });
                sh.Publish("slot1", 1); w.Net.Step(300); T.True(!m0.Values.ContainsKey("slot1")); T.Eq(1, s0.Pending.Count);
                m0.NotReady.Clear(); T.True(s0.OnModelReady("slot1")); T.Eq(1.0, m0.Values["slot1"]);
                s0.Apply("slot2", 1, false); s0.LoadSnapshot(new[] { ("slot2", 0.0) }); T.Eq(1.0, s0.Get("slot2"), "last live value kept (latest wins is the default merge, snapshot applies as merge(value, current))");
            });
            T.Test("latched/counters merge monotonic", () =>
            {
                var (w, h, g) = Rig(1); var mh = new FakeModel();
                var sh = new LatchedSystem<PlortDepositor>(h, mh, m => (m.Id, m.Amount), (k, v) => new PlortDepositor { Id = k, Amount = (int)v }, Math.Max, (k, v) => !string.IsNullOrEmpty(k) && v >= 0);
                sh.Publish("dep", 5); sh.Publish("dep", 3); w.Net.Step(200); T.Eq(5.0, sh.Get("dep")); g[0].Node.SendToHost(new PlortDepositor { Id = "dep", Amount = -4 }); w.Net.Step(200); T.Eq(5.0, sh.Get("dep"), "negative rejected");
            });
            T.Test("drones/claim rules, release, takeover", () =>
            {
                var (w, h, g) = Rig(2); var dw = new FakeDroneWorld(); var lh = new DroneLeases(h, dw); var l0 = new DroneLeases(g[0], dw); var l1 = new DroneLeases(g[1], dw);
                g[0].Node.SendToHost(new DroneOwnership { StationId = 5, ClaimerId = g[0].LocalId, PreviousOwnerId = "" }); w.Net.Step(300); T.Eq(g[0].LocalId, lh.OwnerOf(5)); T.Eq(g[0].LocalId, l1.OwnerOf(5));
                g[1].Node.SendToHost(new DroneOwnership { StationId = 5, ClaimerId = g[1].LocalId, PreviousOwnerId = g[0].LocalId }); w.Net.Step(300); T.Eq(g[0].LocalId, lh.OwnerOf(5), "refused while the owner is connected and has not released");
                g[1].Node.SendToHost(new DroneOwnership { StationId = 5, ClaimerId = g[0].LocalId, PreviousOwnerId = g[0].LocalId }); w.Net.Step(300); T.Eq(g[0].LocalId, lh.OwnerOf(5), "claiming for someone else is rejected");
                dw.Exists = true; dw.Hibernated = true; g[0].Node.SendToHost(new DroneOwnership { StationId = 5, ClaimerId = "", PreviousOwnerId = g[0].LocalId }); w.Net.Step(300); T.Eq("", lh.OwnerOf(5), "released, drone hibernated: nobody takes over");
                g[1].Node.SendToHost(new DroneOwnership { StationId = 5, ClaimerId = g[1].LocalId, PreviousOwnerId = "" }); w.Net.Step(300); T.Eq(g[1].LocalId, lh.OwnerOf(5), "accepted after a release");
                dw.Hibernated = false; g[1].Node.SendToHost(new DroneOwnership { StationId = 5, ClaimerId = "", PreviousOwnerId = g[1].LocalId }); w.Net.Step(300); T.Eq(h.LocalId, lh.OwnerOf(5), "host takes over a released, non-hibernated drone"); 
                int battery = 0; h.Node.On<DroneBattery>(Side.Host, (c, m) => battery++); g[0].Node.SendToHost(new DroneBattery { ActorId = 5, Charge = 1 }); w.Net.Step(300); T.True(lh.OwnerOf(5) == h.LocalId);
                var l2 = lh; g[0].Node.SendToHost(new DroneTask { ActorId = 5 }); w.Net.Step(200);
            });
            T.Test("economy/currency retry never doubles; overspend refused with canonical state; conflicting id rejected", () =>
            {
                var (w, h, g) = Rig(1); var ch = new CurrencyLedger(h); var cg = new CurrencyLedger(g[0]);
                var id = cg.Request(0, 100); w.Net.Step(300); T.Eq(100, ch.Get(0)); T.Eq(100, cg.Get(0));
                g[0].Node.SendToHost(new CurrencyAdjust { CurrencyType = 0, RequestId = id, Delta = 100 }); w.Net.Step(300); T.Eq(100, ch.Get(0), "a retry does not double the balance");
                g[0].Node.SendToHost(new CurrencyAdjust { CurrencyType = 0, RequestId = id, Delta = 5000 }); w.Net.Step(300); T.Eq(100, ch.Get(0), "same id with another payload is rejected");
                cg.Request(0, -500); w.Net.Step(300); T.Eq(100, ch.Get(0), "overspend refused"); T.Eq(100, cg.Get(0), "canonical state");
                cg.Request(0, -40); w.Net.Step(300); T.Eq(60, ch.Get(0)); T.Eq(60, cg.Get(0)); T.True(ch.Revision[0] == 2);
                var late = w.AddGuest("Late"); var cl = new CurrencyLedger(late); w.Join(late); T.True(w.Net.RunUntil(() => late.Joined, 4000)); T.Eq(60, cl.Get(0), "approve carries balances");
            });
            T.Test("economy/market sale pays host price once", () =>
            {
                var (w, h, g) = Rig(1); var ch = new CurrencyLedger(h); var mh = new MarketSystem(h, ch); mh.SetPrices(new Dictionary<int, int> { [7] = 25 }); w.Net.Step(200);
                g[0].Node.SendToHost(new PlortSold { RequestId = Guid.NewGuid(), ItemId = 7, Count = 4, Price = 99999 }); w.Net.Step(300); T.Eq(100, ch.Get(0), "host price is truth, not the payload's");
            });
            T.Test("refinery/two concurrent +1 give +2, a duplicate adds nothing, snapshot orders by revision", () =>
            {
                var (w, h, g) = Rig(2); var snap = new SnapshotHost(h); var rh = new RefinerySystem(h, snap); var r0 = new RefinerySystem(g[0], null); var r1 = new RefinerySystem(g[1], null);
                var id = Guid.NewGuid(); g[0].Node.SendToHost(new RefineryUpdate { RequestId = id, ItemId = 3, Delta = 1 }); g[1].Node.SendToHost(new RefineryUpdate { RequestId = Guid.NewGuid(), ItemId = 3, Delta = 1 }); w.Net.Step(400);
                T.Eq(2, rh.Items[3]); T.Eq(2, r0.Items[3]); T.Eq(2, r1.Items[3]);
                g[0].Node.SendToHost(new RefineryUpdate { RequestId = id, ItemId = 3, Delta = 1 }); w.Net.Step(300); T.Eq(2, rh.Items[3], "duplicate adds nothing");
                g[0].Node.SendToHost(new RefineryUpdate { RequestId = id, ItemId = 3, Delta = 7 }); w.Net.Step(300); T.Eq(2, rh.Items[3], "conflicting duplicate rejected");
                var late = w.AddGuest("Late"); var rl = new RefinerySystem(late, null); w.Join(late); T.True(w.Net.RunUntil(() => late.Joined, 4000)); w.Net.Step(500); 
                snap.Register(SnapStep.Refinery, _ => { }); snap.Run(h.Node.FindByPlayer(late.LocalId), false); w.Net.Step(500); T.Eq(2, rl.Items[3]);
            });
            T.Test("gardens/final crop follows host revision order", () =>
            {
                var (w, h, g) = Rig(2); var gh = new GardenSync(h); var g0 = new GardenSync(g[0]); var g1 = new GardenSync(g[1]);
                g[0].Node.SendToHost(new GardenPlant { PlotId = "p", GardenId = "garden1", ActorType = 9 }); g[1].Node.SendToHost(new GardenPlant { PlotId = "p", GardenId = "garden1", ActorType = 0 }); w.Net.Step(400);
                T.Eq(g1.Crops.GetValueOrDefault("garden1"), gh.Crops.GetValueOrDefault("garden1"), "everyone converges on the host's order"); T.Eq(g0.Crops.GetValueOrDefault("garden1"), gh.Crops.GetValueOrDefault("garden1"));
                g[0].Node.SendToHost(new GardenPlant { GardenId = "garden1", ActorType = 5 }); w.Net.Step(300); T.Eq(5, g1.Crops["garden1"]);
                h.Node.SendToAll(new GardenPlant { GardenId = "garden1", ActorType = 0, Revision = 1 }); w.Net.Step(300); T.Eq(5, g1.Crops["garden1"], "a late message with an old revision cannot remove the newer crop");
            });
            T.Test("gordos/host diet units complete burst without a guest burst packet", () =>
            {
                var (w, host, guests) = Rig(1); var h = new GordoSync(host); var g = new GordoSync(guests[0]);
                h.FoodUnits = (id, food) => food == 518 ? 2 : 0; h.Seed("favorite", 3, 9);
                int paid = 0; h.OnPayout = id => paid++;
                var request = new GordoFeed { Id = "favorite", RequestId = "food1", FoodActorId = 910, FoodType = 518, NewFoodCount = 2, RequiredFoodCount = 3, GordoType = 9 };
                guests[0].Node.SendToHost(request); w.Net.Step(300); T.Eq(2, h.Gordos["favorite"].Eaten);
                var late = w.AddGuest("Later feeder"); var later = new GordoSync(late); w.Join(late);
                T.True(w.Net.RunUntil(() => late.Joined, 4000));
                guests[0].Node.SendToHost(request); w.Net.Step(300); T.Eq(2, h.Gordos["favorite"].Eaten, "one food actor cannot count twice");
                T.False(h.Gordos["favorite"].Popped, "joining cannot erase the consumed-food deduplication ledger");
                T.Eq(2, h.HostLocal("favorite", 4, 3, 9, false, 910, 2), "host observing the same favorite food cannot count its two units again");
                T.False(h.Gordos["favorite"].Popped, "native overcount cannot start a canonical burst");
                guests[0].Node.SendToHost(new GordoFeed { Id = "favorite", RequestId = "food2", FoodActorId = 911, FoodType = 518, NewFoodCount = 3, RequiredFoodCount = 3, GordoType = 9 }); w.Net.Step(300);
                T.True(h.Gordos["favorite"].Popped); T.True(g.Gordos["favorite"].Popped); T.Eq(3, g.Gordos["favorite"].Eaten); T.Eq(1, paid);
            });
            T.Test("gordos/feeds count once, target 0 adopted, burst pays once", () =>
            {
                var (w, h, g) = Rig(2); var gh = new GordoSync(h); var g0 = new GordoSync(g[0]); var g1 = new GordoSync(g[1]); int payouts = 0; gh.OnPayout = _ => payouts++;
                gh.Seed("gordo", 0, 3);
                g[0].Node.SendToHost(new GordoFeed { Id = "gordo", RequestId = "r1", FoodActorId = 101, NewFoodCount = 1, RequiredFoodCount = 5, GordoType = 3 }); g[0].Node.SendToHost(new GordoFeed { Id = "gordo", RequestId = "r1", FoodActorId = 101, NewFoodCount = 1, RequiredFoodCount = 5, GordoType = 3 }); w.Net.Step(400);
                T.Eq(1, gh.Gordos["gordo"].Eaten, "duplicates count once"); T.Eq(5, gh.Gordos["gordo"].Required, "host target 0 adopted"); T.Eq(1, g1.Gordos["gordo"].Eaten);
                for (int i = 2; i <= 5; i++)
                {
                    g[1].Node.SendToHost(new GordoFeed { Id = "gordo", RequestId = "r" + i, FoodActorId = 100 + i, NewFoodCount = i, RequiredFoodCount = 5, GordoType = 3 });
                    w.Net.Step(200);
                }
                T.Eq(5, gh.Gordos["gordo"].Eaten);
                g[0].Node.SendToHost(new GordoBurst { Id = "gordo", EatenCount = 5, RequiredFoodCount = 5, GordoType = 3 }); g[0].Node.SendToHost(new GordoBurst { Id = "gordo", EatenCount = 5, RequiredFoodCount = 5, GordoType = 3 }); w.Net.Step(400);
                T.Eq(1, payouts, "the host pays out once"); T.True(g1.Gordos["gordo"].Popped);
                gh.Seed("native", 0, 0);
                gh.HostLocal("native", 5, 5, 3, true); w.Net.Step(200);
                T.True(g0.Gordos["native"].Popped); T.True(g1.Gordos["native"].Popped);
                g0.Seed("native", 5, 3, 0, false);
                T.True(g0.Gordos["native"].Popped, "late pre-burst snapshot cannot resurrect a Gordo");
                T.Eq(5, g0.Gordos["native"].Eaten);
                int applied = 0; g0.OnState = (id, state) => { if (id == "native") applied++; };
                gh.HostLocal("native", 5, 5, 3, true); w.Net.Step(200);
                T.Eq(0, applied, "a repeated native terminal event is not republished");
            });
            T.Test("pods/open once", () =>
            {
                var (w, h, g) = Rig(2); var ph = new TreasurePodSystem(h); var p1 = new TreasurePodSystem(g[1]); int pay = 0; ph.OnPayout = (id, r) => pay++; bool rejected = false; var p0 = new TreasurePodSystem(g[0]);
                g[0].Node.On<PodOpen>(Side.Guest, (c, m) => { if (m.Rejected) rejected = true; });
                g[0].Node.SendToHost(new PodOpen { PodId = 42, RequestId = 1 }); w.Net.Step(300); g[1].Node.SendToHost(new PodOpen { PodId = 42, RequestId = 2 }); w.Net.Step(300);
                T.Eq(1, pay); T.True(p1.IsOpen(42) && p0.IsOpen(42));
            });
            T.Test("teleporters/unlink removes only its own partner and snapshot carries links", () =>
            {
                var (w, h, g) = Rig(1); var snap = new SnapshotHost(h); var th = new TeleporterLinks(h, snap); var t0 = new TeleporterLinks(g[0], null);
                g[0].Node.SendToHost(new TeleporterLink { SourceNodeId = "A1", DestinationNodeId = "A2", DestinationSceneGroup = 1 }); g[0].Node.SendToHost(new TeleporterLink { SourceNodeId = "B1", DestinationNodeId = "B2", DestinationSceneGroup = 1 }); w.Net.Step(300);
                T.Eq(2, th.Links.Count); g[0].Node.SendToHost(new TeleporterUnlink { SourceNodeId = "A1", DestinationNodeId = "A2" }); w.Net.Step(300); T.Eq(1, th.Links.Count); T.True(th.Links.ContainsKey("B1")); T.Eq(1, t0.Links.Count);
                var late = w.AddGuest("Late"); var tl = new TeleporterLinks(late, null); w.Join(late); T.True(w.Net.RunUntil(() => late.Joined, 4000)); w.Net.Step(500); T.True(!tl.Links.ContainsKey("A1"), "a link removed on the host never reappears for a joiner");
            });
            T.Test("events/zone unlock counts only grow and reach late joiners", () =>
            {
                var (w, h, g) = Rig(1); var snap = new SnapshotHost(h); var eh = new EventSync(h, snap); var e0 = new EventSync(g[0], null); int raised = 0; eh.OnRaise = (e, d, n) => raised++;
                g[0].Node.SendToHost(new EventRaised { EventKey = "zoneUnlocked", DataKey = "Desert", Count = 1 }); w.Net.Step(300); T.Eq(1, eh.Count("zoneUnlocked", "Desert")); T.Eq(1, e0.Count("zoneUnlocked", "Desert"));
                g[0].Node.SendToHost(new EventRaised { EventKey = "zoneUnlocked", DataKey = "Desert", Count = 1 }); g[0].Node.SendToHost(new EventRaised { EventKey = "zoneUnlocked", DataKey = "Desert", Count = 0 }); w.Net.Step(300); T.Eq(1, raised, "no duplicate rewards, a stale count rolls nothing back");
                g[0].Node.SendToHost(new EventRaised { EventKey = "zoneUnlocked", DataKey = "bad\u0001key", Count = 1 }); w.Net.Step(200); T.Eq(0, eh.Count("zoneUnlocked", "bad\u0001key"));
                var late = w.AddGuest("Late"); var el = new EventSync(late, null); w.Join(late); T.True(w.Net.RunUntil(() => late.Joined, 4000)); w.Net.Step(500); T.Eq(1, el.Count("zoneUnlocked", "Desert"));
                eh.Seed("fogRevealed", "Fields", 1); eh.Seed("fogRevealed", "Fields", 0); eh.Seed("objectDiscovered", "x", 3);
                T.Eq(0, e0.Count("fogRevealed", "Fields"), "seeding is not a broadcast");
                int fogReplays = 0, hostSnapshots = 0; e0.OnRaise = (key, data, count) => { if (key == "fogRevealed") fogReplays++; };
                e0.OnHostSnapshot = () => { T.True(e0.HasHostSnapshot); hostSnapshots++; };
                int fogTraces = 0; e0.Trace = (name, details) => { fogTraces++; if (fogTraces == 1) throw new InvalidOperationException("diagnostic sink unavailable"); };
                eh.RepublishFog(); w.Net.Step(300); T.Eq(1, e0.Count("fogRevealed", "Fields"));
                eh.RepublishFog(); w.Net.Step(300); T.Eq(2, fogReplays, "duplicate map checkpoint retries physical rendering without growing its count");
                T.Eq(2, hostSnapshots, "native reconciliation also runs for unchanged checkpoints");
                T.Eq(1, e0.Count("fogRevealed", "Fields"));
                T.Eq(2, fogTraces, "both checkpoint decisions are observable and a failed trace cannot block application");
                var late2 = w.AddGuest("Late2"); var el2 = new EventSync(late2, null); w.Join(late2); T.True(w.Net.RunUntil(() => late2.Joined, 4000)); w.Net.Step(500);
                T.True(el2.HasHostSnapshot, "canonical map rendering waits for the host event snapshot");
                T.Eq(1, el2.Count("fogRevealed", "Fields"), "the host's saved map unlocks reach joiners"); T.Eq(3, el2.Count("objectDiscovered", "x"));
            });
            T.Test("time/weather newest only, fast-forward to the past ignored, early weather waits for the model", () =>
            {
                var (w, h, g) = Rig(1); var c = new WorldClockSync(g[0]); h.Node.SendToAll(new WorldTime { Revision = 2, Time = 100 }, Delivery.ReliableOrdered); w.Net.Step(300); T.Eq(100.0, c.Time);
                h.Node.SendToAll(new WorldTime { Revision = 1, Time = 50 }); w.Net.Step(300); T.Eq(100.0, c.Time, "older revision ignored");
                h.Node.SendToAll(new FastForward { Time = 40 }); w.Net.Step(300); T.Eq(100.0, c.Time, "fast-forward to an earlier time ignored"); h.Node.SendToAll(new FastForward { Time = 200 }); w.Net.Step(300); T.Eq(200.0, c.Time);
                c.ModelReady = false; c.OnWeatherRevision(5, new byte[] { 5 }); c.OnWeatherRevision(4, new byte[] { 4 }); c.OnWeatherRevision(7, new byte[] { 7 }); T.True(c.AppliedWeather == null); c.ModelBecameReady(); T.Eq(7L, c.AppliedRevision, "only the newest applies");
                T.True(WeatherSyncPolicy_Ok());
            });
            T.Test("upgrades/guest purchase validated and charged once, retry free, wrong level rejected", () =>
            {
                var (w, h, g) = Rig(1); var snap = new SnapshotHost(h); var wh = new FakeUpgradeWorld { Money = 150 }; var wg = new FakeUpgradeWorld(); var uh = new UpgradeSync(h, wh, snap); var ug = new UpgradeSync(g[0], wg, null);
                var id = Guid.NewGuid(); g[0].Node.SendToHost(new PlayerUpgrade { UpgradeId = 4, Level = 0, RequestId = id }); w.Net.Step(300); T.Eq(1, wh.Levels[4]); T.Eq(1, wg.Levels[4]); T.Eq(50, wh.Money);
                g[0].Node.SendToHost(new PlayerUpgrade { UpgradeId = 4, Level = 0, RequestId = id }); w.Net.Step(300); T.Eq(50, wh.Money, "a same-id retry charges nothing"); T.Eq(1, wh.Levels[4]);
                g[0].Node.SendToHost(new PlayerUpgrade { UpgradeId = 5, Level = 0, RequestId = id }); w.Net.Step(300); T.True(!wh.Levels.ContainsKey(5), "the same id with a different item is rejected");
                g[0].Node.SendToHost(new PlayerUpgrade { UpgradeId = 4, Level = 0, RequestId = Guid.NewGuid() }); w.Net.Step(300); T.Eq(1, wh.Levels[4], "stale expected level refused");
                g[0].Node.SendToHost(new PlayerUpgrade { UpgradeId = 4, Level = 1, RequestId = Guid.NewGuid() }); w.Net.Step(300); T.Eq(1, wh.Levels[4], "cannot afford"); T.Eq(50, wh.Money);
            });
            T.Test("upgrade purchase waits while the host Fabricator UI is open", () =>
            {
                var D = Lycoreia.Owned.UpgradePurchaseDeferral.Decide;
                T.True(D(false, 0, 5) == Lycoreia.Owned.UpgradePurchaseDeferral.Verdict.RunNow);
                T.True(D(true, 0, 5) == Lycoreia.Owned.UpgradePurchaseDeferral.Verdict.Wait);
                T.True(D(true, 0, Lycoreia.Owned.UpgradePurchaseDeferral.MaxWaitMilliseconds) == Lycoreia.Owned.UpgradePurchaseDeferral.Verdict.Refuse);
                T.True(D(false, 0, 99999) == Lycoreia.Owned.UpgradePurchaseDeferral.Verdict.RunNow);
            });
            T.Test("owned policies verify", () =>
            {
                T.True(Ver.Run(typeof(Lycoreia.Owned.DroneMutationPolicy), "VerifyRules")); T.True(Ver.Run(typeof(Lycoreia.Owned.RefineryMutationPolicy), "VerifyRules")); T.True(Ver.Run(typeof(Lycoreia.Owned.EventSyncPolicy), "VerifyRules")); T.True(Ver.Run(typeof(Lycoreia.Owned.EventSyncPolicy), "VerifyZoneUnlockRules"));
                T.True(Ver.Run(typeof(Lycoreia.Owned.WeatherSyncPolicy), "VerifyRules")); T.True(Ver.Run(typeof(Lycoreia.Owned.TreasurePodOpenPolicy), "VerifyRules")); T.True(Ver.Run(typeof(Lycoreia.Owned.GadgetLinkAuthorizationPolicy), "VerifyRules")); T.True(Ver.Run(typeof(Lycoreia.Owned.ShopPurchaseRequestLedger), "VerifyRules"));
                T.True(Ver.Run(typeof(Lycoreia.Owned.UpgradePurchaseRequestLedger), "VerifyRules")); T.True(Ver.Run(typeof(Lycoreia.Owned.MapUnlockPolicy), "VerifyRules")); T.True(Ver.Run(typeof(Lycoreia.Owned.ProgressionPacketInputPolicy), "VerifyRules")); T.True(Ver.Run(typeof(Lycoreia.Owned.ActorSpawnPublicationPolicy), "VerifyRules"));
                T.True(Ver.Run(typeof(Lycoreia.Owned.ActorDestroyPolicy), "VerifyRules")); T.True(Ver.Run(typeof(Lycoreia.Owned.ActorCaptureTransferPolicy), "VerifyRules"));
            });
        }
        static bool WeatherSyncPolicy_Ok() => Ver.Run(typeof(Lycoreia.Owned.WeatherSyncPolicy), "VerifyRules");
    }

    sealed class FakeDroneWorld : IDroneWorld { public bool Exists, Hibernated; public bool DroneExists(long id) => Exists; public bool DroneHibernated(long id) => Hibernated; }
    sealed class FakeUpgradeWorld : IUpgradeWorld
    {
        public Dictionary<int, int> Levels = new Dictionary<int, int>(); public int Money; public int Level(int id) => Levels.GetValueOrDefault(id); public int PriceFor(int id, int level) => 100; public int Currency => Money;
        public bool TryCharge(int amount) { if (Money < amount) return false; Money -= amount; return true; } public void SetLevel(int id, int level) { Levels[id] = level; }
    }
}

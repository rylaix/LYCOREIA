// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Lycoreia.Actors;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.Owned;
using Lycoreia.Snapshot;
using Lycoreia.World;

namespace Lycoreia.Tests
{
    sealed class FakeNodes : INodeWorld
    {
        public readonly Dictionary<string, NodePlacement> Recs = new Dictionary<string, NodePlacement>(); public bool IsReady = true; public int Applies;
        public bool Ready => IsReady;
        public bool TryRead(string id, out NodePlacement p) => Recs.TryGetValue(id, out p);
        public IEnumerable<NodePlacement> ReadAll() => Recs.Values.ToList();
        public bool Apply(NodeApply a, in NodePlacement p)
        {
            Applies++;
            if (a == NodeApply.Remove) Recs[p.Id] = new NodePlacement { Id = p.Id };
            else if (a == NodeApply.Place) Recs[p.Id] = p;
            else if (a == NodeApply.MarkEmpty) { var c = Recs[p.Id]; c.State = NodeRules.Harvested; Recs[p.Id] = c; }
            return true;
        }
        public static NodePlacement N(string id, string def, int variant = 0, double at = 500, int state = 1) => new NodePlacement { Id = id, HasNode = true, Definition = def, VariantIndex = variant, DespawnAtWorldTime = at, State = state, TypeIds = new List<int> { 4, 5 } };
    }

    sealed class FakeGadgets : IGadgetWorld
    {
        public readonly Dictionary<long, GadgetRecord> G = new Dictionary<long, GadgetRecord>(); public bool IsReady = true;
        public bool Ready => IsReady;
        public IEnumerable<GadgetRecord> All() => G.Values.ToList();
        public bool Exists(long id) => G.ContainsKey(id);
        public bool Create(in GadgetRecord r) { G[r.Id] = r; return true; }
        public bool Remove(in GadgetRecord r) => G.Remove(r.Id);
        public static GadgetRecord R(long id, int type, float x = 0, bool pre = false) => new GadgetRecord { Id = id, TypeId = type, Scene = 1, Position = new Vec3(x, 0, 0), PrePlaced = pre };
    }

    static class SyncFixTests
    {
        static (World w, Session h, List<Session> g) Rig(int guests)
        {
            var w = new World(); var h = w.StartHost(); var list = new List<Session>();
            for (int i = 0; i < guests; i++) { var g = w.AddGuest("G" + i); w.Join(g); T.True(w.Net.RunUntil(() => g.Joined, 4000)); list.Add(g); }
            w.Net.Step(300); return (w, h, list);
        }
        static void JoinLate(World w, Session l) { w.Join(l); T.True(w.Net.RunUntil(() => l.Joined, 4000)); w.Net.Step(600); }
        static long Block(Session s) => ActorIdBlock.For(s.LocalId, false).start;

        public static void Register()
        {
            T.Test("map/host snapshot replaces guest native fog counts in both directions", () =>
            {
                T.Eq(4, MapFogRules.NativeCount(false, 0, 4), "guest save stays intact before host snapshot");
                T.Eq(0, MapFogRules.NativeCount(true, 0, 4), "guest-only explored area becomes locked");
                T.Eq(1, MapFogRules.NativeCount(true, 1, 0), "host explored area becomes unlocked");
                T.Eq(1, MapFogRules.NativeCount(true, 1, 4), "native count matches host exactly");
            });
            T.Test("produce/prediction preserves native plant release before dynamic vacuum pull", () =>
            {
                foreach (bool owned in new[] { false, true })
                {
                    T.True(BodyModeRule.PredictionKinematic(true, owned, true), "approved attached fruit keeps native pending/release path");
                    T.True(BodyModeRule.PredictionKinematic(false, owned, true), "aborting cannot unfreeze an attached fruit");
                    T.False(BodyModeRule.PredictionKinematic(true, owned, false), "detached approved fruit can move");
                }
                T.False(BodyModeRule.PredictionKinematic(false, true, false));
                T.True(BodyModeRule.PredictionKinematic(false, false, false));
            });
            T.Test("hazards/host clock rollback preserves local remaining cooldown and expires old deadlines", () =>
            {
                T.Eq(102.0, HazardClockRules.RebaseDeadline(5002, 5000, 100));
                T.Eq(100.0, HazardClockRules.RebaseDeadline(4999, 5000, 100));
                T.Eq(50.0, HazardClockRules.RebaseDeadline(50, 5000, 100));
                T.Eq(102.0, HazardClockRules.RebaseDeadline(102, 100, 5000));
                T.Eq(102.0, HazardClockRules.RebaseDeadline(102, 100, 100));
            });
            // ---------------------------------------------------------------- map fog (owner report 5)
            T.Test("map/fog element is repaired only when its record says revealed, it still draws fog and no reveal animation is queued", () =>
            {
                T.True(MapFogRules.NeedsRepair(true, 1f, false));
                T.False(MapFogRules.NeedsRepair(true, 0f, false), "already drawn revealed");
                T.False(MapFogRules.NeedsRepair(true, 1f, true), "the reveal animation clears it");
                T.False(MapFogRules.NeedsRepair(false, 1f, false), "no record: stays fogged");
                T.True(MapFogRules.LooksFogged(1f)); T.False(MapFogRules.LooksFogged(0f));
                T.True(MapFogRules.NeedsCanonicalRepair(true, 1f), "host reveal overrides unexplored guest save");
                T.True(MapFogRules.NeedsCanonicalRepair(false, 0f), "host fog overrides explored guest save");
                T.False(MapFogRules.NeedsCanonicalRepair(true, 0f));
                T.False(MapFogRules.NeedsCanonicalRepair(false, 1f));
            });

            // ---------------------------------------------------------------- frozen owned actors (owner report 1)
            T.Test("bodies/a hibernating member keeps its frozen body; the change goes to the cache the game restores on wake", () =>
            {
                T.Eq((false, true), BodyModeRule.Plan(true, true));
                T.Eq((true, false), BodyModeRule.Plan(true, false));
                T.Eq((true, false), BodyModeRule.Plan(false, false), "no region member: write the body");
            });
            T.Test("bodies/census: hibernating and game-held bodies are not frozen-owned or dynamic replicas", () =>
            {
                string me = "ME", other = "OTHER";
                var table = new List<ActorEntry> { new ActorEntry { Id = 1, Owner = me }, new ActorEntry { Id = 2, Owner = me }, new ActorEntry { Id = 3, Owner = me }, new ActorEntry { Id = 4, Owner = other } };
                var local = new List<LocalActorFact>
                {
                    new LocalActorFact { Id = 1, Live = true, HasBody = true, Kinematic = true },                         // the 1896 case: owned, awake, frozen
                    new LocalActorFact { Id = 2, Live = true, HasBody = true, Kinematic = true, Hibernating = true },     // region asleep: the game holds it
                    new LocalActorFact { Id = 3, Live = true, HasBody = true, Kinematic = true, NativeHold = true },      // in a vacuum / on a plant
                    new LocalActorFact { Id = 4, Live = true, HasBody = true, Kinematic = false, Hibernating = true },
                };
                var r = ActorCensus.Build(table, local, me, id => true, 10);
                T.Eq(1, r.FrozenOwned.Count); T.Eq(1L, r.FrozenOwned[0]); T.Eq(0, r.DynamicReplicas.Count); T.Eq(2, r.Hibernating); T.Eq(1, r.NativeHeld);
                T.True(r.Summary().Contains("hibernating=2"));
            });
            T.Test("bodies/frozen owned repair fires once after the persistence time and again only after the actor recovered", () =>
            {
                var f = new FrozenOwnedRepair();
                T.Eq(0, f.Update(new long[] { 7 }, 0).Count);
                T.Eq(0, f.Update(new long[] { 7 }, FrozenOwnedRepair.PersistSeconds - 0.1).Count, "transient holds are left alone");
                T.Eq(7L, f.Update(new long[] { 7 }, FrozenOwnedRepair.PersistSeconds + 0.1).Single());
                T.Eq(0, f.Update(new long[] { 7 }, 20).Count, "once per episode");
                T.Eq(0, f.Update(new long[0], 21).Count);
                T.Eq(0, f.Update(new long[] { 7 }, 22).Count); T.Eq(1, f.Update(new long[] { 7 }, 22 + FrozenOwnedRepair.PersistSeconds).Count, "a new episode fires again");
            });

            // ---------------------------------------------------------------- resource nodes (owner report 3)
            T.Test("nodes/plan: place a new or different node, mark empty on a harvest, remove a despawn, nothing when equal", () =>
            {
                var a = FakeNodes.N("s1", "Ore"); var none = new NodePlacement { Id = "s1" };
                T.Eq(NodeApply.Place, NodeRules.Plan(false, default, a));
                T.Eq(NodeApply.Place, NodeRules.Plan(true, none, a));
                T.Eq(NodeApply.Place, NodeRules.Plan(true, FakeNodes.N("s1", "Brine"), a), "a different roll is replaced");
                T.Eq(NodeApply.Nothing, NodeRules.Plan(true, a, a));
                var h = a; h.State = NodeRules.Harvested; T.Eq(NodeApply.MarkEmpty, NodeRules.Plan(true, a, h)); T.Eq(NodeApply.Nothing, NodeRules.Plan(true, h, h));
                T.Eq(NodeApply.Remove, NodeRules.Plan(true, a, none)); T.Eq(NodeApply.Nothing, NodeRules.Plan(true, none, none));
                T.False(NodeRules.Valid(new NodePlacement { Id = "", HasNode = false })); T.False(NodeRules.Valid(new NodePlacement { Id = "x", HasNode = true, Definition = "" }));
            });
            T.Test("nodes/host keeps a guest's roll only where it has no node; a harvest only on the same node", () =>
            {
                var hostA = FakeNodes.N("s1", "Ore"); var g = FakeNodes.N("s1", "Brine"); var none = new NodePlacement { Id = "s1" };
                T.True(NodeRules.HostAccepts(false, default, g, false)); T.True(NodeRules.HostAccepts(true, none, g, false));
                T.False(NodeRules.HostAccepts(true, hostA, g, false), "host already has a node there");
                T.True(NodeRules.HostAccepts(true, hostA, hostA, true)); T.False(NodeRules.HostAccepts(true, hostA, g, true), "harvest of another node");
                T.False(NodeRules.HostAccepts(true, none, hostA, true));
                T.False(ResourceNodeSync.GuestMayRoll(true, false, false), "never before the snapshot"); T.True(ResourceNodeSync.GuestMayRoll(true, true, false)); T.True(ResourceNodeSync.GuestMayRoll(false, false, true));
            });
            T.Test("nodes/two seats end with the same nodes: snapshot replaces the guest's rolls, offers its extra ones, harvests and rolls flow both ways", () =>
            {
                var (w, h, gs) = Rig(1); var snap = new SnapshotHost(h);
                var hw = new FakeNodes(); hw.Recs["s1"] = FakeNodes.N("s1", "Ore"); hw.Recs["s2"] = new NodePlacement { Id = "s2" }; var hs = new ResourceNodeSync(h, snap, hw);
                var w0 = new FakeNodes(); var s0 = new ResourceNodeSync(gs[0], null, w0);
                var late = w.AddGuest("Late"); var lw = new FakeNodes(); lw.Recs["s1"] = FakeNodes.N("s1", "Brine", 2); lw.Recs["s3"] = FakeNodes.N("s3", "Coral"); lw.IsReady = false;
                var ls = new ResourceNodeSync(late, null, lw);
                JoinLate(w, late); T.True(ls.SnapshotApplied);
                T.Eq(1, lw.Recs.Count(kv => kv.Key == "s1" && kv.Value.Definition == "Brine"), "nothing applied while the model table is missing");
                lw.IsReady = true; ls.Pump(); w.Net.Step(300);
                T.Eq("Ore", lw.Recs["s1"].Definition, "the host's node replaces the guest's own roll"); T.True(!lw.Recs.TryGetValue("s2", out var s2) || !s2.HasNode);
                // the offer of s3 happens at snapshot time only when ready: re-offer through a local placement
                ls.LocalPlacement(lw.Recs["s3"]); w.Net.Step(300);
                T.Eq("Coral", hw.Recs["s3"].Definition, "a guest's node where the host has none is kept"); T.Eq("Coral", w0.Recs["s3"].Definition, "and relayed to the other guest");
                var roll = FakeNodes.N("s1", "Brine", 1, 900); ls.LocalPlacement(roll); w.Net.Step(300);
                T.Eq("Ore", hw.Recs["s1"].Definition, "host keeps its node"); T.Eq("Ore", lw.Recs["s1"].Definition, "the guest is corrected back");
                ls.LocalEmptied(lw.Recs["s1"]); w.Net.Step(300);
                T.Eq(NodeRules.Harvested, hw.Recs["s1"].State); T.Eq(NodeRules.Harvested, w0.Recs["s1"].State, "every seat shows the node empty");
                hs.LocalPlacement(new NodePlacement { Id = "s1" }); w.Net.Step(300);
                T.False(lw.Recs["s1"].HasNode, "host despawn reaches guests"); T.False(w0.Recs["s1"].HasNode);
            });
            T.Test("nodes/a guest's local despawn is never published (every seat's timer fires at the same world time)", () =>
            {
                var (w, h, gs) = Rig(1); var hw = new FakeNodes(); hw.Recs["s1"] = FakeNodes.N("s1", "Ore"); var hs = new ResourceNodeSync(h, new SnapshotHost(h), hw);
                var gw = new FakeNodes(); var gsync = new ResourceNodeSync(gs[0], null, gw);
                gsync.LocalPlacement(new NodePlacement { Id = "s1" }); w.Net.Step(300); T.True(hw.Recs["s1"].HasNode);
            });

            // ---------------------------------------------------------------- placed gadgets (owner reports 2 and 4)
            T.Test("gadgets/snapshot plan: create what the host has, remove own placed gadgets it lacks, leave pre-placed ones", () =>
            {
                var host = new[] { FakeGadgets.R(1, 5), FakeGadgets.R(2, 6), FakeGadgets.R(50, 9, 3, pre: true) };
                var local = new[] { FakeGadgets.R(1, 5), FakeGadgets.R(2, 7), FakeGadgets.R(3, 5), FakeGadgets.R(99, 9, 3, pre: true) };
                var (create, remove) = GadgetRules.SnapshotPlan(host, local);
                T.Eq("2", string.Join(",", create.Select(r => r.Id))); T.Eq("2,3", string.Join(",", remove.Select(r => r.Id).OrderBy(x => x)));
            });
            T.Test("gadgets/a removal names the same id, or the nearest pre-placed gadget of that type", () =>
            {
                var local = new[] { FakeGadgets.R(1, 5), FakeGadgets.R(80, 9, 10.3f, pre: true), FakeGadgets.R(81, 9, 30, pre: true), FakeGadgets.R(82, 8, 10, pre: true) };
                T.Eq(1L, GadgetRules.Match(FakeGadgets.R(1, 5), local).Value.Id);
                T.Eq(80L, GadgetRules.Match(FakeGadgets.R(7, 9, 10, pre: true), local).Value.Id, "pre-placed ids differ per save");
                T.True(GadgetRules.Match(FakeGadgets.R(7, 9, 20, pre: true), local) == null, "too far away");
                T.True(GadgetRules.Match(FakeGadgets.R(2, 5), local) == null);
            });
            T.Test("gadgets/two seats: joiner gets the host's placed gadgets, placements and pick-ups flow both ways, foreign ids refused", () =>
            {
                var (w, h, gs) = Rig(1); var snap = new SnapshotHost(h); double now = 0;
                var hw = new FakeGadgets(); hw.G[1] = FakeGadgets.R(1, 5); hw.G[50] = FakeGadgets.R(50, 9, 3, pre: true); var hs = new GadgetSync(h, snap, hw, () => now);
                var w0 = new FakeGadgets(); var s0 = new GadgetSync(gs[0], null, w0, () => now);
                var late = w.AddGuest("Late"); var lw = new FakeGadgets(); lw.G[1] = FakeGadgets.R(1, 6); lw.G[3] = FakeGadgets.R(3, 5); lw.G[99] = FakeGadgets.R(99, 9, 3.2f, pre: true);
                var ls = new GadgetSync(late, null, lw, () => now);
                JoinLate(w, late);
                T.Eq(5, lw.G[1].TypeId, "the host's gadget replaces the guest's own save object with that id"); T.False(lw.G.ContainsKey(3), "the guest's own placed gadget is not in the shared world");
                T.True(lw.G.ContainsKey(99), "pre-placed scene gadgets are left alone");
                var mine = FakeGadgets.R(Block(late) + 4, 11, 7); lw.G[mine.Id] = mine; ls.LocalPlaced(mine); w.Net.Step(300);
                T.True(hw.G.ContainsKey(mine.Id), "a guest's placement reaches the host"); T.True(w0.G.ContainsKey(mine.Id), "and the other guest");
                var foreign = FakeGadgets.R(Block(gs[0]) + 1, 11); ls.LocalPlaced(foreign); w.Net.Step(300); T.False(hw.G.ContainsKey(foreign.Id), "ids outside the sender's block are refused");
                var hostOwn = FakeGadgets.R(12, 13, 1); hw.G[12] = hostOwn; hs.LocalPlaced(hostOwn); w.Net.Step(300); T.True(lw.G.ContainsKey(12)); T.True(w0.G.ContainsKey(12));
                // the guest picks up the host's gadget and a pre-placed one; the host picks up the guest's
                lw.G.Remove(1); ls.LocalRemoved(FakeGadgets.R(1, 5)); w.Net.Step(300); T.False(hw.G.ContainsKey(1)); T.False(w0.G.ContainsKey(1));
                var pre = lw.G[99]; lw.G.Remove(99); ls.LocalRemoved(pre); w.Net.Step(300); T.False(hw.G.ContainsKey(50), "pre-placed matched by type and position");
                hw.G.Remove(mine.Id); hs.LocalRemoved(mine); w.Net.Step(300); T.False(lw.G.ContainsKey(mine.Id)); T.False(w0.G.ContainsKey(mine.Id));
                h.Node.SendToAll(new GadgetPlaced { G = mine }); w.Net.Step(300); T.False(lw.G.ContainsKey(mine.Id), "a late placement of a removed gadget is ignored (tombstone)");
            });
            T.Test("gadgets/nothing is created while the game model is missing; it is retried", () =>
            {
                var (w, h, gs) = Rig(1); double now = 0; var hw = new FakeGadgets(); var hs = new GadgetSync(h, new SnapshotHost(h), hw, () => now);
                var gw = new FakeGadgets { IsReady = false }; var g0 = new GadgetSync(gs[0], null, gw, () => now);
                var r = FakeGadgets.R(20, 3); hw.G[20] = r; hs.LocalPlaced(r); w.Net.Step(300); T.False(gw.G.ContainsKey(20));
                gw.IsReady = true; g0.Pump(); T.True(gw.G.ContainsKey(20));
            });

            // ---------------------------------------------------------------- inventory counts (owner report 2)
            T.Test("inventory/a count applied from the network is the baseline: no echo; local changes publish once", () =>
            {
                var b = new ItemCountBaseline();
                T.Eq(0, b.Diff(new[] { (465, 10), (512, 3) }).Count, "first pass only takes the baseline");
                b.Record(465, 0);                                                                            // host spent 10: canonical 0 applied
                T.Eq(0, b.Diff(new[] { (465, 0), (512, 3) }).Count, "15:30 run: no -10 echo after a network-applied count");
                var c = b.Diff(new[] { (465, 0), (512, 4) }); T.Eq(1, c.Count); T.Eq((512, 1, 4), c[0]); b.Record(512, 4);                                  // the publisher records what it sent
                T.Eq(0, b.Diff(new[] { (465, 0), (512, 4) }).Count, "published once");
                var gone = b.Diff(new[] { (465, 0) }); T.Eq((512, -4, 0), gone.Single(), "an item that vanished counts as 0"); b.Record(512, 0);
                T.Eq(0, b.Diff(new[] { (0, 5) }).Count, "unmapped items (id 0) are never published");
                b.Reset(); T.False(b.Ready);
            });
        }
    }
}

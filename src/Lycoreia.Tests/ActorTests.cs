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

namespace Lycoreia.Tests
{
    sealed class FakeActorWorld : IActorWorld
    {
        public readonly HashSet<long> Present = new HashSet<long>(); public readonly Dictionary<string, Vec3> Players = new Dictionary<string, Vec3>();
        public readonly HashSet<long> Hibernating = new HashSet<long>(), Protected = new HashSet<long>(), HostHolds = new HashSet<long>(); public readonly Dictionary<long, bool> LocalOwned = new Dictionary<long, bool>();
        public readonly List<long> Destroyed = new List<long>(); public readonly Dictionary<int, SpawnClass> Classes = new Dictionary<int, SpawnClass>(); public int WildCount, SameTypeCount; public bool Ranch;
        public CaptureFacts Facts = new CaptureFacts { Live = true, NativeReadable = true, HasVacuumable = true, CanCapture = true, PlayerScene = 1, ActorScene = 1, PlayerToActor = 3, MaxVacuumDistance = 10 };
        public bool ConsumeFails; public float MaxVacuumDistance => 10; public readonly Dictionary<long, Vec3> Poses = new Dictionary<long, Vec3>();
        public bool TypeKnown(int t) => t < 1000; public SpawnClass ClassOf(int t) => Classes.TryGetValue(t, out var c) ? c : SpawnClass.Item; public bool IsRanch(Vec3 p) => Ranch;
        public int CountSameTypeWithin(int t, Vec3 p, float r) => SameTypeCount; public int CountWildSlimesAndAnimals() => WildCount; public bool TryPlayerPosition(string id, out Vec3 p) => Players.TryGetValue(id, out p);
        public bool CreateReplica(ActorEntry e) { Present.Add(e.Id); Poses[e.Id] = e.Position; return true; }
        public void DestroyReplica(long id) { Present.Remove(id); Destroyed.Add(id); }
        public void ApplyPose(ActorEntry e) { Poses[e.Id] = e.Position; }
        public readonly List<ActorState> AppliedStates = new List<ActorState>();
        public void ApplyState(ActorState s) { AppliedStates.Add(s); }
        public void ApplyLocalOwnership(ActorEntry e, bool owned) { LocalOwned[e.Id] = owned; }
        public bool IsHibernating(long id) => Hibernating.Contains(id); public bool IsProtectedRanchSlime(long id) => Protected.Contains(id);
        public CaptureFacts GetCaptureFacts(long id, string player) => Facts;
        public bool RemoveForConsume(long id) { if (ConsumeFails) return false; Present.Remove(id); return true; }
        public bool HostHoldsActor(long id) => HostHolds.Contains(id);
        public float DistanceToActor(string p, long id, float fb) => Players.TryGetValue(p, out var pp) && Poses.TryGetValue(id, out var ap) ? HostSpawnBudget.Dist(pp, ap) : fb;
    }

    sealed class ActorRig
    {
        public World W; public SnapshotHost Snap; public ActorSync HostSync; public FakeActorWorld HostWorld = new FakeActorWorld();
        public List<(Session s, ActorSync sync, FakeActorWorld world, GuestSnapshotController gc)> G = new List<(Session, ActorSync, FakeActorWorld, GuestSnapshotController)>();
        public List<long> SentDestroys = new List<long>();
        public ActorRig(int guests = 2, int seed = 1)
        {
            W = new World(seed); var hs = W.StartHost("Host"); Snap = new SnapshotHost(hs);
            HostSync = new ActorSync(hs, Snap, HostWorld, () => W.Net.Now / 1000.0); HostSync.RegisterSnapshotWriter(0);
            HostWorld.Players[hs.LocalId] = new Vec3(0, 0, 0);
            hs.Node.PacketSeen += (t, b, p, sent) => { if (sent && t == hs.Node.Registry.Get<ActorDestroy>().Id) SentDestroys.Add(0); };
            for (int i = 0; i < guests; i++)
            {
                var g = W.AddGuest("G" + i); var fw = new FakeActorWorld(); var gc = new GuestSnapshotController(g);
                var sync = new ActorSync(g, new SnapshotHost(g), fw, () => W.Net.Now / 1000.0, gc.Buffer);
                gc.Applier = new SyncApplier(sync); G.Add((g, sync, fw, gc));
                W.Join(g); T.True(W.Net.RunUntil(() => gc.Applied, 8000), "guest joined");
                HostWorld.Players[g.LocalId] = new Vec3(0, 0, 0); fw.Players[g.LocalId] = new Vec3(0, 0, 0);
            }
            W.Net.Step(300);
        }
        public void Step(int ms = 300) => W.Net.Step(ms);
        public ActorEntry HostActor(long id, string owner, int type = 1, Vec3? pos = null)
        {
            var e = new ActorEntry { Id = id, Owner = owner, TypeId = type, Scene = 1, Position = pos ?? Vec3.Zero }; HostSync.Table.Add(e); HostWorld.Present.Add(id); HostWorld.Poses[id] = e.Position; return e;
        }
        public void Share(ActorEntry e)
        {
            foreach (var g in G) { var c = new ActorEntry { Id = e.Id, Owner = e.Owner, TypeId = e.TypeId, Scene = 1, Position = e.Position, Epoch = e.Epoch }; g.sync.Table.Add(c); g.world.Present.Add(e.Id); g.world.Poses[e.Id] = e.Position; }
        }
        public string HostId => W.Host.LocalId;
    }

    sealed class SyncApplier : IActorSnapshotApplier
    {
        readonly ActorSync _s; public SyncApplier(ActorSync s) { _s = s; }
        public void Apply(ActorSnapshot snap, Func<bool> cur, Action<ApplyOutcome> done) => done(_s.ApplySnapshot(snap));
    }

    static class ActorTests
    {
        static ArbiterCandidate C(string id, float d) => new ArbiterCandidate(id, d, id == "HOST");
        static string D(string owner, bool conn, bool rel, bool eng, float since, float od, params ArbiterCandidate[] c) => OwnershipArbiter.Decide(owner, conn, rel, eng, since, od, c);

        public static void Register()
        {
            T.Test("actors/state overtaking grant cannot strand owner or defer terminal destroy forever", () =>
            {
                var r = new ActorRig(1); var g = r.G[0];
                var e = r.HostActor(19853, r.HostId, 702); r.Share(e);
                long grant = r.Snap.NextWorldRevision(), state = r.Snap.NextWorldRevision();
                r.W.Host.Node.SendToAll(new ActorState { ActorId = e.Id, Kind = ActorStateKind.Slime, Rev = new Revision { Has = true, Value = state } }); r.Step();
                r.W.Host.Node.SendToAll(new ActorTransfer { ActorId = e.Id, NewOwner = g.s.LocalId, Epoch = 1, Rev = new Revision { Has = true, Value = grant } }); r.Step();
                T.Eq(g.s.LocalId, g.sync.Table.OwnerOf(e.Id));
                T.True(g.world.LocalOwned[e.Id], "grant must survive newer state stream");
                r.W.Host.Node.SendToAll(new ActorDestroy { ActorId = e.Id, OwnerToken = g.s.LocalId, Rev = new Revision { Has = true, Value = r.Snap.NextWorldRevision() } }); r.Step();
                T.False(g.sync.Table.TryGet(e.Id, out _)); T.False(g.world.Present.Contains(e.Id));
                // A distinct expelled id remains live; the consumed id cannot be resurrected.
                r.W.Host.Node.SendToAll(new ActorSpawn { ActorId = 19854, OwnerId = r.HostId, TypeId = 702, SceneGroup = 1, Kind = SpawnKind.VacShot, Rev = new Revision { Has = true, Value = r.Snap.NextWorldRevision() } }); r.Step();
                T.True(g.world.Present.Contains(19854)); T.False(g.world.Present.Contains(19853));
            });
            T.Test("produce/latest remote stage is replayed immediately after materialization", () =>
            {
                var r = new ActorRig(1); var guest = r.G[0];
                var e = r.HostActor(780, guest.s.LocalId); r.Share(e); r.Step();
                guest.s.Node.SendToHost(new ActorState { ActorId = 780, Kind = ActorStateKind.Produce, CycleState = 2, Attached = true, Scale = 1, Progress = 12 }); r.Step();
                int count = r.HostWorld.AppliedStates.Count;
                r.HostSync.ApplyOwnershipAgain(e);
                T.Eq(count + 1, r.HostWorld.AppliedStates.Count);
                T.True(r.HostWorld.AppliedStates.Last().Attached);
                T.Eq(2, r.HostWorld.AppliedStates.Last().CycleState);
            });
            T.Test("owner/fresh transform retracts an earlier unload before proximity arbitration", () =>
            {
                var r = new ActorRig(1); var guest = r.G[0]; var e = r.HostActor(791, guest.s.LocalId); r.Share(e); r.Step();
                e.ReleasedByOwner = true;
                guest.s.Node.SendToHost(new ActorUpdate { ActorId = 791, Epoch = e.Epoch, Position = new Vec3(1, 0, 0), Rotation = Quat.Identity }); r.Step();
                T.False(e.ReleasedByOwner, "a live owner sample is no longer an asleep/free actor");
            });
            T.Test("arbiter/decision table", () =>
            {
                T.True(D("", false, false, false, 99, float.PositiveInfinity) == null, "no candidates: no change");
                T.Eq("B", D("", false, false, false, 99, float.PositiveInfinity, C("A", 30), C("B", 10)), "nearest wins free actor");
                T.Eq("B", D("A", false, false, false, 0, float.PositiveInfinity, C("B", 10)), "owner disconnected");
                T.Eq("A", D("A", true, true, false, 0, 20, C("A", 20), C("B", 50)), "released owner can win again");
                T.Eq("B", D("A", true, true, true, 0, 20, C("B", 50)), "released overrides engaged");
                T.Eq("HOST", D("", false, false, false, 99, float.PositiveInfinity, C("A", 10), C("HOST", 10)), "host wins ties");
                T.Eq("HOST", D("", false, false, false, 99, float.PositiveInfinity, C("HOST", 10), C("A", 10)), "regardless of order");
                T.Eq("A", D("", false, false, false, 99, float.PositiveInfinity, C("A", 9), C("HOST", 10)), "9 m guest beats 10 m host");
                T.True(D("A", true, false, false, 1.49f, 100, C("B", 1)) == null, "hold 1.49 fails"); T.Eq("B", D("A", true, false, false, 1.5f, 100, C("B", 1)), "hold 1.5 passes");
                T.True(D("A", true, false, false, 5, 100, C("B", 80)) == null, "80 vs 100 fails"); T.Eq("B", D("A", true, false, false, 5, 100, C("B", 75)), "75 vs 100 passes");
                T.True(D("A", true, false, false, 5, 100, C("A", 1)) == null, "incumbent never contests itself");
                T.True(D("A", true, false, true, 60, 100, C("B", 1)) == null, "engaged owner keeps it");
                T.Eq("HOST", D("A", true, false, false, 5, 100, C("B", 50), C("HOST", 50)), "host wins a qualifying tie");
            });
            T.Test("arbiter/an actor in its owner's vacuum is never handed over, however far", () =>
            {
                var w = new FakeArbiterWorld(); var a = new OwnershipArbiter(w); w.Owner[8] = "A"; w.Connected.UnionWith(new[] { "A", "B", "HOST" });
                w.Dist["A"] = 50; w.Dist["B"] = 1; w.Held.Add(8); a.RegisterInterest("B", 8, 1, 0f); a.Tick(0.5f); T.Eq(0, w.Grants.Count, "held: kept");
                w.Held.Remove(8); a.RegisterInterest("B", 8, 1, 1f); a.Tick(1.0f); T.Eq(1, w.Grants.Count, "released: the closer player gets it");
            });
            T.Test("arbiter/released sleeping simulator and distant stale candidates cannot regain ownership", () =>
            {
                var w = new FakeArbiterWorld(); var a = new OwnershipArbiter(w); w.Owner[18176] = "A";
                w.Connected.UnionWith(new[] { "A", "B" }); w.Released.Add(18176);
                w.Dist["A"] = 1; w.Dist["B"] = 87;
                a.RegisterInterest("A", 18176, 1, 0); a.RegisterInterest("B", 18176, 87, 0); a.Tick(0.5f);
                T.Eq(0, w.Grants.Count);
                w.Dist["B"] = 10; a.RegisterInterest("B", 18176, 10, 1); a.Tick(1);
                T.Eq(1, w.Grants.Count); T.Eq("B", w.Grants[0].Item2);
            });
            T.Test("arbiter/reservation survives old simulator unload until transaction releases", () =>
            {
                var w = new FakeArbiterWorld(); var a = new OwnershipArbiter(w); w.Owner[18] = "A";
                w.Connected.UnionWith(new[] { "A", "B" }); w.Released.Add(18); w.Reserved.Add(18); w.Dist["B"] = 1;
                a.RegisterInterest("B", 18, 1, 0); a.Tick(0.5f); T.Eq(0, w.Grants.Count);
                w.Reserved.Remove(18); a.Tick(1); T.Eq(1, w.Grants.Count);
            });
            T.Test("arbiter/bookkeeping caps and evaluation through the world", () =>
            {
                var w = new FakeArbiterWorld(); var a = new OwnershipArbiter(w); w.Owner[7] = "A"; w.Connected.UnionWith(new[] { "A", "B", "HOST" });
                w.Dist["A"] = 50; w.Dist["B"] = 10; a.RegisterInterest("B", 7, 10, 0f); a.Tick(0.5f);
                T.Eq(1, w.Grants.Count); T.Eq("B", w.Grants[0].Item2); T.Eq(0, a.TrackedActors, "interest cleared after the grant");
                a.RegisterInterest("A", 7, 1, 1f); w.Owner[7] = "B"; a.Tick(1.0f); T.Eq(1, w.Grants.Count, "hold time 1.5 s since the grant"); a.Tick(2.2f);
                for (long i = 0; i < OwnershipArbiter.MaxInterestActors + 10; i++) a.RegisterInterest("A", 1000 + i, 5, 100f);
                T.True(a.TrackedActors <= OwnershipArbiter.MaxInterestActors, "interest bounded");
                w.Connected.Remove("A"); a.Tick(200f); T.Eq(0, a.TrackedActors, "stale interest purged");
            });
            T.Test("epoch/rules", () =>
            {
                T.True(Ver.Run(typeof(OwnershipEpochPolicy), "VerifyRules"));
                var t = new ActorTable(); t.Add(new ActorEntry { Id = 1, Owner = "A" });
                T.Eq(1u, t.HostGrant(1, "B"), "first grant takes epoch 0 to 1"); T.Eq(1u, t.HostGrant(1, "B"), "same owner, no bump"); T.Eq(2u, t.HostGrant(1, "A"));
                T.Eq(OwnershipEpochDecision.Stale, t.ApplyOwner(1, "C", 1)); T.Eq(OwnershipEpochDecision.Conflict, t.ApplyOwner(1, "C", 2)); T.Eq("A", t.OwnerOf(1));
                T.Eq(OwnershipEpochDecision.Apply, t.ApplyOwner(1, "C", 3)); T.Eq("C", t.OwnerOf(1)); T.Eq(OwnershipEpochDecision.Apply, t.ApplyOwner(1, "D", 0), "epoch-less applies unordered");
                t.Get(1).Epoch = uint.MaxValue; T.Eq(uint.MaxValue, t.HostGrant(1, "Z"), "saturates, never wraps");
                t.Add(new ActorEntry { Id = 2, Owner = "A", Epoch = 5 }); t.Add(new ActorEntry { Id = 2, Owner = "A", Epoch = 5 }); T.Eq(0u, t.Get(2).Epoch, "reused id clears the epoch history");
            });
            T.Test("destroy/suppression table and deferral queue", () =>
            {
                T.True(Ver.Run(typeof(ActorDestroyPolicy), "VerifyRules")); T.True(Ver.Run(typeof(ActorDestroyOrderingPolicy), "VerifyRules"));
                var t = new ActorTable(); for (long i = 0; i < ActorTable.MaxDeferredDestroys + 5; i++) t.Defer(i, "P", 1); T.Eq(ActorTable.MaxDeferredDestroys, t.DeferredCount, "deferred queue bounded");
                var t2 = new ActorTable(); t2.Defer(9, "GUEST", 5); t2.Defer(9, "OTHER", 6); T.True(!t2.TryResolveDeferred(9, "HOST", out _)); T.True(t2.TryResolveDeferred(9, "GUEST", out long rev)); T.Eq(5L, rev, "first authoritative token kept");
            });
            T.Test("capture/policy rules", () =>
            {
                T.True(Ver.Run(typeof(ActorCaptureTransferPolicy), "VerifyRules"));
                T.Eq(0, ActorCaptureTransferPolicy.GetCaptureDenialRetryDelayMilliseconds(0)); T.Eq(250, ActorCaptureTransferPolicy.GetCaptureDenialRetryDelayMilliseconds(1)); T.Eq(500, ActorCaptureTransferPolicy.GetCaptureDenialRetryDelayMilliseconds(2)); T.Eq(1000, ActorCaptureTransferPolicy.GetCaptureDenialRetryDelayMilliseconds(3)); T.Eq(1000, ActorCaptureTransferPolicy.GetCaptureDenialRetryDelayMilliseconds(9));
                var st = new CaptureClientState(); T.Eq(0.25, st.OnDenied(5, 10.0) - 10.0, "after the first denial"); T.Eq(0.5, st.OnDenied(5, 10.0) - 10.0); T.Eq(1.0, st.OnDenied(5, 10.0) - 10.0); T.Eq(1.0, st.OnDenied(5, 10.0) - 10.0, "stays at one second");
                T.True(st.ShouldSuppress(5, 10.5) && !st.ShouldSuppress(5, 11.1), "suppressed until retry time"); T.True(!st.ShouldSuppress(99, 10.5)); T.True(!st.CanRetryAfterDenial(5));
                T.True(!ActorCaptureTransferPolicy.CanCancelPendingCapture(true, false, false, false, 0, 0, false, 0), "cancel refused once a destroy was observed");
                T.True(ActorCaptureTransferPolicy.CanCancelPendingCapture(false, false, false, false, 0, 0, false, 0));
                T.True(ActorCaptureTransferPolicy.ShouldPreserveCaptureAcrossResync(true, false, false, 0, false, 0) && ActorCaptureTransferPolicy.ShouldPreserveCaptureAcrossResync(false, true, false, 0, false, 0), "resync keeps irreversible captures");
                T.True(!ActorCaptureTransferPolicy.ShouldResyncAfterCancel(true, true, true, true), "a reversible cancel never triggers a world resync");
            });
            T.Test("capture/host: forged commit, stale owner, reservation needed, downgrade rules, exact abort", () =>
            {
                double now = 0; var h = new CaptureHost(() => now); var rid = Guid.NewGuid();
                var prep = new ActorTransfer { ActorId = 5, CaptureIntent = true, CaptureRequestId = rid, HasExpected = true, ExpectedPreviousOwner = "HOST", ConsumesActor = true };
                var f = new CaptureFacts { Live = true, NativeReadable = true, HasVacuumable = true, CanCapture = true, PlayerScene = 1, ActorScene = 1, PlayerToActor = 3, MaxVacuumDistance = 10 };
                T.Eq(CaptureOutcome.Rejected, h.Prepare("G1", new ActorTransfer { ActorId = 5, CaptureIntent = true, CaptureRequestId = Guid.NewGuid(), HasExpected = true, ExpectedPreviousOwner = "OLD" }, "HOST", f, out var why)); T.Eq(CaptureRejectionReason.OwnerChanged, why, "stale expected owner");
                T.Eq(CaptureOutcome.Rejected, h.Prepare("G1", new ActorTransfer { ActorId = 5, CaptureIntent = true, CaptureRequestId = Guid.Empty, HasExpected = true, ExpectedPreviousOwner = "HOST" }, "HOST", f, out why));
                T.Eq(CaptureOutcome.Approved, h.Prepare("G1", prep, "HOST", f, out why), why.ToString());
                T.Eq(CaptureOutcome.Replayed, h.Prepare("G1", prep, "HOST", f, out why), "duplicate request replays the cached approval");
                T.Eq(CaptureOutcome.Rejected, h.Prepare("G2", new ActorTransfer { ActorId = 5, CaptureIntent = true, CaptureRequestId = Guid.NewGuid(), HasExpected = true, ExpectedPreviousOwner = "HOST" }, "HOST", f, out why)); T.Eq(CaptureRejectionReason.ReservationBusy, why);
                var commit = new ActorTransfer { ActorId = 5, CaptureCommit = true, CaptureRequestId = rid, ConsumesActor = true };
                T.Eq(CaptureOutcome.CommitRejected, h.Commit("G2", commit, "HOST", true, out _, out _, out _), "forged commit by another player");
                T.Eq(CaptureOutcome.CommitRejected, h.Commit("G1", new ActorTransfer { ActorId = 5, CaptureCommit = true, CaptureRequestId = Guid.NewGuid(), ConsumesActor = true }, "HOST", true, out _, out _, out _), "wrong request id");
                T.Eq(CaptureOutcome.CommitRejected, h.Commit("G1", commit, "SOMEONE", true, out _, out _, out _), "owner changed under the reservation");
                T.Eq(CaptureOutcome.CommitRejected, h.Commit("G1", commit, "HOST", false, out _, out _, out _), "target no longer eligible");
                T.Eq(CaptureOutcome.AbortRefused, h.Abort("G2", new CaptureAbort { ActorId = 5, RequestId = rid }), "abort by the wrong player");
                T.Eq(CaptureOutcome.AbortRefused, h.Abort("G1", new CaptureAbort { ActorId = 5, RequestId = Guid.NewGuid() }), "wrong request id"); T.Eq(CaptureOutcome.AbortRefused, h.Abort("G1", new CaptureAbort { ActorId = 5, RequestId = Guid.Empty }), "empty request id");
                T.Eq(CaptureOutcome.AbortRefused, h.Abort("G1", new CaptureAbort { ActorId = 6, RequestId = rid }), "wrong actor");
                T.Eq(CaptureOutcome.Committed, h.Commit("G1", new ActorTransfer { ActorId = 5, CaptureCommit = true, CaptureRequestId = rid, ConsumesActor = false }, "HOST", true, out var r, out bool consumes, out _), "downgrade consume to plain is allowed"); T.True(!consumes);
                T.Eq(CaptureOutcome.AbortRefused, h.Abort("G1", new CaptureAbort { ActorId = 5, RequestId = rid }), "no abort after commit");
                // plain approval may never be upgraded to consume
                var rid2 = Guid.NewGuid(); h.Prepare("G1", new ActorTransfer { ActorId = 8, CaptureIntent = true, CaptureRequestId = rid2, HasExpected = true, ExpectedPreviousOwner = "HOST", ConsumesActor = false }, "HOST", f, out _);
                T.Eq(CaptureOutcome.CommitRejected, h.Commit("G1", new ActorTransfer { ActorId = 8, CaptureCommit = true, CaptureRequestId = rid2, ConsumesActor = true }, "HOST", true, out _, out _, out _), "never the reverse");
                var granted = new ActorTransfer { ActorId = 90, CaptureIntent = true, CaptureRequestId = Guid.NewGuid(), HasExpected = true, ExpectedPreviousOwner = "HOST", ConsumesActor = true };
                T.True(h.ReserveGranted("G1", granted, "G1"));
                now = 40; T.True(!h.HasReservation(8), "reservation expires after 35 s");
                T.False(h.ReserveGranted("G1", granted, "G1"), "expired immediate grants cannot resurrect cached approval");
                T.Eq(CaptureOutcome.Rejected, h.Prepare("G1", prep, "HOST", f, out _), "cached approval is not a live reservation after abort/commit");
                T.Eq(CaptureOutcome.CommitRejected, h.Commit("G1", new ActorTransfer { ActorId = 8, CaptureCommit = true, CaptureRequestId = rid2 }, "HOST", true, out _, out _, out _), "expired");
            });
            T.Test("two players/guest vacuums a host plort: guest owns and epochs agree", () =>
            {
                var r = new ActorRig(1); var e = r.HostActor(40, r.HostId, pos: new Vec3(1, 0, 0)); r.Share(e); var g = r.G[0];
                r.HostWorld.Players[g.s.LocalId] = new Vec3(2, 0, 0); r.HostWorld.Facts.PlayerToActor = 2;
                var rid = Guid.NewGuid(); ActorTransfer reply = null; g.sync.OnCaptureReply += m => reply = m;
                g.s.Node.SendToHost(new ActorTransfer { ActorId = 40, CaptureIntent = true, CaptureRequestId = rid, HasExpected = true, ExpectedPreviousOwner = r.HostId, NewOwner = g.s.LocalId });
                T.True(r.W.Net.RunUntil(() => reply != null, 2000)); T.True(reply.CaptureApproved && reply.CaptureRequestId == rid); T.Eq(r.HostId, r.HostSync.Table.OwnerOf(40), "owner unchanged on prepare");
                reply = null; g.s.Node.SendToHost(new ActorTransfer { ActorId = 40, CaptureCommit = true, CaptureRequestId = rid, ConsumesActor = false });
                T.True(r.W.Net.RunUntil(() => reply != null, 2000)); r.Step(200);
                T.Eq(g.s.LocalId, r.HostSync.Table.OwnerOf(40)); T.Eq(g.s.LocalId, g.sync.Table.OwnerOf(40)); T.Eq(r.HostSync.Table.Get(40).Epoch, g.sync.Table.Get(40).Epoch); T.Eq(1u, g.sync.Table.Get(40).Epoch);
                T.True(r.HostSync.Table.Get(40).HeldByOwner, "held commit closes the grant-to-first-sample window for competing vacuums");
                T.True(g.world.LocalOwned[40], "guest simulates it now"); T.True(!r.HostWorld.LocalOwned[40]);
            });
            T.Test("capture/held commit acknowledges the existing owner without an epoch change", () =>
            {
                var r = new ActorRig(1); var guest = r.G[0];
                var e = r.HostActor(409, guest.s.LocalId); e.Epoch = 3; r.Share(e); r.Step();
                var request = Guid.NewGuid(); ActorTransfer reply = null; guest.sync.OnCaptureReply += m => reply = m;
                guest.s.Node.SendToHost(new ActorTransfer { ActorId = 409, CaptureIntent = true, CaptureRequestId = request, HasExpected = true, ExpectedPreviousOwner = guest.s.LocalId, NewOwner = guest.s.LocalId });
                T.True(r.W.Net.RunUntil(() => reply != null, 2000)); T.True(reply.CaptureApproved); reply = null;
                guest.s.Node.SendToHost(new ActorTransfer { ActorId = 409, CaptureCommit = true, CaptureRequestId = request });
                T.True(r.W.Net.RunUntil(() => reply != null && reply.CaptureCommit, 2000)); T.True(reply.CaptureApproved);
                T.Eq(3u, guest.sync.Table.Get(409).Epoch); T.True(r.HostSync.Table.Get(409).HeldByOwner);
            });
            T.Test("capture/guest-owned actor still reserves before consume and excludes a competing requester", () =>
            {
                var r = new ActorRig(2); var owner = r.G[0]; var other = r.G[1];
                var e = r.HostActor(410, owner.s.LocalId); r.Share(e); r.Step();
                r.HostWorld.Facts.Captive = true; e.HeldByOwner = true;
                var request = Guid.NewGuid(); ActorTransfer reply = null; owner.sync.OnCaptureReply += m => reply = m;
                owner.s.Node.SendToHost(new ActorTransfer { ActorId = 410, CaptureIntent = true, ConsumesActor = true, CaptureRequestId = request, HasExpected = true, ExpectedPreviousOwner = owner.s.LocalId, NewOwner = owner.s.LocalId });
                T.True(r.W.Net.RunUntil(() => reply != null, 2000)); T.True(reply.CaptureApproved); T.True(r.HostSync.Captures.HasReservation(410));
                ActorTransfer denied = null; other.sync.OnCaptureReply += m => denied = m;
                other.s.Node.SendToHost(new ActorTransfer { ActorId = 410, CaptureIntent = true, ConsumesActor = true, CaptureRequestId = Guid.NewGuid(), HasExpected = true, ExpectedPreviousOwner = owner.s.LocalId, NewOwner = other.s.LocalId });
                T.True(r.W.Net.RunUntil(() => denied != null, 2000)); T.False(denied.CaptureApproved);
                reply = null;
                owner.s.Node.SendToHost(new ActorTransfer { ActorId = 410, CaptureCommit = true, ConsumesActor = true, CaptureRequestId = request });
                T.True(r.W.Net.RunUntil(() => reply != null && reply.CaptureCommit, 2000)); r.Step();
                T.True(reply.CaptureApproved); T.False(r.HostSync.Table.TryGet(410, out _));
                T.False(owner.sync.Table.TryGet(410, out _)); T.False(other.sync.Table.TryGet(410, out _));
            });
            T.Test("two players/consume removes the actor exactly once on both sides", () =>
            {
                var r = new ActorRig(1); var e = r.HostActor(41, r.HostId, type: 2); r.Share(e); var g = r.G[0]; var rid = Guid.NewGuid(); ActorTransfer reply = null; g.sync.OnCaptureReply += m => reply = m;
                g.s.Node.SendToHost(new ActorTransfer { ActorId = 41, CaptureIntent = true, CaptureRequestId = rid, HasExpected = true, ExpectedPreviousOwner = r.HostId, ConsumesActor = true });
                T.True(r.W.Net.RunUntil(() => reply != null, 2000)); reply = null;
                g.s.Node.SendToHost(new ActorTransfer { ActorId = 41, CaptureCommit = true, CaptureRequestId = rid, ConsumesActor = true });
                T.True(r.W.Net.RunUntil(() => reply != null && reply.ConsumesActor, 2000)); r.Step(200);
                T.True(!r.HostSync.Table.TryGet(41, out _) && !g.sync.Table.TryGet(41, out _)); T.Eq(1, g.world.Destroyed.Count(x => x == 41)); T.True(!r.HostWorld.Present.Contains(41));
                // retried commit gets the final result resent, not a second removal
                reply = null; g.s.Node.SendToHost(new ActorTransfer { ActorId = 41, CaptureCommit = true, CaptureRequestId = rid, ConsumesActor = true }); r.Step(300);
                T.Eq(1, g.world.Destroyed.Count(x => x == 41), "consumed exactly once");
                T.True(reply != null && reply.CaptureApproved && reply.CaptureCommit, "commit retry receives its cached acknowledgement after the actor is gone");
                // removal failure restores ownership and rejects
                var e2 = r.HostActor(42, r.HostId); r.Share(e2); r.HostWorld.ConsumeFails = true; var rid2 = Guid.NewGuid(); reply = null;
                g.s.Node.SendToHost(new ActorTransfer { ActorId = 42, CaptureIntent = true, CaptureRequestId = rid2, HasExpected = true, ExpectedPreviousOwner = r.HostId, ConsumesActor = true }); r.Step(300);
                g.s.Node.SendToHost(new ActorTransfer { ActorId = 42, CaptureCommit = true, CaptureRequestId = rid2, ConsumesActor = true }); r.Step(300);
                T.True(r.HostSync.Table.TryGet(42, out var still) && still.Owner == r.HostId, "ownership restored");
            });
            T.Test("two players/sleeping host copy releases to the requester at once", () =>
            {
                var r = new ActorRig(1); var e = r.HostActor(43, r.HostId); r.Share(e); r.HostWorld.Hibernating.Add(43); var g = r.G[0]; ActorTransfer reply = null; g.sync.OnCaptureReply += m => reply = m;
                g.s.Node.SendToHost(new ActorTransfer { ActorId = 43, CaptureIntent = true, CaptureRequestId = Guid.NewGuid(), HasExpected = true, ExpectedPreviousOwner = r.HostId, NewOwner = g.s.LocalId }); r.Step(500);
                T.Eq(g.s.LocalId, r.HostSync.Table.OwnerOf(43)); T.Eq(g.s.LocalId, g.sync.Table.OwnerOf(43));
            });
            T.Test("two players/contest transfers only after margin and hold", () =>
            {
                var r = new ActorRig(2); var e = r.HostActor(44, r.G[0].s.LocalId, pos: new Vec3(0, 0, 0)); r.Share(e);
                r.HostWorld.Players[r.G[0].s.LocalId] = new Vec3(100, 0, 0); r.HostWorld.Players[r.G[1].s.LocalId] = new Vec3(30, 0, 0); r.HostWorld.Players[r.HostId] = new Vec3(500, 0, 0);
                r.HostSync.Arbiter.RegisterInterest(r.G[1].s.LocalId, 44, 30, (float)(r.W.Net.Now / 1000.0)); r.HostSync.Arbiter.NoteGrant(44, (float)(r.W.Net.Now / 1000.0));
                r.Step(1000); r.HostSync.Arbiter.Tick((float)(r.W.Net.Now / 1000.0)); T.Eq(r.G[0].s.LocalId, r.HostSync.Table.OwnerOf(44), "hold time not over");
                r.HostSync.Arbiter.RegisterInterest(r.G[1].s.LocalId, 44, 30, (float)(r.W.Net.Now / 1000.0)); r.Step(1500); r.HostSync.Arbiter.Tick((float)(r.W.Net.Now / 1000.0)); r.Step(300);
                T.Eq(r.G[1].s.LocalId, r.HostSync.Table.OwnerOf(44)); T.Eq(r.G[1].s.LocalId, r.G[0].sync.Table.OwnerOf(44), "everyone agrees");
            });
            T.Test("two players/region hibernates: unload hands the actor to the nearest awake peer", () =>
            {
                var r = new ActorRig(2); var e = r.HostActor(45, r.G[0].s.LocalId); r.Share(e);
                r.HostWorld.Players[r.G[0].s.LocalId] = new Vec3(500, 0, 0); r.HostWorld.Players[r.G[1].s.LocalId] = new Vec3(5, 0, 0);
                r.G[0].s.Node.SendToHost(new ActorUnload { ActorId = 45, SenderId = "spoofed" }); r.Step(300);
                T.True(r.HostSync.Table.Get(45).ReleasedByOwner, "released recorded, sender from the endpoint"); r.HostSync.Arbiter.RegisterInterest(r.G[1].s.LocalId, 45, 5, (float)(r.W.Net.Now / 1000.0));
                r.HostSync.Arbiter.EvaluateNow(45, (float)(r.W.Net.Now / 1000.0)); r.Step(300);
                T.Eq(r.G[1].s.LocalId, r.HostSync.Table.OwnerOf(45));
                var rogue = r.G[1].s.Node; rogue.SendToHost(new ActorUnload { ActorId = 45 }); r.Step(200); // not the owner? it IS the owner now; use another actor
                var e2 = r.HostActor(46, r.HostId); r.Share(e2); r.G[1].s.Node.SendToHost(new ActorUnload { ActorId = 46 }); r.Step(300); T.True(!r.HostSync.Table.Get(46).ReleasedByOwner, "non-owner unload rejected");
            });
            T.Test("transfer/compare and swap, repair reply, legacy rules", () =>
            {
                var r = new ActorRig(2); var g0 = r.G[0]; var g1 = r.G[1]; var e = r.HostActor(50, r.HostId); r.Share(e);
                ActorTransfer repair = null; g0.sync.OnCaptureReply += _ => { }; 
                g0.s.Node.SendToHost(new ActorTransfer { ActorId = 50, NewOwner = g0.s.LocalId, HasExpected = true, ExpectedPreviousOwner = "SOMEONE_ELSE" }); r.Step(400);
                T.Eq(r.HostId, r.HostSync.Table.OwnerOf(50), "wrong expected owner: refused, owner unchanged"); T.Eq(r.HostId, g0.sync.Table.OwnerOf(50)); 
                g0.s.Node.SendToHost(new ActorTransfer { ActorId = 50, NewOwner = g0.s.LocalId, HasExpected = true, ExpectedPreviousOwner = r.HostId }); r.Step(400);
                T.Eq(g0.s.LocalId, r.HostSync.Table.OwnerOf(50)); T.Eq(g0.s.LocalId, g1.sync.Table.OwnerOf(50)); T.Eq(1u, g1.sync.Table.Get(50).Epoch);
                g1.s.Node.SendToHost(new ActorTransfer { ActorId = 50, NewOwner = g1.s.LocalId }); r.Step(400); T.Eq(g0.s.LocalId, r.HostSync.Table.OwnerOf(50), "legacy transfer refused while the owner is connected");
                g1.s.Node.SendToHost(new ActorTransfer { ActorId = 50, NewOwner = g0.s.LocalId, HasExpected = true, ExpectedPreviousOwner = g0.s.LocalId }); r.Step(400); T.Eq(g0.s.LocalId, r.HostSync.Table.OwnerOf(50), "a guest can only transfer to itself");
                var empty = r.HostActor(51, ""); r.Share(empty); g1.s.Node.SendToHost(new ActorTransfer { ActorId = 51, NewOwner = g1.s.LocalId, HasExpected = true, ExpectedPreviousOwner = "" }); r.Step(400);
                T.Eq(g1.s.LocalId, r.HostSync.Table.OwnerOf(51), "empty expected owner means actually unowned");
                var owned = r.HostActor(52, r.HostId); r.Share(owned); g1.s.Node.SendToHost(new ActorTransfer { ActorId = 52, NewOwner = g1.s.LocalId, HasExpected = true, ExpectedPreviousOwner = "" }); r.Step(400);
                T.Eq(r.HostId, r.HostSync.Table.OwnerOf(52), "empty never means anyone");
            });
            T.Test("transfer/interest is a claim only: no reply, no relay, authenticated", () =>
            {
                var r = new ActorRig(2); var e = r.HostActor(53, r.HostId); r.Share(e); int seen = 0; r.G[1].s.Node.PacketSeen += (t, b, p, sent) => { if (!sent && t == r.G[1].s.Node.Registry.Get<ActorTransfer>().Id) seen++; };
                r.G[0].sync.SendInterest(r.G[0].sync.Table.Get(53), 12); r.Step(400); T.Eq(0, seen); T.Eq(1, r.HostSync.Arbiter.TrackedActors);
            });
            T.Test("update/only from the owner, epoch ordered, relayed once", () =>
            {
                var r = new ActorRig(2); var e = r.HostActor(60, r.G[0].s.LocalId); r.Share(e); e.Epoch = 3; foreach (var g in r.G) g.sync.Table.Get(60).Epoch = 3;
                r.G[0].s.Node.SendToHost(new ActorUpdate { ActorId = 60, Position = new Vec3(1, 2, 3), Rotation = Quat.Identity, Epoch = 3 }); r.Step(300);
                T.Eq(1f, r.HostWorld.Poses[60].X); T.Eq(1f, r.G[1].world.Poses[60].X, "relayed to the other guest");
                r.G[1].s.Node.SendToHost(new ActorUpdate { ActorId = 60, Position = new Vec3(99, 0, 0), Rotation = Quat.Identity, Epoch = 3 }); r.Step(300); T.Eq(1f, r.HostWorld.Poses[60].X, "not the owner: dropped");
                r.G[0].s.Node.SendToHost(new ActorUpdate { ActorId = 60, Position = new Vec3(50, 0, 0), Rotation = Quat.Identity, Epoch = 2 }); r.Step(300); T.Eq(1f, r.HostWorld.Poses[60].X, "older epoch: a past owner still talking");
            });
            T.Test("update/held flag: owner only, relayed, cleared by an owner change", () =>
            {
                var r = new ActorRig(2); var e = r.HostActor(62, r.G[0].s.LocalId); r.Share(e);
                r.G[0].s.Node.SendToHost(new ActorUpdate { ActorId = 62, Position = new Vec3(1, 0, 0), Rotation = Quat.Identity, Held = true }); r.Step(300);
                T.True(r.HostSync.Table.Get(62).HeldByOwner, "host learns the owner holds it"); T.True(r.G[1].sync.Table.Get(62).HeldByOwner, "relayed to the other guest");
                r.G[1].s.Node.SendToHost(new ActorUpdate { ActorId = 62, Position = new Vec3(2, 0, 0), Rotation = Quat.Identity, Held = false }); r.Step(300);
                T.True(r.HostSync.Table.Get(62).HeldByOwner, "a non-owner cannot clear it");
                r.HostSync.GrantAndBroadcast(r.HostSync.Table.Get(62), r.HostId, null); r.Step(300);
                T.True(!r.HostSync.Table.Get(62).HeldByOwner && !r.G[1].sync.Table.Get(62).HeldByOwner, "cleared on an owner change everywhere");
            });
            T.Test("capture/an actor nobody simulates goes straight to the asking player, in range only", () =>
            {
                var r = new ActorRig(1); var g = r.G[0]; ActorTransfer rep = null; g.sync.OnCaptureReply += m => rep = m;
                var e = r.HostActor(64, "", pos: new Vec3(3, 0, 0)); r.Share(e); r.HostWorld.Facts.Live = false;          // the host's copy is not loaded
                g.s.Node.SendToHost(new ActorTransfer { ActorId = 64, CaptureIntent = true, CaptureRequestId = Guid.NewGuid(), HasExpected = true, ExpectedPreviousOwner = "", NewOwner = g.s.LocalId });
                T.True(r.W.Net.RunUntil(() => rep != null, 2000)); T.True(rep.CaptureApproved, "approved"); T.Eq(g.s.LocalId, r.HostSync.Table.OwnerOf(64)); T.Eq(g.s.LocalId, g.sync.Table.OwnerOf(64), "the guest owns it before the reply");
                var far = r.HostActor(65, "", pos: new Vec3(500, 0, 0)); r.Share(far); rep = null;
                g.s.Node.SendToHost(new ActorTransfer { ActorId = 65, CaptureIntent = true, CaptureRequestId = Guid.NewGuid(), HasExpected = true, ExpectedPreviousOwner = "", NewOwner = g.s.LocalId });
                T.True(r.W.Net.RunUntil(() => rep != null, 2000)); T.True(!rep.CaptureApproved, "out of range"); T.Eq("", r.HostSync.Table.OwnerOf(65));
                var gone = r.HostActor(66, "PLAYER_GONE", pos: new Vec3(2, 0, 0)); r.Share(gone); rep = null;
                g.s.Node.SendToHost(new ActorTransfer { ActorId = 66, CaptureIntent = true, CaptureRequestId = Guid.NewGuid(), HasExpected = true, ExpectedPreviousOwner = "PLAYER_GONE", NewOwner = g.s.LocalId });
                T.True(r.W.Net.RunUntil(() => rep != null, 2000)); T.True(rep.CaptureApproved, "owner disconnected"); T.Eq(g.s.LocalId, r.HostSync.Table.OwnerOf(66));
                var crop = r.HostActor(67, r.HostId, pos: new Vec3(2, 0, 0)); crop.Attached = true; r.Share(crop); rep = null; r.HostWorld.Facts.Live = true;
                g.s.Node.SendToHost(new ActorTransfer { ActorId = 67, CaptureIntent = true, ConsumesActor = true, CaptureRequestId = Guid.NewGuid(), HasExpected = true, ExpectedPreviousOwner = r.HostId, NewOwner = g.s.LocalId });
                T.True(r.W.Net.RunUntil(() => rep != null, 2000)); T.True(rep.CaptureApproved); T.Eq(g.s.LocalId, r.HostSync.Table.OwnerOf(67), "produce on its plant: handed over so the guest's own game detaches it");
                T.True(r.HostSync.Captures.HasReservation(67), "an immediate grant retains its capture reservation");
            });
            T.Test("capture/an actor held in another player's vacuum is never approved", () =>
            {
                var r = new ActorRig(2); var e = r.HostActor(63, r.G[0].s.LocalId); r.Share(e); var g = r.G[1];
                r.G[0].s.Node.SendToHost(new ActorUpdate { ActorId = 63, Rotation = Quat.Identity, Held = true }); r.Step(300);
                ActorTransfer rep = null; g.sync.OnCaptureReply += m => rep = m;
                g.s.Node.SendToHost(new ActorTransfer { ActorId = 63, CaptureIntent = true, CaptureRequestId = Guid.NewGuid(), HasExpected = true, ExpectedPreviousOwner = r.G[0].s.LocalId, NewOwner = g.s.LocalId });
                T.True(r.W.Net.RunUntil(() => rep != null, 2000)); T.True(!rep.CaptureApproved, "denied while held");
                r.G[0].s.Node.SendToHost(new ActorUpdate { ActorId = 63, Rotation = Quat.Identity, Held = false }); r.Step(300); rep = null;
                g.s.Node.SendToHost(new ActorTransfer { ActorId = 63, CaptureIntent = true, CaptureRequestId = Guid.NewGuid(), HasExpected = true, ExpectedPreviousOwner = r.G[0].s.LocalId, NewOwner = g.s.LocalId });
                T.True(r.W.Net.RunUntil(() => rep != null, 2000)); T.True(rep.CaptureApproved, "approved once released");
            });
            T.Test("update/approved consume pull is accepted only under the pull rules", () =>
            {
                var r = new ActorRig(1); var e = r.HostActor(61, r.HostId, pos: new Vec3(0, 0, 0)); r.Share(e); var g = r.G[0]; r.HostWorld.Players[g.s.LocalId] = new Vec3(3, 0, 0);
                g.s.Node.SendToHost(new ActorUpdate { ActorId = 61, Position = new Vec3(2, 0, 0), Rotation = Quat.Identity }); r.Step(300); T.Eq(0f, r.HostWorld.Poses[61].X, "no reservation: rejected");
                var rid = Guid.NewGuid(); ActorTransfer rep = null; g.sync.OnCaptureReply += m => rep = m;
                g.s.Node.SendToHost(new ActorTransfer { ActorId = 61, CaptureIntent = true, CaptureRequestId = rid, HasExpected = true, ExpectedPreviousOwner = r.HostId, ConsumesActor = true }); T.True(r.W.Net.RunUntil(() => rep != null, 2000));
                g.sync.SendCapturePull(61, new Vec3(2, 0, 0), Quat.Identity, Vec3.Zero); r.Step(300); T.Eq(2f, r.HostWorld.Poses[61].X, "approved pull accepted");
                g.s.Node.SendToHost(new ActorUpdate { ActorId = 61, Position = new Vec3(float.NaN, 0, 0), Rotation = Quat.Identity }); r.Step(300); T.Eq(2f, r.HostWorld.Poses[61].X, "non finite rejected");
                g.s.Node.SendToHost(new ActorUpdate { ActorId = 61, Position = new Vec3(1, 0, 0), Rotation = new Quat(0, 0, 0, 0.1f), FullRotation = true }); r.Step(300); T.Eq(2f, r.HostWorld.Poses[61].X, "degenerate quaternion rejected");
                g.sync.SendCapturePull(61, new Vec3(300, 0, 0), Quat.Identity, Vec3.Zero); r.Step(300);
                T.Eq(2f, r.HostWorld.Poses[61].X, "reservation cannot teleport actor outside the requester's vacuum range");
                r.HostWorld.Players[g.s.LocalId] = new Vec3(500, 0, 0);
                g.s.Node.SendToHost(new ActorUpdate { ActorId = 61, Position = new Vec3(1, 0, 0), Rotation = Quat.Identity }); r.Step(300); T.Eq(2f, r.HostWorld.Poses[61].X, "out of vacuum range");
            });
            T.Test("destroy/host accepts only from the owner, tokens bind, unknown ids ignored, gadgets by anyone", () =>
            {
                var r = new ActorRig(2); var e = r.HostActor(70, r.G[0].s.LocalId); r.Share(e); var gad = r.HostActor(71, r.HostId); gad.IsGadget = true; r.Share(gad); foreach (var g in r.G) g.sync.Table.Get(71).IsGadget = true;
                r.G[1].s.Node.SendToHost(new ActorDestroy { ActorId = 70, OwnerToken = r.G[1].s.LocalId }); r.Step(300); T.True(r.HostSync.Table.TryGet(70, out _), "not the owner");
                r.G[0].s.Node.SendToHost(new ActorDestroy { ActorId = 70, OwnerToken = r.G[1].s.LocalId }); r.Step(300); T.True(r.HostSync.Table.TryGet(70, out _), "claimed owner must equal the sender");
                r.G[0].s.Node.SendToHost(new ActorDestroy { ActorId = 9999 }); r.Step(300);
                r.G[1].s.Node.SendToHost(new ActorDestroy { ActorId = 71 }); r.Step(400); T.True(!r.HostSync.Table.TryGet(71, out _) && !r.G[0].sync.Table.TryGet(71, out _), "any player may pick up a gadget");
                r.G[0].s.Node.SendToHost(new ActorDestroy { ActorId = 70, OwnerToken = r.G[0].s.LocalId }); r.Step(400); T.True(!r.HostSync.Table.TryGet(70, out _) && !r.G[1].sync.Table.TryGet(70, out _), "owner destroy relayed");
            });
            T.Test("destroy/guest defers when the owner token is ahead of the known owner", () =>
            {
                var r = new ActorRig(2); var e = r.HostActor(72, r.HostId); r.Share(e); var g1 = r.G[1];
                var d = r.HostSync.Table.Get(72); r.Snap.NextWorldRevision();
                // destroy token names guest 0 as owner but g1 still believes the host owns it
                r.W.Host.Node.SendToAll(new ActorDestroy { ActorId = 72, OwnerToken = r.G[0].s.LocalId, Rev = new Revision { Has = true, Value = r.Snap.NextWorldRevision() } }); r.Step(300);
                T.True(g1.sync.Table.TryGet(72, out _), "deferred, not applied"); T.Eq(1, g1.sync.Table.DeferredCount);
                r.W.Host.Node.SendToAll(new ActorTransfer { ActorId = 72, NewOwner = r.G[0].s.LocalId, Epoch = 1, Rev = new Revision { Has = true, Value = r.Snap.NextWorldRevision() } }); r.Step(300);
                T.True(!g1.sync.Table.TryGet(72, out _), "applied when the ownership message arrived"); T.Eq(0, g1.sync.Table.DeferredCount);
            });
            T.Test("destroy/resync guard refuses protected ranch slime destroys for 8 s", () =>
            {
                var r = new ActorRig(1); var g = r.G[0]; var e = r.HostActor(73, g.s.LocalId, type: 3); r.Share(e); r.HostWorld.Protected.Add(73);
                r.Snap.ArmDestroyGuard(g.s.LocalId); g.s.Node.SendToHost(new ActorDestroy { ActorId = 73, OwnerToken = g.s.LocalId }); r.Step(300);
                T.True(r.HostSync.Table.TryGet(73, out _), "refused during the guard"); r.Step(8000);
                g.s.Node.SendToHost(new ActorDestroy { ActorId = 73, OwnerToken = g.s.LocalId }); r.Step(300); T.True(!r.HostSync.Table.TryGet(73, out _));
            });
            T.Test("spawn/host rules: id block, relay, gadgets, refusal destroys at the sender", () =>
            {
                var r = new ActorRig(2); var g = r.G[0]; long start = ActorSync.ActorStart(g.s.LocalId);
                g.s.Node.SendToHost(new ActorSpawn { ActorId = start - 5, TypeId = 1, OwnerId = g.s.LocalId, Rotation = Quat.Identity }); r.Step(300); T.True(!r.HostSync.Table.TryGet(start - 5, out _), "below the sender's id block: its own save contents");
                g.s.Node.SendToHost(new ActorSpawn { ActorId = start + 1, TypeId = 1, OwnerId = g.s.LocalId, Rotation = Quat.Identity, Position = new Vec3(1, 1, 1) }); r.Step(300);
                T.Eq(g.s.LocalId, r.HostSync.Table.OwnerOf(start + 1)); T.True(r.G[1].sync.Table.TryGet(start + 1, out var rep) && rep.Owner == g.s.LocalId, "relayed to the other guest with the sender as owner");
                g.s.Node.SendToHost(new ActorSpawn { ActorId = start + 2, TypeId = 5000, OwnerId = g.s.LocalId, Rotation = Quat.Identity }); r.Step(300); T.True(!r.HostSync.Table.TryGet(start + 2, out _), "unknown type");
                g.s.Node.SendToHost(new ActorSpawn { ActorId = start + 3, TypeId = 1, Kind = SpawnKind.Gadget, OwnerId = g.s.LocalId, Rotation = Quat.Identity }); r.Step(300); T.True(!r.HostSync.Table.TryGet(start + 3, out _), "orphan gadget not adopted");
            });
            T.Test("spawn/host refuses a stale spawn for a destroyed id and the sender drops its ghost", () =>
            {
                var r = new ActorRig(1); var g = r.G[0]; long id = ActorSync.ActorStart(g.s.LocalId) + 1; var e = new ActorEntry { Id = id, TypeId = 1, Position = new Vec3(1, 1, 1), Scene = 1 };
                g.world.Present.Add(id); g.sync.PublishSpawn(e); r.Step(300); T.True(r.HostSync.Table.TryGet(id, out _), "host admitted the spawn");
                g.s.Node.SendToHost(new ActorDestroy { ActorId = id, OwnerToken = g.s.LocalId }); r.Step(300);   // raw destroy: the guest keeps its own entry, the host records the tombstone
                T.True(!r.HostSync.Table.TryGet(id, out _) && r.HostSync.Table.IsTombstoned(id), "host tombstoned the id"); g.world.Destroyed.Clear(); g.world.Present.Add(id);
                g.s.Node.SendToHost(new ActorSpawn { ActorId = id, TypeId = 1, OwnerId = g.s.LocalId, Rotation = Quat.Identity, Position = new Vec3(1, 1, 1) }); r.Step(300);
                T.True(!r.HostSync.Table.TryGet(id, out _), "not resurrected"); T.True(r.HostSync.Table.IsTombstoned(id), "tombstone kept"); T.True(g.world.Destroyed.Contains(id), "the sender dropped its ghost");
            });
            T.Test("spawn/100 hens in a burst are cut and the sender destroys the refused ones", () =>
            {
                var r = new ActorRig(1); var g = r.G[0]; long start = ActorSync.ActorStart(g.s.LocalId); r.HostWorld.Classes[7] = SpawnClass.Animal; r.HostWorld.Players[g.s.LocalId] = new Vec3(500, 0, 0);
                var refused = new List<long>(); g.sync.OnGuestSpawnRefused += refused.Add;
                for (int i = 0; i < 100; i++) { var e = new ActorEntry { Id = start + 10 + i, TypeId = 7, Position = new Vec3(0, 0, 0), Scene = 1 }; g.world.Present.Add(e.Id); g.sync.PublishSpawn(e); }
                r.Step(1500);
                int accepted = r.HostSync.Table.All.Count(a => a.Id >= start + 10); T.True(accepted <= 80 + 15 && accepted >= 70, "accepted " + accepted); T.True(refused.Count >= 100 - accepted - 1, "refused ones are destroyed at the sender: " + refused.Count);
                T.True(r.HostSync.Budget.Refusals > 0);
            });
            T.Test("spawn/budget exemptions and caps", () =>
            {
                var w = new FakeActorWorld(); double now = 0; var b = new HostSpawnBudget(w, () => now); b.LogRefusal = _ => { }; w.Classes[1] = SpawnClass.Slime; w.Classes[2] = SpawnClass.Animal; w.Players["G"] = new Vec3(100, 0, 0);
                for (int i = 0; i < 80; i++) T.Eq(SpawnDecision.Allow, b.Check("G", 0, Vec3.Zero, false, false), "burst " + i); T.Eq(SpawnDecision.RefusedRate, b.Check("G", 0, Vec3.Zero, false, false)); now = 1; for (int i = 0; i < 10; i++) T.Eq(SpawnDecision.Allow, b.Check("G", 0, Vec3.Zero, false, false)); T.Eq(SpawnDecision.RefusedRate, b.Check("G", 0, Vec3.Zero, false, false), "refill 10 per second");
                now = 1000; w.SameTypeCount = 30; T.Eq(SpawnDecision.RefusedPile, b.Check("G", 1, Vec3.Zero, false, false), "slime pile cap 30"); w.SameTypeCount = 29; T.Eq(SpawnDecision.Allow, b.Check("G", 1, Vec3.Zero, false, false));
                w.SameTypeCount = 20; T.Eq(SpawnDecision.RefusedPile, b.Check("G", 2, Vec3.Zero, false, false), "animal pile cap 20"); w.SameTypeCount = 120; T.Eq(SpawnDecision.RefusedPile, b.Check("G", 0, Vec3.Zero, false, false), "item pile cap 120");
                w.Players["G"] = new Vec3(2, 0, 0); T.Eq(SpawnDecision.Allow, b.Check("G", 0, Vec3.Zero, false, false), "within 6 m of the avatar: vac shot at the nozzle"); w.Players["G"] = new Vec3(100, 0, 0);
                w.SameTypeCount = 999; T.Eq(SpawnDecision.Allow, b.Check("G", 1, Vec3.Zero, true, true), "ranch spawns skip pile and wild caps"); w.Ranch = true; T.Eq(SpawnDecision.Allow, b.Check("G", 1, Vec3.Zero, false, false)); w.Ranch = false; w.SameTypeCount = 0;
                now = 2000; w.WildCount = 900; T.Eq(SpawnDecision.RefusedWild, b.Check("G", 1, Vec3.Zero, false, false), "wild cap 900"); T.Eq(SpawnDecision.Allow, b.Check("G", 1, Vec3.Zero, true, false), "vac shots exempt"); T.Eq(SpawnDecision.Allow, b.Check("G", 0, Vec3.Zero, false, false), "items are not wild"); 
                now = 3000; w.WildCount = 0; T.Eq(SpawnDecision.Allow, b.Check("G", 1, Vec3.Zero, false, false), "recount after 2 s");
                var bad = new HostSpawnBudget(new ThrowingWorld(), () => 0); T.Eq(SpawnDecision.Allow, bad.Check("G", 1, Vec3.Zero, false, false), "if the check throws, allow");
            });
            T.Test("spawn/leader backoff animal guard chicken cap", () =>
            {
                T.Eq("HOST", SpawnLeader.Leader("HOST", true, new[] { "A", "B" })); T.Eq("A", SpawnLeader.Leader("HOST", false, new[] { "B", "A" })); T.True(SpawnLeader.Leader("HOST", false, new string[0]) == null);
                var b = new SpawnBackoff(); b.NoteLocalSpawn(1, new Vec3(10, 0, 0), 100, true); T.True(b.OnHostRefusal(1, 102)); T.True(b.Blocks(new Vec3(70, 0, 0), 110), "within 80 m"); T.True(!b.Blocks(new Vec3(100, 0, 0), 110)); T.True(!b.Blocks(new Vec3(10, 0, 0), 165), "60 s later");
                b.NoteLocalSpawn(2, new Vec3(15, 0, 0), 200, true); T.True(!b.OnHostRefusal(2, 201) || b.Zones <= 2); b.NoteLocalSpawn(3, new Vec3(500, 0, 0), 300, true); T.True(!b.OnHostRefusal(3, 306), "only fresh spawns (5 s) count");
                b.NoteLocalSpawn(4, new Vec3(900, 0, 0), 400, false); T.True(!b.OnHostRefusal(4, 401), "only wild spawns"); for (int i = 0; i < 400; i++) b.NoteLocalSpawn(1000 + i, Vec3.Zero, 500, true); T.True(b.Recent <= SpawnBackoff.MaxRecent);
                var g = new AnimalSpawnerGuard(); T.Eq(10, g.Allowed(1, SpawnClass.Animal, 10, 0, 0, false, true)); T.Eq(0, g.Allowed(1, SpawnClass.Animal, 3, 0, 1, false, true), "10 per 60 s"); T.Eq(3, g.Allowed(1, SpawnClass.Animal, 3, 0, 61, false, true));
                T.Eq(0, new AnimalSpawnerGuard().Allowed(2, SpawnClass.Animal, 5, 14, 0, false, true), "cap 14 nearby: nothing this round"); T.Eq(5, new AnimalSpawnerGuard().Allowed(2, SpawnClass.Animal, 5, 14, 0, true, true), "never on the ranch"); T.Eq(5, new AnimalSpawnerGuard().Allowed(2, SpawnClass.Animal, 5, 14, 0, false, false), "never in single player");
                T.Eq(14, new AnimalSpawnerGuard().Allowed(3, SpawnClass.Slime, 20, 0, 0, false, true), "14 slimes per spawner");
                var c = new ChickenCap(); T.True(!c.Allow(Vec3.Zero, 20, false, 0), "wild cap applies even at avatar"); T.True(c.Allow(Vec3.Zero, 19, false, 0), "wild flock can grow below cap"); T.True(c.Allow(Vec3.Zero, 20, true, 0), "ranch flock below burst cap");
                T.True(ChickenCap.IsChickenTypeName("Hen") && ChickenCap.IsChickenTypeName("Roostro") && ChickenCap.IsChickenTypeName("Chickadoo") && ChickenCap.IsChickenTypeName("ChickenHen"));
                T.False(ChickenCap.IsChickenTypeName("SteamFox"), "non-chicken actors are not counted by the chicken cap");
                T.True(c.Allow(Vec3.Zero, ChickenCap.RanchWithin30m - 1, true, 0), "normal ranch flock remains allowed"); T.True(!c.Allow(Vec3.Zero, ChickenCap.RanchWithin30m, true, 0), "ranch burst capped");
                var cellCap = new ChickenCap(); int ok = 0; for (int i = 0; i < 30; i++) if (cellCap.Allow(new Vec3(5, 0, 5), 0, false, 1)) ok++; T.Eq(12, ok, "12 per 40 m cell per 60 s"); T.True(cellCap.Allow(new Vec3(5, 0, 5), 0, false, 62));
                T.True(GuestSlimeSpawnerGate.CanSpawn(10, 20) && !GuestSlimeSpawnerGate.CanSpawn(17, 20) && GuestSlimeSpawnerGate.CanSpawn(16, 20));
            });
            T.Test("ranch/protection fall rescue and cleanup", () =>
            {
                T.True(RanchProtection.IsProtected(true, false, false) && !RanchProtection.IsProtected(true, true, false) && !RanchProtection.IsProtected(true, false, true) && !RanchProtection.IsProtected(false, false, false));
                T.Eq(RanchProtection.FallAction.Rescue, RanchProtection.OnFallTrigger(true, true, true)); T.Eq(RanchProtection.FallAction.KeepReplica, RanchProtection.OnFallTrigger(true, false, true)); T.Eq(RanchProtection.FallAction.AllowDestroy, RanchProtection.OnFallTrigger(true, true, false)); T.Eq(RanchProtection.FallAction.AllowDestroy, RanchProtection.OnFallTrigger(false, true, true));
                T.True(RanchProtection.IsLethalSeaTrigger("seaKillCube") && !RanchProtection.IsLethalSeaTrigger("ranchKillCube"));
                T.Eq(RanchProtection.FallAction.AllowDestroy, RanchProtection.OnFallTrigger(true, true, true, lethalSea: true), "the owner lets the lethal sea kill a ranch-protected slime");
                T.Eq(RanchProtection.FallAction.KeepReplica, RanchProtection.OnFallTrigger(true, false, false, lethalSea: true), "a remote copy waits for its owner to die it");
                var items = new List<CleanupItem>(); for (int i = 0; i < 50; i++) items.Add(new CleanupItem(i + 1, 5, 1, new Vec3(1, 0, 1), SpawnClass.Slime));
                for (int i = 0; i < 130; i++) items.Add(new CleanupItem(1000 + i, 6, 1, new Vec3(1, 0, 1), SpawnClass.Item)); for (int i = 0; i < 50; i++) items.Add(new CleanupItem(5000 + i, 5, 1, new Vec3(1, 0, 1), SpawnClass.Slime, gadget: true));
                for (int i = 0; i < 50; i++) items.Add(new CleanupItem(6000 + i, 5, 1, new Vec3(1, 0, 1), SpawnClass.Slime, ranch: true)); for (int i = 0; i < 50; i++) items.Add(new CleanupItem(7000 + i, 5, 9, new Vec3(1, 0, 1), SpawnClass.Slime, conservatory: true));
                var plan = Cleanup.Plan(items, ranchKnown: true); T.Eq(20 + 10 + 20, plan.Remove.Count, "slimes 30 kept of 50, items 120 kept of 130, conservatory 30 kept of 50"); T.True(plan.Remove.All(id => id < 5000 || id >= 7000), "gadgets and ranch untouched");
                var unknown = Cleanup.Plan(items, ranchKnown: false); T.True(unknown.Remove.All(id => id < 7000), "unknown ranch: conservatory groups skipped"); T.Eq(plan.Remove.Count - 20, unknown.Remove.Count);
                T.Eq(plan.Remove.Count, plan.Remove.Count, "preview count equals removal count"); var list = plan.Remove.ToList(); int frames = 0, removed = 0; while (list.Count > 0) { removed += Cleanup.Slice(list).Count(); frames++; } T.Eq(plan.Remove.Count, removed); T.Eq(2, frames, "40 per frame");
            });
            T.Test("snapshot/actors join with ownership and epochs, internal rebuild sends nothing", () =>
            {
                var r = new ActorRig(0); var e = r.HostActor(80, r.HostId); e.Epoch = 4; var e2 = r.HostActor(81, ""); e2.Owner = "";
                var g = r.W.AddGuest("Late"); var fw = new FakeActorWorld(); var gc = new GuestSnapshotController(g); var sync = new ActorSync(g, new SnapshotHost(g), fw, () => 0, gc.Buffer); gc.Applier = new SyncApplier(sync);
                fw.Present.Add(999); sync.Table.Add(new ActorEntry { Id = 999, Owner = "x" }); int destroysSent = 0; g.Node.PacketSeen += (t, b, p, sent) => { if (sent && t == g.Node.Registry.Get<ActorDestroy>().Id) destroysSent++; };
                r.W.Join(g); T.True(r.W.Net.RunUntil(() => gc.Applied, 8000)); r.Step(300);
                T.Eq(2, sync.Table.Count); T.Eq(4u, sync.Table.Get(80).Epoch); T.Eq(r.HostId, sync.Table.OwnerOf(80)); T.Eq("", sync.Table.OwnerOf(81), "ownerless saved actor still reaches the guest"); T.True(!sync.Table.TryGet(999, out _));
                T.Eq(0, destroysSent, "snapshot rebuild destroys are internal"); T.True(fw.Destroyed.Contains(999));
            });
            T.Test("snapshot/live changes during join apply once after the snapshot", () =>
            {
                var r = new ActorRig(0); for (int i = 0; i < 5; i++) r.HostActor(90 + i, r.HostId);
                var g = r.W.AddGuest("Late"); var fw = new FakeActorWorld(); var gc = new GuestSnapshotController(g); var sync = new ActorSync(g, new SnapshotHost(g), fw, () => 0, gc.Buffer); gc.Applier = new SyncApplier(sync);
                r.W.Join(g); r.Step(30);
                // a live destroy that happens right while the guest is still joining
                r.HostSync.PublishDestroy(92); T.True(r.W.Net.RunUntil(() => gc.Applied, 8000)); r.Step(400);
                T.True(!sync.Table.TryGet(92, out _), "live destroy not lost"); T.Eq(4, sync.Table.Count); T.Eq(1, fw.Destroyed.Count(x => x == 92) + (fw.Present.Contains(92) ? 0 : 0) , "destroyed at most once");
            });
            T.Test("destroy/no network destroys for internal removals (zone unload, scene change)", () =>
            {
                var r = new ActorRig(1); var g = r.G[0]; var e = r.HostActor(100, g.s.LocalId); r.Share(e); int sent = 0; g.s.Node.PacketSeen += (t, b, p, s) => { if (s && t == g.s.Node.Registry.Get<ActorDestroy>().Id) sent++; };
                g.world.DestroyReplica(100); g.sync.Table.Remove(100); r.Step(300); T.Eq(0, sent); T.True(g.sync.PublishDestroy(100) == false, "unknown now");
                var e2 = r.HostActor(101, r.HostId); r.Share(e2); T.True(!g.sync.PublishDestroy(101), "a non-owner never destroys a networked actor");
            });
            T.Test("transform sync keep-alive cadence", () =>
            {
                T.True(ActorSync.ShouldSendTransform(true, 3, 5)); T.True(!ActorSync.ShouldSendTransform(false, 3, 5)); T.True(ActorSync.ShouldSendTransform(false, 5, 5));
                int sends = 0; for (long tick = 0; tick < 100; tick++) if (ActorSync.ShouldSendTransform(false, tick, 4)) sends++; T.Eq(10, sends, "every 10th tick"); 
                bool a = false, b = false; for (long tick = 0; tick < 10; tick++) { if (ActorSync.ShouldSendTransform(false, tick, 1)) a = tick == 9; if (ActorSync.ShouldSendTransform(false, tick, 2)) b = tick == 8; } T.True(a && b, "staggered by id modulo 10");
            });
        }
    }

    sealed class FakeArbiterWorld : IArbiterWorld
    {
        public Dictionary<long, string> Owner = new Dictionary<long, string>(); public HashSet<string> Connected = new HashSet<string>(); public Dictionary<string, float> Dist = new Dictionary<string, float>();
        public List<(long, string, string)> Grants = new List<(long, string, string)>();
        public bool ActorExists(long id) => true; public string OwnerOf(long id) => Owner.GetValueOrDefault(id, ""); public bool IsConnected(string p) => Connected.Contains(p);
        public HashSet<long> Released = new HashSet<long>(), Reserved = new HashSet<long>();
        public bool WasReleasedByOwner(long id, string o) => Released.Contains(id); public bool HasCaptureReservation(long id) => Reserved.Contains(id); public bool HostHoldsActor(long id) => false; public HashSet<long> Held = new HashSet<long>(); public bool OwnerHoldsActor(long id) => Held.Contains(id);
        public HashSet<long> Pinned = new HashSet<long>(); public bool IsPinned(long id) => Pinned.Contains(id);
        public float DistanceToActor(string p, long id, float fb) => Dist.TryGetValue(p, out var d) ? d : fb; public string HostId => "HOST";
        public void GrantOwnership(long id, string n, string p) { Grants.Add((id, n, p)); Owner[id] = n; }
    }
    sealed class ThrowingWorld : ISpawnWorld
    {
        public SpawnClass ClassOf(int t) => throw new InvalidOperationException(); public bool IsRanch(Vec3 p) => throw new InvalidOperationException(); public int CountSameTypeWithin(int t, Vec3 p, float r) => throw new InvalidOperationException();
        public int CountWildSlimesAndAnimals() => throw new InvalidOperationException(); public bool TryPlayerPosition(string id, out Vec3 p) => throw new InvalidOperationException();
    }
}

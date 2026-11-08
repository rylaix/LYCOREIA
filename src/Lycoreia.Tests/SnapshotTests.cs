// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.Snapshot;

namespace Lycoreia.Tests
{
    sealed class FakeApplier : IActorSnapshotApplier
    {
        public readonly List<ActorSnapshot> Applied = new List<ActorSnapshot>(); public Queue<ApplyOutcome> Script = new Queue<ApplyOutcome>();
        public Action<ActorSnapshot, Action<ApplyOutcome>> Hook;
        public void Apply(ActorSnapshot s, Func<bool> current, Action<ApplyOutcome> done)
        {
            Applied.Add(s); var o = Script.Count > 0 ? Script.Dequeue() : ApplyOutcome.Success; done(o);
        }
    }

    static class SnapshotTests
    {
        static List<ActorRecord> Actors(int n) => Enumerable.Range(0, n).Select(i => new ActorRecord { ActorId = 100 + i, OwnerId = "PLAYER_HOST", Position = new Vec3(i, 0, 0), Rotation = Quat.Identity, TypeId = 3 }).ToList();

        static (World w, SnapshotHost host, Session g, GuestSnapshotController gc, FakeApplier ap, List<string> order) Rig(int actors = 450, double loss = 0, int seed = 1)
        {
            var w = new World(seed); w.Net.Loss = loss; if (loss > 0) w.Net.MaxDelay = 30;
            var hostS = w.StartHost(); var host = new SnapshotHost(hostS); var g = w.AddGuest();
            var list = Actors(actors); var order = new List<string>();
            foreach (SnapStep s in Enum.GetValues(typeof(SnapStep)))
            {
                if (s == SnapStep.Approve) continue; var step = s;
                host.Register(s, ctx => { if (step == SnapStep.Actors) ctx.SendActors(list, 100, 12.5); else ctx.Send(new ConversationPlayed { Name = step.ToString() }); });
            }
            var gc = new GuestSnapshotController(g); var ap = new FakeApplier(); gc.Applier = ap;
            g.Node.On<ConversationPlayed>(Side.Guest, (c, m) => order.Add(m.Name));
            return (w, host, g, gc, ap, order);
        }

        public static void Register()
        {
            T.Test("snapshot/34 steps in the specified order (gadgets before their links)", () =>
            {
                var names = Enum.GetNames(typeof(SnapStep)); T.Eq(34, names.Length);
                T.Eq("Approve", names[0]); T.Eq("Actors", names[15]); T.Eq("SlimeBehavior", names[16]); T.Eq("Gadgets", names[17]); T.Eq("GadgetLinks", names[18]); T.Eq("StabilizingSpawner", names[33]); T.Eq("Components", names[27]);
            });
            T.Test("snapshot/join runs all steps in order and applies the actor snapshot once", () =>
            {
                var r = Rig(); r.w.Join(r.g);
                T.True(r.w.Net.RunUntil(() => r.gc.Applied && r.order.Count == 32, 8000), "order count " + r.order.Count);
                T.Eq(1, r.ap.Applied.Count); T.Eq(450, r.ap.Applied[0].Actors.Count); T.Eq(100L, r.ap.Applied[0].StartingActorId); T.Eq(12.5, r.ap.Applied[0].WorldTime);
                T.Eq("LandPlots", r.order[0]); T.Eq("StabilizingSpawner", r.order.Last());
                T.True(!r.gc.Buffer.IsBuffering, "buffer drained");
            });
            T.Test("snapshot/pages carry the sync id and no page is bigger than the page size", () =>
            {
                var pages = ActorPaging.Split(Actors(450), 9, 1, 0, 0).ToList(); T.Eq(3, pages.Count);
                T.True(pages.All(p => p.SyncId == 9 && p.Actors.Count <= ActorPaging.DefaultPageSize && p.PageCount == 3));
                T.Eq(1, ActorPaging.Split(new List<ActorRecord>(), 1, 1, 0, 0).Count());
            });
            T.Test("snapshot/300 part world completes under 5 and 10 percent loss", () =>
            {
                foreach (var loss in new[] { 0.05, 0.10 })
                {
                    var r = Rig(actors: 6000, loss: loss, seed: 4); r.w.Join(r.g);
                    T.True(r.w.Net.RunUntil(() => r.gc.Applied, 30_000), "loss " + loss); T.Eq(6000, r.ap.Applied[0].Actors.Count);
                }
            });
            T.Test("snapshot/assembler handles reordered and duplicate pages, newer sync replaces", () =>
            {
                var a = new ActorPageAssembler(); var pages = ActorPaging.Split(Actors(450), 5, 1, 0, 7).ToList();
                T.True(a.Add(pages[2]) == null); T.True(a.Add(pages[2]) == null); T.True(a.Add(pages[0]) == null);
                var snap = a.Add(pages[1]); T.True(snap != null && snap.Actors.Count == 450 && snap.HasRevision && snap.WorldRevision == 7);
                var np = ActorPaging.Split(Actors(450), 6, 1, 0, 0).ToList(); a.Add(np[0]); a.Add(pages[0]); T.Eq(1, a.PagesHeld, "older sync ignored");
                a.Add(np[1]); T.True(a.Add(np[2]) != null);
            });
            T.Test("delta/live changes during join apply once after snapshot in revision order, stale dropped", () =>
            {
                var b = new ActorDeltaBuffer(); var log = new List<long>();
                b.BeginSync(1);
                foreach (long rev in new long[] { 30, 10, 25, 5 }) T.True(b.TryDefer(ActorDeltaKind.ActorUpdate, rev, true, rev, () => { log.Add(rev); return true; }));
                var gen = b.BeginSnapshot(1); b.SetSnapshotRevision(gen, true, 20);
                T.Eq(0, log.Count, "nothing applied while buffering");
                b.Complete(gen); T.SeqEq(new byte[] { 25, 30 }, log.Select(x => (byte)x).ToArray(), "applied in revision order, 5 and 10 dropped");
                T.True(b.TryDefer(ActorDeltaKind.ActorUpdate, 77, true, 18, () => { log.Add(18); return true; }), "late pre-snapshot delta from another lane is dropped");
                T.Eq(2, log.Count);
                T.True(!b.TryDefer(ActorDeltaKind.ActorUpdate, 77, true, 40, () => true) || log.Count == 2);
            });
            T.Test("delta/changes without a revision replay in arrival order", () =>
            {
                var b = new ActorDeltaBuffer(); var log = new List<int>(); b.BeginSync(1);
                b.TryDefer(ActorDeltaKind.ActorState, 1, false, 0, () => { log.Add(1); return true; });
                b.TryDefer(ActorDeltaKind.ActorState, 2, true, 50, () => { log.Add(2); return true; });
                b.TryDefer(ActorDeltaKind.ActorState, 3, false, 0, () => { log.Add(3); return true; });
                var g = b.BeginSnapshot(1); b.SetSnapshotRevision(g, true, 10); b.Complete(g);
                T.SeqEq(new byte[] { 1, 2, 3 }, log.Select(x => (byte)x).ToArray());
            });
            T.Test("delta/overflow replays what was not applied, aborts and requests a resync", () =>
            {
                var b = new ActorDeltaBuffer(maxPending: 5); int resync = 0; var log = new List<int>(); b.RequestResync = () => resync++;
                b.BeginSync(1);
                for (int i = 1; i <= 6; i++) { int k = i; b.TryDefer(ActorDeltaKind.ActorState, k, false, 0, () => { log.Add(k); return true; }); }
                T.Eq(6, log.Count, "all six replayed, none discarded"); T.Eq(1, resync); T.True(b.Invalidated && !b.IsBuffering);
                T.Eq(0L, b.BeginSnapshot(), "unsynced snapshot ignored while invalidated");
                T.True(b.BeginSnapshot(2) > 0, "fresh sync recovers");
            });
            T.Test("delta/snapshot failure: two retries then abort with watermark unchanged", () =>
            {
                var r = Rig(actors: 10);
                r.ap.Script = new Queue<ApplyOutcome>(new[] { ApplyOutcome.RuntimeFailure, ApplyOutcome.RuntimeFailure, ApplyOutcome.RuntimeFailure });
                bool aborted = false; r.gc.SnapshotAborted += () => aborted = true; bool applied = false; r.gc.SnapshotApplied += () => applied = true;
                r.w.Join(r.g);
                T.True(r.w.Net.RunUntil(() => aborted, 20_000), "aborted after retries");
                T.Eq(3, r.ap.Applied.Count, "initial attempt plus two automatic retries"); T.Eq(2, r.gc.ResyncRequests); T.True(!applied);
                T.True(!r.gc.Buffer.HasCompletedWatermark, "watermark not advanced by failures"); T.True(!r.gc.Buffer.IsBuffering, "buffer replayed and closed");
            });
            T.Test("resync/client limit 2 minutes, all limit 5 seconds", () =>
            {
                var w = new World(); var s = w.StartHost(); long now = 0; var h = new SnapshotHost(s);
                T.True(h.TryClientResync("PLAYER_A")); T.True(!h.TryClientResync("PLAYER_A")); T.True(h.TryClientResync("PLAYER_B"));
                w.Net.Step(119_000); T.True(!h.TryClientResync("PLAYER_A")); w.Net.Step(2_000); T.True(h.TryClientResync("PLAYER_A"));
                T.True(h.ResyncAll() == null); T.Eq(SnapshotHost.AllCooldownText, h.ResyncAll());
                w.Net.Step(5_100); T.True(h.ResyncAll() == null);
            });
            T.Test("resync/client gets cooldown chat reply and success reply", () =>
            {
                var r = Rig(actors: 5); var chats = new List<string>(); r.g.ChatReceived += m => { if (m.Kind == ChatKind.System) chats.Add(m.Text); };
                r.w.Join(r.g); T.True(r.w.Net.RunUntil(() => r.gc.Applied, 8000));
                r.w.Net.Step(200); chats.Clear(); r.gc.RequestResync(); r.w.Net.Step(1500);
                T.True(chats.Contains("Your client has been resynced."), string.Join("|", chats));
                chats.Clear(); r.gc.RequestResync(); r.w.Net.Step(500);
                T.True(chats.Contains(SnapshotHost.ClientCooldownText), string.Join("|", chats));
            });
            T.Test("resync/destroy guard lasts 8 seconds after any snapshot", () =>
            {
                var r = Rig(actors: 5); r.w.Join(r.g); T.True(r.w.Net.RunUntil(() => r.gc.Applied, 8000));
                var host = r.w.Host; string id = r.g.LocalId; var h = new SnapshotHost(host);
                h.ArmDestroyGuard(id); T.True(h.DestroyGuardActive(id)); r.w.Net.Step(7_900); T.True(h.DestroyGuardActive(id)); r.w.Net.Step(200); T.True(!h.DestroyGuardActive(id));
            });
            T.Test("delta/ordering policies hold", () =>
            {
                T.True(Ver.Run(typeof(ActorRevisionOrderingPolicy), "VerifyRules")); T.True(Ver.Run(typeof(ActorDeltaJournalPolicy), "VerifyRules")); T.True(Ver.Run(typeof(ActorDeltaRevisionPolicy), "VerifyPermutations"));
                T.True(Ver.Run(typeof(ActorSyncGenerationPolicy), "VerifyRules")); T.True(Ver.Run(typeof(InitialActorSnapshotPolicy), "VerifyRules")); T.True(Ver.Run(typeof(ActorSnapshotDeltaQueue<int>), "VerifyOrderingAndCleanup"));
            });
            T.Test("delta/stale transfer cannot resurrect old owner; destroy tombstone beats older spawn", () =>
            {
                var st = default(ActorDeltaRevisionState);
                st = ActorDeltaRevisionPolicy.Record(st, ActorDeltaKind.ActorDestroy, 8, null);
                T.True(!ActorDeltaRevisionPolicy.ShouldApply(st, ActorDeltaKind.ActorSpawn, 5, false));
                var b = new ActorDeltaBuffer(); string owner = b.OwnerForSpawn(1, 4, "PLAYER_HOST"); T.Eq("PLAYER_HOST", owner);
                b.RecordApplied(ActorDeltaKind.ActorTransfer, 1, 9, "PLAYER_GUEST"); T.Eq("PLAYER_GUEST", b.OwnerForSpawn(1, 4, "PLAYER_HOST"));
            });
        }
    }
}

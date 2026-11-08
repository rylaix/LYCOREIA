// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Lycoreia.Actors;
using Lycoreia.Net;
using Lycoreia.Net.Messages;

namespace Lycoreia.Tests
{
    static class StageATests
    {
        static ActorTransfer Reply(ClientCapture c, bool commit, bool approved) => new ActorTransfer { ActorId = c.ActorId, CaptureRequestId = c.RequestId, CaptureIntent = !commit, CaptureCommit = commit, CaptureApproved = approved, ConsumesActor = c.Consumes };

        public static void Register()
        {
            T.Test("loopback/cleanup force-destroy reaches every guest without deferral, even for a guest-owned actor", () =>
            {
                var r = new ActorRig(2); var g0 = r.G[0]; var e = r.HostActor(70, g0.s.LocalId); r.Share(e);
                T.True(r.HostSync.HostForceDestroy(70)); r.Step();
                T.False(r.HostSync.Table.TryGet(70, out _), "host table"); foreach (var g in r.G) { T.False(g.sync.Table.TryGet(70, out _), "guest table"); T.Eq(0, g.sync.Table.DeferredCount); }
                T.False(g0.sync.HostForceDestroy(1), "guests cannot");
            });
            T.Test("loopback/owner unload releases the actor on the host and other guests; state reaches non-owners only from the owner", () =>
            {
                var r = new ActorRig(2); var g0 = r.G[0]; var g1 = r.G[1]; var e = r.HostActor(71, g0.s.LocalId); r.Share(e);
                T.True(g0.sync.SendUnload(71)); r.Step();
                T.True(r.HostSync.Table.Get(71).ReleasedByOwner, "host saw release");
                T.False(g1.sync.SendUnload(71), "not the owner");
                T.True(g0.sync.SendOwnedState(new ActorState { ActorId = 71, Kind = ActorStateKind.Slime, Hunger = 0.5f })); r.Step();
                T.False(g1.sync.SendOwnedState(new ActorState { ActorId = 71, Kind = ActorStateKind.Slime }), "non-owner state refused locally");
            });
            T.Test("guest-capture/prepare retries 3 times every 1.5 s then times out at 8 s", () =>
            {
                var m = new GuestCaptureMachine(); T.Eq(CaptureAskResult.Queued, m.Ask(5, "H", false, 0, out var c));
                T.Eq(CaptureAskResult.AlreadyPending, m.Ask(5, "H", false, 0.1, out _));
                int sends = 0; for (double t = 0; t <= 7.9; t += 0.1) sends += m.Pump(t).Count(a => a.Kind == CaptureActionKind.SendPrepare);
                T.Eq(3, sends, "prepare sends");
                var gave = m.Pump(8.2); T.True(gave.Any(a => a.Kind == CaptureActionKind.GaveUp), "timeout"); T.Eq(0, m.PendingCount);
            });
            T.Test("guest-capture/consume: approve, arm permit, destroy allowed once, commit, completed", () =>
            {
                var m = new GuestCaptureMachine(); m.Ask(7, "H", true, 0, out var c); m.Pump(0);
                T.True(m.OnReply(Reply(c, false, true), 0.1, out _)); T.Eq(CapturePhase.Approved, c.Phase);
                T.False(m.AllowConsumeDestroy(7, true, 0.2, false), "no permit yet");
                T.True(m.TryArmConsume(7, 0.2)); T.True(m.AllowConsumeDestroy(7, true, 0.25, false), "permit");
                T.False(m.AllowConsumeDestroy(7, false, 0.25, false), "not connected");
                T.False(m.CanCancel(7), "irreversible once armed");
                m.OnConsumeFinished(7, true, 0.3);
                var acts = m.Pump(0.31); T.True(acts.Any(a => a.Kind == CaptureActionKind.SendCommit), "commit right away for a consume");
                T.False(m.AllowConsumeDestroy(7, true, 0.32, true), "no destroy after the commit was sent");
                T.True(m.OnReply(Reply(c, true, true), 0.5, out _)); T.True(m.WasCompleted(c.RequestId)); T.Eq(0, m.PendingCount);
            });
            T.Test("guest-capture/expired native consume permit becomes reversible and releases reservation", () =>
            {
                var m = new GuestCaptureMachine(); m.Ask(71, "H", true, 0, out var c);
                m.OnReply(Reply(c, false, true), 0, out _);
                T.True(m.TryArmConsume(71, 0.1));
                m.Pump(2.2); T.True(m.CanCancel(71), "failed native animation cannot strand an irreversible capture");
                T.False(m.AllowConsumeDestroy(71, true, 2.2, false));
                T.True(m.Pump(36).Any(a => a.Kind == CaptureActionKind.SendAbort)); T.Eq(0, m.PendingCount);
            });
            T.Test("guest-capture/nested model removal stays transactional through host acknowledgement", () =>
            {
                var m = new GuestCaptureMachine(); m.Ask(72, "G", true, 0, out var c);
                T.False(m.OwnsConsumeRemoval(72)); m.OnReply(Reply(c, false, true), 0.1, out _);
                T.False(m.OwnsConsumeRemoval(72)); T.True(m.TryArmConsume(72, 0.2));
                T.True(m.OwnsConsumeRemoval(72)); m.ObserveDestroy(72); m.OnConsumeFinished(72, true, 0.3);
                T.True(m.OwnsConsumeRemoval(72), "nested GameModel hook follows Destroyer hook after permit cleared");
                m.Pump(0.4); T.True(m.OwnsConsumeRemoval(72), "missing-model sweep must wait for commit acknowledgement");
                m.OnReply(Reply(c, true, true), 0.5, out _); T.False(m.OwnsConsumeRemoval(72));
            });
            T.Test("guest-capture/held commit waits 0.3 s and retries at most 16", () =>
            {
                var m = new GuestCaptureMachine(); m.Ask(9, "H", false, 0, out var c); m.Pump(0); m.OnReply(Reply(c, false, true), 0, out _);
                T.True(m.AllowsHeldNativePass(9, false), "native GIANT pull is allowed after held approval");
                T.False(m.AllowsHeldNativePass(9, true), "held approval cannot store a normal actor");
                T.False(m.TryArmConsume(9, 0.1), "held path has no consume permit");
                T.True(m.NoteNativeConfirmed(9, 1.0));
                T.False(m.Pump(1.2).Any(a => a.Kind == CaptureActionKind.SendCommit), "too early");
                T.True(m.Pump(1.31).Any(a => a.Kind == CaptureActionKind.SendCommit), "after 0.3 s");
                int n = 1; for (double t = 1.4; t < 29; t += 0.5) n += m.Pump(t).Count(a => a.Kind == CaptureActionKind.SendCommit);
                T.Eq(16, n, "commit sends");
                T.True(m.Pump(31.5).Any(a => a.Kind == CaptureActionKind.GaveUp), "commit timeout");
            });
            T.Test("guest-capture/denial back-off survives the pump (250, 500, 1000 ms) and gives up after 3 retries", () =>
            {
                var m = new GuestCaptureMachine(); double t = 0;
                for (int i = 1; i <= 4; i++)
                {
                    T.Eq(CaptureAskResult.Queued, m.Ask(3, "H", false, t, out var c), "ask " + i); m.Pump(t); m.OnReply(Reply(c, false, false), t, out _);
                    double wait = i == 1 ? 0.25 : i == 2 ? 0.5 : 1.0;
                    m.Pump(t + wait / 2);
                    if (i < 4) T.Eq(CaptureAskResult.Suppressed, m.Ask(3, "H", false, t + wait / 2, out _), "suppressed " + i);
                    t += wait + 0.01; m.Pump(t);
                }
                T.Eq(CaptureAskResult.GaveUp, m.Ask(3, "H", false, t, out _));
                m.Pump(t + 31); T.Eq(CaptureAskResult.Queued, m.Ask(3, "H", false, t + 31, out _), "denial memory 30 s");
            });
            T.Test("guest-capture/cancel and resync keep irreversible captures; downgrade only consume to held", () =>
            {
                var m = new GuestCaptureMachine();
                m.Ask(1, "H", true, 0, out var a); m.Ask(2, "H", false, 0, out var b); m.Ask(3, "H", true, 0, out var c); m.Pump(0);
                foreach (var x in new[] { a, b, c }) m.OnReply(Reply(x, false, true), 0, out _);
                m.ObserveDestroy(1);
                T.True(m.DowngradeToHeld(3)); T.False(c.Consumes); T.False(m.DowngradeToHeld(2), "held stays held");
                var aborted = m.OnResync(); T.Eq(2, aborted.Count, "two reversible aborted"); T.True(aborted.All(x => x.Kind == CaptureActionKind.SendAbort));
                T.True(m.Get(1) != null, "irreversible kept"); T.True(m.Cancel(1, "x") == null, "cancel refused after destroy seen");
            });
            T.Test("guest-capture/approval expires after 35 s with a per-actor abort; candidates last 2.5 s per scene", () =>
            {
                var m = new GuestCaptureMachine(); m.Ask(4, "H", false, 0, out var c); m.Pump(0); m.OnReply(Reply(c, false, true), 0, out _);
                T.True(m.Pump(36).Any(x => x.Kind == CaptureActionKind.SendAbort && x.Capture.ActorId == 4));
                m.NoteCandidate(8, 2, 10); T.True(m.IsFreshCandidate(8, 2, 12.4)); T.False(m.IsFreshCandidate(8, 2, 12.6)); T.False(m.IsFreshCandidate(8, 3, 11));
                T.False(m.OnReply(new ActorTransfer { ActorId = 4, CaptureRequestId = Guid.NewGuid(), CaptureIntent = true, CaptureApproved = true }, 1, out _), "unknown request");
            });
            T.Test("replica/interpolates, extrapolates briefly, settles and skips predicted ids", () =>
            {
                var r = new ReplicaMotion(); r.Teleport(1, new Vec3(0, 0, 0), Quat.Identity);
                r.Push(1, 1.0, new Vec3(0, 0, 0), Quat.Identity, Vec3.Zero);
                r.Push(1, 1.1, new Vec3(10, 0, 0), Quat.Identity, new Vec3(1, 0, 0));
                var w = new Dictionary<long, Vec3>(); Func<long, bool> rep = _ => true;
                r.Step(1.15, rep, (id, p, q) => w[id] = p); T.True(w[1].X > 0 && w[1].X < 10, "mid blend " + w[1].X);
                r.Step(1.3, rep, (id, p, q) => w[id] = p); T.True(w[1].X >= 10 && w[1].X <= 10.26f, "extrapolated " + w[1].X);
                r.Step(2.0, rep, (id, p, q) => w[id] = p); T.Eq(10f, w[1].X, "settled on target");
                w.Clear(); T.Eq(0, r.Step(2.5, rep, (id, p, q) => w[id] = p), "settled: not rewritten");
                r.Push(1, 3, new Vec3(20, 0, 0), Quat.Identity, Vec3.Zero); r.SetPredicted(1, true);
                T.Eq(0, r.Step(3.05, rep, (id, p, q) => w[id] = p), "predicted skipped");
                r.Push(2, 3, new Vec3(float.NaN, 0, 0), Quat.Identity, Vec3.Zero); T.Eq(0, r.Step(3.1, rep, (id, p, q) => w[id] = p), "non-finite ignored");
            });
            T.Test("owner-sampler/sends on change, else every 10th tick staggered by id", () =>
            {
                var s = new OwnerSampler(); var p = new Vec3(1, 2, 3);
                T.True(s.ShouldSend(5, p, Quat.Identity, Vec3.Zero, 0), "first");
                int sent = 0; for (long t = 1; t <= 20; t++) if (s.ShouldSend(5, p, Quat.Identity, Vec3.Zero, t)) sent++;
                T.Eq(2, sent, "keep-alives in 20 ticks");
                T.True(s.ShouldSend(5, new Vec3(1.5f, 2, 3), Quat.Identity, Vec3.Zero, 21), "moved");
            });
            T.Test("interest/128 per pass, rotating start, 25 % rule within 60 m, unknown owner never proof", () =>
            {
                var all = Enumerable.Range(1, 300).Select(i => new InterestCandidate { Id = i, Unsimulated = true, MyDistance = 5 }).ToList();
                var ip = new InterestPass(); var a = ip.Select(all); var b = ip.Select(all);
                T.Eq(128, a.Count); T.Eq(129L, b[0].id, "rotates");
                var ip2 = new InterestPass();
                var c = new List<InterestCandidate>
                {
                    new InterestCandidate { Id = 1, OwnedByOther = true, MyDistance = 7, OwnerDistance = 10 },
                    new InterestCandidate { Id = 2, OwnedByOther = true, MyDistance = 8, OwnerDistance = 10 },
                    new InterestCandidate { Id = 3, OwnedByOther = true, MyDistance = 61, OwnerDistance = 200 },
                    new InterestCandidate { Id = 4, OwnedByOther = true, MyDistance = 5, OwnerDistance = float.PositiveInfinity },
                    new InterestCandidate { Id = 6, Unsimulated = true, MyDistance = 87 },
                    new InterestCandidate { Id = 5, OwnedByOther = true, MyDistance = 5, OwnerDistance = float.PositiveInfinity, OwnerSilent = true },
                };
                T.Eq("1,5", string.Join(",", ip2.Select(c).Select(x => x.id)));
            });
            T.Test("owner-silence/threshold 5..11 s per actor", () =>
            {
                for (long i = 1; i < 500; i++) { var th = OwnerSilence.Threshold(i); T.True(th >= 5 && th < 11, "range " + th); }
                T.True(OwnerSilence.Threshold(1) != OwnerSilence.Threshold(2), "random per actor");
                T.False(OwnerSilence.IsSilent(1, 100, 0), "never heard: not silent"); T.True(OwnerSilence.IsSilent(1, 100, 80));
            });
            T.Test("ranch-bounds/expanded boxes, unknown when nothing is loaded", () =>
            {
                var r = new RanchBoundsSet(); T.True(r.Contains(Vec3.Zero) == null, "unknown"); T.True(r.NeedsRefresh(0));
                r.Set(new[] { (new Vec3(0, 0, 0), new Vec3(10, 10, 10)) }, 0);
                T.True(r.Contains(new Vec3(8.9f, 0, 0)) == true, "4 m pad"); T.True(r.Contains(new Vec3(9.1f, 0, 0)) == false);
                T.True(r.Contains(new Vec3(0, 204, 0)) == true, "200 m vertical"); T.False(r.NeedsRefresh(9.9)); T.True(r.NeedsRefresh(10));
            });
            T.Test("produce/size, sweep and cycle cache rules", () =>
            {
                T.False(ProduceRules.ShouldBeFullSize(ProduceRules.Phase.Unripe, true)); T.True(ProduceRules.ShouldBeFullSize(ProduceRules.Phase.Rotten, true));
                T.True(ProduceRules.SweepRestores(ProduceRules.Phase.Ripe, false, true, true, false, false));
                T.False(ProduceRules.SweepRestores(ProduceRules.Phase.Ripe, false, true, true, true, false), "being pulled");
                T.False(ProduceRules.SweepRestores(ProduceRules.Phase.Ripe, false, true, true, false, true), "held kinematic");
                var c = new ProduceCycleCache(); c.Freeze(1, double.PositiveInfinity); T.False(c.Has(1), "infinite not cached");
                c.Freeze(2, 500); c.Freeze(2, 900); T.Eq(500.0, c.Restore(2, 1000)); T.Eq(1000.0, c.Restore(2, 1000), "reset to now");
            });
            T.Test("spawner-gate/animal and slime batches, need checks", () =>
            {
                var g = new AnimalSpawnerGuard();
                T.Eq(0, SpawnerGate.AnimalBatch(g, 1, 5, 0, false, false, false, true, 0, out var why)); T.Eq(SpawnerGate.Reason.NotLeader, why);
                T.Eq(5, SpawnerGate.AnimalBatch(g, 1, 5, 0, true, false, true, true, 0, out why), "ranch exempt");
                T.Eq(2, SpawnerGate.AnimalBatch(g, 1, 5, 12, true, false, false, true, 0, out why)); T.Eq(SpawnerGate.Reason.Full, why);
                T.Eq(5, SpawnerGate.AnimalBatch(g, 1, 5, 0, true, false, false, false, 0, out _), "offline");
                T.Eq(Math.Max(1, (int)MathF.Round(20 * 0.84f)) - 10, SpawnerGate.SlimeBatch(g, 2, 50, 10, 20, true, false, false, true, 0, out _));
                T.False(SpawnerGate.NeedsMoreAnimals(true, true, false, true, false, new[] { 14, 20 }));
                T.True(SpawnerGate.NeedsMoreAnimals(true, true, false, true, false, new[] { 14, 3 }));
                T.False(SpawnerGate.NeedsMoreSlimes(true, true, false, true, false, 17, 20)); T.True(SpawnerGate.NeedsMoreSlimes(true, true, false, true, false, 16, 20));
                T.True(SpawnerGate.NeedsMoreSlimes(true, true, true, false, true, 99, 20), "ranch keeps native answer");
            });
        }
    }
}

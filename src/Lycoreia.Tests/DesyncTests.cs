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
    static class DesyncTests
    {
        public static void Register()
        {
            T.Test("desync/host registers saved actors: live ones host-owned, unloaded ones unowned, both reach guests", () =>
            {
                var r = new ActorRig(1); var g = r.G[0];
                T.True(r.HostSync.HostRegisterExisting(new ActorEntry { Id = 501, TypeId = 1, Scene = 1, Position = new Vec3(1, 2, 3) }, live: true));
                T.True(r.HostSync.HostRegisterExisting(new ActorEntry { Id = 502, TypeId = 1, Scene = 1 }, live: false));
                T.Eq(r.HostId, r.HostSync.Table.OwnerOf(501)); T.Eq("", r.HostSync.Table.OwnerOf(502), "unloaded: no owner, the arbiter hands it out");
                r.Step(500);
                T.True(g.sync.Table.TryGet(501, out var a) && a.Owner == r.HostId, "live saved actor replicated with the host as owner");
                T.True(g.sync.Table.TryGet(502, out var b) && b.Owner == "", "unloaded saved actor replicated unowned");
                T.True(g.world.Present.Contains(501) && g.world.Present.Contains(502), "replicas created");
                T.False(r.HostSync.HostRegisterExisting(new ActorEntry { Id = 501, TypeId = 1 }, true), "already known: no second announcement");
                T.False(g.sync.HostRegisterExisting(new ActorEntry { Id = 503, TypeId = 1 }, true), "only the host registers");
                T.False(r.HostSync.HostRegisterExisting(new ActorEntry { Id = 504, TypeId = 1, IsGadget = true }, true), "gadgets are not world actors here");
            });
            T.Test("desync/a destroyed id never comes back from a sweep", () =>
            {
                var r = new ActorRig(1); r.HostSync.HostRegisterExisting(new ActorEntry { Id = 610, TypeId = 1, Scene = 1 }, true); r.Step(300);
                T.True(r.HostSync.PublishDestroy(610)); T.True(r.HostSync.Table.IsTombstoned(610));
                T.False(r.HostSync.HostRegisterExisting(new ActorEntry { Id = 610, TypeId = 1, Scene = 1 }, true), "tombstoned");
            });
            T.Test("desync/saved actors registered before a join are in the join snapshot", () =>
            {
                var r = new ActorRig(0);
                for (int i = 0; i < 40; i++) r.HostSync.HostRegisterExisting(new ActorEntry { Id = 700 + i, TypeId = 1, Scene = 1 }, live: i % 2 == 0);
                var g = r.W.AddGuest("Late"); var fw = new FakeActorWorld(); var gc = new Lycoreia.Snapshot.GuestSnapshotController(g);
                var sync = new ActorSync(g, new Lycoreia.Snapshot.SnapshotHost(g), fw, () => r.W.Net.Now / 1000.0, gc.Buffer); gc.Applier = new SyncApplier(sync);
                r.W.Join(g); T.True(r.W.Net.RunUntil(() => gc.Applied, 8000), "joined");
                T.Eq(40, sync.Table.Count, "every saved actor reached the joiner");
                T.Eq(20, sync.Table.All.Count(e => e.Owner == ""), "unloaded ones arrive unowned");
            });
            T.Test("desync/guest never publishes ids outside its block; the host tells an old client to drop such an orphan", () =>
            {
                var r = new ActorRig(1); var g = r.G[0]; long start = ActorSync.ActorStart(g.s.LocalId);
                T.False(g.sync.GuestMayPublish(183), "save-local id"); T.True(g.sync.GuestMayPublish(start + 5)); T.True(r.HostSync.GuestMayPublish(183), "host ids are always publishable");
                T.False(g.sync.PublishSpawn(new ActorEntry { Id = 183, TypeId = 1, Scene = 1 }), "refused locally"); T.False(g.sync.Table.TryGet(183, out _), "not recorded");
                var refused = new List<long>(); g.sync.OnGuestSpawnRefused += refused.Add;
                g.sync.Table.Add(new ActorEntry { Id = 184, Owner = g.s.LocalId, TypeId = 1 }); g.world.Present.Add(184);
                g.s.Node.SendToHost(new ActorSpawn { ActorId = 184, TypeId = 1, OwnerId = g.s.LocalId, Rotation = Quat.Identity }); r.Step(300);
                T.False(r.HostSync.Table.TryGet(184, out _)); T.True(refused.Contains(184), "the sender removed its unshareable actor");
                // an out-of-block id that names a shared actor is never destroyed at the sender
                r.HostSync.HostRegisterExisting(new ActorEntry { Id = 185, TypeId = 1, Scene = 1 }, true); r.Step(300);
                g.s.Node.SendToHost(new ActorSpawn { ActorId = 185, TypeId = 1, OwnerId = g.s.LocalId, Rotation = Quat.Identity }); r.Step(300);
                T.True(g.sync.Table.TryGet(185, out _) && r.HostSync.Table.OwnerOf(185) == r.HostId, "shared actor untouched");
            });
            T.Test("desync/id block repair keeps the allocator inside the peer's block", () =>
            {
                var host = ActorIdBlock.For("PLAYER_AAAAAAAAA", isHost: true); T.Eq(1L, host.start); T.Eq(Identity.ActorIdOffset, host.end);
                var gb = ActorIdBlock.For("PLAYER_AAAAAAAAA", isHost: false); T.Eq((long)Identity.ShortId("PLAYER_AAAAAAAAA") * Identity.ActorIdOffset, gb.start);
                T.Eq(250L, ActorIdBlock.Repair(250, host, 100), "inside and ahead: kept");
                T.Eq(101L, ActorIdBlock.Repair(90, host, 100), "inside but behind the highest used id: continues after it");
                T.Eq(gb.start, ActorIdBlock.Repair(184, gb, gb.start - 1), "a guest's save allocator moves to its block start");
                T.Eq(gb.start + 13, ActorIdBlock.Repair(184, gb, gb.start + 12), "after its own earlier spawns");
                T.Eq(5001L, ActorIdBlock.Repair(gb.start + 3, host, 5000), "host claimed into a guest block: back after its own ids");
            });
            T.Test("desync/census classifies orphans, missing models, body modes, stalls and ghost owners", () =>
            {
                string me = "ME", other = "OTHER", gone = "GONE"; double now = 100;
                var table = new List<ActorEntry>
                {
                    new ActorEntry { Id = 1, Owner = me }, new ActorEntry { Id = 2, Owner = other, LastOwnerHeardAt = 99 }, new ActorEntry { Id = 3, Owner = other, LastOwnerHeardAt = 90 },
                    new ActorEntry { Id = 4, Owner = "" }, new ActorEntry { Id = 5, Owner = gone }, new ActorEntry { Id = 6, Owner = other }, new ActorEntry { Id = 7, Owner = other, LastOwnerHeardAt = 80, ReleasedByOwner = true },
                    new ActorEntry { Id = 8, Owner = me }, new ActorEntry { Id = 9, IsGadget = true },
                };
                var local = new List<LocalActorFact>
                {
                    new LocalActorFact { Id = 1, Live = true, HasBody = true, Kinematic = false }, new LocalActorFact { Id = 2, Live = true, HasBody = true, Kinematic = false },
                    new LocalActorFact { Id = 3, Live = true, HasBody = true, Kinematic = true }, new LocalActorFact { Id = 4, Live = false }, new LocalActorFact { Id = 5, Live = true, HasBody = true, Kinematic = true },
                    new LocalActorFact { Id = 7, Live = true, HasBody = true, Kinematic = true }, new LocalActorFact { Id = 8, Live = true, HasBody = true, Kinematic = true },
                    new LocalActorFact { Id = 50, Live = true, TypeName = "Pink" },
                };
                var rep = ActorCensus.Build(table, local, me, id => id == me || id == other, now);
                T.Eq(9, rep.Table); T.Eq(1, rep.Gadgets); T.Eq(2, rep.Owned); T.Eq(5, rep.OwnedByOther, "other and departed owners"); T.Eq(1, rep.Unowned);
                T.True(rep.Orphans.Select(o => o.Id).SequenceEqual(new[] { 50L }), "orphan: local model not in the table");
                T.True(rep.Missing.SequenceEqual(new[] { 6L }), "missing: table entry without a local model");
                T.True(rep.DynamicReplicas.SequenceEqual(new[] { 2L }), "dynamic replica");
                T.True(rep.FrozenOwned.SequenceEqual(new[] { 8L }), "owned but kinematic");
                T.True(rep.Stalled.SequenceEqual(new[] { 3L }), "silent connected owner, not released");
                T.True(rep.GhostOwned.SequenceEqual(new[] { 5L }), "owner no longer connected");
                T.True(rep.Summary().Contains("orphans=1") && rep.Samples().Contains("50:Pink:L"));
                T.Eq("1,ME,0,0,0.0,0.0,0.0,L", ActorCensus.DumpLine(table[0], local[0]));
                T.Eq("6,THER,0,0,0.0,0.0,0.0,M", ActorCensus.DumpLine(table[5], null));
            });
            T.Test("replica/a thrown arc with jittered 20 Hz samples stays on the true path", () =>
            {
                // owner: thrown at 12 m/s forward, 8 m/s up, gravity 9.81; samples every 50 ms arriving with up to 20 ms jitter
                Vec3 P(double t) => new Vec3((float)(12 * t), (float)(8 * t - 4.905 * t * t), 0); Vec3 V(double t) => new Vec3(12, (float)(8 - 9.81 * t), 0);
                var tr = new ReplicaTrack(); tr.Teleport(P(0), Quat.Identity); var rng = new Random(7); double worst = 0; int frames = 0;
                var arrivals = new List<(double at, double t)>(); for (double t = 0; t <= 1.5; t += 0.05) arrivals.Add((t + 0.03 + rng.NextDouble() * 0.02, t));
                int next = 0;
                for (double now = 0; now <= 1.5; now += 1.0 / 144)
                {
                    while (next < arrivals.Count && arrivals[next].at <= now) { var t = arrivals[next++].t; tr.Push(arrivals[next - 1].at, P(t), Quat.Identity, V(t)); }
                    if (!tr.Evaluate(now, out var p, out _) || now < 0.25) continue;
                    // the replica shows the arc about Delay + transit behind; compare with the closest point of the true path near that time
                    double best = double.MaxValue; for (double t = now - 0.25; t <= now; t += 0.002) { var q = P(t); double d = Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y)); if (d < best) best = d; }
                    worst = Math.Max(worst, best); frames++;
                }
                T.True(frames > 100, "frames " + frames); T.True(worst < 0.05, "max distance from the true arc " + worst.ToString("0.000") + " m");
            });
            T.Test("replica/resting actor resumes from where it is shown, never jumps", () =>
            {
                var tr = new ReplicaTrack(); tr.Teleport(new Vec3(5, 0, 0), Quat.Identity);
                tr.Push(10, new Vec3(5.5f, 0, 0), Quat.Identity, new Vec3(5, 0, 0));
                T.True(tr.Evaluate(10.0, out var p, out _)); T.Eq(5f, p.X, "starts at the shown pose");
                T.True(tr.Evaluate(10.1, out p, out _)); T.True(Math.Abs(p.X - 5.5f) < 1e-3, "reaches the sample after the delay " + p.X);
            });
            T.Test("replica/ownership handoff clears the previous owner's motion stream", () =>
            {
                var tr = new ReplicaTrack(); tr.Teleport(new Vec3(0, 0, 0), Quat.Identity);
                tr.Push(1, new Vec3(10, 0, 0), Quat.Identity, new Vec3(10, 0, 0));
                T.True(tr.Evaluate(1.05, out var before, out _)); T.True(before.X > 0 && before.X < 10);
                tr.Teleport(new Vec3(4, 0, 0), Quat.Identity);                         // ownership update: current displayed pose
                tr.Push(2, new Vec3(5, 0, 0), Quat.Identity, new Vec3(1, 0, 0));       // first sample from the new owner
                T.True(tr.Evaluate(2.0, out var after, out _));
                T.Eq(4f, after.X, "new stream begins at the pose shown at handoff, not old-owner extrapolation");
            });
            T.Test("produce/attached produce stays with its plant's simulator; state carries the attach flag", () =>
            {
                var w = new FakeArbiterWorld(); var a = new OwnershipArbiter(w); w.Owner[9] = "A"; w.Connected.UnionWith(new[] { "A", "B", "HOST" }); w.Dist["A"] = 50; w.Dist["B"] = 1;
                w.Pinned.Add(9); a.RegisterInterest("B", 9, 1, 0f); a.Tick(5f); T.Eq(0, w.Grants.Count, "pinned: no handover to a closer player");
                w.Pinned.Remove(9); a.RegisterInterest("B", 9, 1, 6f); a.Tick(6.5f); T.Eq(1, w.Grants.Count, "detached: normal rules");
                w.Owner[10] = "GONE"; w.Pinned.Add(10); a.RegisterInterest("B", 10, 1, 11f); a.Tick(11.5f); T.True(w.Grants.Exists(g => g.Item1 == 10), "owner left: pinned produce is handed on");
                var r = new ActorRig(1); var g = r.G[0]; var e = r.HostActor(77, g.s.LocalId); r.Share(e);
                g.sync.Table.Get(77).Owner = g.s.LocalId;
                T.True(g.sync.SendOwnedState(new ActorState { ActorId = 77, Kind = ActorStateKind.Produce, Progress = 5, CycleState = 1, Scale = 1, Attached = true })); r.Step(300);
                T.True(r.HostSync.Table.Get(77).Attached, "host learns the attach state"); T.True(g.sync.Table.Get(77).Attached);
            });
            T.Test("desync/orphan decisions", () =>
            {
                OrphanAction D(bool host, bool applied, double seen, bool tomb = false, bool prot = false, bool inBlock = false, bool player = false) => ActorCensus.Decide(host, applied, seen, 10, player, tomb, prot, inBlock);
                T.Eq(OrphanAction.Wait, D(true, true, 9), "grace period");
                T.Eq(OrphanAction.Register, D(true, true, 0), "host: load rule");
                T.Eq(OrphanAction.Keep, D(true, true, 0, tomb: true), "host never resurrects a destroyed id");
                T.Eq(OrphanAction.Wait, D(false, false, 0), "guest before its snapshot");
                T.Eq(OrphanAction.Drop, D(false, true, 0), "guest save actor outside its block");
                T.Eq(OrphanAction.Register, D(false, true, 0, inBlock: true), "guest actor the spawn hook missed");
                T.Eq(OrphanAction.Drop, D(false, true, 0, tomb: true, inBlock: true), "guest native leftover of a destroyed actor");
                T.Eq(OrphanAction.Keep, D(false, true, 0, prot: true), "irreversible capture in flight");
                T.Eq(OrphanAction.Keep, D(true, true, 0, player: true));
                var c = new OrphanClock(); T.Eq(5.0, c.FirstSeen(1, 5)); T.Eq(5.0, c.FirstSeen(1, 9)); c.Retain(new HashSet<long>()); T.Eq(9.0, c.FirstSeen(1, 9), "resolved orphans restart their grace");
            });
        }
    }
}

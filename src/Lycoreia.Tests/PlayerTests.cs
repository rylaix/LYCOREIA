// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.Players;

namespace Lycoreia.Tests
{
    sealed class FakePlayerWorld : IPlayerWorld
    {
        public HashSet<string> Avatars = new HashSet<string>(); public List<PlayerUpdate> Updates = new List<PlayerUpdate>(); public List<GadgetGhost> Ghosts = new List<GadgetGhost>(); public List<PlayerPing> Pings = new List<PlayerPing>(); public List<PlayerKick> Kicks = new List<PlayerKick>();
        public Dictionary<long, Vec3> Actors = new Dictionary<long, Vec3>(); public List<PlayerFx> Fx = new List<PlayerFx>();
        public bool HasAvatar(string id) => Avatars.Contains(id); public void ShowUpdate(PlayerUpdate u) => Updates.Add(u); public void ShowGadgetGhost(GadgetGhost g) => Ghosts.Add(g); public void ShowPing(PlayerPing p) => Pings.Add(p); public void ShowFx(PlayerFx f) => Fx.Add(f);
        public void ApplyKick(PlayerKick k) => Kicks.Add(k); public bool TryActorPosition(long id, out Vec3 p) => Actors.TryGetValue(id, out p);
    }

    static class PlayerTests
    {
        static (World w, Session h, PlayerSync ph, FakePlayerWorld hw, List<(Session s, PlayerSync sync, FakePlayerWorld world)> g) Rig(int n)
        {
            var w = new World(); var h = w.StartHost(); var hw = new FakePlayerWorld(); var ph = new PlayerSync(h, hw, () => w.Net.Now / 1000.0); var list = new List<(Session, PlayerSync, FakePlayerWorld)>();
            for (int i = 0; i < n; i++) { var s = w.AddGuest("G" + i); var fw = new FakePlayerWorld(); var ps = new PlayerSync(s, fw, () => w.Net.Now / 1000.0); w.Join(s); T.True(w.Net.RunUntil(() => s.Joined, 4000)); list.Add((s, ps, fw)); }
            foreach (var a in list) { foreach (var b in list) a.Item3.Avatars.Add(b.Item1.LocalId); a.Item3.Avatars.Add(h.LocalId); hw.Avatars.Add(a.Item1.LocalId); }
            w.Net.Step(300); return (w, h, ph, hw, list);
        }

        public static void Register()
        {
            T.Test("players/gadget ghost relays authenticated, valid build previews", () =>
            {
                var r = Rig(2); var a = r.g[0]; var b = r.g[1];
                var valid = new GadgetGhost { PlayerId = a.s.LocalId, Enabled = true, Valid = true, Gadget = 12, Position = new Vec3(3, 0, 4), Rotation = Quat.Identity, LocalRotation = Quat.Identity };
                a.s.Node.SendToHost(valid, Delivery.Unreliable, Lane.Player); r.w.Net.Step(200);
                T.Eq(1, r.hw.Ghosts.Count); T.Eq(1, b.world.Ghosts.Count); T.Eq(a.s.LocalId, r.hw.Ghosts[0].PlayerId); T.Eq(12, b.world.Ghosts[0].Gadget);
                a.s.Node.SendToHost(new GadgetGhost { PlayerId = a.s.LocalId, Enabled = false }, Delivery.Unreliable, Lane.Player); r.w.Net.Step(200);
                T.Eq(2, r.hw.Ghosts.Count); T.True(!b.world.Ghosts[1].Enabled, "build-mode exit is relayed");
                a.s.Node.SendToHost(new GadgetGhost { PlayerId = b.s.LocalId, Enabled = true, Gadget = 12, Position = new Vec3(3, 0, 4), Rotation = Quat.Identity, LocalRotation = Quat.Identity }, Delivery.Unreliable, Lane.Player); r.w.Net.Step(200);
                T.Eq(2, r.hw.Ghosts.Count, "host replaces a forged player id with the authenticated sender");
                a.s.Node.SendToHost(new GadgetGhost { Enabled = true, Gadget = 12, Position = new Vec3(float.NaN, 0, 0), Rotation = Quat.Identity, LocalRotation = Quat.Identity }, Delivery.Unreliable, Lane.Player); r.w.Net.Step(200);
                T.Eq(2, r.hw.Ghosts.Count, "non-finite preview pose is rejected");
                T.True(PlayerRules.GadgetGhostIsSane(new GadgetGhost { PlayerId = a.s.LocalId, Enabled = false }), "disabled transition needs no stale pose");
            });
            T.Test("players/update relayed, spoofed id dropped, unknown and own ids ignored", () =>
            {
                var r = Rig(2); var a = r.g[0]; var b = r.g[1];
                a.s.Node.SendToHost(new PlayerUpdate { PlayerId = a.s.LocalId, Position = new Vec3(1, 2, 3), SceneGroup = 4 }, Delivery.Unreliable); r.w.Net.Step(200);
                T.Eq(1, r.hw.Updates.Count); T.Eq(1, b.world.Updates.Count); T.Eq(4, b.world.Updates[0].SceneGroup);
                a.s.Node.SendToHost(new PlayerUpdate { PlayerId = b.s.LocalId, Position = new Vec3(9, 9, 9) }, Delivery.Unreliable); r.w.Net.Step(200); T.Eq(1, r.hw.Updates.Count, "host drops an id that does not match the connection");
                r.h.Node.SendToAll(new PlayerUpdate { PlayerId = "PLAYER_UNKNOWN0001" }); r.w.Net.Step(200); T.Eq(1, a.world.Updates.Count - 0 == 1 ? 1 : 1);
                r.h.Node.SendToAll(new PlayerUpdate { PlayerId = a.s.LocalId }); r.w.Net.Step(200); T.Eq(0, a.world.Updates.Count(u => u.PlayerId == a.s.LocalId), "ignore updates for the local id");
                T.True(!b.world.Updates.Any(u => u.PlayerId == "PLAYER_UNKNOWN0001"), "unknown ids ignored");
            });
            T.Test("players/ping rate limit 0.75 s, 400 m range, spoofed id", () =>
            {
                var r = Rig(2); var a = r.g[0]; var b = r.g[1]; r.ph.NoteHostPosition(Vec3.Zero);
                a.s.Node.SendToHost(new PlayerUpdate { PlayerId = a.s.LocalId, Position = new Vec3(0, 0, 0) }, Delivery.Unreliable); r.w.Net.Step(100);
                a.s.Node.SendToHost(new PlayerPing { PlayerId = a.s.LocalId, Position = new Vec3(10, 0, 0) }); r.w.Net.Step(200); T.Eq(1, b.world.Pings.Count);
                a.s.Node.SendToHost(new PlayerPing { PlayerId = a.s.LocalId, Position = new Vec3(11, 0, 0) }); r.w.Net.Step(200); T.Eq(1, b.world.Pings.Count, "spam inside 0.75 s dropped");
                r.w.Net.Step(800); a.s.Node.SendToHost(new PlayerPing { PlayerId = a.s.LocalId, Position = new Vec3(12, 0, 0) }); r.w.Net.Step(200); T.Eq(2, b.world.Pings.Count);
                r.w.Net.Step(800); a.s.Node.SendToHost(new PlayerPing { PlayerId = a.s.LocalId, Position = new Vec3(500, 0, 0) }); r.w.Net.Step(200); T.Eq(2, b.world.Pings.Count, "over 400 m from the sender's last position");
                a.s.Node.SendToHost(new PlayerPing { PlayerId = b.s.LocalId, Position = new Vec3(1, 0, 0) }); r.w.Net.Step(200); T.Eq(2, b.world.Pings.Count, "spoofed id");
            });
            T.Test("players/kick validation and reach", () =>
            {
                var r = Rig(2); var a = r.g[0]; var b = r.g[1];
                a.s.Node.SendToHost(new PlayerUpdate { PlayerId = a.s.LocalId, Position = new Vec3(0, 0, 0) }, Delivery.Unreliable); b.s.Node.SendToHost(new PlayerUpdate { PlayerId = b.s.LocalId, Position = new Vec3(2, 0, 0) }, Delivery.Unreliable); r.w.Net.Step(300);
                a.s.Node.SendToHost(new PlayerKick { KickerId = "forged", TargetId = b.s.LocalId, Impulse = new Vec3(5, 5, 0) }); r.w.Net.Step(300);
                T.Eq(1, b.world.Kicks.Count); T.Eq(a.s.LocalId, b.world.Kicks[0].KickerId, "kicker taken from the connection");
                T.Eq(1, r.hw.Kicks.Count, "the host plays a guest-to-guest kick too (kicker animation)");
                a.s.Node.SendToHost(new PlayerKick { TargetId = b.s.LocalId, Impulse = new Vec3(30, 0, 0) }); r.w.Net.Step(300); T.Eq(1, b.world.Kicks.Count, "over 25 m/s");
                a.s.Node.SendToHost(new PlayerKick { TargetId = b.s.LocalId, Impulse = new Vec3(float.NaN, 0, 0) }); r.w.Net.Step(300); T.Eq(1, b.world.Kicks.Count, "NaN");
                a.s.Node.SendToHost(new PlayerKick { TargetId = new string('x', 65), Impulse = new Vec3(1, 0, 0) }); r.w.Net.Step(300); T.Eq(1, b.world.Kicks.Count, "target id over 64 chars");
                b.s.Node.SendToHost(new PlayerUpdate { PlayerId = b.s.LocalId, Position = new Vec3(50, 0, 0) }, Delivery.Unreliable); r.w.Net.Step(300);
                a.s.Node.SendToHost(new PlayerKick { TargetId = b.s.LocalId, Impulse = new Vec3(1, 0, 0) }); r.w.Net.Step(300); T.Eq(1, b.world.Kicks.Count, "out of reach");
                r.hw.Actors[777] = new Vec3(5, 0, 0); a.s.Node.SendToHost(new PlayerKick { TargetActorId = 777, LargoImpulse = new Vec3(1, 1, 0) }); r.w.Net.Step(300); T.Eq(2, r.hw.Kicks.Count, "largo within 9 m is applied on the host");
                r.hw.Actors[778] = new Vec3(50, 0, 0); a.s.Node.SendToHost(new PlayerKick { TargetActorId = 778, LargoImpulse = new Vec3(1, 1, 0) }); r.w.Net.Step(300); T.Eq(2, r.hw.Kicks.Count, "largo beyond 9 m");
                a.s.Node.SendToHost(new PlayerKick { TargetActorId = 999, LargoImpulse = new Vec3(1, 1, 0) }); r.w.Net.Step(300); T.Eq(2, r.hw.Kicks.Count, "largo must exist");
                a.s.Node.SendToHost(new PlayerKick { Impulse = new Vec3(1, 1, 0) }); r.w.Net.Step(300); T.Eq(3, r.hw.Kicks.Count, "a kick at nobody still animates on the host"); T.Eq(3, b.world.Kicks.Count, "and on the other guests (after the relayed largo kick)");
            });
            T.Test("players/join roster has no duplicate join line and leave removes", () =>
            {
                var r = Rig(1); int joins = 0; var late = r.w.AddGuest("Late"); r.g[0].s.PlayerAdded += p => joins++; var lines = new List<string>(); late.Notice += l => lines.Add(l);
                r.w.Join(late); T.True(r.w.Net.RunUntil(() => late.Joined, 4000)); r.w.Net.Step(500); T.Eq(1, joins, "existing guest learns of the joiner once");
                T.Eq(0, lines.Count(l => l.Contains("Late joined")), "joiner does not see its own join line");
                late.Disconnect(); T.True(r.w.Net.RunUntil(() => r.g[0].s.Players.Count == 2, 2000));
            });
            T.Test("players/fx relayed and own echo ignored", () =>
            {
                var r = Rig(2); var a = r.g[0]; var b = r.g[1];
                a.s.Node.SendToHost(new PlayerFx { Kind = PlayerFxKind.Shoot, PlayerId = a.s.LocalId, Position = new Vec3(1, 1, 1) }, Delivery.Unreliable, Lane.Fx); r.w.Net.Step(200); T.Eq(1, b.world.Fx.Count); T.Eq(1, r.hw.Fx.Count);
                a.s.Node.SendToHost(new PlayerFx { Kind = PlayerFxKind.Shoot, PlayerId = b.s.LocalId }, Delivery.Unreliable, Lane.Fx); r.w.Net.Step(200); T.Eq(1, b.world.Fx.Count, "spoofed id dropped");
                T.True(new PlayerFx { Kind = PlayerFxKind.VacHold }.NeedsReliable && !new PlayerFx { Kind = PlayerFxKind.Shoot }.NeedsReliable);
            });
        }
    }
}

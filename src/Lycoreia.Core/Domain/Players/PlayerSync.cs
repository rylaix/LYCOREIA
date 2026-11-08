// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using Lycoreia.Domain;
using Lycoreia.Net;
using Lycoreia.Net.Messages;

namespace Lycoreia.Players
{
    public interface IPlayerWorld
    {
        bool HasAvatar(string playerId);
        void ShowUpdate(PlayerUpdate u);
        void ShowGadgetGhost(GadgetGhost g);
        void ShowPing(PlayerPing p);
        void ShowFx(PlayerFx fx);
        void ApplyKick(PlayerKick k);
        bool TryActorPosition(long actorId, out Vec3 position);
    }

    /// <summary>Pure validation rules for pings and kicks.</summary>
    public static class PlayerRules
    {
        public const double PingMinGapSeconds = 0.75; public const float PingMaxDistance = 400f;
        public const float MaxKickImpulse = 25f, KickReachHorizontal = 2.8f, KickLatencySlack = 6f, LargoReach = 9f; public const int MaxIdLength = 64;

        public static bool Finite(Vec3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
        public static float Length(Vec3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        public static float Horizontal(Vec3 a, Vec3 b) { float x = a.X - b.X, z = a.Z - b.Z; return MathF.Sqrt(x * x + z * z); }
        public static float Distance(Vec3 a, Vec3 b) => Length(new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z));

        /// <summary>All peers: reject non-finite values, impulse over 25 m/s and over-long target ids.</summary>
        public static bool KickIsSane(PlayerKick k)
            => Finite(k.Impulse) && Finite(k.LargoImpulse) && Length(k.Impulse) <= MaxKickImpulse && Length(k.LargoImpulse) <= MaxKickImpulse && (k.TargetId ?? "").Length <= MaxIdLength;

        public static bool GadgetGhostIsSane(GadgetGhost g)
        {
            if (g == null || (g.PlayerId ?? "").Length > MaxIdLength) return false;
            if (!g.Enabled) return true;
            if (g.Gadget <= 0 || !Finite(g.Position) || !Finite(g.Rotation) || !Finite(g.LocalRotation)) return false;
            float n = g.Rotation.X * g.Rotation.X + g.Rotation.Y * g.Rotation.Y + g.Rotation.Z * g.Rotation.Z + g.Rotation.W * g.Rotation.W;
            float ln = g.LocalRotation.X * g.LocalRotation.X + g.LocalRotation.Y * g.LocalRotation.Y + g.LocalRotation.Z * g.LocalRotation.Z + g.LocalRotation.W * g.LocalRotation.W;
            return n >= 0.25f && n <= 4f && ln >= 0.25f && ln <= 4f;
        }

        static bool Finite(Quat q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);

        public static bool PlayerKickInReach(Vec3 kicker, Vec3 target) => Horizontal(kicker, target) <= KickReachHorizontal + KickLatencySlack;
        public static bool LargoKickInReach(bool actorExists, Vec3 kicker, Vec3 actor) => actorExists && Distance(kicker, actor) <= LargoReach;
    }

    /// <summary>Player update, ping, kick, FX and palette relays with host-side validation.</summary>
    public sealed class PlayerSync
    {
        readonly Session _s; readonly IPlayerWorld _w; readonly Func<double> _now;
        readonly Dictionary<string, Vec3> _lastPos = new Dictionary<string, Vec3>(); readonly Dictionary<string, double> _lastPing = new Dictionary<string, double>();
        public int RejectedPings, RejectedKicks;

        public PlayerSync(Session s, IPlayerWorld w, Func<double> nowSeconds)
        {
            _s = s; _w = w; _now = nowSeconds; var n = s.Node;
            n.On<PlayerUpdate>(Side.Host, (c, m) => { if (!HostAuthority.Require(c)) return Verdict.Reject; _lastPos[c.SenderId] = m.Position; _w.ShowUpdate(m); return Verdict.Relay; });
            n.On<PlayerUpdate>(Side.Guest, (c, m) => { if (m.PlayerId == s.LocalId || !_w.HasAvatar(m.PlayerId)) return; _w.ShowUpdate(m); });
            n.On<PlayerPing>(Side.Host, HostPing); n.On<PlayerPing>(Side.Guest, (c, m) => { if (m.PlayerId != s.LocalId) _w.ShowPing(m); });
            n.On<PlayerKick>(Side.Host, HostKick); n.On<PlayerKick>(Side.Guest, (c, m) => { if (m.KickerId == s.LocalId || !PlayerRules.KickIsSane(m)) return; _w.ApplyKick(m); });
            n.On<PlayerFx>(Side.Both, (c, m) => { if (m.PlayerId != s.LocalId) _w.ShowFx(m); return c.OnHost ? Verdict.Relay : Verdict.Accept; });
            n.On<PlayerVacPalette>(Side.Both, (c, m) => c.OnHost ? Verdict.Relay : Verdict.Accept);
            n.On<GadgetGhost>(Side.Host, HostGadgetGhost);
            n.On<GadgetGhost>(Side.Guest, (c, m) => { if (!string.IsNullOrEmpty(m.PlayerId) && m.PlayerId != s.LocalId && PlayerRules.GadgetGhostIsSane(m) && _w.HasAvatar(m.PlayerId)) _w.ShowGadgetGhost(m); });
            n.On<VacSuctionPose>(Side.Both, (c, m) => c.OnHost ? Verdict.Relay : Verdict.Accept);
            n.On<MovementSound>(Side.Both, (c, m) => c.OnHost ? Verdict.Relay : Verdict.Accept);
            s.PlayerRemoved += p => { _lastPos.Remove(p.Id); _lastPing.Remove(p.Id); };
        }

        public void PublishGadgetGhost(GadgetGhost message)
        {
            if (message == null || _s.LocalId == null || !PlayerRules.GadgetGhostIsSane(message)) return;
            message.PlayerId = _s.LocalId;
            _s.Node.SendAuto(message);
        }

        Verdict HostGadgetGhost(Ctx c, GadgetGhost m)
        {
            if (!HostAuthority.Require(c) || !PlayerRules.GadgetGhostIsSane(m)) return Verdict.Reject;
            string sender = c.SenderId;
            m.PlayerId = sender;
            if (m.Enabled && _lastPos.TryGetValue(sender, out var p) && PlayerRules.Distance(p, m.Position) > 80f) return Verdict.Reject;
            _w.ShowGadgetGhost(m);
            return Verdict.Relay;
        }

        public void NoteHostPosition(Vec3 p) { if (_s.LocalId != null) _lastPos[_s.LocalId] = p; }
        public bool TryLastPosition(string id, out Vec3 p) => _lastPos.TryGetValue(id, out p);

        Verdict HostPing(Ctx c, PlayerPing m)
        {
            if (!HostAuthority.Require(c)) return Verdict.Reject;
            string sender = c.SenderId; double now = _now();
            if (_lastPing.TryGetValue(sender, out var last) && now - last < PlayerRules.PingMinGapSeconds) { RejectedPings++; return Verdict.Reject; }
            if (!PlayerRules.Finite(m.Position) || (_lastPos.TryGetValue(sender, out var pos) && PlayerRules.Distance(pos, m.Position) > PlayerRules.PingMaxDistance)) { RejectedPings++; return Verdict.Reject; }
            _lastPing[sender] = now; if (sender != _s.LocalId) _w.ShowPing(m);
            return Verdict.Relay;
        }

        Verdict HostKick(Ctx c, PlayerKick m)
        {
            if (!HostAuthority.Require(c)) return Verdict.Reject;
            string sender = c.SenderId; m.KickerId = sender;                                  // sender always from the connection
            if (!PlayerRules.KickIsSane(m)) { RejectedKicks++; return Verdict.Reject; }
            _lastPos.TryGetValue(sender, out var kp);
            if (!string.IsNullOrEmpty(m.TargetId))
            {
                if (!_lastPos.TryGetValue(m.TargetId, out var tp) || !PlayerRules.PlayerKickInReach(kp, tp)) { RejectedKicks++; return Verdict.Reject; }
            }
            else if (m.TargetActorId != 0)
            {
                bool exists = _w.TryActorPosition(m.TargetActorId, out var ap);
                if (!PlayerRules.LargoKickInReach(exists, kp, ap)) { RejectedKicks++; return Verdict.Reject; }
            }
            // The host is a viewer too: it plays the kicker's animation for every accepted kick (the world decides whether the
            // host's own player is the target or owns the kicked largo).
            if (sender != _s.LocalId) _w.ApplyKick(m);
            return Verdict.Relay;
        }
    }
}

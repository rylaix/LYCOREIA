// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using Lycoreia.Domain;
using Lycoreia.Net;
using Lycoreia.Net.Messages;

namespace Lycoreia.Players
{
    public interface IBoostFxWorld
    {
        /// <summary>Plays the puff and sound at the position and the squash of the stood-on player (or the local nudge). Never throws.</summary>
        void PlayBoostFx(BoostFx fx);
    }

    /// <summary>Pure rules: finite values, a plausible strength, bounded ids, one effect per jumper per 0.2 s.</summary>
    public static class BoostFxRules
    {
        public const double MinGapSeconds = 0.2; public const float MaxStrength = 60f; public const int MaxIdLength = 64;

        public static bool IsSane(BoostFx m)
            => m != null && !string.IsNullOrEmpty(m.JumperId) && m.JumperId.Length <= MaxIdLength && (m.StoodOnId ?? "").Length <= MaxIdLength
               && PlayerRules.Finite(m.Position) && float.IsFinite(m.Strength) && m.Strength >= 0f && m.Strength <= MaxStrength;
    }

    /// <summary>Host-authoritative relay of <see cref="BoostFx"/>; counters feed the "boost-fx" diagnostics.</summary>
    public sealed class BoostFxSync
    {
        readonly Session _s; readonly IBoostFxWorld _w; readonly Func<double> _now;
        readonly Dictionary<string, double> _last = new Dictionary<string, double>();
        public int Sent, Received, Played, Rejected;
        /// <summary>(sent | received | played | rejected, detail) for diagnostics.</summary>
        public event Action<string, string> Trace;

        public BoostFxSync(Session s, IBoostFxWorld w, Func<double> nowSeconds)
        {
            _s = s; _w = w; _now = nowSeconds;
            s.Node.On<BoostFx>(Side.Host, HostIn);
            s.Node.On<BoostFx>(Side.Guest, (c, m) => { Received++; Trace?.Invoke("received", Describe(m)); if (m.JumperId == _s.LocalId || !BoostFxRules.IsSane(m) || !Gate(m.JumperId)) { Rejected++; return; } Play(m); });
            s.PlayerRemoved += p => _last.Remove(p.Id);
        }

        static string Describe(BoostFx m) => $"jumper={m?.JumperId}; on={m?.StoodOnId}; strength={m?.Strength:0.0}";

        bool Gate(string id)
        {
            double now = _now();
            if (_last.TryGetValue(id, out var last) && now - last < BoostFxRules.MinGapSeconds) return false;
            _last[id] = now; return true;
        }

        void Play(BoostFx m) { Played++; try { _w.PlayBoostFx(m); } catch { } Trace?.Invoke("played", Describe(m)); }

        Verdict HostIn(Ctx c, BoostFx m)
        {
            if (!HostAuthority.Require(c)) return Verdict.Reject;
            Received++; m.JumperId = c.SenderId;                                         // the jumper always comes from the connection
            Trace?.Invoke("received", Describe(m));
            if (!BoostFxRules.IsSane(m) || !Gate(m.JumperId)) { Rejected++; Trace?.Invoke("rejected", Describe(m)); return Verdict.Reject; }
            Play(m);                                                                    // the host is a viewer too
            return Verdict.Relay;
        }

        /// <summary>This seat boosted: the host broadcasts, a guest sends to the host. The caller plays its own effect.</summary>
        public void Publish(string stoodOnId, Vec3 position, float strength)
        {
            if (_s.LocalId == null) return;
            var m = new BoostFx { JumperId = _s.LocalId, StoodOnId = stoodOnId ?? "", Position = position, Strength = strength };
            if (!BoostFxRules.IsSane(m)) return;
            _s.Node.SendAuto(m); Sent++; Trace?.Invoke("sent", Describe(m));
        }
    }
}

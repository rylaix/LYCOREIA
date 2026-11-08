// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Lycoreia.Actors
{
    public readonly struct ArbiterCandidate
    {
        public readonly string PlayerId; public readonly float Distance; public readonly bool IsHost;
        public ArbiterCandidate(string playerId, float distance, bool isHost) { PlayerId = playerId; Distance = distance; IsHost = isHost; }
    }

    /// <summary>Everything the arbiter needs from the game world, so the rules run in unit tests.</summary>
    public interface IArbiterWorld
    {
        bool ActorExists(long actorId);
        string OwnerOf(long actorId);
        bool IsConnected(string playerId);
        bool WasReleasedByOwner(long actorId, string owner);
        bool HasCaptureReservation(long actorId);
        bool HostHoldsActor(long actorId);
        /// <summary>The owner reported that its vacuum holds or pulls the actor (any owner, from its transform samples).</summary>
        bool OwnerHoldsActor(long actorId);
        /// <summary>Produce still on its plant at the owner: only that owner's spawner simulates it.</summary>
        bool IsPinned(long actorId);
        /// <summary>Distance from the player's host-known avatar to the actor, or the fallback when unknown.</summary>
        float DistanceToActor(string playerId, long actorId, float fallback);
        string HostId { get; }
        void GrantOwnership(long actorId, string newOwner, string previousOwner);
    }

    /// <summary>Host-only ownership decisions with hysteresis. Everyone registers interest; only the host decides and grants.</summary>
    public sealed class OwnershipArbiter
    {
        public const float HoldSeconds = 1.5f, ChallengerRatio = 0.75f, InterestTtlSeconds = 3f, TickSeconds = 0.5f, TieEpsilon = 0.01f, OwnerEngagedRadius = 8f;
        public const int MaxInterestActors = 4096, MaxGrantRecords = 8192;

        struct Interest { public float ReportedDistance, LastSeen; }
        readonly Dictionary<long, Dictionary<string, Interest>> _interest = new Dictionary<long, Dictionary<string, Interest>>();
        readonly Dictionary<long, float> _grantedAt = new Dictionary<long, float>();
        readonly IArbiterWorld _w; float _lastTick = float.NegativeInfinity;
        public int TrackedActors => _interest.Count; public int GrantRecords => _grantedAt.Count;

        public OwnershipArbiter(IArbiterWorld world) { _w = world; }

        // ---- pure rules
        /// <summary>The player who should own the actor, or null to keep things as they are.</summary>
        public static string Decide(string owner, bool ownerConnected, bool released, bool engaged, float secondsSinceGrant, float ownerDistance, IReadOnlyList<ArbiterCandidate> candidates)
        {
            bool ownerFree = string.IsNullOrEmpty(owner) || !ownerConnected || released;
            if (!ownerFree && (engaged || secondsSinceGrant < HoldSeconds)) return null;
            bool found = false; ArbiterCandidate best = default;
            foreach (var c in candidates)
            {
                if (!ownerFree && string.Equals(c.PlayerId, owner, StringComparison.Ordinal)) continue;
                if (!found || IsBetter(c, best)) { best = c; found = true; }
            }
            if (!found) return null;
            if (ownerFree) return best.PlayerId;
            return best.Distance <= ownerDistance * ChallengerRatio ? best.PlayerId : null;
        }
        static bool IsBetter(ArbiterCandidate c, ArbiterCandidate best)
        {
            if (c.Distance < best.Distance - TieEpsilon) return true;
            return Math.Abs(c.Distance - best.Distance) <= TieEpsilon && c.IsHost && !best.IsHost;   // ties go to the host
        }

        // ---- interest bookkeeping
        public void RegisterInterest(string playerId, long actorId, float reportedDistance, float now)
        {
            if (actorId == 0 || string.IsNullOrEmpty(playerId)) return;
            if (!_interest.TryGetValue(actorId, out var per))
            {
                if (_interest.Count >= MaxInterestActors) { Purge(now); if (_interest.Count >= MaxInterestActors) return; }
                _interest[actorId] = per = new Dictionary<string, Interest>(2);
            }
            per[playerId] = new Interest { ReportedDistance = reportedDistance, LastSeen = now };
        }

        void Purge(float now)
        {
            List<long> emptied = null;
            foreach (var kv in _interest)
            {
                List<string> stale = null;
                foreach (var p in kv.Value) if (now - p.Value.LastSeen > InterestTtlSeconds || !_w.IsConnected(p.Key)) (stale ??= new List<string>()).Add(p.Key);
                if (stale != null) foreach (var s in stale) kv.Value.Remove(s);
                if (kv.Value.Count == 0) (emptied ??= new List<long>()).Add(kv.Key);
            }
            if (emptied != null) foreach (var id in emptied) _interest.Remove(id);
        }

        public void NoteGrant(long actorId, float now)
        {
            if (_grantedAt.Count >= MaxGrantRecords)
            {
                foreach (var id in _grantedAt.Where(kv => now - kv.Value > HoldSeconds).Select(kv => kv.Key).ToList()) _grantedAt.Remove(id);
                if (_grantedAt.Count >= MaxGrantRecords) _grantedAt.Clear();
            }
            _grantedAt[actorId] = now;
        }

        public void Reset() { _interest.Clear(); _grantedAt.Clear(); _lastTick = float.NegativeInfinity; }

        // ---- evaluation
        /// <summary>Called every frame; evaluates every 0.5 s.</summary>
        public void Tick(float now)
        {
            if (now - _lastTick < TickSeconds) return;
            _lastTick = now; Purge(now);
            foreach (var id in _interest.Keys.ToArray()) Evaluate(id, now);
        }

        /// <summary>A release triggers an immediate evaluation.</summary>
        public void EvaluateNow(long actorId, float now) => Evaluate(actorId, now);

        void Evaluate(long actorId, float now)
        {
            try
            {
                if (!_interest.TryGetValue(actorId, out var per) || per.Count == 0) return;
                if (!_w.ActorExists(actorId)) { _interest.Remove(actorId); return; }
                // Reservation is a host transaction, independent of whether its
                // old simulator just unloaded or disconnected.
                if (_w.HasCaptureReservation(actorId)) return;
                string owner = _w.OwnerOf(actorId) ?? "";
                bool ownerConnected = owner.Length > 0 && _w.IsConnected(owner);
                if (ownerConnected && _w.IsPinned(actorId)) { _interest.Remove(actorId); return; }   // attached produce stays with its plant's simulator while that peer simulates it
                bool released = ownerConnected && _w.WasReleasedByOwner(actorId, owner);
                bool engaged = ownerConnected && (_w.HasCaptureReservation(actorId) || (owner == _w.HostId && _w.HostHoldsActor(actorId)) || _w.OwnerHoldsActor(actorId));
                float since = _grantedAt.TryGetValue(actorId, out var g) ? now - g : float.MaxValue;
                float ownerDistance = ownerConnected ? _w.DistanceToActor(owner, actorId, float.PositiveInfinity) : float.PositiveInfinity;
                // a connected owner near its actor may be holding or have just thrown it; an unknown position is not proof of distance
                if (ownerConnected && !released && (float.IsPositiveInfinity(ownerDistance) || ownerDistance < OwnerEngagedRadius)) engaged = true;
                var cands = new List<ArbiterCandidate>(per.Count);
                foreach (var kv in per)
                {
                    if (now - kv.Value.LastSeen > InterestTtlSeconds || !_w.IsConnected(kv.Key)) continue;
                    // A released actor must not return to its sleeping simulator from stale interest.
                    if (released && kv.Key == owner) continue;
                    float distance = _w.DistanceToActor(kv.Key, actorId, kv.Value.ReportedDistance);
                    if (!float.IsFinite(distance) || distance > InterestPass.NearRange) continue;
                    cands.Add(new ArbiterCandidate(kv.Key, distance, kv.Key == _w.HostId));
                }
                string winner = Decide(owner, ownerConnected, released, engaged, since, ownerDistance, cands);
                if (winner == null) return;
                if (string.Equals(winner, owner, StringComparison.Ordinal) && !released) return;
                _interest.Remove(actorId);                      // interest is cleared after a grant
                NoteGrant(actorId, now);
                _w.GrantOwnership(actorId, winner, owner);
            }
            catch (Exception e) { Log.Warn($"arbiter evaluate {actorId} failed: {e.Message}"); }
        }
    }
}

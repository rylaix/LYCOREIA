// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Lycoreia.Net;

namespace Lycoreia.Actors
{
    /// <summary>
    /// Interpolation of one replica from the owner's samples. Samples are buffered with their arrival time and the replica is shown
    /// <see cref="Delay"/> behind the newest one, moving along a Hermite curve through the real samples (positions plus the owner's
    /// velocities), so fast arcs such as a thrown slime keep their shape instead of being cut short by chasing the newest sample.
    /// Past the newest sample it extrapolates with the velocity for at most <see cref="MaxExtrapolate"/>, then settles on that sample
    /// (stops being rewritten) until the next one arrives.
    /// </summary>
    public sealed class ReplicaTrack
    {
        public const double Delay = 0.1, MaxExtrapolate = 0.25, MaxHermiteGap = 0.3, KeepSeconds = 1.0; public const int MaxSamples = 16;
        struct Sample { public double T; public Vec3 P, V; public Quat Q; }
        readonly List<Sample> _s = new List<Sample>(MaxSamples);
        bool _has, _settled = true; Vec3 _cur; Quat _curQ = Quat.Identity;
        public bool Settled => _settled;
        public bool Has => _has;
        public int Buffered => _s.Count;

        public void Push(double now, Vec3 pos, Quat rot, Vec3 vel)
        {
            if (!Finite(pos) || !Finite(vel)) return;
            if (_settled || _s.Count == 0)
            {
                // resuming after a rest (or the first sample): start from where the replica is shown, so it blends instead of jumping
                _s.Clear(); if (_has) _s.Add(new Sample { T = now - Delay, P = _cur, Q = _curQ, V = Vec3.Zero });
                else { _cur = pos; _curQ = rot; }
            }
            if (_s.Count > 0 && now <= _s[_s.Count - 1].T) now = _s[_s.Count - 1].T + 1e-4;          // same-frame bursts keep their order
            _s.Add(new Sample { T = now, P = pos, Q = rot, V = vel });
            while (_s.Count > MaxSamples || (_s.Count > 2 && _s[1].T < now - KeepSeconds)) _s.RemoveAt(0);
            _has = true; _settled = false;
        }

        /// <summary>Pose at <paramref name="now"/>; false when settled (nothing to write).</summary>
        public bool Evaluate(double now, out Vec3 pos, out Quat rot)
        {
            pos = _cur; rot = _curQ; if (!_has || _settled || _s.Count == 0) return false;
            double rt = now - Delay; var last = _s[_s.Count - 1];
            if (rt >= last.T)
            {
                double extra = rt - last.T; bool still = Math.Abs(last.V.X) + Math.Abs(last.V.Y) + Math.Abs(last.V.Z) < 0.001f;
                if (extra >= MaxExtrapolate || still) { pos = last.P; rot = last.Q; _settled = true; _s.Clear(); }
                else { float e = (float)extra; pos = new Vec3(last.P.X + last.V.X * e, last.P.Y + last.V.Y * e, last.P.Z + last.V.Z * e); rot = last.Q; }
            }
            else if (rt <= _s[0].T) { pos = _s[0].P; rot = _s[0].Q; }
            else
            {
                int i = 0; while (i < _s.Count - 2 && _s[i + 1].T <= rt) i++;
                var a = _s[i]; var b = _s[i + 1]; double dt = b.T - a.T; float u = (float)((rt - a.T) / dt);
                pos = dt <= MaxHermiteGap ? Hermite(a.P, a.V, b.P, b.V, (float)dt, u) : Lerp(a.P, b.P, u);
                rot = NLerp(a.Q, b.Q, u);
                while (i-- > 0) _s.RemoveAt(0);                                                   // samples before the segment are done
            }
            _cur = pos; _curQ = rot; return true;
        }

        /// <summary>The game moved the replica itself (spawn, snapshot, region load): adopt that pose without blending.</summary>
        public void Teleport(Vec3 pos, Quat rot) { _cur = pos; _curQ = rot; _s.Clear(); _has = true; _settled = true; }

        /// <summary>Cubic Hermite between two samples with their velocities (tangents scaled by the gap).</summary>
        public static Vec3 Hermite(Vec3 p0, Vec3 v0, Vec3 p1, Vec3 v1, float dt, float u)
        {
            float u2 = u * u, u3 = u2 * u, h00 = 2 * u3 - 3 * u2 + 1, h10 = u3 - 2 * u2 + u, h01 = -2 * u3 + 3 * u2, h11 = u3 - u2;
            return new Vec3(h00 * p0.X + h10 * dt * v0.X + h01 * p1.X + h11 * dt * v1.X, h00 * p0.Y + h10 * dt * v0.Y + h01 * p1.Y + h11 * dt * v1.Y, h00 * p0.Z + h10 * dt * v0.Z + h01 * p1.Z + h11 * dt * v1.Z);
        }

        static bool Finite(Vec3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
        public static Vec3 Lerp(Vec3 a, Vec3 b, float t) => new Vec3(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
        public static Quat NLerp(Quat a, Quat b, float t)
        {
            float dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W; float s = dot < 0 ? -1f : 1f;
            float x = a.X + (b.X * s - a.X) * t, y = a.Y + (b.Y * s - a.Y) * t, z = a.Z + (b.Z * s - a.Z) * t, w = a.W + (b.W * s - a.W) * t;
            float n = MathF.Sqrt(x * x + y * y + z * z + w * w); if (n < 1e-6f) return b; return new Quat(x / n, y / n, z / n, w / n);
        }
    }

    /// <summary>All replica tracks; ids flagged as locally predicted (vacuum pull) are skipped so the local physics keeps control.</summary>
    public sealed class ReplicaMotion
    {
        readonly Dictionary<long, ReplicaTrack> _tracks = new Dictionary<long, ReplicaTrack>(); readonly HashSet<long> _skip = new HashSet<long>();
        public int Count => _tracks.Count;
        public void Push(long id, double now, Vec3 pos, Quat rot, Vec3 vel) { if (!_tracks.TryGetValue(id, out var t)) _tracks[id] = t = new ReplicaTrack(); t.Push(now, pos, rot, vel); }
        public void Teleport(long id, Vec3 pos, Quat rot) { if (!_tracks.TryGetValue(id, out var t)) _tracks[id] = t = new ReplicaTrack(); t.Teleport(pos, rot); }
        public void Remove(long id) { _tracks.Remove(id); _skip.Remove(id); }
        public void Clear() { _tracks.Clear(); _skip.Clear(); }
        public void SetPredicted(long id, bool on) { if (on) _skip.Add(id); else _skip.Remove(id); }
        public bool IsPredicted(long id) => _skip.Contains(id);
        /// <summary>Runs one frame; <paramref name="write"/> is called only for moving, non-predicted replicas.</summary>
        public int Step(double now, Func<long, bool> isReplica, Action<long, Vec3, Quat> write)
        {
            int n = 0;
            foreach (var kv in _tracks)
            {
                if (_skip.Contains(kv.Key) || kv.Value.Settled || !isReplica(kv.Key)) continue;
                if (kv.Value.Evaluate(now, out var p, out var q)) { write(kv.Key, p, q); n++; }
            }
            return n;
        }
    }

    /// <summary>Owner-side transform sender decision: changed since the last send, else a keep-alive every 10th tick staggered by id.</summary>
    public sealed class OwnerSampler
    {
        public const float PosEps = 0.01f, VelEps = 0.05f, RotDot = 0.99999f;
        readonly Dictionary<long, (Vec3 p, Quat q, Vec3 v)> _last = new Dictionary<long, (Vec3, Quat, Vec3)>();
        public bool ShouldSend(long id, Vec3 p, Quat q, Vec3 v, long tick)
        {
            bool changed = true;
            if (_last.TryGetValue(id, out var l))
            {
                float d = Math.Abs(p.X - l.p.X) + Math.Abs(p.Y - l.p.Y) + Math.Abs(p.Z - l.p.Z);
                float dv = Math.Abs(v.X - l.v.X) + Math.Abs(v.Y - l.v.Y) + Math.Abs(v.Z - l.v.Z);
                float dot = Math.Abs(q.X * l.q.X + q.Y * l.q.Y + q.Z * l.q.Z + q.W * l.q.W);
                changed = d > PosEps || dv > VelEps || dot < RotDot;
            }
            bool send = ActorSync.ShouldSendTransform(changed, tick, id);
            if (send) _last[id] = (p, q, v);
            return send;
        }
        public void Forget(long id) => _last.Remove(id);
        public void Clear() => _last.Clear();
    }

    public struct InterestCandidate
    {
        public long Id; public bool Unsimulated, OwnedByOther, OwnerSilent; public float MyDistance, OwnerDistance;
    }

    /// <summary>Proximity pass (4.2): at most 128 interests per pass with a rotating start; hopeless interest is never sent.</summary>
    public sealed class InterestPass
    {
        public const int MaxPerPass = 128; public const float NearRange = 60f, Ratio = 0.75f;
        int _start;
        public List<(long id, float distance)> Select(IReadOnlyList<InterestCandidate> all)
        {
            var res = new List<(long, float)>(); int n = all.Count; if (n == 0) return res;
            _start = ((_start % n) + n) % n; int i = _start, seen = 0;
            for (; seen < n && res.Count < MaxPerPass; seen++, i = (i + 1) % n)
            {
                var c = all[i];
                if (!float.IsFinite(c.MyDistance) || c.MyDistance > NearRange) continue;
                if (c.Unsimulated) { res.Add((c.Id, c.MyDistance)); continue; }
                if (!c.OwnedByOther || c.MyDistance > NearRange) continue;
                bool ownerKnown = float.IsFinite(c.OwnerDistance);
                if (ownerKnown ? c.MyDistance <= c.OwnerDistance * Ratio : c.OwnerSilent) res.Add((c.Id, c.MyDistance));   // an unknown owner position is never proof of distance
            }
            _start = i; return res;
        }
    }

    /// <summary>Owner silence (4.10): a per-actor random threshold of 5 to 11 s.</summary>
    public static class OwnerSilence
    {
        public const double Min = 5, Max = 11;
        public static double Threshold(long id) { unchecked { ulong h = (ulong)id * 0x9E3779B97F4A7C15UL; h ^= h >> 29; h *= 0xBF58476D1CE4E5B9UL; h ^= h >> 32; return Min + (Max - Min) * ((h & 0xFFFFFF) / (double)0x1000000); } }
        public static bool IsSilent(long id, double now, double heardAt) => heardAt > 0 && now - heardAt > Threshold(id);
    }

    /// <summary>Ranch bounds (4.7): loaded ranch cells' region boxes, expanded 4 m horizontally and 200 m vertically, refreshed every 10 s. No boxes = unknown.</summary>
    public sealed class RanchBoundsSet
    {
        readonly List<(Vec3 min, Vec3 max)> _boxes = new List<(Vec3, Vec3)>(); double _at = double.NegativeInfinity;
        public int Count => _boxes.Count;
        public bool NeedsRefresh(double now) => now - _at >= RanchProtectionConst.BoundsRefreshSeconds;
        public void Set(IEnumerable<(Vec3 center, Vec3 size)> regions, double now)
        {
            _at = now; _boxes.Clear();
            foreach (var (c, s) in regions)
            {
                float hx = s.X / 2 + RanchProtectionConst.BoundsPadHorizontal, hy = s.Y / 2 + RanchProtectionConst.BoundsPadVertical, hz = s.Z / 2 + RanchProtectionConst.BoundsPadHorizontal;
                _boxes.Add((new Vec3(c.X - hx, c.Y - hy, c.Z - hz), new Vec3(c.X + hx, c.Y + hy, c.Z + hz)));
            }
        }
        /// <summary>null = unknown (nothing loaded): callers stay safe (never refuse a spawn, never clean up a Conservatory group).</summary>
        public bool? Contains(Vec3 p)
        {
            if (_boxes.Count == 0) return null;
            foreach (var (a, b) in _boxes) if (p.X >= a.X && p.X <= b.X && p.Y >= a.Y && p.Y <= b.Y && p.Z >= a.Z && p.Z <= b.Z) return true;
            return false;
        }
        public void Clear() { _boxes.Clear(); _at = double.NegativeInfinity; }
    }
    static class RanchProtectionConst
    {
        public const float BoundsPadHorizontal = RanchProtection.BoundsPadHorizontal, BoundsPadVertical = RanchProtection.BoundsPadVertical; public const double BoundsRefreshSeconds = RanchProtection.BoundsRefreshSeconds;
    }

    /// <summary>Produce rules (4.11).</summary>
    public static class ProduceRules
    {
        public enum Phase { Unripe, Ripe, Edible, Rotten }
        public const double SweepSeconds = 1.0;
        /// <summary>Ripe, edible or rotten produce is always full size; only unripe produce on the tree is small.</summary>
        public static bool ShouldBeFullSize(Phase phase, bool onTree) => !(phase == Phase.Unripe && onTree);
        /// <summary>The once-a-second sweep: loose, resting, non-unripe produce that is still small gets full size; vacuum-pulled or held-kinematic produce is skipped.</summary>
        public static bool SweepRestores(Phase phase, bool onTree, bool resting, bool tooSmall, bool beingPulled, bool heldKinematic)
            => tooSmall && !onTree && resting && phase != Phase.Unripe && !beingPulled && !heldKinematic;
        /// <summary>A frozen replica stores an effectively infinite next-cycle time; such a value must never be cached.</summary>
        public static bool IsCacheableTime(double t) => double.IsFinite(t) && t > 0 && t < 1e12;
        /// <summary>A produce scale from the owner only applies to a local object that really is produce (has a resource cycle).</summary>
        public static bool ScaleApplies(bool targetHasResourceCycle, float scale) => targetHasResourceCycle && float.IsFinite(scale) && scale > 0.01f && scale < 20f;
    }

    /// <summary>
    /// Plorts never grow natively: SlimeEat.Produce tweens a new plort from 0.01 to its spawn scale in 0.5 s, the vacuum only shrinks.
    /// A plort above its prefab scale is a mod-side corruption (issue4 "slime-sized plort"); it is put back and logged.
    /// </summary>
    public static class PlortScaleRule
    {
        public const float Tolerance = 1.25f;
        public static bool IsOversized(float scale, float prefabScale) => prefabScale > 0.001f && float.IsFinite(scale) && scale > prefabScale * Tolerance;
    }

    /// <summary>
    /// Ringtail day/night stone form (native TurnToStoneInDaylight.BecomeStatue spawns a statue actor and removes the slime's object,
    /// keeping its model; RingtailStatue.Unstatue re-creates the slime from that model and destroys the statue). Both run on every
    /// seat natively, so before 2026-11-12 every seat published its own statue and every statue copy re-created the slime: Ringtails
    /// multiplied each day/night cycle (issue3). Only the owner of the actor that transforms may run the transformation.
    /// </summary>
    public static class StatueRules
    {
        /// <summary><paramref name="tracked"/>: the actor has a table entry; untracked actors fall back to the generic behaviour authority.</summary>
        public static bool MayTransform(bool networked, bool tracked, bool locallyOwned, bool fallback) => !networked || (tracked ? locallyOwned : fallback);
    }

    /// <summary>
    /// A slime eats only food its own seat simulates. Native SlimeEat.EatAndProduce destroys the food (Destroyer.DestroyActor) and then
    /// produces plorts; for food owned by another seat that destroy is suppressed, so before 2026-11-12 the plorts appeared while
    /// the food (often a chicken shot into a corral by the other player) stayed, and was eaten again and again (issue4 chicken burst,
    /// actor table +800 in 13 min). Untracked food (local only) is the native case.
    /// </summary>
    public static class FoodAuthority
    {
        public enum Verdict { Eat, EatHostConsumes, Skip }
        public static bool MayEat(bool networked, bool foodTracked, bool foodLocallyOwned) => !networked || !foodTracked || foodLocallyOwned;
        /// <summary>The host is the authority: its slime may eat a guest's food and removes it for everyone; a guest's slime waits for ownership.</summary>
        public static Verdict Decide(bool networked, bool isHost, bool foodTracked, bool foodLocallyOwned, bool foodReserved)
            => MayEat(networked, foodTracked, foodLocallyOwned) ? Verdict.Eat : isHost && !foodReserved ? Verdict.EatHostConsumes : Verdict.Skip;
    }

    /// <summary>Census line for latest.log: the largest types and the ones that grew most since the previous line.</summary>
    public static class TypeGrowth
    {
        public static string Describe(IReadOnlyDictionary<string, int> now, IReadOnlyDictionary<string, int> before, int top)
        {
            string Delta(string k) { int d = now[k] - (before != null && before.TryGetValue(k, out var b) ? b : 0); return before == null || d == 0 ? "" : d > 0 ? "(+" + d + ")" : "(" + d + ")"; }
            var largest = now.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(top).Select(kv => kv.Key + "=" + kv.Value + Delta(kv.Key));
            string text = "top " + string.Join(", ", largest);
            if (before == null) return text;
            var grew = now.Select(kv => (kv.Key, d: kv.Value - (before.TryGetValue(kv.Key, out var b) ? b : 0))).Where(x => x.d >= 5)
                          .OrderByDescending(x => x.d).ThenBy(x => x.Key, StringComparer.Ordinal).Take(top).Select(x => x.Key + " +" + x.d).ToList();
            return grew.Count == 0 ? text : text + "; grew " + string.Join(", ", grew);
        }
    }

    /// <summary>Statue actor id to the slime it holds, learned from link messages (replica statue models carry no reference).</summary>
    public sealed class StatueLinkTable
    {
        public const int Cap = 4096;
        readonly Dictionary<long, long> _slimeOf = new Dictionary<long, long>();
        readonly Dictionary<long, long> _statueOf = new Dictionary<long, long>();
        public int Count => _slimeOf.Count;
        public void Note(long statueId, long slimeId)
        {
            if (statueId == 0 || slimeId == 0) return;
            if (_slimeOf.Count >= Cap && !_slimeOf.ContainsKey(statueId)) Clear();
            Forget(statueId); if (_statueOf.TryGetValue(slimeId, out var old)) _slimeOf.Remove(old);
            _slimeOf[statueId] = slimeId; _statueOf[slimeId] = statueId;
        }
        public bool TryGet(long statueId, out long slimeId) => _slimeOf.TryGetValue(statueId, out slimeId);
        /// <summary>The slime is currently held in stone form by a statue (its object must stay hidden on this seat).</summary>
        public bool IsStone(long slimeId) => _statueOf.ContainsKey(slimeId);
        public void Forget(long statueId) { if (_slimeOf.Remove(statueId, out var slime) && _statueOf.TryGetValue(slime, out var st) && st == statueId) _statueOf.Remove(slime); }
        public void Clear() { _slimeOf.Clear(); _statueOf.Clear(); }
        public IEnumerable<(long statue, long slime)> All() => _slimeOf.Select(kv => (kv.Key, kv.Value));
    }

    /// <summary>Per-actor cache of the real produce cycle time across a non-owner freeze; restored when ownership is gained.</summary>
    public sealed class ProduceCycleCache
    {
        readonly Dictionary<long, double> _t = new Dictionary<long, double>();
        public void Freeze(long id, double realTime) { if (ProduceRules.IsCacheableTime(realTime) && !_t.ContainsKey(id)) _t[id] = realTime; }
        /// <summary>Real time when cached, else <paramref name="now"/> (reset to now when the cache is invalid).</summary>
        public double Restore(long id, double now) { if (_t.Remove(id, out var t) && ProduceRules.IsCacheableTime(t)) return t; return now; }
        public bool Has(long id) => _t.ContainsKey(id);
        public void Clear() => _t.Clear();
    }

    /// <summary>Spawner batch trim and need checks (4.5) as pure decisions over counts the adapter measured.</summary>
    public static class SpawnerGate
    {
        public enum Reason { Ok, Offline, Ranch, NotLeader, Full, Rate }
        public const float CoopSlimeFactor = 0.84f;
        public static int CoopSlimeTarget(int cellTarget) => Math.Max(1, (int)MathF.Round(cellTarget * CoopSlimeFactor));

        /// <summary>Animal batch: leader and back-off gate, cap 14 within 45 m, per-spawner grants. Count 0 = nothing this round.</summary>
        public static int AnimalBatch(AnimalSpawnerGuard g, long spawnerId, int requested, int nearby, bool leader, bool backedOff, bool onRanch, bool networked, double now, out Reason why)
        {
            why = Reason.Ok; if (!networked || requested <= 0) { why = Reason.Offline; return requested; }
            if (onRanch) { why = Reason.Ranch; return requested; }
            if (!leader || backedOff) { why = Reason.NotLeader; return 0; }
            int n = g.Allowed(spawnerId, SpawnClass.Animal, requested, nearby, now, false, true);
            if (n < requested) why = AnimalSpawnerGuard.NearbyCap - nearby < requested ? Reason.Full : Reason.Rate; return n;
        }
        /// <summary>Slime batch: trim to what the cell still needs (co-op target, any owner), then the per-spawner grant.</summary>
        public static int SlimeBatch(AnimalSpawnerGuard g, long spawnerId, int requested, int nearby, int cellTarget, bool leader, bool backedOff, bool onRanch, bool networked, double now, out Reason why)
        {
            why = Reason.Ok; if (!networked || requested <= 0) { why = Reason.Offline; return requested; }
            if (onRanch) { why = Reason.Ranch; return requested; }
            if (!leader || backedOff) { why = Reason.NotLeader; return 0; }
            int n = Math.Min(requested, Math.Max(0, CoopSlimeTarget(cellTarget) - nearby)); if (n < requested) why = Reason.Full;
            int granted = g.Allowed(spawnerId, SpawnClass.Slime, n, nearby, now, false, true); if (granted < n) why = Reason.Rate; return granted;
        }
        /// <summary>Cell "needs more animals" check: false when out of turn, or every spawner is saturated.</summary>
        public static bool NeedsMoreAnimals(bool native, bool networked, bool onRanch, bool leader, bool backedOff, IEnumerable<int> nearbyPerSpawner)
        {
            if (!native || !networked || onRanch) return native; if (!leader || backedOff) return false;
            foreach (var n in nearbyPerSpawner) if (n < AnimalSpawnerGuard.NearbyCap) return true; return false;
        }
        /// <summary>Cell "needs more slimes" check.</summary>
        public static bool NeedsMoreSlimes(bool native, bool networked, bool onRanch, bool leader, bool backedOff, int nearby, int cellTarget)
        {
            if (!native || !networked || onRanch) return native; if (!leader || backedOff) return false;
            return GuestSlimeSpawnerGate.CanSpawn(nearby, cellTarget);
        }
    }
}

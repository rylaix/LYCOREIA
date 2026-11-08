// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Lycoreia.Net;

namespace Lycoreia.Actors
{
    public enum SpawnClass { Item, Slime, Animal }
    public enum SpawnDecision { Allow, RefusedRate, RefusedPile, RefusedWild }

    public interface ISpawnWorld
    {
        SpawnClass ClassOf(int typeId);
        bool IsRanch(Vec3 position);                                    // game's own ranch-cell test; false when unknown
        int CountSameTypeWithin(int typeId, Vec3 position, float radius);
        int CountWildSlimesAndAnimals();                                // off the ranch, any owner
        bool TryPlayerPosition(string playerId, out Vec3 position);
    }

    /// <summary>Host budget for a guest's non-gadget spawns: token bucket, pile caps, wild cap.</summary>
    public sealed class HostSpawnBudget
    {
        public const double RefillPerSecond = 10, Burst = 80, WildRecountSeconds = 2, LogEverySeconds = 10;
        public const float PileRadius = 30f, NozzleRadius = 6f;
        public const int PileAnimals = 20, PileSlimes = 30, PileItems = 120, WildCap = 900;

        readonly ISpawnWorld _w; readonly Func<double> _now;
        readonly Dictionary<string, (double tokens, double at)> _bucket = new Dictionary<string, (double, double)>();
        int _wild; double _wildAt = double.NegativeInfinity; double _lastLog = double.NegativeInfinity;
        public int Refusals { get; private set; }
        public Action<string> LogRefusal = m => Log.Warn(m);

        public HostSpawnBudget(ISpawnWorld w, Func<double> nowSeconds) { _w = w; _now = nowSeconds; }
        public void Reset() { _bucket.Clear(); _wildAt = double.NegativeInfinity; Refusals = 0; }

        public SpawnDecision Check(string sender, int typeId, Vec3 position, bool isVacShot, bool isRanchSpawn)
        {
            try
            {
                double now = _now();
                if (!_bucket.TryGetValue(sender, out var b)) b = (Burst, now);
                double tokens = Math.Min(Burst, b.tokens + (now - b.at) * RefillPerSecond);
                if (tokens < 1) { _bucket[sender] = (tokens, now); return Refuse(SpawnDecision.RefusedRate, sender, typeId); }
                _bucket[sender] = (tokens - 1, now);
                bool ranch = isRanchSpawn || _w.IsRanch(position);
                if (ranch) return SpawnDecision.Allow;                                  // ranch spawns skip the pile and wild caps
                var cls = _w.ClassOf(typeId);
                bool nearAvatar = _w.TryPlayerPosition(sender, out var pp) && Dist(pp, position) <= NozzleRadius;
                if (!nearAvatar)
                {
                    int cap = cls == SpawnClass.Animal ? PileAnimals : cls == SpawnClass.Slime ? PileSlimes : PileItems;
                    if (_w.CountSameTypeWithin(typeId, position, PileRadius) >= cap) return Refuse(SpawnDecision.RefusedPile, sender, typeId);
                }
                if (!isVacShot && cls != SpawnClass.Item)
                {
                    if (now - _wildAt >= WildRecountSeconds) { _wild = _w.CountWildSlimesAndAnimals(); _wildAt = now; }
                    if (_wild >= WildCap) return Refuse(SpawnDecision.RefusedWild, sender, typeId);
                    _wild++;
                }
                return SpawnDecision.Allow;
            }
            catch (Exception) { return SpawnDecision.Allow; }                            // if the check throws, allow the spawn
        }

        SpawnDecision Refuse(SpawnDecision d, string sender, int typeId)
        {
            Refusals++; double now = _now();
            if (now - _lastLog >= LogEverySeconds) { _lastLog = now; LogRefusal($"spawn refused ({d}) for {sender}, type {typeId}"); }
            return d;
        }
        public static float Dist(Vec3 a, Vec3 b) { float x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z; return MathF.Sqrt(x * x + y * y + z * z); }
    }

    /// <summary>Every peer computes the same wild-spawn leader: the host when in range, else the lowest player id in range.</summary>
    public static class SpawnLeader
    {
        public static string Leader(string hostId, bool hostInRange, IEnumerable<string> playersInRange)
        {
            if (hostInRange) return hostId;
            string best = null; foreach (var p in playersInRange) if (best == null || string.CompareOrdinal(p, best) < 0) best = p;
            return best;
        }
    }

    /// <summary>Guest-side back-off after the host deleted one of its fresh wild spawns.</summary>
    public sealed class SpawnBackoff
    {
        public const float Radius = 80f, MergeRadius = 10f; public const double FreshSeconds = 5, DurationSeconds = 60; public const int MaxRecent = 256;
        readonly List<(Vec3 pos, double until)> _zones = new List<(Vec3, double)>();
        readonly Dictionary<long, (Vec3 pos, double at, bool wild)> _recent = new Dictionary<long, (Vec3, double, bool)>();
        public int Zones => _zones.Count; public int Recent => _recent.Count;

        public void NoteLocalSpawn(long actorId, Vec3 pos, double now, bool wild)
        {
            PruneRecent(now);
            if (_recent.Count >= MaxRecent) { var oldest = _recent.OrderBy(kv => kv.Value.at).First().Key; _recent.Remove(oldest); }
            _recent[actorId] = (pos, now, wild);
        }
        void PruneRecent(double now) { foreach (var id in _recent.Where(kv => now - kv.Value.at > FreshSeconds).Select(kv => kv.Key).ToList()) _recent.Remove(id); }

        /// <summary>The host destroyed our actor. If it was a fresh wild spawn, start a back-off zone (unless one is within 10 m).</summary>
        public bool OnHostRefusal(long actorId, double now)
        {
            if (!_recent.TryGetValue(actorId, out var r) || !r.wild || now - r.at > FreshSeconds) return false;
            _recent.Remove(actorId);
            _zones.RemoveAll(z => z.until <= now);
            if (_zones.Any(z => HostSpawnBudget.Dist(z.pos, r.pos) <= MergeRadius)) return false;
            _zones.Add((r.pos, now + DurationSeconds)); return true;
        }
        public bool Blocks(Vec3 pos, double now) { _zones.RemoveAll(z => z.until <= now); return _zones.Any(z => HostSpawnBudget.Dist(z.pos, pos) <= Radius); }
        public void Reset() { _zones.Clear(); _recent.Clear(); }
    }

    /// <summary>Animal spawner guard: cap 14 within 45 m; per-spawner grants of 10 animals and 14 slimes per 60 s. Count 0 spawns nothing.</summary>
    public sealed class AnimalSpawnerGuard
    {
        public const int NearbyCap = 14, GrantAnimals = 10, GrantSlimes = 14; public const double WindowSeconds = 60; public const float NearbyRadius = 45f;
        readonly Dictionary<long, (double start, int animals, int slimes)> _grants = new Dictionary<long, (double, int, int)>();
        /// <summary>Returns how many of the requested spawns may happen (0 = none this round). Never limits on the ranch or offline.</summary>
        public int Allowed(long spawnerId, SpawnClass cls, int requested, int nearby, double now, bool onRanch, bool networked)
        {
            if (!networked || onRanch || requested <= 0) return requested;
            int room = cls == SpawnClass.Animal ? Math.Max(0, NearbyCap - nearby) : requested;
            _grants.TryGetValue(spawnerId, out var g); if (now - g.start > WindowSeconds) g = (now, 0, 0);
            int left = cls == SpawnClass.Animal ? GrantAnimals - g.animals : cls == SpawnClass.Slime ? GrantSlimes - g.slimes : requested;
            int n = Math.Max(0, Math.Min(requested, Math.Min(room, left)));
            if (cls == SpawnClass.Animal) g.animals += n; else if (cls == SpawnClass.Slime) g.slimes += n;
            _grants[spawnerId] = g; return n;
        }
    }

    /// <summary>Last-line chicken cap for every spawn path (wild hens, roosters and chicks only).</summary>
    public sealed class ChickenCap
    {
        public const int Within30m = 20, RanchWithin30m = 64, PerCell = 12; public const float Radius = 30f, CellSize = 40f, AvatarExemption = 6f; public const double CellWindowSeconds = 60;
        readonly Dictionary<(int, int), (double start, int count)> _cells = new Dictionary<(int, int), (double, int)>();
        public static bool IsChickenTypeName(string name)
            => !string.IsNullOrEmpty(name) && (name.Contains("Chicken", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Hen", StringComparison.OrdinalIgnoreCase) || name.Contains("Roostro", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Rooster", StringComparison.OrdinalIgnoreCase) || name.Contains("Chick", StringComparison.OrdinalIgnoreCase));
        public bool Allow(Vec3 pos, int chickensWithin30m, bool onRanch, double now)
        {
            // A save can legitimately keep a large ranch flock, so this is deliberately higher than the wild cap.
            // Both limits apply near players too: avatar proximity must not let a runaway native spawn loop bypass the cap.
            // The network table includes each accepted local spawn immediately.
            if (onRanch) return chickensWithin30m < RanchWithin30m;
            if (chickensWithin30m >= Within30m) return false;
            var cell = ((int)MathF.Floor(pos.X / CellSize), (int)MathF.Floor(pos.Z / CellSize));
            _cells.TryGetValue(cell, out var c); if (now - c.start > CellWindowSeconds) c = (now, 0);
            if (c.count >= PerCell) { _cells[cell] = c; return false; }
            c.count++; _cells[cell] = c; return true;
        }
    }

    public static class GuestSlimeSpawnerGate
    {
        public const float Radius = 40f, Factor = 0.84f, CacheSeconds = 2f;
        public static bool CanSpawn(int slimesNearSpawners, int cellTarget) => slimesNearSpawners < cellTarget * Factor;
    }

    // ------------------------------------------------------------------ /cleanup
    public readonly struct CleanupItem
    {
        public readonly long ActorId; public readonly int TypeId; public readonly int Scene; public readonly Vec3 Position; public readonly SpawnClass Class;
        public readonly bool IsGadget, IsPlayer, IsRanch, IsConservatory;
        public CleanupItem(long id, int type, int scene, Vec3 pos, SpawnClass cls, bool gadget = false, bool player = false, bool ranch = false, bool conservatory = false)
        { ActorId = id; TypeId = type; Scene = scene; Position = pos; Class = cls; IsGadget = gadget; IsPlayer = player; IsRanch = ranch; IsConservatory = conservatory; }
    }
    public sealed class CleanupPlan { public List<long> Remove = new List<long>(); public List<(int TypeId, int Count)> Summary = new List<(int, int)>(); public int Total; }

    public static class Cleanup
    {
        public const float Grid = 30f; public const int PerFrame = 40;
        public static CleanupPlan Plan(IEnumerable<CleanupItem> items, bool ranchKnown)
        {
            var plan = new CleanupPlan(); var groups = new Dictionary<(int, int, int, int), List<CleanupItem>>();
            foreach (var it in items)
            {
                plan.Total++;
                if (it.IsGadget || it.IsPlayer || it.IsRanch) continue;
                if (!ranchKnown && it.IsConservatory) continue;                     // unknown ranch: never clean up a Conservatory group
                var key = (it.TypeId, it.Scene, (int)MathF.Floor(it.Position.X / Grid), (int)MathF.Floor(it.Position.Z / Grid));
                if (!groups.TryGetValue(key, out var l)) groups[key] = l = new List<CleanupItem>(); l.Add(it);
            }
            var perType = new Dictionary<int, int>();
            foreach (var g in groups)
            {
                var list = g.Value.OrderBy(i => i.ActorId).ToList(); if (list.Count == 0) continue;
                int cap = list[0].Class == SpawnClass.Animal ? HostSpawnBudget.PileAnimals : list[0].Class == SpawnClass.Slime ? HostSpawnBudget.PileSlimes : HostSpawnBudget.PileItems;
                for (int i = cap; i < list.Count; i++) { plan.Remove.Add(list[i].ActorId); perType[g.Key.Item1] = perType.GetValueOrDefault(g.Key.Item1) + 1; }
            }
            plan.Summary = perType.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList();
            return plan;
        }
        /// <summary>Takes the next frame's slice (40 actors) from the removal list.</summary>
        public static IEnumerable<long> Slice(List<long> remaining) { int n = Math.Min(PerFrame, remaining.Count); var s = remaining.GetRange(0, n); remaining.RemoveRange(0, n); return s; }
    }

    // ------------------------------------------------------------------ ranch protection
    public static class RanchProtection
    {
        /// <summary>Protected: every ranch actor except Tarr and produce (produce keeps its vanilla rot).</summary>
        public static bool IsProtected(bool onRanch, bool isTarr, bool isProduce) => onRanch && !isTarr && !isProduce;
        public static bool ExemptFromCull(bool onRanch, bool isTarr, bool isProduce, bool isSlime) => isSlime || IsProtected(onRanch, isTarr, isProduce);

        /// <summary>Fall rescue: place 1 m above the ground hit; a replica owned by someone else is only kept.</summary>
        public enum FallAction { Rescue, KeepReplica, AllowDestroy }
        public const string LethalSeaTriggerName = "seaKillCube";
        public static bool IsLethalSeaTrigger(string triggerName)
            => string.Equals(triggerName, LethalSeaTriggerName, StringComparison.OrdinalIgnoreCase);
        public static FallAction OnFallTrigger(bool isProtected, bool locallyOwned, bool groundFound, bool lethalSea = false)
        {
            if (lethalSea) return locallyOwned ? FallAction.AllowDestroy : FallAction.KeepReplica;
            if (!isProtected) return FallAction.AllowDestroy;
            if (!locallyOwned) return FallAction.KeepReplica;
            return groundFound ? FallAction.Rescue : FallAction.AllowDestroy;
        }
        public const float RaycastFrom = 300f, PlaceAbove = 1f, BoundsPadHorizontal = 4f, BoundsPadVertical = 200f; public const double BoundsRefreshSeconds = 10;
    }
}

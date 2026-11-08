// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Lycoreia.Net;

namespace Lycoreia.Actors
{
    /// <summary>
    /// Actor-id blocks: the host allocates below <see cref="Identity.ActorIdOffset"/>, each guest inside
    /// [ShortId * offset, ShortId * offset + offset). A peer's game allocator must stay inside its own block, otherwise its new actors
    /// collide with (or are refused by) the others.
    /// </summary>
    public static class ActorIdBlock
    {
        public static (long start, long end) For(string playerId, bool isHost)
        {
            if (isHost) return (1, Identity.ActorIdOffset);
            long s = (long)Identity.ShortId(playerId) * Identity.ActorIdOffset; return (s, s + Identity.ActorIdOffset);
        }
        public static bool Contains((long start, long end) b, long id) => id >= b.start && id < b.end;

        /// <summary>
        /// The next id the local allocator should continue from. Keeps <paramref name="next"/> when it is inside the block, otherwise
        /// continues after the highest id this peer already used inside the block (or at the block start).
        /// </summary>
        public static long Repair(long next, (long start, long end) b, long highestUsedInBlock)
        {
            if (Contains(b, next) && next > highestUsedInBlock) return next;
            long after = highestUsedInBlock >= b.start ? highestUsedInBlock + 1 : b.start;
            return Math.Max(after, Contains(b, next) ? next : b.start);
        }
    }

    /// <summary>One actor model as this peer's game holds it.</summary>
    public struct LocalActorFact
    {
        public long Id; public int TypeId; public bool Live, HasBody, Kinematic; public Vec3 Position; public string TypeName;
        /// <summary>Its region is hibernating: the game itself holds the body kinematic and restores its cached flag on wake.</summary>
        public bool Hibernating;
        /// <summary>The game holds it kinematic on purpose (in a vacuum, on a plant joint).</summary>
        public bool NativeHold;
    }

    public enum OrphanAction { Keep, Register, Drop, Wait }

    /// <summary>
    /// Compares the networked actor table with what the local game holds and classifies every difference
    ///.
    /// </summary>
    public sealed class CensusReport
    {
        public int Table, Models, Live, Owned, OwnedByOther, Unowned, Released, Gadgets, Hibernating, NativeHeld;
        /// <summary>In the local game but not in the table (never published, or published and refused).</summary>
        public readonly List<LocalActorFact> Orphans = new List<LocalActorFact>();
        /// <summary>In the table but the local game has no model for it (replica creation failed or the model was removed natively).</summary>
        public readonly List<long> Missing = new List<long>();
        /// <summary>Replicas (owned by someone else) whose live body is dynamic: the local physics and AI move them.</summary>
        public readonly List<long> DynamicReplicas = new List<long>();
        /// <summary>Owned here, awake, not held by the game, but the body is kinematic: nobody simulates it (it cannot be vacuumed either).</summary>
        public readonly List<long> FrozenOwned = new List<long>();
        /// <summary>Live replicas whose connected owner has been silent for longer than <see cref="ActorCensus.StallSeconds"/> without releasing them.</summary>
        public readonly List<long> Stalled = new List<long>();
        /// <summary>Owner recorded as a player who is no longer connected.</summary>
        public readonly List<long> GhostOwned = new List<long>();

        public string Summary() =>
            $"table={Table}; models={Models}; live={Live}; owned={Owned}; other={OwnedByOther}; unowned={Unowned}; released={Released}; " +
            $"orphans={Orphans.Count}; missing={Missing.Count}; dynamic-replicas={DynamicReplicas.Count}; frozen-owned={FrozenOwned.Count}; stalled={Stalled.Count}; ghost-owned={GhostOwned.Count}; hibernating={Hibernating}; native-held={NativeHeld}";

        /// <summary>Up to <paramref name="max"/> ids of each problem list, for the diagnostics line.</summary>
        public string Samples(int max = 8)
        {
            var sb = new StringBuilder();
            void Ids(string name, IEnumerable<long> ids) { int n = 0; foreach (var id in ids) { if (n == 0) sb.Append(name).Append('='); else sb.Append(','); sb.Append(id); if (++n >= max) break; } if (n > 0) sb.Append("; "); }
            int k = 0; foreach (var o in Orphans) { sb.Append(k == 0 ? "orphans=" : ",").Append(o.Id).Append(':').Append(o.TypeName ?? o.TypeId.ToString(CultureInfo.InvariantCulture)).Append(o.Live ? ":L" : ""); if (++k >= max) break; }
            if (k > 0) sb.Append("; ");
            Ids("missing", Missing); Ids("dynamic", DynamicReplicas); Ids("frozen", FrozenOwned); Ids("stalled", Stalled); Ids("ghost", GhostOwned);
            return sb.ToString();
        }
    }

    public static class ActorCensus
    {
        public const double StallSeconds = 5.0;
        /// <summary>An orphan must be seen this long before it is acted on (the spawn hook and replica creation finish within a frame or two).</summary>
        public const double OrphanGraceSeconds = 3.0;

        public static CensusReport Build(IEnumerable<ActorEntry> table, IEnumerable<LocalActorFact> local, string me, Func<string, bool> connected, double now)
        {
            var r = new CensusReport(); var models = new Dictionary<long, LocalActorFact>();
            foreach (var f in local) { models[f.Id] = f; r.Models++; if (f.Live) r.Live++; }
            var known = new HashSet<long>();
            foreach (var e in table)
            {
                known.Add(e.Id); r.Table++;
                if (e.IsGadget) { r.Gadgets++; continue; }
                string owner = e.Owner ?? "";
                bool mine = owner.Length > 0 && owner == me, none = owner.Length == 0;
                if (mine) r.Owned++; else if (none) r.Unowned++; else r.OwnedByOther++;
                if (e.ReleasedByOwner) r.Released++;
                if (!none && !mine && !connected(owner)) r.GhostOwned.Add(e.Id);
                if (!models.TryGetValue(e.Id, out var f)) { r.Missing.Add(e.Id); continue; }
                if (!f.Live || !f.HasBody) continue;
                if (f.Hibernating) { r.Hibernating++; continue; }                 // the region member owns the body flag until it wakes
                if (f.NativeHold) r.NativeHeld++;
                if (!mine && !f.Kinematic && !f.NativeHold) r.DynamicReplicas.Add(e.Id);
                if (mine && f.Kinematic && !f.NativeHold) r.FrozenOwned.Add(e.Id);
                if (!mine && !none && !e.ReleasedByOwner && connected(owner) && e.LastOwnerHeardAt > 0 && now - e.LastOwnerHeardAt > StallSeconds) r.Stalled.Add(e.Id);
            }
            foreach (var kv in models) if (!known.Contains(kv.Key)) r.Orphans.Add(kv.Value);
            return r;
        }

        /// <summary>
        /// What to do with a local actor the table does not know. The host registers it (load rule). A guest publishes one inside its
        /// id block that the spawn hook missed (the game created it on another path), so both sides hold it; one outside its block came
        /// from the guest's private save allocator, would be refused by the host and would exist on this guest alone, so it is dropped.
        /// Players, drones and protected ids (irreversible captures in flight) are kept. Nothing happens before the guest's snapshot is
        /// applied, nor before the grace period.
        /// </summary>
        public static OrphanAction Decide(bool isHost, bool snapshotApplied, double firstSeen, double now, bool isPlayerOrDrone, bool isTombstoned, bool isProtected, bool inOwnBlock)
        {
            if (isPlayerOrDrone || isProtected) return OrphanAction.Keep;
            if (now - firstSeen < OrphanGraceSeconds) return OrphanAction.Wait;
            if (isTombstoned) return isHost ? OrphanAction.Keep : OrphanAction.Drop;       // destroyed for everyone: a native leftover
            if (isHost) return OrphanAction.Register;
            if (!snapshotApplied) return OrphanAction.Wait;
            return inOwnBlock ? OrphanAction.Register : OrphanAction.Drop;
        }

        /// <summary>Compact per-actor line for offline two-seat diffs: id,owner-tail,epoch,x,y,z,flags (L live, K kinematic, R released).</summary>
        public static string DumpLine(ActorEntry e, LocalActorFact? f)
        {
            string o = string.IsNullOrEmpty(e.Owner) ? "-" : e.Owner.Length > 4 ? e.Owner.Substring(e.Owner.Length - 4) : e.Owner;
            var p = f.HasValue && f.Value.Live ? f.Value.Position : e.Position;
            string flags = (f.HasValue && f.Value.Live ? "L" : "") + (f.HasValue && f.Value.Kinematic ? "K" : "") + (f.HasValue && f.Value.Hibernating ? "H" : "") + (e.ReleasedByOwner ? "R" : "") + (f.HasValue ? "" : "M");
            return string.Create(CultureInfo.InvariantCulture, $"{e.Id},{o},{e.Epoch},{e.TypeId},{p.X:0.0},{p.Y:0.0},{p.Z:0.0},{flags}");
        }
    }

    /// <summary>First-seen times of orphans so the grace period can be measured; forgotten when they are resolved.</summary>
    public sealed class OrphanClock
    {
        readonly Dictionary<long, double> _seen = new Dictionary<long, double>();
        public double FirstSeen(long id, double now) { if (!_seen.TryGetValue(id, out var t)) _seen[id] = t = now; return t; }
        /// <summary>Drops every id that is not in <paramref name="current"/> (resolved since the last pass).</summary>
        public void Retain(ICollection<long> current) { List<long> gone = null; foreach (var id in _seen.Keys) if (!current.Contains(id)) (gone ??= new List<long>()).Add(id); if (gone != null) foreach (var id in gone) _seen.Remove(id); }
        public void Clear() => _seen.Clear();
        public int Count => _seen.Count;
    }

    /// <summary>
    /// Where a body-mode change must go. RegionMember.Hibernate caches the body's kinematic flag and forces it kinematic; Unhibernate
    /// restores the cached flag. Writing the body of a hibernating member is undone on wake (an owned actor came back frozen and could
    /// not be vacuumed until a resync, 15:30 run actor 1896) and lets a collider-less body fall; the cache is what must change.
    /// </summary>
    public static class BodyModeRule
    {
        public static (bool writeBody, bool writeCache) Plan(bool hasRegionMember, bool hibernating) => hasRegionMember && hibernating ? (false, true) : (true, false);
        // Attached produce must remain kinematic so the native vacuum signals Pending
        // and ResourceCycle performs its release timer before the fruit is pulled.
        public static bool PredictionKinematic(bool predicted, bool locallyOwned, bool attachedToPlant)
            => attachedToPlant || !predicted && !locallyOwned;
    }

    /// <summary>
    /// Owned, awake, kinematic actors that the game is not holding: unfrozen after they stayed that way for <see cref="PersistSeconds"/>
    /// (transient native holds such as a vortex or a shop dispenser end sooner). Each id fires once per episode.
    /// </summary>
    public sealed class FrozenOwnedRepair
    {
        public const double PersistSeconds = 6.0;
        readonly Dictionary<long, double> _since = new Dictionary<long, double>(); readonly HashSet<long> _fired = new HashSet<long>();

        public List<long> Update(IEnumerable<long> frozenNow, double now)
        {
            var cur = new HashSet<long>(frozenNow); var due = new List<long>();
            foreach (var id in new List<long>(_since.Keys)) if (!cur.Contains(id)) { _since.Remove(id); _fired.Remove(id); }
            foreach (var id in cur)
            {
                if (!_since.TryGetValue(id, out var t)) { _since[id] = now; continue; }
                if (now - t >= PersistSeconds && _fired.Add(id)) due.Add(id);
            }
            return due;
        }
        public void Clear() { _since.Clear(); _fired.Clear(); }
    }
}

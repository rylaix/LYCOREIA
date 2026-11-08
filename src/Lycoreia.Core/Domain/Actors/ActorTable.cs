// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.Owned;

namespace Lycoreia.Actors
{
    public sealed class ActorEntry
    {
        public long Id; public string Owner = ""; public uint Epoch; public int TypeId; public int Scene; public bool IsGadget;
        public Vec3 Position; public Quat Rotation = Quat.Identity; public Vec3 Velocity;
        public bool HasModel = true; public bool ReleasedByOwner; public double LastOwnerHeardAt; public long LastRevision;
        /// <summary>Produce hanging on its plant at the owner (the owner's spawner simulates it); never moved to another peer while the owner is connected.</summary>
        public bool Attached;
        /// <summary>The owner's vacuum holds or is pulling this actor (from the owner's transform samples; cleared on every owner change).</summary>
        public bool HeldByOwner;
        /// <summary>Owner side: the produce stage last published (-1 = none yet), so every stage change is sent once.</summary>
        public int SentStage = -1;
        public SpawnKind Kind; public bool HasSlime; public SlimeExtras Slime; public bool HasMaterial; public int MaterialIndex; public bool HasCharge; public double ChargeTime;
        public override string ToString() => $"actor {Id} owner={Owner} epoch={Epoch}";
    }

    /// <summary>Id to actor table with ownership epochs, tombstones and the bounded deferred-destroy queue.</summary>
    public sealed class ActorTable
    {
        public const int MaxDeferredDestroys = 1024, MaxTombstones = 65536;
        readonly Dictionary<long, ActorEntry> _actors = new Dictionary<long, ActorEntry>();
        readonly Dictionary<long, long> _tombstones = new Dictionary<long, long>(); readonly Queue<long> _tombOrder = new Queue<long>();
        readonly Dictionary<long, (string token, long revision)> _deferred = new Dictionary<long, (string, long)>();

        public int Count => _actors.Count;
        public IEnumerable<ActorEntry> All => _actors.Values;
        public bool TryGet(long id, out ActorEntry e) => _actors.TryGetValue(id, out e);
        public ActorEntry Get(long id) => _actors.TryGetValue(id, out var e) ? e : null;
        public string OwnerOf(long id) => _actors.TryGetValue(id, out var e) ? e.Owner : null;

        /// <summary>Adds or replaces an entry. A reused id starts a fresh epoch history.</summary>
        public ActorEntry Add(ActorEntry e) { e.Epoch = _actors.TryGetValue(e.Id, out var old) ? 0 : e.Epoch; _actors[e.Id] = e; _tombstones.Remove(e.Id); return e; }
        public ActorEntry Remove(long id, long revision = 0)
        {
            _actors.TryGetValue(id, out var e); _actors.Remove(id);
            if (revision > 0) { if (!_tombstones.ContainsKey(id)) _tombOrder.Enqueue(id); _tombstones[id] = revision; while (_tombOrder.Count > MaxTombstones) _tombstones.Remove(_tombOrder.Dequeue()); }
            return e;
        }
        public void Clear() { _actors.Clear(); _tombstones.Clear(); _tombOrder.Clear(); _deferred.Clear(); }
        public bool WasDestroyed(long id, long spawnRevision) => _tombstones.TryGetValue(id, out var r) && r >= spawnRevision;
        /// <summary>The id was removed with a revision (destroyed or consumed) and must not come back from a sweep.</summary>
        public bool IsTombstoned(long id) => _tombstones.ContainsKey(id);
        /// <summary>Highest id inside the block that is live or tombstoned (block start - 1 when none): a rejoining peer must continue after it.</summary>
        public long HighestIn((long start, long end) b) { long h = b.start - 1; foreach (var id in _actors.Keys) if (id >= b.start && id < b.end && id > h) h = id; foreach (var id in _tombstones.Keys) if (id >= b.start && id < b.end && id > h) h = id; return h; }

        // ---- ownership epochs
        /// <summary>Guest/host receiver: epoch ordering. Returns the decision and applies it when it is Apply.</summary>
        public OwnershipEpochDecision ApplyOwner(long id, string newOwner, uint incomingEpoch)
        {
            if (!_actors.TryGetValue(id, out var e)) return OwnershipEpochDecision.Stale;
            bool hasEpoch = incomingEpoch > 0;
            var d = OwnershipEpochPolicy.DecideTransfer(e.Epoch, e.Owner, hasEpoch, incomingEpoch, newOwner);
            if (d == OwnershipEpochDecision.Apply) { e.Owner = newOwner ?? ""; if (hasEpoch) e.Epoch = incomingEpoch; e.ReleasedByOwner = false; e.HeldByOwner = false; e.SentStage = -1; }
            else if (d == OwnershipEpochDecision.Conflict) Log.Warn($"epoch conflict on actor {id}: keeping {e.Owner} over {newOwner} at epoch {incomingEpoch}");
            return d;
        }

        /// <summary>Host: sets the owner and bumps the epoch (on an owner change and on the first grant of an epoch-0 actor).</summary>
        public uint HostGrant(long id, string newOwner)
        {
            if (!_actors.TryGetValue(id, out var e)) return 0;
            if (OwnershipEpochPolicy.ShouldBump(e.Owner, newOwner, e.Epoch)) e.Epoch = OwnershipEpochPolicy.Next(e.Epoch);
            e.Owner = newOwner ?? ""; e.ReleasedByOwner = false; e.HeldByOwner = false; e.SentStage = -1; return e.Epoch;
        }

        // ---- deferred destroys (owner token ahead of what we know)
        public bool Defer(long id, string token, long revision)
        {
            if (_deferred.Count >= MaxDeferredDestroys && !_deferred.ContainsKey(id)) return false;
            if (!_deferred.ContainsKey(id)) _deferred[id] = (token, revision);       // keep the first authoritative token
            return true;
        }
        public bool TryResolveDeferred(long id, string appliedOwner, out long revision)
        {
            revision = 0;
            if (_deferred.TryGetValue(id, out var d) && ActorDestroyOrderingPolicy.CanResolve(d.token, appliedOwner)) { revision = d.revision; _deferred.Remove(id); return true; }
            return false;
        }
        public int DeferredCount => _deferred.Count;
        public void ClearDeferred() => _deferred.Clear();
    }
}

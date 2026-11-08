// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Lycoreia.Domain;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.Snapshot;

namespace Lycoreia.World
{
    // =================================================================== leases (drones and gardens share the hand-over rule)
    public enum LeaseAction { None, Claim, Release }

    /// <summary>
    /// Per-entity hand-over decision run on every peer: the owner releases what hibernated locally; a peer with the entity loaded claims
    /// it once nobody (connected) owns it and it has been loaded for the claim delay (drones 2 s; gardens a random 12-16 s per garden so two
    /// peers do not claim at once). An unclaimed entity keeps its old owner, who resumes on reload through the normal claim path.
    /// </summary>
    public static class LeasePlanner
    {
        public static LeaseAction Decide(string owner, string localId, bool localLoaded, bool ownerConnected, double loadedForSeconds, double claimDelaySeconds)
        {
            owner ??= "";
            if (owner == localId) return localLoaded ? LeaseAction.None : LeaseAction.Release;
            if (!localLoaded) return LeaseAction.None;
            if (owner.Length == 0 || !ownerConnected) return loadedForSeconds >= claimDelaySeconds ? LeaseAction.Claim : LeaseAction.None;
            return LeaseAction.None;
        }
        /// <summary>Garden takeover delay: 12-16 s, stable per garden id and peer (spreads competing claims).</summary>
        public static double GardenDelay(string gardenId, string localId)
        {
            unchecked { uint h = 2166136261; foreach (char c in (gardenId ?? "") + "|" + (localId ?? "")) h = (h ^ c) * 16777619; return 12.0 + (h % 4001) / 1000.0; }
        }
    }

    // =================================================================== drone cloud
    /// <summary>The shared drone cloud: guests send signed deltas, the host clamps at 0, applies and publishes absolute amounts; joiners get a snapshot.</summary>
    public sealed class DroneCloudSync
    {
        readonly Session _s; public readonly Dictionary<int, int> Amounts = new Dictionary<int, int>();
        public Action<int, int> OnAmount = (type, amount) => { };
        public DroneCloudSync(Session s, SnapshotHost snap)
        {
            _s = s;
            s.Node.On<DroneCloud>(Side.Host, (c, m) =>
            {
                if (!HostAuthority.Require(c) || !m.IsDelta || m.TypeId <= 0 || m.Amount == 0) return Verdict.Reject;
                long next = Math.Clamp((long)Get(m.TypeId) + m.Amount, 0, int.MaxValue);
                Set(m.TypeId, (int)next); _s.Node.SendToAll(new DroneCloud { TypeId = m.TypeId, Amount = (int)next }); return Verdict.Reject;
            });
            s.Node.On<DroneCloud>(Side.Guest, (c, m) => { if (!m.IsDelta && m.TypeId > 0 && m.Amount >= 0) Set(m.TypeId, m.Amount); });
            snap?.Register(SnapStep.DroneCloud, ctx => { var m = new InitialDroneCloud(); foreach (var kv in Amounts) m.Amounts[kv.Key] = kv.Value; ctx.Send(m); });
            s.Node.On<InitialDroneCloud>(Side.Guest, (c, m) => { foreach (var kv in m.Amounts) if (kv.Key > 0 && kv.Value >= 0) Set(kv.Key, kv.Value); });
        }
        public int Get(int type) => Amounts.TryGetValue(type, out var v) ? v : 0;
        void Set(int type, int amount) { Amounts[type] = amount; OnAmount(type, amount); }
        /// <summary>Host: seeds from the native model (server start), no broadcast.</summary>
        public void Seed(int type, int amount) { if (type > 0 && amount >= 0) Amounts[type] = amount; }
        /// <summary>A native local change (not applying a message). Host publishes the absolute amount; a guest sends the delta.</summary>
        public void LocalChanged(int type, int newAmount)
        {
            if (Applying.Active || type <= 0 || newAmount < 0) return;
            int old = Get(type); if (old == newAmount) return;
            if (_s.IsHost) { Amounts[type] = newAmount; _s.Node.SendToAll(new DroneCloud { TypeId = type, Amount = newAmount }); }
            else if (_s.IsGuest) { Amounts[type] = newAmount; _s.Node.SendToHost(new DroneCloud { TypeId = type, Amount = newAmount - old, IsDelta = true }); }
        }
        public void Clear() => Amounts.Clear();
    }
}

namespace Lycoreia.World
{
    // =================================================================== keyed leases (gardens)
    /// <summary>
    /// One owner per key (a garden): the host validates claims (empty, disconnected or own owner only) and releases (current owner only),
    /// and relays the canonical owner to everyone. The host counts as connected. Joiners get the owner table in the snapshot.
    /// </summary>
    public sealed class KeyLeases
    {
        readonly Session _s; readonly Dictionary<string, string> _owner = new Dictionary<string, string>(StringComparer.Ordinal);
        public Action<string, string> OnOwnerChanged = (k, o) => { };
        public KeyLeases(Session s, SnapshotHost snap)
        {
            _s = s;
            s.Node.On<GardenOwnership>(Side.Host, (c, m) => HostOwnership(c, m));
            s.Node.On<GardenOwnership>(Side.Guest, (c, m) => { if (!string.IsNullOrEmpty(m.GardenId)) Set(m.GardenId, m.OwnerId); });
            snap?.Register(SnapStep.GardenOwnership, ctx => { foreach (var kv in _owner) ctx.Send(new GardenOwnership { GardenId = kv.Key, OwnerId = kv.Value }); });
        }
        Verdict HostOwnership(Ctx c, GardenOwnership m)
        {
            if (!HostAuthority.Require(c) || string.IsNullOrWhiteSpace(m.GardenId)) return Verdict.Reject;
            string cur = OwnerOf(m.GardenId), sender = c.SenderId, want = m.OwnerId ?? "";
            if (want.Length == 0) { if (cur != sender) return Verdict.Reject; }                                       // only the owner releases
            else if (want != sender || !(cur.Length == 0 || cur == sender || !Connected(cur))) return Verdict.Reject;   // claims are for oneself, never over a connected owner
            Set(m.GardenId, want); return HostAuthority.Broadcast(c, new GardenOwnership { GardenId = m.GardenId, OwnerId = want, HasFinalState = m.HasFinalState });
        }
        bool Connected(string id) => id == _s.LocalId || _s.Players.ContainsKey(id);
        void Set(string k, string o) { o ??= ""; if (_owner.TryGetValue(k, out var cur) && cur == o) return; _owner[k] = o; OnOwnerChanged(k, o); }
        public string OwnerOf(string k) => k != null && _owner.TryGetValue(k, out var o) ? o : "";
        public bool Known(string k) => k != null && _owner.ContainsKey(k);
        /// <summary>True when this peer runs the key's simulation: its owner, or the host for a key nobody ever claimed.</summary>
        public bool ShouldRun(string k) { var o = OwnerOf(k); return o == _s.LocalId || (o.Length == 0 && !Known(k) && _s.IsHost); }
        public bool OwnerConnected(string k) { var o = OwnerOf(k); return o.Length > 0 && Connected(o); }
        public IEnumerable<string> Keys => _owner.Keys;
        public void Claim(string k) => Send(k, _s.LocalId);
        public void Release(string k, bool hasFinalState = true) { if (OwnerOf(k) == _s.LocalId) Send(k, "", hasFinalState); }
        void Send(string k, string owner, bool final = false)
        {
            var m = new GardenOwnership { GardenId = k, OwnerId = owner, HasFinalState = final };
            if (_s.IsHost) HostOwnership(new Ctx(_s.Node, new Peer { PlayerId = _s.LocalId, Registered = true }, true), m); else if (_s.IsGuest) _s.Node.SendToHost(m);
        }
        /// <summary>Host: claims every listed key at server start (no message; joiners get owners from the snapshot).</summary>
        public void HostClaimAll(IEnumerable<string> keys) { if (!_s.IsHost) return; foreach (var k in keys) if (!string.IsNullOrEmpty(k)) Set(k, _s.LocalId); }
        public void Clear() => _owner.Clear();
    }
}

namespace Lycoreia.World
{
    // =================================================================== gordo reward payout ledger (host = reward authority)
    /// <summary>
    /// Begin/complete/abort ledger per gordo: rewards are paid at most once. A burst accepted while the gordo is not loaded on the host
    /// stays pending until it loads. A payout that returned normally is complete (never re-paid even if indexing needs more frames); one
    /// that threw is "uncertain" unless a scan proved nothing spawned (then it is a safe retry).
    /// </summary>
    public sealed class GordoRewardLedger
    {
        readonly HashSet<string> _pending = new HashSet<string>(StringComparer.Ordinal), _inProgress = new HashSet<string>(StringComparer.Ordinal),
            _given = new HashSet<string>(StringComparer.Ordinal), _uncertain = new HashSet<string>(StringComparer.Ordinal);
        public void MarkPending(string id) { if (!string.IsNullOrEmpty(id) && !_given.Contains(id) && !_uncertain.Contains(id)) _pending.Add(id); }
        /// <summary>Starts a native payout; false when it was already paid, is running, or is uncertain.</summary>
        public bool TryBegin(string id) { if (string.IsNullOrEmpty(id) || _given.Contains(id) || _uncertain.Contains(id) || !_inProgress.Add(id)) return false; return true; }
        public void Complete(string id) { _inProgress.Remove(id); _pending.Remove(id); _given.Add(id); }
        /// <summary>The native payout threw. spawnedSeen = null: no scan possible (uncertain); 0: nothing spawned (safe retry); more: uncertain.</summary>
        public void Fail(string id, int? spawnedSeen)
        {
            _inProgress.Remove(id);
            if (spawnedSeen == 0) _pending.Add(id); else { _pending.Remove(id); _uncertain.Add(id); }
        }
        public bool HasPending(string id) => _pending.Contains(id);
        public bool HasGiven(string id) => _given.Contains(id);
        public bool IsUncertain(string id) => _uncertain.Contains(id);
        public bool InProgress(string id) => _inProgress.Contains(id);
        /// <summary>Records gordos already rewarded in the save (host start) so a later Start never re-pays them.</summary>
        public void LoadGiven(IEnumerable<string> ids) { foreach (var i in ids) if (!string.IsNullOrEmpty(i)) _given.Add(i); }
        public void Clear() { _pending.Clear(); _inProgress.Clear(); _given.Clear(); _uncertain.Clear(); }
    }
}

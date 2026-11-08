// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Lycoreia.Net;

namespace Lycoreia.Domain
{
    public enum LedgerLookup { Unknown, Replay, Conflict }

    /// <summary>
    /// Bounded map from request id to the host's decision, bound to the authenticated sender and a payload hash.
    /// A retry from the same sender with the same payload replays the decision; sender or payload reuse conflicts.
    /// </summary>
    public sealed class RequestLedger<TDecision>
    {
        readonly Dictionary<Guid, (string sender, long hash, TDecision decision, long at)> _map = new Dictionary<Guid, (string, long, TDecision, long)>();
        readonly Queue<Guid> _order = new Queue<Guid>();
        readonly int _capacity; readonly Func<long> _clock; readonly long _ttlMs;

        public RequestLedger(int capacity = 4096, long ttlMs = 45_000, Func<long> clock = null)
        { _capacity = Math.Max(1, capacity); _ttlMs = ttlMs; _clock = clock ?? (() => Environment.TickCount64); }
        public int Count => _map.Count;

        /// <summary>Unknown: process the request. Replay: return the stored decision. Conflict: same id, different payload, reject.</summary>
        public LedgerLookup TryGet(string sender, Guid requestId, long payloadHash, out TDecision decision)
        {
            decision = default;
            if (requestId == Guid.Empty || !_map.TryGetValue(requestId, out var e)) return LedgerLookup.Unknown;
            if (_ttlMs > 0 && _clock() - e.at > _ttlMs) return LedgerLookup.Unknown;
            if (!string.Equals(e.sender, sender ?? "", StringComparison.Ordinal) || e.hash != payloadHash) return LedgerLookup.Conflict;
            decision = e.decision; return LedgerLookup.Replay;
        }

        public void Remember(string sender, Guid requestId, long payloadHash, TDecision decision)
        {
            if (requestId == Guid.Empty) return;
            string owner = sender ?? "";
            long now = _clock();
            if (_map.TryGetValue(requestId, out var old))
            {
                if (_ttlMs <= 0 || now - old.at <= _ttlMs) return;
                _map[requestId] = (owner, payloadHash, decision, now);
                return;
            }
            _order.Enqueue(requestId);
            while (_order.Count > _capacity) _map.Remove(_order.Dequeue());
            _map[requestId] = (owner, payloadHash, decision, now);
        }
        public void Clear() { _map.Clear(); _order.Clear(); }

        public static long Hash(params object[] parts)
        {
            unchecked { long h = 1469598103934665603L; foreach (var p in parts) { string s = p?.ToString() ?? ""; foreach (char c in s) h = (h ^ c) * 1099511628211L; h = (h ^ 0x1F) * 1099511628211L; } return h; }
        }
    }

    /// <summary>Monotonic revisions per entity (or global with key null): accepts only strictly newer values.</summary>
    public sealed class RevisionTracker<TKey>
    {
        readonly Dictionary<TKey, long> _last = new Dictionary<TKey, long>(); long _global;
        public bool TryAccept(TKey key, long revision)
        {
            if (revision <= 0) return true;     // unrevisioned messages apply unordered
            if (_last.TryGetValue(key, out var l) && revision <= l) return false;
            _last[key] = revision; return true;
        }
        public bool TryAcceptGlobal(long revision) { if (revision <= 0) return true; if (revision <= _global) return false; _global = revision; return true; }
        public long Last(TKey key) => _last.TryGetValue(key, out var l) ? l : 0;
        public long Next(TKey key) { var n = Last(key) + 1; _last[key] = n; return n; }
        public long NextGlobal() => ++_global;
        public long Global => _global;
        public void Clear() { _last.Clear(); _global = 0; }
    }

    /// <summary>Values that arrived before their model exists; applied when the model appears. Bounded and expiring.</summary>
    public sealed class PendingCache<TKey, TValue>
    {
        readonly Dictionary<TKey, (TValue v, long at)> _map = new Dictionary<TKey, (TValue, long)>();
        readonly int _cap; readonly long _ttl; readonly Func<long> _clock;
        public PendingCache(int capacity = 2048, long ttlMs = 120_000, Func<long> clock = null) { _cap = capacity; _ttl = ttlMs; _clock = clock ?? (() => Environment.TickCount64); }
        public int Count => _map.Count;
        public IEnumerable<TKey> Keys => _map.Keys;
        public bool Contains(TKey key) => _map.ContainsKey(key);
        public void Put(TKey key, TValue value)
        {
            if (_map.Count >= _cap && !_map.ContainsKey(key)) Prune(true);
            _map[key] = (value, _clock());
        }
        public bool TryTake(TKey key, out TValue value)
        {
            if (_map.TryGetValue(key, out var e)) { _map.Remove(key); if (_clock() - e.at <= _ttl) { value = e.v; return true; } }
            value = default; return false;
        }
        public void Prune(bool forceOldest = false)
        {
            long now = _clock(); List<TKey> dead = null;
            foreach (var kv in _map) if (now - kv.Value.at > _ttl) (dead ??= new List<TKey>()).Add(kv.Key);
            if (dead != null) foreach (var k in dead) _map.Remove(k);
            if (forceOldest && _map.Count >= _cap)
            {
                TKey old = default; long t = long.MaxValue; foreach (var kv in _map) if (kv.Value.at < t) { t = kv.Value.at; old = kv.Key; }
                _map.Remove(old);
            }
        }
        /// <summary>Offers every live entry to <paramref name="apply"/>; applied entries are removed, the rest keep their original age (expired ones are dropped).</summary>
        public int Retry(Func<TKey, TValue, bool> apply)
        {
            if (_map.Count == 0) return 0;
            long now = _clock(); int applied = 0;
            foreach (var kv in _map.ToArray())
            {
                if (now - kv.Value.at > _ttl) { _map.Remove(kv.Key); continue; }
                if (apply(kv.Key, kv.Value.v)) { _map.Remove(kv.Key); applied++; }
            }
            return applied;
        }
        public void Clear() => _map.Clear();
    }

    /// <summary>Host-authority checks shared by handlers: an authenticated sender, and relaying the canonical result.</summary>
    public static class HostAuthority
    {
        public static bool Require(in Ctx c) => c.OnHost && c.Sender != null && c.Sender.Registered && !string.IsNullOrEmpty(c.Sender.PlayerId);
        public static Verdict AcceptRelay(in Ctx c, bool ok) => ok ? Verdict.Relay : Verdict.Reject;
        /// <summary>Canonical result to everyone including the sender (who may not have applied its own change); receivers apply idempotently.</summary>
        public static Verdict Broadcast(in Ctx c, Msg m) { c.Node.SendToAll(m); return Verdict.Reject; }
    }
}

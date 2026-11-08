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
    /// <summary>Game side of one latched keyed state (a switch, a door, a puzzle slot ...). Returns false while the model is not ready.</summary>
    public interface ILatchedModel { bool TryApply(string key, double value, bool fromSnapshot); double Current(string key, double fallback); }

    /// <summary>
    /// One keyed state kind synchronised host-authoritatively: guests send intents, the host validates, applies and relays; snapshot
    /// entries and live updates are cached until their model exists and never overwritten by an older snapshot.
    /// </summary>
    public sealed class LatchedSystem<TMsg> where TMsg : Msg
    {
        readonly Session _s; readonly ILatchedModel _model; readonly Func<TMsg, (string key, double value)> _read; readonly Func<string, double, TMsg> _make;
        readonly Func<double, double, double> _merge; readonly Func<string, double, bool> _valid;
        public readonly PendingCache<string, double> Pending = new PendingCache<string, double>();
        readonly Dictionary<string, double> _known = new Dictionary<string, double>();
        readonly Dictionary<string, double> _confirmed = new Dictionary<string, double>();

        /// <param name="merge">Chooses the stored value from (current, incoming); default last wins. Use Math.Max for monotonic counters.</param>
        public LatchedSystem(Session s, ILatchedModel model, Func<TMsg, (string, double)> read, Func<string, double, TMsg> make, Func<double, double, double> merge = null, Func<string, double, bool> valid = null)
        {
            _s = s; _model = model; _read = read; _make = make; _merge = merge ?? ((cur, inc) => inc); _valid = valid ?? ((k, v) => !string.IsNullOrWhiteSpace(k) && double.IsFinite(v));
            s.Node.On<TMsg>(Side.Host, (c, m) => OnHost(c, m));
            s.Node.On<TMsg>(Side.Guest, (c, m) => { var (k, v) = _read(m); Apply(k, v, false); });
        }

        Verdict OnHost(Ctx c, TMsg m)
        {
            if (!HostAuthority.Require(c)) return Verdict.Reject;
            var (k, v) = _read(m); if (!_valid(k, v)) return Verdict.Reject;               // empty ids, negatives, NaN never reach a game model
            v = _merge(_known.TryGetValue(k, out var cur) ? cur : _model.Current(k, double.NaN), v);
            Apply(k, v, false); return HostAuthority.Broadcast(c, _make(k, v));
        }

        /// <summary>Records and applies a value; when the model is not ready it is cached and applied later.</summary>
        public bool Apply(string key, double value, bool fromSnapshot)
        {
            double confirmed = value;
            if (_confirmed.TryGetValue(key, out var prior)) confirmed = fromSnapshot ? _merge(value, prior) : _merge(prior, value);
            _confirmed[key] = confirmed;
            if (_known.TryGetValue(key, out var cur)) value = fromSnapshot ? _merge(value, cur) : _merge(cur, value);   // an older snapshot never overwrites a newer live value
            _known[key] = value;
            if (_model.TryApply(key, value, fromSnapshot)) { Pending.TryTake(key, out _); return true; }
            Pending.Put(key, value); return false;
        }

        /// <summary>Called when a model appears (plot loaded, scene object created): applies what arrived early.</summary>
        public bool OnModelReady(string key) => Pending.TryTake(key, out var v) ? _model.TryApply(key, v, false) || Requeue(key, v) : false;
        bool Requeue(string k, double v) { Pending.Put(k, v); return false; }
        /// <summary>Retries every pending key against the model without refreshing its age (the cache TTL still applies).</summary>
        public int RetryPending() => Pending.Retry((k, v) => _model.TryApply(k, v, false));

        /// <summary>Host: local changes (hooks) publish once; the applying flag keeps received messages from echoing.</summary>
        public void Publish(string key, double value)
        {
            if (Applying.Active || !_valid(key, value)) return;
            if (_known.TryGetValue(key, out var cur)) value = _merge(cur, value);
            _known[key] = value; if (_s.IsHost) _confirmed[key] = value; var m = _make(key, value);
            if (_s.IsHost) _s.Node.SendToAll(m); else if (_s.IsGuest) _s.Node.SendToHost(m);
        }

        public IEnumerable<(string, double)> Snapshot() => _known.Select(kv => (kv.Key, kv.Value));
        public void LoadSnapshot(IEnumerable<(string, double)> entries) { foreach (var (k, v) in entries) Apply(k, v, true); }
        public double? Get(string key) => _known.TryGetValue(key, out var v) ? v : (double?)null;
        public double? Confirmed(string key) => key != null && _confirmed.TryGetValue(key, out var value) ? value : (double?)null;
        public void Clear() { _known.Clear(); _confirmed.Clear(); Pending.Clear(); }
        /// <summary>Drops every known key matching <paramref name="match"/> (e.g. a demolished plot's upgrades).</summary>
        public void Forget(Func<string, bool> match) { foreach (var k in _known.Keys.Where(match).ToList()) { _known.Remove(k); _confirmed.Remove(k); Pending.TryTake(k, out _); } }
        public IEnumerable<string> Keys => _known.Keys;
    }
}

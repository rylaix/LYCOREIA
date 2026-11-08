// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Lycoreia.Domain;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.Owned;
using Lycoreia.Snapshot;

namespace Lycoreia.World
{
    // =================================================================== drones
    public interface IDroneWorld { bool DroneExists(long stationId); bool DroneHibernated(long stationId); }

    /// <summary>Drone leases: one owner simulates each drone; claims, releases and owned-only mutations are validated by the host.</summary>
    public sealed class DroneLeases
    {
        readonly Session _s; readonly IDroneWorld _w; readonly Dictionary<long, string> _owner = new Dictionary<long, string>(); readonly HashSet<long> _released = new HashSet<long>();
        readonly Dictionary<long, string> _lastAuthority = new Dictionary<long, string>();
        public Action<long, string> OnOwnerChanged = (id, o) => { };
        /// <summary>Applies a task / battery state on this peer (game adapter). Called for the owner's canonical messages and for intents routed to the local owner.</summary>
        public Action<DroneTask> OnTask = m => { }; public Action<DroneBattery> OnBattery = m => { };
        public DroneLeases(Session s, IDroneWorld w, Snapshot.SnapshotHost snap = null)
        {
            _s = s; _w = w;
            s.Node.On<DroneOwnership>(Side.Host, HostClaim); s.Node.On<DroneOwnership>(Side.Guest, (c, m) => SetOwner(m.StationId, m.ClaimerId));
            s.Node.On<DroneTask>(Side.Host, (c, m) => m.Intent ? RouteIntent(c, m.ActorId, m, () => OnTask(m), () => { m.Intent = false; return m; }) : Owned(c, m.ActorId));
            s.Node.On<DroneBattery>(Side.Host, (c, m) => m.Intent ? RouteIntent(c, m.ActorId, m, () => OnBattery(m), () => null) : Owned(c, m.ActorId));
            s.Node.On<DroneTask>(Side.Guest, (c, m) => { OnTask(m); if (m.Intent && OwnerOf(m.ActorId) == _s.LocalId) _s.Node.SendToHost(new DroneTask { ActorId = m.ActorId, TargetIdent = m.TargetIdent, Target = m.Target, Sink = m.Sink, Source = m.Source }); });
            s.Node.On<DroneBattery>(Side.Guest, (c, m) => OnBattery(m));
            // owner relays applied on the host too (the host is a receiver of a guest owner's canonical state)
            s.Node.On<DroneTask>(Side.Host, (c, m) => { if (!m.Intent) OnTask(m); });
            s.Node.On<DroneBattery>(Side.Host, (c, m) => { if (!m.Intent) OnBattery(m); });
            snap?.Register(Snapshot.SnapStep.DroneResources, ctx => { foreach (var kv in _owner) if (!string.IsNullOrEmpty(kv.Value)) ctx.Send(new DroneOwnership { StationId = kv.Key, ClaimerId = kv.Value, PreviousOwnerId = "" }); });
            s.Node.On<DroneAnimation>(Side.Host, (c, m) => Owned(c, m.StationId)); s.Node.On<DroneUpdate>(Side.Host, (c, m) => Owned(c, m.StationId)); s.Node.On<DroneEvent>(Side.Host, (c, m) => Owned(c, m.StationId));
        }
        public string OwnerOf(long id) => _owner.TryGetValue(id, out var o) ? o : "";
        void SetOwner(long id, string o) { _owner[id] = o ?? ""; if (!string.IsNullOrEmpty(o)) _released.Remove(id); OnOwnerChanged(id, o); }

        Verdict Owned(Ctx c, long station)
        {
            _owner.TryGetValue(station, out var owner);
            return DroneMutationPolicy.CanAcceptOwnedMutation(true, HostAuthority.Require(c), c.SenderId, owner) ? Verdict.Relay : Verdict.Reject;
        }

        Verdict HostClaim(Ctx c, DroneOwnership m)
        {
            if (!HostAuthority.Require(c)) return Verdict.Reject;
            _owner.TryGetValue(m.StationId, out var cur); cur ??= ""; string sender = c.SenderId;
            bool isRelease = string.IsNullOrEmpty(m.ClaimerId);
            if (isRelease)
            {
                if (!DroneMutationPolicy.CanAcceptRelease(sender, cur, m.PreviousOwnerId)) return Verdict.Reject;
                _released.Add(m.StationId); _lastAuthority[m.StationId] = cur;               // remember the previous owner as the authority until a new claim
                if (DroneMutationPolicy.ShouldHostTakeOverAfterRelease(true, true, _w.DroneExists(m.StationId), _w.DroneHibernated(m.StationId))) { SetOwner(m.StationId, _s.LocalId); _s.Node.SendToAll(new DroneOwnership { StationId = m.StationId, ClaimerId = _s.LocalId, PreviousOwnerId = cur }); return Verdict.Reject; }
                _owner[m.StationId] = ""; return HostAuthority.Broadcast(c, m);
            }
            if (m.ClaimerId != sender) return Verdict.Reject;                                   // identity comes from the connection
            bool connected = !string.IsNullOrEmpty(cur) && (cur == _s.LocalId || _s.Node.FindByPlayer(cur) != null);
            if (!DroneMutationPolicy.CanAcceptClaim(cur, m.ClaimerId, m.PreviousOwnerId, connected, _released.Contains(m.StationId)))
                return Verdict.Reject;
            SetOwner(m.StationId, m.ClaimerId); return HostAuthority.Broadcast(c, m);
        }
        /// <summary>
        /// Host: an intent (a non-owner's UI action) goes to the lease owner. Owner = host or nobody: apply here and publish the canonical
        /// result; owner = a guest: forward to that guest, who applies and republishes it as owner. Never relayed as is.
        /// </summary>
        Verdict RouteIntent(Ctx c, long station, Msg m, Action applyHere, Func<Msg> canonical)
        {
            if (!HostAuthority.Require(c) || station == 0) return Verdict.Reject;
            var owner = OwnerOf(station);
            if (string.IsNullOrEmpty(owner) || owner == _s.LocalId) { applyHere(); var out_ = canonical(); if (out_ != null) _s.Node.SendToAll(out_); return Verdict.Reject; }
            var p = _s.Node.FindByPlayer(owner); if (p != null) _s.Node.SendTo(p, m); else applyHere();
            return Verdict.Reject;
        }
        /// <summary>True when this peer runs the drone's simulation: its lease owner, or the host while nobody owns it.</summary>
        public bool ShouldSimulate(long station) { var o = OwnerOf(station); return o == _s.LocalId || (string.IsNullOrEmpty(o) && _s.IsHost); }
        public bool IsReleased(long station) => _released.Contains(station);
        /// <summary>Host: takes every listed station at server start (no message; joiners get owners from the snapshot).</summary>
        public void HostClaimAll(IEnumerable<long> stations) { if (!_s.IsHost) return; foreach (var id in stations) if (id != 0) SetOwner(id, _s.LocalId); }
        /// <summary>Local peer claims a drone, naming the owner it believes is current.</summary>
        public void Claim(long station)
        {
            var m = new DroneOwnership { StationId = station, ClaimerId = _s.LocalId, PreviousOwnerId = OwnerOf(station) };
            if (_s.IsHost) HostClaim(new Ctx(_s.Node, new Peer { PlayerId = _s.LocalId, Registered = true }, true), m); else _s.Node.SendToHost(m);
        }
        /// <summary>The local owner releases a drone (it hibernated here).</summary>
        public void Release(long station)
        {
            if (OwnerOf(station) != _s.LocalId) return;
            var m = new DroneOwnership { StationId = station, ClaimerId = "", PreviousOwnerId = _s.LocalId };
            if (_s.IsHost) HostClaim(new Ctx(_s.Node, new Peer { PlayerId = _s.LocalId, Registered = true }, true), m); else _s.Node.SendToHost(m);
        }
        public IEnumerable<long> Stations => _owner.Keys;
        public void Clear() { _owner.Clear(); _released.Clear(); _lastAuthority.Clear(); }
        public void ForgetPlayer(string id) { foreach (var k in _owner.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList()) { _owner[k] = ""; _released.Add(k); } }
    }

    // =================================================================== economy
    /// <summary>Host-authoritative balances with revisions and idempotent request memory (8,192 requests).</summary>
    public sealed class CurrencyLedger
    {
        public const int RequestMemory = 8192, MaxPending = 1024;
        readonly Session _s;
        readonly RequestLedger<(bool accepted, int balance)> _ledger = new(RequestMemory, 0);
        readonly Dictionary<Guid, (int type, int delta)> _pending = new();
        public readonly Dictionary<int, int> Balance = new();
        public readonly Dictionary<int, ulong> Revision = new();
        public Action<int, int> OnBalanceChanged = (type, amount) => { };
        public bool ApplyingNotification { get; private set; }
        public Action<string, string> Trace = (ev, detail) => { };
        void T(string ev, string detail) { try { Trace(ev, detail); } catch { } }
        public CurrencyLedger(Session s)
        {
            _s = s;
            s.ServerStarted += Reset; s.ClientDisconnected += _ => Reset();
            s.Node.On<CurrencyAdjust>(Side.Host, HostAdjust);
            s.Node.On<CurrencyAdjust>(Side.Guest, (c, m) => GuestApply(m));
            s.BuildApprove += (p, a) => { foreach (var kv in Balance) a.Currencies[kv.Key] = kv.Value; foreach (var kv in Revision) a.CurrencyRevisions[kv.Key] = kv.Value; };
            s.ApproveApplied += a => { if (a.InitialJoin) Reset(); foreach (var kv in a.Currencies) { ulong rev = a.CurrencyRevisions.TryGetValue(kv.Key, out var r) ? r : 0; Accept(kv.Key, kv.Value, rev, false); } };
        }
        public void Reset() { Balance.Clear(); Revision.Clear(); _pending.Clear(); _ledger.Clear(); ApplyingNotification = false; }
        public int Get(int type) => Balance.TryGetValue(type, out var v) ? v : 0;
        public int ExpectedNative(int type)
        {
            long amount = Get(type);
            foreach (var pending in _pending.Values) if (pending.type == type) amount += pending.delta;
            return (int)Math.Clamp(amount, 0, int.MaxValue);
        }
        Verdict HostAdjust(Ctx c, CurrencyAdjust m)
        {
            if (!HostAuthority.Require(c) || m.RequestId == Guid.Empty) return Verdict.Reject;
            long hash = RequestLedger<int>.Hash(m.CurrencyType, m.Delta, m.SourceIdent);
            var look = _ledger.TryGet(c.SenderId, m.RequestId, hash, out var d);
            if (look == LedgerLookup.Conflict) { Reply(c, m, false); return Verdict.Reject; }
            if (look == LedgerLookup.Replay) { Reply(c, m, d.accepted); return Verdict.Reject; }
            int cur = Get(m.CurrencyType); long next = (long)cur + m.Delta;
            bool ok = m.Delta != 0 && next >= 0 && next <= int.MaxValue;
            // Existing direct domain callers may omit optional optimistic metadata. When
            // provided, a guest cannot claim it spent money absent from its own balance.
            if (m.Before != 0 || m.After != 0 || m.Requested != 0)
                ok &= m.Before >= 0 && m.After >= 0 && (long)m.Before + m.Delta == m.After &&
                    (m.Delta >= 0 || m.Before >= -(long)m.Delta);
            T("adjust-resolved", $"request={m.RequestId}; from={c.SenderId}; type={m.CurrencyType}; delta={m.Delta}; before={cur}; accepted={ok}; amount={(ok ? next : cur)}");
            _ledger.Remember(c.SenderId, m.RequestId, hash, (ok, ok ? (int)next : cur));
            if (ok)
            {
                Set(m.CurrencyType, (int)next, NextRevision(m.CurrencyType), m.ShowNotification);
                _s.Node.SendToAll(Canonical(m.CurrencyType, m, true));
            }
            else Reply(c, m, false);
            return Verdict.Reject;
        }
        ulong NextRevision(int t) { Revision.TryGetValue(t, out var r); return r + 1; }
        void Set(int type, int amount, ulong rev, bool notify)
        {
            Balance[type] = amount; Revision[type] = rev;
            ApplyingNotification = notify;
            try { OnBalanceChanged(type, amount); } finally { ApplyingNotification = false; }
        }
        CurrencyAdjust Canonical(int type, CurrencyAdjust req, bool accepted) => new()
        {
            CurrencyType = type, RequestId = req.RequestId,
            Revision = Revision.TryGetValue(type, out var r) ? r : 0, NewAmount = Get(type),
            Delta = accepted ? req.Delta : 0, SourceIdent = req.SourceIdent,
            ShowNotification = accepted && req.ShowNotification
        };
        void Reply(Ctx c, CurrencyAdjust req, bool accepted) => _s.Node.SendTo(c.Sender, Canonical(req.CurrencyType, req, accepted));
        void GuestApply(CurrencyAdjust m)
        {
            if (m.NewAmount < 0) return;
            T("canonical-received", $"request={m.RequestId}; type={m.CurrencyType}; amount={m.NewAmount}; revision={m.Revision}; delta={m.Delta}; notify={m.ShowNotification}");
            bool ownReply = _pending.TryGetValue(m.RequestId, out var pending) && pending.type == m.CurrencyType;
            if (ownReply) _pending.Remove(m.RequestId);
            if (!Accept(m.CurrencyType, m.NewAmount, m.Revision, m.ShowNotification) && ownReply)
            {
                // A rejection can legitimately repeat the same revision. Clear its
                // prediction and correct from our newest canonical state, never stale data.
                OnBalanceChanged(m.CurrencyType, Get(m.CurrencyType));
            }
        }
        bool Accept(int type, int amount, ulong rev, bool notify)
        {
            if (amount < 0 || Revision.TryGetValue(type, out var cur) && rev <= cur) return false;
            Set(type, amount, rev, notify); return true;
        }
        public void Seed(int type, int amount) { Balance[type] = amount; if (!Revision.ContainsKey(type)) Revision[type] = 0; }
        public void HostLocalChanged(int type, int amount)
        {
            if (!_s.IsHost || amount < 0 || Balance.TryGetValue(type, out var cur) && cur == amount) return;
            int delta = amount - Get(type);
            Set(type, amount, NextRevision(type), false);
            _s.Node.SendToAll(new CurrencyAdjust { CurrencyType = type, Revision = Revision[type], NewAmount = amount, Delta = delta, ShowNotification = delta > 0 });
        }
        // Reliable absolute checkpoint repairs native readiness or a lost application;
        // equal revisions never replay the earning notification.
        public void PublishCheckpoint()
        {
            if (!_s.IsHost) return;
            foreach (var kv in Balance) _s.Node.SendToAll(new CurrencyAdjust { CurrencyType = kv.Key, NewAmount = kv.Value, Revision = Revision[kv.Key] });
        }
        public Guid Request(int type, int delta, int source = 0)
        {
            if (_pending.Count >= MaxPending || delta == 0) return Guid.Empty;
            int before = ExpectedNative(type); long after = (long)before + delta;
            var id = Guid.NewGuid();
            var m = new CurrencyAdjust { CurrencyType = type, RequestId = id, Delta = delta, SourceIdent = source,
                Before = before, After = (int)Math.Clamp(after, int.MinValue, int.MaxValue), Requested = delta, ShowNotification = delta > 0 };
            if (_s.IsHost) HostAdjust(new Ctx(_s.Node, new Peer { PlayerId = _s.LocalId, Registered = true }, true), m);
            else { _pending[id] = (type, delta); _s.Node.SendToHost(m); }
            return id;
        }
    }

    /// <summary>Plort sales: host prices, retries never double the balance, prices published to everyone.</summary>
    public sealed class MarketSystem
    {
        /// <summary>The currency a plort sale pays in (the game's persistence id of its main currency).</summary>
        public int SaleCurrency;
        readonly Session _s; readonly CurrencyLedger _c; readonly Dictionary<int, int> _price = new Dictionary<int, int>(); readonly RequestLedger<bool> _sold = new RequestLedger<bool>(8192, 0);
        public MarketSystem(Session s, CurrencyLedger c)
        {
            _s = s; _c = c;
            s.ServerStarted += () => { _sold.Clear(); _price.Clear(); };
            s.ClientDisconnected += _ => { _sold.Clear(); _price.Clear(); };
            s.Node.On<MarketPrices>(Side.Guest, (cx, m) => { int accepted = 0; foreach (var p in m.Prices) if (p.A > 0 && p.B >= 0) { _price[p.A] = p.B; accepted++; } OnPrices(_price); Mod.Diagnostics?.Record("market", "prices-received", $"items={accepted}"); });
            s.Node.On<PlortSold>(Side.Host, (cx, m) =>
            {
                if (!HostAuthority.Require(cx) || m.RequestId == Guid.Empty) return;
                long hash = RequestLedger<int>.Hash(m.ItemId, m.Count, m.Price);
                var prior = _sold.TryGet(cx.SenderId, m.RequestId, hash, out var priorDecision);
                if (prior == LedgerLookup.Replay) { Mod.Diagnostics?.Record("market", "sale-replayed", $"item={m.ItemId}; count={m.Count}; from={cx.SenderId}; accepted={priorDecision}"); return; }
                if (prior == LedgerLookup.Conflict) { Mod.Diagnostics?.Record("market", "sale-rejected", $"item={m.ItemId}; from={cx.SenderId}; reason=request-id-conflict"); return; }
                if (m.Count < 1 || m.Count > MaxSaleCount || !IsKnownItem(m.ItemId))
                {
                    _sold.Remember(cx.SenderId, m.RequestId, hash, false);
                    Mod.Diagnostics?.Record("market", "sale-rejected", $"item={m.ItemId}; count={m.Count}; reason=invalid-sale; from={cx.SenderId}"); return;
                }
                // A sale may land between price broadcasts. Prefer the host's current native value and only fall back to its last published table.
                int unit = CurrentHostPrice(m.ItemId);
                if (unit < 0 && !_price.TryGetValue(m.ItemId, out unit))
                {
                    _sold.Remember(cx.SenderId, m.RequestId, hash, false);
                    Mod.Diagnostics?.Record("market", "sale-rejected", $"item={m.ItemId}; count={m.Count}; reason=host-price-unavailable; from={cx.SenderId}"); return;
                }
                Mod.Diagnostics?.Record("market", "sale-accepted", $"item={m.ItemId}; count={m.Count}; host-price={unit}; guest-price={m.Price}; from={cx.SenderId}");
                OnSold(m.ItemId, m.Count, unit); _c.Request(SaleCurrency, (int)Math.Min(int.MaxValue, (long)unit * m.Count));
                _sold.Remember(cx.SenderId, m.RequestId, hash, true);
            });
        }
        public void SetPrices(Dictionary<int, int> prices) { _price.Clear(); foreach (var kv in prices) _price[kv.Key] = kv.Value; var m = new MarketPrices(); foreach (var kv in prices) m.Prices.Add(new IntPair { A = kv.Key, B = kv.Value }); _s.Node.SendToAll(m); }
        public int Price(int item) => _price.TryGetValue(item, out var p) ? p : 0;
        public IReadOnlyDictionary<int, int> Prices => _price;
        public const int MaxSaleCount = 10_000;
        /// <summary>Host: a guest's accepted sale (the adapter registers it natively so saturation follows); default nothing.</summary>
        public Action<int, int, int> OnSold = (item, count, unitPrice) => { };
        public Func<int, bool> IsKnownItem = id => id > 0;
        /// <summary>Fallback before the first price snapshot; this must read the host's live native price.</summary>
        public Func<int, int> CurrentHostPrice = id => -1;
        /// <summary>Guest: prices received from the host (the adapter applies them natively).</summary>
        public Action<IReadOnlyDictionary<int, int>> OnPrices = p => { };
        /// <summary>Host: publishes the price table when it differs from the last one sent (the adapter calls this at most every 0.5 s).</summary>
        public bool PublishIfChanged(Dictionary<int, int> prices)
        {
            if (!_s.IsHost || prices == null || (prices.Count == _price.Count && prices.All(kv => _price.TryGetValue(kv.Key, out var v) && v == kv.Value))) return false;
            SetPrices(prices); Mod.Diagnostics?.Record("market", "prices-published", $"items={prices.Count}"); return true;
        }
    }

    /// <summary>Refinery counts: concurrent +1s give +2, a duplicate request adds nothing, revisions order the broadcasts.</summary>
    public sealed class RefinerySystem
    {
        readonly Session _s; readonly RefineryRequestLedger _ledger = new RefineryRequestLedger(); public readonly RefineryRevisionTracker Revisions = new RefineryRevisionTracker();
        public readonly Dictionary<int, int> Items = new Dictionary<int, int>(); public Action<int, int> OnCount = (item, count) => { };
        public RefinerySystem(Session s, SnapshotHost snap)
        {
            _s = s; s.Node.On<RefineryUpdate>(Side.Host, HostUpdate); s.Node.On<RefineryUpdate>(Side.Guest, (c, m) => { if (Revisions.TryAcceptUpdate(m.Revision)) Set(m.ItemId, m.ItemCount); });
            snap?.Register(SnapStep.Refinery, ctx => { var m = new InitialRefinery { Revision = Revisions.Current }; foreach (var kv in Items) m.Items[kv.Key] = kv.Value; ctx.Send(m); });
            s.Node.On<InitialRefinery>(Side.Guest, (c, m) => { if (Revisions.TryApplySnapshot(m.Revision)) { Items.Clear(); foreach (var kv in m.Items) Set(kv.Key, kv.Value); } });
        }
        void Set(int item, int count) { Items[item] = count; OnCount(item, count); }
        Verdict HostUpdate(Ctx c, RefineryUpdate m)
        {
            if (!HostAuthority.Require(c)) return Verdict.Reject;
            var look = _ledger.Observe(c.SenderId, m.RequestId, m.ItemId, m.Delta);
            Items.TryGetValue(m.ItemId, out int cur);
            if (look == RefineryRequestLookup.Duplicate) { _s.Node.SendTo(c.Sender, new RefineryUpdate { RequestId = m.RequestId, ItemId = m.ItemId, ItemCount = cur, Revision = Revisions.Current }); return Verdict.Reject; }   // current canonical state, effect not re-run
            if (!RefineryMutationPolicy.TryApplyNewRequest(look, cur, m.Delta, out int next)) return Verdict.Reject;
            Set(m.ItemId, next); _s.Node.SendToAll(new RefineryUpdate { RequestId = m.RequestId, ItemId = m.ItemId, ItemCount = next, Revision = Revisions.Next() });
            return Verdict.Reject;
        }
        public void Request(int item, int delta)
        {
            var m = new RefineryUpdate { RequestId = Guid.NewGuid(), ItemId = item, Delta = delta };
            if (_s.IsHost) HostUpdate(new Ctx(_s.Node, new Peer { PlayerId = _s.LocalId, Registered = true }, true), m); else _s.Node.SendToHost(m);
        }
    }

    // =================================================================== time, weather, events
    /// <summary>
    /// Weather and time. The host is authoritative for the forecast (global revision) and world time. Guests: "new" is strictly above
    /// the latest received; only the latest received and above the last applied is applied; while the scene is not ready the newest is
    /// kept pending (replaced only by a higher one above the last applied) and applied from the main-thread pump. A fast-forward to an
    /// earlier time is ignored. Everything resets on connect and disconnect.
    /// </summary>
    public sealed class WorldClockSync
    {
        long _latestReceived, _lastApplied; public double Time; public byte[] AppliedWeather; public long AppliedRevision; byte[] _pending; long _pendingRev;
        public bool ModelReady = true;
        /// <summary>Applies a forecast to the game; returns false when the scene is not ready (kept pending and retried).</summary>
        public Func<byte[], bool> ApplyWeather = d => true;
        readonly Session _s; long _hostRev; byte[] _hostLast;
        public long LatestReceived => _latestReceived; public bool HasPending => _pending != null;
        public WorldClockSync(Session s, SnapshotHost snap = null)
        {
            _s = s;
            s.Node.On<WorldTime>(Side.Guest, (c, m) => { if (WeatherSyncPolicy.IsNewRevision(m.Revision, _latestTime)) { _latestTime = m.Revision; if (WeatherSyncPolicy.ShouldCorrectWorldTime(m.Time, Time)) Time = m.Time; } });
            s.Node.On<FastForward>(Side.Both, (c, m) => { if (double.IsFinite(m.Time) && m.Time > Time) Time = m.Time; return c.OnHost ? Verdict.Relay : Verdict.Accept; });   // a fast-forward to an earlier time is ignored
            s.Node.On<WeatherUpdate>(Side.Guest, (c, m) => OnWeatherRevision(m.Revision, m.Data));
            s.Node.On<InitialWeather>(Side.Guest, (c, m) => OnWeatherRevision(m.Revision, m.Data));
            snap?.Register(SnapStep.Weather, ctx => { if (_hostLast != null) ctx.Send(new InitialWeather { Revision = _hostRev, Data = _hostLast }); });
            s.ServerStarted += Reset; s.ClientJoined += Reset; s.ClientDisconnected += _ => Reset();
        }
        long _latestTime;
        public void Reset() { _latestReceived = _lastApplied = _pendingRev = _latestTime = 0; _pending = null; AppliedWeather = null; AppliedRevision = 0; _hostRev = 0; _hostLast = null; }
        public void OnWeatherRevision(long revision, byte[] data)
        {
            if (!WeatherSyncPolicy.IsNewRevision(revision, _latestReceived)) return; _latestReceived = revision;
            if (!ModelReady || !TryApply(revision, data)) { if (WeatherSyncPolicy.ShouldReplacePending(revision, _pendingRev, _lastApplied)) { _pending = data; _pendingRev = revision; } }
        }
        bool TryApply(long rev, byte[] data)
        {
            if (!WeatherSyncPolicy.CanApplyRevision(rev, _latestReceived, _lastApplied)) return true;             // superseded: nothing to do
            if (!ApplyWeather(data)) return false;
            _lastApplied = rev; AppliedRevision = rev; AppliedWeather = data; if (_pendingRev <= rev) { _pending = null; _pendingRev = 0; } return true;
        }
        /// <summary>Main-thread retry of a pending forecast (scene became ready).</summary>
        public bool Pump() { if (_pending == null || !ModelReady) return false; var (r, d) = (_pendingRev, _pending); if (!WeatherSyncPolicy.CanApplyRevision(r, _latestReceived, _lastApplied)) { _pending = null; return false; } return TryApply(r, d); }
        /// <summary>Native registries/directors may change with no new host revision.</summary>
        public bool ReapplyWeather() => ModelReady && AppliedWeather != null && ApplyWeather(AppliedWeather);
        public void ModelBecameReady() { ModelReady = true; Pump(); }
        public long NextHostRevision() => ++_hostRev;
        /// <summary>Host: publishes the forecast when it differs from the last one published (new global revision).</summary>
        public bool HostPublish(byte[] data)
        {
            if (!_s.IsHost || data == null || (_hostLast != null && _hostLast.AsSpan().SequenceEqual(data))) return false;
            _hostLast = data; _s.Node.SendToAll(new WeatherUpdate { Revision = ++_hostRev, Data = data }); return true;
        }
    }

    /// <summary>Zone unlock events: counts only grow; a stale count rolls nothing back; unlocks reach late joiners.</summary>
    public sealed class EventSync
    {
        public bool HasHostSnapshot { get; private set; }
        public Action OnHostSnapshot = () => { };
        readonly Dictionary<(string, string), int> _counts = new Dictionary<(string, string), int>(); readonly Session _s;
        public Action<string, string, int> OnRaise = (e, d, n) => { };
        public Action<string, string> Trace = (name, details) => { };
        void Record(string name, string details) { try { Trace(name, details); } catch { } }
        public EventSync(Session s, SnapshotHost snap)
        {
            _s = s; s.ServerStarted += () => { _counts.Clear(); HasHostSnapshot = false; }; s.ClientJoined += () => { _counts.Clear(); HasHostSnapshot = false; };
            s.Node.On<EventRaised>(Side.Host, (c, m) => { if (!HostAuthority.Require(c) || !Valid(m)) return Verdict.Reject; return Apply(m) ? HostAuthority.Broadcast(c, m) : Verdict.Reject; });
            s.Node.On<EventRaised>(Side.Guest, (c, m) => { Apply(m); });
            snap?.Register(SnapStep.RaisedEvents, ctx => { var m = new InitialEvents(); foreach (var kv in _counts) if (!EventSyncPolicy.IsZoneUnlockedEvent(kv.Key.Item1) || EventSyncPolicy.ShouldIncludeZoneUnlockSnapshot(kv.Value)) m.Items.Add(new EventRecord { EventKey = kv.Key.Item1, DataKey = kv.Key.Item2, Count = kv.Value }); ctx.Send(m); });
            s.Node.On<InitialEvents>(Side.Guest, (c, m) => { foreach (var e in m.Items) { var update = new EventRaised { EventKey = e.EventKey, DataKey = e.DataKey, Count = e.Count }; if (!Valid(update)) continue; if (!Apply(update) && e.EventKey == "fogRevealed" && Count(e.EventKey, e.DataKey) > 0) OnRaise(e.EventKey, e.DataKey, Count(e.EventKey, e.DataKey)); } HasHostSnapshot = true; OnHostSnapshot(); });
        }
        static bool Valid(EventRaised m) => !string.IsNullOrWhiteSpace(m.EventKey) && m.Count >= 0 && (!EventSyncPolicy.IsZoneUnlockedEvent(m.EventKey) || EventSyncPolicy.IsValidZoneDataKey(m.DataKey));
        bool Apply(EventRaised m)
        {
            int target = EventSyncPolicy.NormalizeTargetCount(m.EventKey, m.Count); _counts.TryGetValue((m.EventKey, m.DataKey ?? ""), out int cur);
            int inc = EventSyncPolicy.GetIncrement(cur, target);
            Record("event-state-received", $"key={m.EventKey}; data={m.DataKey}; wire={m.Count}; cached={cur}; target={target}; increment={inc}");
            if (inc == 0) return false;      // a stale count rolls nothing back
            _counts[(m.EventKey, m.DataKey ?? "")] = cur + inc; OnRaise(m.EventKey, m.DataKey, cur + inc); return true;
        }
        /// <summary>Host map checkpoint: repairs a missed physical apply without incrementing progression.</summary>
        public void RepublishFog()
        {
            if (!_s.IsHost) return;
            var snapshot = new InitialEvents();
            foreach (var pair in _counts) if (pair.Key.Item1 == "fogRevealed" && pair.Value > 0)
                snapshot.Items.Add(new EventRecord { EventKey = pair.Key.Item1, DataKey = pair.Key.Item2, Count = pair.Value });
            if (snapshot.Items.Count > 0) _s.Node.SendToAll(snapshot);
        }
        public int Count(string key, string data) => _counts.TryGetValue((key, data ?? ""), out var n) ? n : 0;
        public IEnumerable<(string Data, int Count)> FogRecords
        {
            get { foreach (var pair in _counts) if (pair.Key.Item1 == "fogRevealed" && pair.Value > 0) yield return (pair.Key.Item2, pair.Value); }
        }
        /// <summary>Host: records a count from its own save (never sent, never lowered) so joiners receive it in the snapshot.</summary>
        public void Seed(string key, string data, int count)
        {
            if (string.IsNullOrWhiteSpace(key) || count <= 0) return; int target = EventSyncPolicy.NormalizeTargetCount(key, count);
            if (!_counts.TryGetValue((key, data ?? ""), out var cur) || target > cur) _counts[(key, data ?? "")] = target;
        }
        public void Raise(string key, string data, int count) { if (Applying.Active) return; var m = new EventRaised { EventKey = key, DataKey = data ?? "", Count = count }; if (_s.IsHost) { Apply(m); _s.Node.SendToAll(m); } else _s.Node.SendToHost(m); }
    }

    // =================================================================== gardens, gordos, pods, teleporters, upgrades
    /// <summary>
    /// Gardens: plant, clear and timer share one host-assigned revision stream per garden. Guest plant/clear requests carry no revision;
    /// the host assigns one and relays the result to everyone (the originator too), so older delayed messages cannot roll the crop back.
    /// A snapshot crop is stale only against a newer crop event or tombstone; timer-only revisions never supersede it. Crop 0 = empty.
    /// </summary>
    public sealed class GardenSync
    {
        readonly Session _s; readonly RevisionTracker<string> _rev = new RevisionTracker<string>(); readonly GardenCropStateCache _crops = new GardenCropStateCache();
        public readonly Dictionary<string, int> Crops = new Dictionary<string, int>();
        public Action<string, int> OnCrop = (g, t) => { };
        /// <summary>Applies a timer state on a peer that does not run the garden's growth.</summary>
        public Action<GardenUpdate> OnTimer = m => { };
        /// <summary>Who runs each garden's growth (null: everyone may publish timers, used by tests of the crop path only).</summary>
        public KeyLeases Leases;
        public GardenSync(Session s)
        {
            _s = s;
            s.Node.On<GardenPlant>(Side.Host, (c, m) => { if (!HostAuthority.Require(c) || string.IsNullOrEmpty(m.GardenId) || m.ActorType < 0) return Verdict.Reject; m.Revision = _rev.Next(m.GardenId); Set(m.GardenId, m.ActorType, m.Revision); return HostAuthority.Broadcast(c, m); });
            s.Node.On<GardenPlant>(Side.Guest, (c, m) => { if (m.ActorType >= 0 && _rev.TryAccept(m.GardenId, m.Revision)) Set(m.GardenId, m.ActorType, m.Revision); });
            s.Node.On<GardenUpdate>(Side.Host, (c, m) =>
            {
                if (!HostAuthority.Require(c) || !ValidTimer(m)) return Verdict.Reject;
                if (Leases != null && Leases.OwnerOf(m.GardenId) != c.SenderId) return Verdict.Reject;                 // only the garden's owner runs its growth
                m.Revision = _rev.Next(m.GardenId); OnTimer(m); return HostAuthority.Broadcast(c, m);
            });
            s.Node.On<GardenUpdate>(Side.Guest, (c, m) => { if (ValidTimer(m) && _rev.TryAccept(m.GardenId, m.Revision) && (Leases == null || Leases.OwnerOf(m.GardenId) != _s.LocalId)) OnTimer(m); });
        }
        static bool ValidTimer(GardenUpdate m) => !string.IsNullOrWhiteSpace(m.GardenId) && double.IsFinite(m.NextSpawnTime) && float.IsFinite(m.StoredWater) && m.StoredWater >= 0;
        void Set(string g, int type, long rev) { if (rev > 0) _crops.TryRemember(g, rev, type); if (type <= 0) Crops.Remove(g); else Crops[g] = type; OnCrop(g, type); }
        public int CropOf(string g) => Crops.TryGetValue(g, out var t) ? t : 0;
        public long Revision(string g) => _rev.Last(g);

        /// <summary>A local plant (crop type) or clear (0) that is not a received message: the host publishes, a guest asks.</summary>
        public void LocalCrop(string plotId, string gardenId, int type)
        {
            if (Applying.Active || string.IsNullOrEmpty(gardenId) || type < 0) return;
            var m = new GardenPlant { PlotId = plotId ?? gardenId, GardenId = gardenId, ActorType = type };
            if (_s.IsHost) { m.Revision = _rev.Next(gardenId); Set(gardenId, type, m.Revision); _s.Node.SendToAll(m); }
            else if (_s.IsGuest) _s.Node.SendToHost(m);
        }
        /// <summary>The garden's owner publishes its growth timer (about every 5 s).</summary>
        public void LocalTimer(string gardenId, double nextSpawnTime, float storedWater, bool ripens)
        {
            if (Applying.Active || (Leases != null && Leases.OwnerOf(gardenId) != _s.LocalId)) return;
            var m = new GardenUpdate { GardenId = gardenId, NextSpawnTime = nextSpawnTime, StoredWater = storedWater, NextSpawnRipens = ripens };
            if (!ValidTimer(m)) return;
            if (_s.IsHost) { m.Revision = _rev.Next(gardenId); _s.Node.SendToAll(m); } else if (_s.IsGuest) _s.Node.SendToHost(m);
        }
        /// <summary>Host: the revision a snapshot entry carries (starts the stream for an untouched garden).</summary>
        public long SnapshotRevision(string g) { var r = _rev.Last(g); return r > 0 ? r : _rev.Next(g); }
        /// <summary>Guest: one snapshot crop. Returns the crop to apply, or null when a newer crop event already decided it.</summary>
        public int? ApplySnapshotCrop(string g, long revision, int crop)
        {
            if (string.IsNullOrEmpty(g) || revision <= 0) return null;
            var sel = _crops.SelectSnapshotCrop(g, revision, crop);
            if (_rev.Last(g) < revision) _rev.TryAccept(g, revision);
            if (sel == null) return null;
            if (sel.Value <= 0) Crops.Remove(g); else Crops[g] = sel.Value; OnCrop(g, sel.Value); return sel;
        }
        public void Clear() { _rev.Clear(); _crops.Clear(); Crops.Clear(); }
    }

    /// <summary>Gordo feeding counts each feed once (request ids), adopts a host target of 0, and pays a burst out once.</summary>
    public sealed class GordoSync
    {
        readonly Session _s; public sealed class State { public int Eaten, Required, Type; public bool Popped, Seen; }
        public readonly Dictionary<string, State> Gordos = new Dictionary<string, State>(); readonly HashSet<string> _feeds = new HashSet<string>(); readonly HashSet<string> _paid = new HashSet<string>();
        readonly GordoFeedRequestLedger _feedRequests = new GordoFeedRequestLedger();
        readonly GordoFeedActorLedger _feedActors = new GordoFeedActorLedger();
        public Action<string> OnPayout = id => { };
        public Func<string, int, int> FoodUnits = (id, type) => 1;
        /// <summary>Called on every peer when a gordo's canonical state changed (id, state); the game adapter applies it.</summary>
        public Action<string, State> OnState = (id, st) => { };
        public GordoSync(Session s)
        {
            _s = s;
            s.ServerStarted += ResetGordos; s.ClientJoined += ResetGordos;
            s.Node.On<GordoFeed>(Side.Host, (c, m) =>
            {
                if (!HostAuthority.Require(c) || string.IsNullOrWhiteSpace(m.Id) || !Gordos.TryGetValue(m.Id, out var g)) return Verdict.Reject;
                if (g.Required <= 0 && m.RequiredFoodCount > 0) g.Required = m.RequiredFoodCount;
                if (g.Required <= 0 || g.Type <= 0 || m.GordoType != g.Type) return Verdict.Reject;
                bool actorSeen = _feedActors.TryGetAcceptedCount(m.Id, m.FoodActorId, out int actorCount);
                string requestKey = c.SenderId + ":" + (m.RequestId ?? "");
                bool requestSeen = _feedRequests.Contains(m.Id, requestKey);
                var decision = GordoSlimeAuthorityPolicy.DecideFeed(m.Id, requestKey, m.FoodActorId,
                    g.Eaten, g.Required, m.RequiredFoodCount, m.NewFoodCount, g.Popped,
                    actorSeen, actorCount, requestSeen);
                if (decision == GordoFeedDecision.DuplicateSnapshot)
                {
                    if (!requestSeen) _feedRequests.TryAccept(m.Id, requestKey);
                    int canonical = GordoSlimeAuthorityPolicy.CanonicalCountForDuplicateRequest(g.Eaten, actorCount);
                    _s.Node.SendTo(c.Sender, new GordoFeed { Id = m.Id, RequestId = m.RequestId, FoodActorId = m.FoodActorId,
                        NewFoodCount = canonical, RequiredFoodCount = g.Required, GordoType = g.Type });
                    return Verdict.Reject;
                }
                if (decision != GordoFeedDecision.CommitOne) return Verdict.Reject;
                int units = FoodUnits(m.Id, m.FoodType); if (units <= 0) return Verdict.Reject;
                int acceptedCount = (int)Math.Min(g.Required, (long)g.Eaten + units);
                if (!_feedRequests.TryAccept(m.Id, requestKey) || !_feedActors.TryAccept(m.Id, m.FoodActorId, acceptedCount, out _)) return Verdict.Reject;
                g.Eaten = acceptedCount; m.NewFoodCount = g.Eaten; m.RequiredFoodCount = g.Required; m.GordoType = g.Type;
                if (g.Eaten >= g.Required && _paid.Add(m.Id))
                {
                    g.Popped = true; OnPayout(m.Id); OnState(m.Id, g);
                    return HostAuthority.Broadcast(c, new GordoBurst { Id = m.Id, EatenCount = g.Eaten, RequiredFoodCount = g.Required, GordoType = g.Type, RewardAuthorityId = _s.LocalId });
                }
                OnState(m.Id, g); return HostAuthority.Broadcast(c, m);
            });
            s.Node.On<GordoFeed>(Side.Guest, (c, m) =>
            {
                if (string.IsNullOrWhiteSpace(m.Id) || m.RequiredFoodCount <= 0 || m.GordoType <= 0 || m.NewFoodCount < 0 || m.NewFoodCount > m.RequiredFoodCount) return;
                var g = Get(m.Id, m.RequiredFoodCount, m.GordoType);
                if ((g.Required > 0 && g.Required != m.RequiredFoodCount) || (g.Type > 0 && g.Type != m.GordoType)) return;
                g.Required = m.RequiredFoodCount; g.Type = m.GordoType; g.Eaten = Math.Max(g.Eaten, m.NewFoodCount); OnState(m.Id, g);
            });
            s.Node.On<GordoBurst>(Side.Host, (c, m) =>
            {
                if (!HostAuthority.Require(c) || string.IsNullOrWhiteSpace(m.Id) || !Gordos.TryGetValue(m.Id, out var gb)) return Verdict.Reject;
                if (gb.Required <= 0 && m.RequiredFoodCount > 0) gb.Required = m.RequiredFoodCount;
                if (gb.Required <= 0 || gb.Type <= 0 || m.GordoType != gb.Type ||
                    !GordoSlimeAuthorityPolicy.CanAcceptBurst(m.Id, gb.Eaten, gb.Required, m.EatenCount, m.RequiredFoodCount, gb.Popped) ||
                    !_paid.Add(m.Id)) return Verdict.Reject;
                gb.Popped = true; gb.Eaten = gb.Required; m.EatenCount = gb.Eaten; m.RequiredFoodCount = gb.Required;
                m.GordoType = gb.Type; m.RewardAuthorityId = _s.LocalId; OnPayout(m.Id); OnState(m.Id, gb); return HostAuthority.Broadcast(c, m);
            });
            s.Node.On<GordoBurst>(Side.Guest, (c, m) =>
            {
                if (string.IsNullOrWhiteSpace(m.Id) || m.RequiredFoodCount <= 0 || m.GordoType <= 0 || m.EatenCount < m.RequiredFoodCount) return;
                var g = Get(m.Id, m.RequiredFoodCount, m.GordoType);
                if (g.Popped || (g.Required > 0 && g.Required != m.RequiredFoodCount) || (g.Type > 0 && g.Type != m.GordoType)) return;
                g.Required = m.RequiredFoodCount; g.Type = m.GordoType; g.Popped = true; g.Eaten = g.Required; OnState(m.Id, g);
            });
        }
        void ResetGordos() { ClearFeedMemories(); Gordos.Clear(); _paid.Clear(); }
        void ClearFeedMemories() { _feeds.Clear(); _feedRequests.Clear(); _feedActors.Clear(); }
        State Get(string id, int required, int type)
        {
            if (!Gordos.TryGetValue(id, out var g)) Gordos[id] = g = new State { Required = required, Type = type };
            if (g.Required <= 0 && required > 0) g.Required = required;                                                                // a host target of 0 is adopted
            if (g.Type <= 0 && type > 0) g.Type = type;
            return g;
        }
        public void Seed(string id, int required, int type, int eaten = 0, bool popped = false)
        {
            // Snapshot and live traffic use separate lanes: an older initial record
            // cannot reverse a terminal burst or food already confirmed this session.
            if (Gordos.TryGetValue(id, out var current))
            {
                popped |= current.Popped;
                eaten = Math.Max(eaten, current.Eaten);
                if (required <= 0) required = current.Required;
                if (type <= 0) type = current.Type;
            }
            Gordos[id] = new State { Required = required, Type = type, Eaten = popped ? Math.Max(eaten, required) : eaten, Popped = popped };
        }
        /// <summary>Host: a local native feed or burst; records it and publishes the canonical state.</summary>
        public int HostLocal(string id, int eaten, int required, int type, bool popped, long foodActorId = 0, int foodUnits = 1)
        {
            if (!_s.IsHost || string.IsNullOrEmpty(id)) return eaten;
            bool known = Gordos.ContainsKey(id); var g = Get(id, required, type);
            if (g.Popped) return g.Eaten;
            if (foodActorId != 0)
            {
                if (!_feedActors.TryGetAcceptedCount(id, foodActorId, out _))
                {
                    int units = Math.Max(1, foodUnits);
                    if (!known) g.Eaten = Math.Max(0, eaten - units);
                    int accepted = (int)Math.Min(g.Required, (long)g.Eaten + units);
                    if (_feedActors.TryAccept(id, foodActorId, accepted, out _)) g.Eaten = accepted;
                }
            }
            else g.Eaten = Math.Max(g.Eaten, eaten);
            popped |= g.Required > 0 && g.Eaten >= g.Required;
            if (popped && g.Required > 0) g.Eaten = g.Required;
            if (popped)
            {
                if (!_paid.Add(id)) return g.Eaten;
                g.Popped = true; OnPayout(id); OnState(id, g);
                _s.Node.SendToAll(new GordoBurst { Id = id, EatenCount = g.Eaten, RequiredFoodCount = g.Required, GordoType = g.Type, RewardAuthorityId = _s.LocalId });
            }
            else _s.Node.SendToAll(new GordoFeed { Id = id, FoodActorId = foodActorId, NewFoodCount = g.Eaten, RequiredFoodCount = g.Required, GordoType = g.Type });
            return g.Eaten;
        }
    }

    /// <summary>Treasure pods open exactly once: the first request pays out, retries and a second opener get nothing.</summary>
    public sealed class TreasurePodSystem
    {
        readonly HashSet<int> _opened = new HashSet<int>(); readonly Session _s; public Action<int, PodRewards> OnPayout = (id, rewards) => { };
        public TreasurePodSystem(Session s)
        {
            _s = s; s.Node.On<PodOpen>(Side.Host, (c, m) =>
            {
                if (!HostAuthority.Require(c)) return Verdict.Reject;
                if (!_opened.Add(m.PodId)) { _s.Node.SendTo(c.Sender, new PodOpen { PodId = m.PodId, RequestId = m.RequestId, Rejected = true }); return Verdict.Reject; }
                OnPayout(m.PodId, m.Rewards); return HostAuthority.Broadcast(c, m);
            });
            s.Node.On<PodOpen>(Side.Guest, (c, m) => { if (!m.Rejected) _opened.Add(m.PodId); });
        }
        public bool IsOpen(int id) => _opened.Contains(id);
        public void LoadOpened(IEnumerable<int> ids) { foreach (var i in ids) _opened.Add(i); }
    }

    /// <summary>
    /// Teleporter links (source teleporter id -> destination node id): host-authoritative, one destination per source (replacing a
    /// destination drops the old edge so no later snapshot resurrects it). Unlink tombstones keep removed edges out of snapshots until a
    /// new authoritative link. A guest publishes nothing before its first snapshot (its own save must not feed old links back), and the
    /// snapshot is authoritative: guest links missing from it are removed.
    /// </summary>
    public sealed class TeleporterLinks
    {
        public readonly Dictionary<string, (string dest, int scene)> Links = new Dictionary<string, (string, int)>(); readonly Session _s;
        public readonly PendingCache<string, (string dest, int scene)> Unresolved = new PendingCache<string, (string, int)>(256, 15_000);
        readonly HashSet<(string, string)> _tombstones = new HashSet<(string, string)>();
        public Func<string, bool> EndpointLoaded = id => true;
        public Action<string, string, int> OnLink = (s, d, g) => { }; public Action<string, string> OnUnlink = (s, d) => { };
        public bool SnapshotApplied { get; private set; }
        public TeleporterLinks(Session s, SnapshotHost snap)
        {
            _s = s;
            s.Node.On<TeleporterLink>(Side.Host, (c, m) => { if (!HostAuthority.Require(c) || !TeleporterLinkSnapshotPolicy.IsValidCachedLink(m.SourceNodeId, m.DestinationNodeId)) return Verdict.Reject; Add(m.SourceNodeId, m.DestinationNodeId, m.DestinationSceneGroup); return HostAuthority.Broadcast(c, m); });
            s.Node.On<TeleporterLink>(Side.Guest, (c, m) => { if (TeleporterLinkSnapshotPolicy.IsValidCachedLink(m.SourceNodeId, m.DestinationNodeId)) Add(m.SourceNodeId, m.DestinationNodeId, m.DestinationSceneGroup); });
            s.Node.On<TeleporterUnlink>(Side.Host, (c, m) => { if (!HostAuthority.Require(c) || string.IsNullOrWhiteSpace(m.SourceNodeId)) return Verdict.Reject; Remove(m.SourceNodeId, m.DestinationNodeId); return HostAuthority.Broadcast(c, m); });
            s.Node.On<TeleporterUnlink>(Side.Guest, (c, m) => Remove(m.SourceNodeId, m.DestinationNodeId));
            snap?.Register(SnapStep.TeleporterLinks, ctx => { var m = new InitialTeleporterLinks(); foreach (var kv in Links) if (TeleporterLinkSnapshotPolicy.ShouldIncludeSnapshotLink(kv.Key, kv.Value.dest, _tombstones.Contains((kv.Key, kv.Value.dest)))) m.Items.Add(new TeleporterRecord { Source = kv.Key, Destination = kv.Value.dest, SceneGroup = kv.Value.scene }); ctx.Send(m); });
            s.Node.On<InitialTeleporterLinks>(Side.Guest, (c, m) =>
            {
                var keep = new HashSet<string>(m.Items.Select(r => r.Source));
                foreach (var kv in Links.ToList()) if (!keep.Contains(kv.Key) || m.Items.First(r => r.Source == kv.Key).Destination != kv.Value.dest) { Links.Remove(kv.Key); OnUnlink(kv.Key, kv.Value.dest); }
                foreach (var r in m.Items) if (TeleporterLinkSnapshotPolicy.IsValidCachedLink(r.Source, r.Destination)) Add(r.Source, r.Destination, r.SceneGroup);
                SnapshotApplied = true;
            });
            s.ClientJoined += () => { Links.Clear(); _tombstones.Clear(); Unresolved.Clear(); SnapshotApplied = false; };
            s.ServerStarted += () => { Links.Clear(); _tombstones.Clear(); Unresolved.Clear(); SnapshotApplied = true; };
        }
        void Add(string src, string dst, int scene)
        {
            if (Links.TryGetValue(src, out var cur)) { if (cur.dest == dst) { _tombstones.Remove((src, dst)); return; } OnUnlink(src, cur.dest); }   // replacing a destination drops the old edge
            Links[src] = (dst, scene); _tombstones.Remove((src, dst));
            if (!EndpointLoaded(src) || !EndpointLoaded(dst)) Unresolved.Put(src, (dst, scene));
            OnLink(src, dst, scene);
        }
        /// <summary>Removes only the source's own link when its destination matches; the partner pair stays.</summary>
        void Remove(string src, string dst)
        {
            if (Links.TryGetValue(src, out var l) && (string.IsNullOrEmpty(dst) || l.dest == dst)) { Links.Remove(src); _tombstones.Add((src, l.dest)); OnUnlink(src, l.dest); }
            else if (!string.IsNullOrEmpty(dst)) _tombstones.Add((src, dst));
            Unresolved.TryTake(src, out _);
        }
        public bool IsTombstoned(string src, string dst) => _tombstones.Contains((src, dst));
        public void OnEndpointLoaded(string id) { if (Unresolved.TryTake(id, out _)) { } }
        bool MayPublish => !Applying.Active && (_s.IsHost || (_s.IsGuest && SnapshotApplied));
        /// <summary>A link the local game made (not applying a message).</summary>
        public void LocalLink(string src, string dst, int scene)
        {
            if (!MayPublish || !TeleporterLinkSnapshotPolicy.IsValidCachedLink(src, dst) || (Links.TryGetValue(src, out var cur) && cur.dest == dst)) return;
            var m = new TeleporterLink { SourceNodeId = src, DestinationNodeId = dst, DestinationSceneGroup = scene };
            if (_s.IsHost) { Add(src, dst, scene); _s.Node.SendToAll(m); } else _s.Node.SendToHost(m);
        }
        /// <summary>An unlink the local game made, observed by watching recorded links (RemoveDestination is never patched).</summary>
        public void LocalUnlink(string src, string dst)
        {
            if (!MayPublish || string.IsNullOrWhiteSpace(src) || !Links.TryGetValue(src, out var cur) || (dst != null && cur.dest != dst)) return;
            var m = new TeleporterUnlink { SourceNodeId = src, DestinationNodeId = cur.dest };
            if (_s.IsHost) { Remove(src, cur.dest); _s.Node.SendToAll(m); } else _s.Node.SendToHost(m);
        }
    }

    /// <summary>
    /// Paired gadgets (warp depots, linked cannons) by actor id: reciprocal pairs held by the host, never matched by proximity. Links whose
    /// endpoints are not known yet (spawns travel on another lane) are deferred up to 15 s (at most 128) and revalidated. A destroyed
    /// gadget drops its pair records. Guests repair both directions from the host map.
    /// </summary>
    public sealed class GadgetLinks
    {
        public const int MaxPending = 128; public const long PendingMs = 15_000;
        readonly Session _s; public readonly Dictionary<long, long> Pairs = new Dictionary<long, long>();
        readonly PendingCache<long, (long partner, string sender)> _pending;
        /// <summary>Host: (gadget, partner, sender) -> null = endpoints not known yet (defer), true = valid pair, false = reject.</summary>
        public Func<long, long, string, bool?> Authorize = (a, b, s) => true;
        public Action<long, long> OnPair = (a, b) => { }; public Action<long> OnUnpair = a => { };
        public GadgetLinks(Session s, SnapshotHost snap, Func<long> clock = null)
        {
            _s = s; _pending = new PendingCache<long, (long, string)>(MaxPending, PendingMs, clock);
            s.Node.On<GadgetLinking>(Side.Host, (c, m) => { if (!HostAuthority.Require(c)) return Verdict.Reject; HostLink(m.GadgetId, m.PartnerId, c.SenderId); return Verdict.Reject; });
            s.Node.On<GadgetLinking>(Side.Guest, (c, m) => { if (m.PartnerId == 0) Unpair(m.GadgetId); else Record(m.GadgetId, m.PartnerId); });
            snap?.Register(SnapStep.GadgetLinks, ctx => { var m = new InitialGadgetLinks(); foreach (var kv in Pairs) if (kv.Key < kv.Value) m.Items.Add(new LongPair { A = kv.Key, B = kv.Value }); ctx.Send(m); });
            s.Node.On<InitialGadgetLinks>(Side.Guest, (c, m) => { foreach (var p in m.Items) Record(p.A, p.B); });
            s.ClientJoined += Clear; s.ServerStarted += Clear;
        }
        void HostLink(long a, long b, string sender)
        {
            if (a == 0 || b == 0 || a == b) return;
            var ok = Authorize(a, b, sender);
            if (ok == null) { _pending.Put(a, (b, sender)); return; }
            if (ok == true && Record(a, b)) _s.Node.SendToAll(new GadgetLinking { GadgetId = a, PartnerId = b });
        }
        bool Record(long a, long b)
        {
            if (GadgetLinkPairPolicy.IsCurrentPair(Pairs, a, b)) return false;
            if (!GadgetLinkPairPolicy.TryRecordPair(Pairs, a, b, out var da, out var db)) return false;
            if (da != 0) OnUnpair(da); if (db != 0) OnUnpair(db);
            OnPair(a, b); return true;
        }
        void Unpair(long a) { if (Pairs.TryGetValue(a, out var b)) { Pairs.Remove(a); if (Pairs.TryGetValue(b, out var back) && back == a) Pairs.Remove(b); OnUnpair(a); OnUnpair(b); } }
        public long PartnerOf(long a) => Pairs.TryGetValue(a, out var b) ? b : 0;
        public int PendingCount => _pending.Count;
        /// <summary>Host: retries deferred links (spawn and owner records arrived); expired ones are dropped by the cache.</summary>
        public void Revalidate()
        {
            if (!_s.IsHost) return; _pending.Prune();
            foreach (var a in _pending.Keys.ToList()) { if (!_pending.TryTake(a, out var p)) continue; HostLink(a, p.partner, p.sender); }
        }
        /// <summary>A pair the local game made; the host records and publishes it, a guest asks.</summary>
        public void LocalPair(long a, long b)
        {
            if (Applying.Active || a == 0 || b == 0 || a == b || GadgetLinkPairPolicy.IsCurrentPair(Pairs, a, b)) return;
            if (_s.IsHost) HostLink(a, b, _s.LocalId); else if (_s.IsGuest) _s.Node.SendToHost(new GadgetLinking { GadgetId = a, PartnerId = b });
        }
        /// <summary>Host: a gadget is gone (model destroyed, not just unloaded): drop its pair and tell everyone.</summary>
        public void GadgetDestroyed(long a) { if (!Pairs.ContainsKey(a)) return; Unpair(a); if (_s.IsHost) _s.Node.SendToAll(new GadgetLinking { GadgetId = a, PartnerId = 0 }); }
        public void Clear() { Pairs.Clear(); _pending.Clear(); }
    }

    /// <summary>Upgrade purchases: the host validates price and level, charges once per request id, applies and announces. A guest never grants itself.</summary>
    public interface IUpgradeWorld { int Level(int upgradeId); int PriceFor(int upgradeId, int level); int Currency { get; } bool TryCharge(int amount); void SetLevel(int upgradeId, int level); }
    public sealed class UpgradeSync
    {
        readonly Session _s; readonly IUpgradeWorld _w; readonly UpgradePurchaseRequestLedger _ledger = new UpgradePurchaseRequestLedger(4096); readonly Dictionary<int, ulong> _rev = new Dictionary<int, ulong>();
        public UpgradeSync(Session s, IUpgradeWorld w, SnapshotHost snap)
        {
            _s = s; _w = w;
            s.Node.On<PlayerUpgrade>(Side.Host, (c, m) => { if (m.Kind == PlayerUpgradeKind.Request && PurchaseHandler != null) { PurchaseHandler(c, m); return Verdict.Reject; } return m.Kind == PlayerUpgradeKind.Rejected ? Verdict.Reject : HostPurchase(c, m); });
            s.Node.On<PlayerUpgrade>(Side.Guest, (c, m) =>
            {
                if (m.Kind == PlayerUpgradeKind.Rejected) { OnRejected?.Invoke(m); return; }
                if (m.Kind != PlayerUpgradeKind.State) return;
                bool accept = AcceptState != null ? AcceptState(m) : (!_rev.TryGetValue(m.UpgradeId, out var r) || m.Revision > r || m.Revision == 0);
                if (!accept) return;
                _rev[m.UpgradeId] = Math.Max(_rev.GetValueOrDefault(m.UpgradeId), m.Revision); _w.SetLevel(m.UpgradeId, m.Level);
            });
            snap?.Register(SnapStep.Upgrades, ctx => { var m = new InitialUpgrades(); foreach (var id in AllIds().Concat(_rev.Keys).Distinct()) { m.Levels[id] = _w.Level(id); m.Revisions[id] = _rev.GetValueOrDefault(id); } ctx.Send(m); });
            s.Node.On<InitialUpgrades>(Side.Guest, (c, m) =>
            {
                foreach (var kv in m.Levels)
                {
                    ulong rev = m.Revisions.TryGetValue(kv.Key, out var r) ? r : 0;
                    if (AcceptSnapshot != null && !AcceptSnapshot(kv.Key, rev)) continue;                       // a pending purchase keeps its value until the correlated state arrives
                    _w.SetLevel(kv.Key, kv.Value); _rev[kv.Key] = rev;
                }
                SnapshotApplied?.Invoke();
            });
        }
        Verdict HostPurchase(Ctx c, PlayerUpgrade m)
        {
            if (!HostAuthority.Require(c)) return Verdict.Reject;
            sbyte expected = (sbyte)m.Level;
            if (!UpgradePurchaseRequestLedger.IsValidRequest(c.SenderId, m.RequestId, expected)) return Verdict.Reject;
            var look = _ledger.TryGetDecision(c.SenderId, m.RequestId, (byte)m.UpgradeId, expected, out bool accepted);
            if (look == UpgradePurchaseRequestLookup.ConflictingDuplicate) return Verdict.Reject;
            if (look == UpgradePurchaseRequestLookup.Duplicate) { Canonical(c.Sender, m.UpgradeId, m.RequestId); return Verdict.Reject; }                        // retry: current canonical state, nothing charged
            int level = _w.Level(m.UpgradeId); bool ok = level == m.Level && _w.TryCharge(_w.PriceFor(m.UpgradeId, level + 1));
            _ledger.Remember(c.SenderId, m.RequestId, (byte)m.UpgradeId, expected, ok);
            if (ok) { _w.SetLevel(m.UpgradeId, level + 1); _rev[m.UpgradeId] = _rev.GetValueOrDefault(m.UpgradeId) + 1; _s.Node.SendToAll(new PlayerUpgrade { UpgradeId = m.UpgradeId, Level = level + 1, Revision = _rev[m.UpgradeId], RequestId = m.RequestId }); }
            else Canonical(c.Sender, m.UpgradeId, m.RequestId);
            return Verdict.Reject;
        }
        void Canonical(Peer p, int id, Guid req) => _s.Node.SendTo(p, new PlayerUpgrade { UpgradeId = id, Level = _w.Level(id), Revision = _rev.GetValueOrDefault(id), RequestId = req });
        public void Request(int upgradeId) => _s.Node.SendToHost(new PlayerUpgrade { UpgradeId = upgradeId, Level = _w.Level(upgradeId), RequestId = Guid.NewGuid() });
        /// <summary>Every upgrade id the host's snapshot covers (the game adapter lists its definitions); the default is none.</summary>
        public Func<IEnumerable<int>> AllIds = () => Enumerable.Empty<int>();
        /// <summary>Host: a level changed through the host's own purchase; announce it with a new revision.</summary>
        public void HostLocalChanged(int upgradeId)
        {
            if (!_s.IsHost || Applying.Active || SuppressHostLocal()) return;
            _rev[upgradeId] = _rev.GetValueOrDefault(upgradeId) + 1;
            _s.Node.SendToAll(new PlayerUpgrade { UpgradeId = upgradeId, Level = _w.Level(upgradeId), Revision = _rev[upgradeId] });
        }
        /// <summary>Optional host purchase executor for Request messages (the owned native-transaction path); returns true when handled.</summary>
        public Func<Ctx, PlayerUpgrade, bool> PurchaseHandler;
        /// <summary>Optional guest ordering rules (owned request correlation): accept a live state / a snapshot entry.</summary>
        public Func<PlayerUpgrade, bool> AcceptState; public Func<int, ulong, bool> AcceptSnapshot; public Action<PlayerUpgrade> OnRejected;
        /// <summary>Host: true while a level change must not publish on its own (a host-run purchase emits one correlated revision).</summary>
        public Func<bool> SuppressHostLocal = () => false;
        /// <summary>Host: publishes the canonical level with a new revision (correlated to a request id when given).</summary>
        public void HostBroadcast(int upgradeId, int level, Guid requestId = default)
        {
            if (!_s.IsHost) return; _rev[upgradeId] = _rev.GetValueOrDefault(upgradeId) + 1;
            _s.Node.SendToAll(new PlayerUpgrade { UpgradeId = upgradeId, Level = level, Revision = _rev[upgradeId], RequestId = requestId, Kind = PlayerUpgradeKind.State });
        }
        public ulong RevisionOf(int upgradeId) => _rev.GetValueOrDefault(upgradeId);
        /// <summary>Raised on guests after the upgrade snapshot was applied.</summary>
        public event Action SnapshotApplied;
    }
}

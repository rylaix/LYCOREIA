// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Economy;
using Il2CppMonomiPark.SlimeRancher.Pedia;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.Snapshot;
using Lycoreia.World;
using UnityEngine;

namespace Lycoreia.Game.World
{
    /// <summary>
    /// Game adapter for one keyed model table of the GameModel (switches, doors, puzzle slots, depositors, prisma barriers):
    /// applies a value by pushing it into the model and notifying its participants (the scene object follows when loaded).
    /// </summary>
    public sealed class KeyedModelAdapter<TModel> : ILatchedModel where TModel : Il2CppSystem.Object
    {
        readonly Func<GameModel, Il2CppSystem.Collections.Generic.Dictionary<string, TModel>> _table;
        readonly Action<TModel, double> _push; readonly Func<TModel, double> _read; readonly Action<TModel> _notify; readonly Action<TModel, bool> _notifyWithMode;
        public KeyedModelAdapter(Func<GameModel, Il2CppSystem.Collections.Generic.Dictionary<string, TModel>> table, Func<TModel, double> read, Action<TModel, double> push, Action<TModel> notify, Action<TModel, bool> notifyWithMode = null)
        { _table = table; _read = read; _push = push; _notify = notify; _notifyWithMode = notifyWithMode; }

        public TModel Model(string key) { var g = GameState.Model; var t = g != null ? _table(g) : null; return t != null && key != null && t.TryGetValue(key, out var m) ? m : null; }
        public bool TryApply(string key, double value, bool fromSnapshot)
        {
            var m = Model(key); if (m == null) return false;
            try { using (Applying.Enter()) { _push(m, value); if (_notifyWithMode != null) _notifyWithMode(m, fromSnapshot); else _notify(m); } if (!fromSnapshot) Log.Info($"world {typeof(TModel).Name} {key}: applied {value}"); return true; }
            catch (Exception e) { Log.Debug($"apply {typeof(TModel).Name} {key}: {e.Message}"); return false; }
        }
        public double Current(string key, double fallback) { var m = Model(key); try { return m != null ? _read(m) : fallback; } catch { return fallback; } }
        /// <summary>Reverse lookup: the key of a model instance (hooks see the model, not its key).</summary>
        public string KeyOf(TModel model)
        {
            var g = GameState.Model; var t = g != null ? _table(g) : null; if (t == null || model == null) return null;
            // 2.1: the door/slot/barrier hooks call this for every object at zone load: cache pointer -> key, rebuild once on a miss
            if (_keyOf.TryGetValue(model.Pointer, out var cached) && t.TryGetValue(cached, out var hit) && hit != null && hit.Pointer == model.Pointer) return cached;
            if (g.Pointer != _keyOfModel || _keyOf.Count == 0 || !_keyOf.ContainsKey(model.Pointer))
            {
                _keyOf.Clear(); _keyOfModel = g.Pointer;
                foreach (var kv in t) if (kv.Value != null && kv.Key != null) _keyOf[kv.Value.Pointer] = kv.Key;
            }
            return _keyOf.TryGetValue(model.Pointer, out var key) ? key : null;
        }
        readonly Dictionary<IntPtr, string> _keyOf = new Dictionary<IntPtr, string>(); IntPtr _keyOfModel;
        public IEnumerable<(string key, double value)> All()
        {
            var g = GameState.Model; var t = g != null ? _table(g) : null; if (t == null) yield break;
            foreach (var kv in t) if (kv.Value != null && kv.Key != null) yield return (kv.Key, _read(kv.Value));
        }
    }

    /// <summary>One keyed kind: domain system + adapter + snapshot + publishing from the model's change notification.</summary>
    public sealed class KeyedKind<TModel, TMsg> where TModel : Il2CppSystem.Object where TMsg : Msg
    {
        public readonly KeyedModelAdapter<TModel> Adapter; public readonly LatchedSystem<TMsg> System; readonly string _name;
        public KeyedKind(string name, Session s, KeyedModelAdapter<TModel> adapter, Func<TMsg, (string, double)> read, Func<string, double, TMsg> make, Func<double, double, double> merge = null)
        { _name = name; Adapter = adapter; System = new LatchedSystem<TMsg>(s, adapter, read, make, merge); }

        /// <summary>The model notified its participants: publish when the value changed and the change is not one we applied.</summary>
        public void OnModelChanged(TModel model)
        {
            try
            {
                if (!Mod.IsNetworkActive || Applying.Active || model == null) return;
                var key = Adapter.KeyOf(model); if (key == null) return;
                double v = Adapter.Current(key, double.NaN); if (!double.IsFinite(v)) return;
                var known = System.Get(key); if (known == v) return;
                // a guest publishes only keys the host already established and that are applied: a zone loading from the guest's own
                // save notifies with stale values, which must never overwrite the host's state
                if (Mod.IsGuest && (known == null || System.Pending.Contains(key))) return;
                System.Publish(key, v);
                Log.Info($"world {_name} {key}: {(Mod.IsHost ? "published" : "sent to the host")} (value {v}, was {(known.HasValue ? known.Value.ToString() : "unknown")})");
                Mod.Diagnostics?.Record("world", "keyed-state-published", $"kind={_name}; id={key}; value={v}; authority={(Mod.IsHost ? "host" : "intent")}");
            }
            catch (Exception e) { Hooks.HookGuard.Fail(_name + ".notify", e); }
        }
        /// <summary>A model appeared (scene object bound): apply anything that arrived early.</summary>
        public void OnModelBound(TModel model) { try { var k = Adapter.KeyOf(model); if (k != null) System.OnModelReady(k); } catch { } }
        /// <summary>Scene objects bind without a hook (see WorldHooks): early states are retried from the pump, keeping their TTL.</summary>
        public void RetryPending() { try { System.RetryPending(); } catch (Exception e) { Log.Debug("world pending retry: " + e.Message); } }
        public IEnumerable<(string, double)> HostState() => Adapter.All();
    }

    /// <summary>Composition of the world systems over the live game (created once the game context exists).</summary>
    public static partial class WorldRuntime
    {
        public static KeyedKind<WorldSwitchModel, SwitchActivate> Switches;
        public static KeyedKind<AccessDoorModel, Lycoreia.Net.Messages.AccessDoor> Doors;
        public static KeyedKind<PuzzleSlotModel, Lycoreia.Net.Messages.PuzzleSlot> Slots;
        public static KeyedKind<PlortDepositorModel, Lycoreia.Net.Messages.PlortDepositor> Depositors;
        public static KeyedKind<PrismaBarrierModel, Lycoreia.Net.Messages.PrismaBarrier> Barriers;
        public static UpgradeSync Upgrades;
        public static CurrencyLedger Currency; public static MarketSystem Market; public static GordoSync Gordos; public static readonly GordoRewardLedger GordoRewards = new GordoRewardLedger(); public static WorldClockSync Clock; public static EventSync Events;
        static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);
        static readonly Dictionary<string, GordoSync.State> _pendingGordos = new Dictionary<string, GordoSync.State>(StringComparer.Ordinal);
        static readonly HashSet<string> _gordoBurstsStarted = new HashSet<string>(StringComparer.Ordinal);
        public static bool Ready => Switches != null;

        /// <summary>A locked door never relocks once anyone opened it (LOCKED = 0).</summary>
        static double DoorMerge(double cur, double inc) => inc == 0 && double.IsFinite(cur) && cur != 0 ? cur : inc;

        public static void Init(Session s, SnapshotHost snap)
        {
            if (Ready) return;
            Switches = new KeyedKind<WorldSwitchModel, SwitchActivate>("switch", s,
                new KeyedModelAdapter<WorldSwitchModel>(g => g.switches, m => (int)m.state, (m, v) => m.state = (SwitchHandler.State)(int)v, m => { if (m.gameObj != null) m.NotifyParticipants(); }, (m, snapshot) =>
                {
                    // Unloaded area (no scene object): the model holds the value and the object reads it when it loads. Notifying
                    // then threw a native NRE that the 0.25 s pending retry repeated for every key (2026-11-08 guest stutter).
                    if (m.gameObj == null) return;
                    var primary = m.gameObj.GetComponent<WorldStatePrimarySwitch>();
                    if (primary != null) primary.SetStateForAll(m.state, snapshot); else m.NotifyParticipants();
                }),
                m => (m.Id, m.State), (k, v) => new SwitchActivate { Id = k, State = (int)v });
            Doors = new KeyedKind<AccessDoorModel, Lycoreia.Net.Messages.AccessDoor>("door", s,
                new KeyedModelAdapter<AccessDoorModel>(g => g.doors, m => (int)m.state, (m, v) => m.state = (Il2CppMonomiPark.World.AccessDoor.State)(int)v, m =>
                {
                    if (m.gameObj == null) return;                                              // unloaded door: the model holds the state (issue4: 2338 NREs)
                    var door = m.gameObj.GetComponent<Il2CppMonomiPark.World.AccessDoor>();
                    if (door != null) door.ForceUpdate(false); else m.NotifyParticipants();     // the native setter skips an equal model state: animate directly
                }),   // unloaded door: the model holds the state (issue4: 2338 NREs, 4 retries/s per door)
                m => (m.Id, m.State), (k, v) => new Lycoreia.Net.Messages.AccessDoor { Id = k, State = (int)v }, DoorMerge);
            Slots = new KeyedKind<PuzzleSlotModel, Lycoreia.Net.Messages.PuzzleSlot>("puzzle-slot", s,
                new KeyedModelAdapter<PuzzleSlotModel>(g => g.slots, m => m.filled ? 1 : 0, (m, v) => m.filled = v > 0, m => { if (m.gameObj != null) m.NotifyParticipants(); }),
                m => (m.Id, m.Filled ? 1 : 0), (k, v) => new Lycoreia.Net.Messages.PuzzleSlot { Id = k, Filled = v > 0 });
            Depositors = new KeyedKind<PlortDepositorModel, Lycoreia.Net.Messages.PlortDepositor>("depositor", s,
                new KeyedModelAdapter<PlortDepositorModel>(g => g.depositors, m => m.AmountDeposited, (m, v) => m.AmountDeposited = (int)v, m => { if (m._gameObject != null) m.NotifyParticipants(); }),
                m => (m.Id, m.Amount), (k, v) => new Lycoreia.Net.Messages.PlortDepositor { Id = k, Amount = (int)v }, Math.Max);
            Barriers = new KeyedKind<PrismaBarrierModel, Lycoreia.Net.Messages.PrismaBarrier>("prisma-barrier", s,
                new KeyedModelAdapter<PrismaBarrierModel>(g => g._prismaBarriers, m => m.ActivationTime, (m, v) => m.ActivationTime = v, m => { if (m._gameObj != null) m.NotifyParticipants(); }),
                m => (m.Id, m.ActivationTime), (k, v) => new Lycoreia.Net.Messages.PrismaBarrier { Id = k, ActivationTime = v });

            // join snapshots: the host's live models are the truth
            snap.Register(SnapStep.Switches, ctx => { var m = new InitialSwitches(); foreach (var (k, v) in Switches.HostState()) m.Items.Add(new IdState { Id = k, State = (int)v }); ctx.Send(m); });
            snap.Register(SnapStep.AccessDoors, ctx => { var m = new InitialAccessDoors(); foreach (var (k, v) in Doors.HostState()) m.Items.Add(new IdState { Id = k, State = (int)v }); ctx.Send(m); });
            snap.Register(SnapStep.PuzzleSlots, ctx => { var m = new InitialPuzzleSlots(); foreach (var (k, v) in Slots.HostState()) m.Items.Add(new IdFlag { Id = k, Flag = v > 0 }); ctx.Send(m); });
            snap.Register(SnapStep.PlortDepositors, ctx => { var m = new InitialPlortDepositors(); foreach (var (k, v) in Depositors.HostState()) m.Items.Add(new IdCount { Id = k, Count = (int)v }); ctx.Send(m); });
            snap.Register(SnapStep.PrismaBarriers, ctx => { var m = new InitialPrismaBarriers(); foreach (var (k, v) in Barriers.HostState()) m.Items.Add(new IdTime { Id = k, Time = v }); ctx.Send(m); });
            s.Node.On<InitialSwitches>(Side.Guest, (c, m) => Switches.System.LoadSnapshot(m.Items.Select(i => (i.Id, (double)i.State))));
            s.Node.On<InitialAccessDoors>(Side.Guest, (c, m) => Doors.System.LoadSnapshot(m.Items.Select(i => (i.Id, (double)i.State))));
            s.Node.On<InitialPuzzleSlots>(Side.Guest, (c, m) => Slots.System.LoadSnapshot(m.Items.Select(i => (i.Id, i.Flag ? 1.0 : 0.0))));
            s.Node.On<InitialPlortDepositors>(Side.Guest, (c, m) => Depositors.System.LoadSnapshot(m.Items.Select(i => (i.Id, (double)i.Count))));
            s.Node.On<InitialPrismaBarriers>(Side.Guest, (c, m) => Barriers.System.LoadSnapshot(m.Items.Select(i => (i.Id, i.Time))));

            InitPedia(s, snap); InitClock(s, snap); InitCurrency(s); InitGordos(s, snap);
            Events = new EventSync(s, snap); EventAdapter.Install(Events, s);
            Upgrades = UpgradeAdapter.Install(s, snap);
            InitStageB2(s, snap);
            s.ClientDisconnected += _ => ClearCaches(); s.ServerStarted += ClearCaches;
        }

        static void ClearCaches() { Switches?.System.Clear(); Doors?.System.Clear(); Slots?.System.Clear(); Depositors?.System.Clear(); Barriers?.System.Clear(); _pediaCache = null; _pendingPedia.Clear(); _pediaKnown.Clear(); _pendingPediaSnapshot = null; }

        // ------------------------------------------------------------------ pedia (host-authoritative replacement snapshot)
        static Dictionary<string, PediaEntry> _pediaCache;
        static readonly HashSet<string> _pediaKnown = new HashSet<string>();
        static readonly Dictionary<string, bool> _pendingPedia = new Dictionary<string, bool>();
        static List<string> _pendingPediaSnapshot;
        public static PediaEntry PediaByKey(string key)
        {
            if (_pediaCache == null || (key != null && !_pediaCache.ContainsKey(key))) { _pediaCache = new Dictionary<string, PediaEntry>(); foreach (var e in Resources.FindObjectsOfTypeAll<PediaEntry>()) if (e != null && !string.IsNullOrEmpty(e.PersistenceId)) _pediaCache[e.PersistenceId] = e; }
            return key != null && _pediaCache.TryGetValue(key, out var p) ? p : null;
        }
        static void InitPedia(Session s, SnapshotHost snap)
        {
            s.Node.On<PediaUnlock>(Side.Host, (c, m) => { if (!Domain.HostAuthority.Require(c) || string.IsNullOrWhiteSpace(m.Id)) return Verdict.Reject; Mod.Diagnostics?.Record("world", "pedia-unlock-received", $"id={m.Id}; popup={m.Popup}; from={c.SenderId}"); ApplyPedia(m.Id, m.Popup); return Verdict.Relay; });
            s.Node.On<PediaUnlock>(Side.Guest, (c, m) => { Mod.Diagnostics?.Record("world", "pedia-unlock-received", $"id={m.Id}; popup={m.Popup}"); ApplyPedia(m.Id, m.Popup); });
            snap.Register(SnapStep.Pedia, ctx => { var m = new InitialPedia(); try { var d = SceneContext.Instance?.PediaDirector; if (d != null && d._pediaModel != null) foreach (var e in d._pediaModel.unlocked) if (e != null) m.Entries.Add(e.PersistenceId); } catch (Exception e) { Log.Warn("pedia snapshot: " + e.Message); } ctx.Send(m); });
            s.Node.On<InitialPedia>(Side.Guest, (c, m) =>
            {
                _pendingPediaSnapshot = m.Entries.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
                _pendingPedia.Clear();
                Mod.Diagnostics?.Record("world", "pedia-snapshot-received", $"entries={_pendingPediaSnapshot.Count}; mode=replace");
                ApplyPediaSnapshotIfReady();
            });
        }
        static bool ApplyPediaSnapshotIfReady()
        {
            if (_pendingPediaSnapshot == null) return true;
            try
            {
                var d = SceneContext.Instance?.PediaDirector;
                if (d == null || d._pediaModel == null) return false;
                using (Applying.Enter())
                {
                    d.DebugClearUnlocked();
                    _pediaKnown.Clear();
                    foreach (var id in _pendingPediaSnapshot)
                    {
                        var e = PediaByKey(id); if (e == null) continue;
                        _pediaKnown.Add(id);
                        if (!d.IsUnlocked(e)) d.Unlock(e, false);
                        _pendingPedia.Remove(id);
                    }
                }
                Mod.Diagnostics?.Record("world", "pedia-snapshot-applied", $"entries={_pediaKnown.Count}; mode=replace");
                _pendingPediaSnapshot = null;
                return true;
            }
            catch (Exception ex) { Rl.Warn("pedia-snapshot", "pedia snapshot apply failed: " + ex.Message); return false; }
        }
        static void ApplyPedia(string key, bool popup)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            _pediaKnown.Add(key);
            try
            {
                var e = PediaByKey(key); var d = SceneContext.Instance?.PediaDirector;
                if (e == null || d == null || d._pediaModel == null) { _pendingPedia[key] = _pendingPedia.TryGetValue(key, out var old) && old || popup; Mod.Diagnostics?.Record("world", "pedia-unlock-queued", $"id={key}; popup={_pendingPedia[key]}"); return; }
                _pendingPedia.Remove(key);
                if (!d.IsUnlocked(e)) { using (Applying.Enter()) d.Unlock(e, popup); Mod.Diagnostics?.Record("world", "pedia-unlock-applied", $"id={key}; popup={popup}"); }
            }
            catch (Exception ex) { Rl.Warn("pedia-apply", "pedia unlock failed: " + ex.Message); }
        }
        static void PumpPedia()
        {
            if (!ApplyPediaSnapshotIfReady()) return;
            if (_pendingPedia.Count == 0) return;
            foreach (var entry in _pendingPedia.ToArray()) ApplyPedia(entry.Key, entry.Value);
        }
        public static void OnLocalPediaUnlock(PediaEntry e)
        {
            if (!Mod.IsNetworkActive || Applying.Active || e == null || string.IsNullOrEmpty(e.PersistenceId) || !_pediaKnown.Add(e.PersistenceId)) return;
            Mod.Diagnostics?.Record("world", "pedia-unlock-raised", $"id={e.PersistenceId}; popup=true");
            Mod.Node.SendAuto(new PediaUnlock { Id = e.PersistenceId, Popup = true });
        }

        // ------------------------------------------------------------------ world time and fast-forward
        public const double TimeEvery = 1.0; static double _nextTime; static long _timeRev;
        static void InitClock(Session s, SnapshotHost snap)
        {
            Clock = new WorldClockSync(s, snap); WeatherAdapter.Install(Clock);
            s.Node.On<WorldTime>(Side.Guest, (c, m) => ApplyTime(Clock.Time));
            s.Node.On<FastForward>(Side.Both, (c, m) => { ApplyFastForward(m.Time); if (c.OnHost && c.Sender != null) c.Node.SendTo(c.Sender, new FastForward { Time = m.Time }); return Verdict.Accept; });   // the requester skipped its own native call
            s.ServerStarted += () => _timeRev = 0;
        }
        static TimeDirector Td => SceneContext.Instance != null ? SceneContext.Instance.TimeDirector : null;
        static void ApplyTime(double t)
        {
            try { var td = Td; if (td == null || td._worldModel == null || !double.IsFinite(t)) return; if (td._worldModel.worldTime != t) { LocalHazardClock.BeforeCorrection(td._worldModel.worldTime, t); td._worldModel.worldTime = t; } }
            catch (Exception e) { Rl.Warn("time-apply", "world time apply failed: " + e.Message); }
        }
        static void ApplyFastForward(double target)
        {
            try { var td = Td; if (td == null || !double.IsFinite(target) || target <= td.WorldTime()) return; using (Applying.Enter()) td.FastForwardTo(target); }
            catch (Exception e) { Rl.Warn("ff-apply", "fast-forward failed: " + e.Message); }
        }
        /// <summary>A local fast-forward (sleeping): a guest asks the host; the host applies and relays.</summary>
        public static bool OnLocalFastForward(double target)
        {
            if (!Mod.IsNetworkActive || Applying.Active) return true;
            if (Mod.IsGuest) { Mod.Node.SendToHost(new FastForward { Time = target }); return false; }
            Mod.Node.SendToAll(new FastForward { Time = target }); return true;
        }
        static void PumpTime(double now)
        {
            if (!Mod.IsHost || now < _nextTime) return; _nextTime = now + TimeEvery;
            var td = Td; if (td == null) return; Clock.Time = td.WorldTime();
            Mod.Node.SendToAll(new WorldTime { Revision = ++_timeRev, Time = Clock.Time });
        }

        // ------------------------------------------------------------------ currency (one host-authoritative balance)
        public static int SaleDepth;
        static Dictionary<int, CurrencyDefinition> _currencies;
        static double _nextCurrencyDefinitions;
        static CurrencyDefinition CurrencyById(int id)
        {
            if (_currencies == null || !_currencies.ContainsKey(id) && Time.unscaledTimeAsDouble >= _nextCurrencyDefinitions) { _nextCurrencyDefinitions = Time.unscaledTimeAsDouble + 5; _currencies = new Dictionary<int, CurrencyDefinition>(); foreach (var c in Resources.FindObjectsOfTypeAll<CurrencyDefinition>()) if (c != null) _currencies[c.PersistenceId] = c; }
            return _currencies.TryGetValue(id, out var d) ? d : null;
        }
        static void InitCurrency(Session s)
        {
            Currency = new CurrencyLedger(s); Market = new MarketSystem(s, Currency);
            Currency.Trace = (ev, detail) => Mod.Diagnostics?.Record("currency", ev, detail);
            s.ServerStarted += () => _nativeCurrencyPending.Clear();
            s.ClientDisconnected += _ => _nativeCurrencyPending.Clear();
            Currency.OnBalanceChanged = (type, amount) =>
            {
                _nativeCurrencyPending[type] = Currency.ApplyingNotification;
                SetNative(type, Currency.ExpectedNative(type), Currency.ApplyingNotification);
            };
            try { foreach (var c in Resources.FindObjectsOfTypeAll<CurrencyDefinition>()) if (c != null && (c.name ?? "").Contains("Newbuck", StringComparison.OrdinalIgnoreCase)) { Market.SaleCurrency = c.PersistenceId; break; } } catch { }
            s.ServerStarted += () => { try { var ps = SceneContext.Instance?.PlayerState; if (ps == null) return; foreach (var c in Resources.FindObjectsOfTypeAll<CurrencyDefinition>()) if (c != null) Currency.Seed(c.PersistenceId, ps.GetCurrency(c.Cast<ICurrency>())); } catch (Exception e) { Log.Warn("currency seed: " + e.Message); } };
        }
        static readonly Dictionary<int, bool> _nativeCurrencyPending = new();
        static double _nextCurrency, _nextCurrencyCheckpoint;
        static void SetNative(int type, int amount, bool notify = false)
        {
            try
            {
                var ps = SceneContext.Instance?.PlayerState; var def = CurrencyById(type); if (ps == null || def == null) return;
                var cur = def.Cast<ICurrency>(); int have = ps.GetCurrency(cur); if (have == amount) { _nativeCurrencyPending.Remove(type); return; }
                using (Applying.Enter())
                {
                    if (amount > have) ps.AddCurrency(cur, amount - have, notify, null);
                    else ps.SpendCurrency(cur, have - amount, null);
                    if (ps.GetCurrency(cur) != amount) ps._model.SetCurrency(cur, amount);
                }
                _nativeCurrencyPending.Remove(type);
                Mod.Diagnostics?.Record("currency", "native-balance-applied", $"type={type}; before={have}; amount={amount}; notify={notify}");
            }
            catch (Exception e) { Rl.Warn("currency-apply", "currency apply failed: " + e.Message); }
        }
        /// <summary>The native balance changed through a local action. Host: publish. Guest: request the delta (sale income goes via the plort-sold message).</summary>
        public static void OnLocalCurrencyChanged(ICurrency c, int delta)
        {
            if (!Mod.IsNetworkActive || Applying.Active || c == null || delta == 0 || Currency == null) return;
            int type = c.PersistenceId;
            Mod.Diagnostics?.Record("currency", "native-delta", $"type={type}; delta={delta}; sale-depth={SaleDepth}; host={Mod.IsHost}");
            if (SaleDepth > 0 && delta > 0) Coop.CoopRuntime.Report(Lycoreia.Coop.CoopStat.MoneyEarned, delta);                // 2.1 recap / Market Day
            if (Mod.IsHost) { var ps = SceneContext.Instance?.PlayerState; if (ps != null) Currency.HostLocalChanged(type, ps.GetCurrency(c)); return; }
            if (SaleDepth > 0 && delta > 0) return;
            var request = Currency.Request(type, delta);
            Mod.Diagnostics?.Record("currency", "adjust-requested", $"type={type}; delta={delta}; request={request}; sale-depth={SaleDepth}");
        }

        static void PumpCurrency(double now)
        {
            if (Currency == null || now < _nextCurrency) return; _nextCurrency = now + 0.5;
            foreach (var pair in _nativeCurrencyPending.ToArray())
                SetNative(pair.Key, Currency.ExpectedNative(pair.Key), pair.Value);
            var ps = SceneContext.Instance?.PlayerState;
            if (ps == null) return;
            var defaultCurrency = CurrencyUtility.DefaultCurrency;
            if (defaultCurrency != null) Market.SaleCurrency = defaultCurrency.PersistenceId;
            if (Mod.IsHost)
            {
                if (_currencies == null) CurrencyById(Market.SaleCurrency);
                foreach (var pair in _currencies) Currency.HostLocalChanged(pair.Key, ps.GetCurrency(pair.Value.Cast<ICurrency>()));
                if (now >= _nextCurrencyCheckpoint) { _nextCurrencyCheckpoint = now + 5; Currency.PublishCheckpoint(); }
            }
            else foreach (var pair in Currency.Balance.ToArray()) SetNative(pair.Key, Currency.ExpectedNative(pair.Key));
        }

        // ------------------------------------------------------------------ gordos
        static void InitGordos(Session s, SnapshotHost snap)
        {
            _pendingGordos.Clear(); _gordoBurstsStarted.Clear();
            s.ClientJoined += () => { _pendingGordos.Clear(); _gordoBurstsStarted.Clear(); };
            s.ServerStarted += () => { _pendingGordos.Clear(); _gordoBurstsStarted.Clear(); };
            Gordos = new GordoSync(s);
            Gordos.FoodUnits = (id, foodType) =>
            {
                var model = GameState.Model;
                if (model?.gordos == null || !model.gordos.TryGetValue(id, out var gm) || gm == null || !ActorTypes.TryGetType(foodType, out var food)) return 0;
                var definition = (gm.gameObj != null ? gm.gameObj.GetComponent<GordoEat>()?.SlimeDefinition : null) ?? gm.identifiableType?.TryCast<SlimeDefinition>();
                if (definition?.Diet?.EatMap == null || food == null) return 0;
                foreach (var entry in definition.Diet.EatMap)
                    if (entry?.EatsIdent != null && entry.EatsIdent.ReferenceId == food.ReferenceId) return Math.Max(0, entry.NumToProduce());
                return 0;
            };
            Gordos.OnState = (id, st) => ApplyGordo(id, st);
            Gordos.OnPayout = id => GordoRewards.MarkPending(id);                                                // paid when the gordo is (or becomes) loaded on the host
            s.ServerStarted += () =>
            {
                GordoRewards.Clear();
                try
                {
                    var g = GameState.Model; if (g?.gordos == null) return;
                    foreach (var kv in g.gordos) if (kv.Value != null && kv.Value.HasPopped()) GordoRewards.LoadGiven(new[] { kv.Key });
                    foreach (var kv in g.gordos)
                    {
                        var gm = kv.Value; if (gm == null) continue; int type = ActorTypes.IdOf(gm.identifiableType);
                        Gordos.Seed(kv.Key, gm.targetCount, type, gm.gordoEatCount, gm.HasPopped());
                        if (!gm.HasPopped() && gm.targetCount > 0 && gm.gordoEatCount >= gm.targetCount)
                            Gordos.HostLocal(kv.Key, gm.gordoEatCount, gm.targetCount, type, false);
                    }
                }
                catch (Exception e) { Rl.Warn("gordo-seed", "host Gordo restore: " + e.Message); }
            };
            snap.Register(SnapStep.Gordos, ctx =>
            {
                var m = new InitialGordos(); var g = GameState.Model;
                if (g?.gordos != null) foreach (var kv in g.gordos)
                {
                    var gm = kv.Value; if (gm == null) continue;
                    int type = ActorTypes.IdOf(gm.identifiableType);
                    Gordos.Seed(kv.Key, gm.targetCount, type, gm.gordoEatCount, gm.HasPopped());
                    m.Items.Add(new GordoRecord { Id = kv.Key, Eaten = gm.gordoEatCount, Required = gm.targetCount, Type = type, WasSeen = gm.gordoSeen, Popped = gm.HasPopped(), ActiveRewards = new List<int>() });
                }
                ctx.Send(m);
            });
            s.Node.On<InitialGordos>(Side.Guest, (c, m) =>
            {
                foreach (var r in m.Items)
                {
                    int required = r.Required > 0 ? r.Required : (Gordos.Gordos.TryGetValue(r.Id, out var known) ? known.Required : 0);   // a guest never takes a 0 target from a snapshot
                    Gordos.Seed(r.Id, required, r.Type, r.Eaten, r.Popped); ApplyGordo(r.Id, Gordos.Gordos[r.Id]);
                }
            });
        }
        static void ApplyGordo(string id, GordoSync.State st)
        {
            if (string.IsNullOrEmpty(id) || st == null) return;
            try
            {
                var g = GameState.Model;
                if (g?.gordos == null || !g.gordos.TryGetValue(id, out var gm) || gm == null)
                {
                    QueueGordo(id, st, "model"); return;
                }
                using (Applying.Enter())
                {
                    if (st.Required > 0 && gm.targetCount <= 0) gm.targetCount = st.Required;
                    var eat = gm.gameObj != null ? gm.gameObj.GetComponent<GordoEat>() : null;
                    bool wasPopped = eat != null && eat.HasPopped();
                    int eaten = st.Popped ? Math.Max(st.Eaten, gm.targetCount) : st.Eaten;
                    if (eat != null)
                    {
                        // A canonical alive snapshot also replaces an advanced guest save.
                        // A canonical popped state must preserve the native -1 sentinel.
                        if ((!st.Popped || !wasPopped) && eat.GetEatenCount() != eaten) eat.SetEatenCount(eaten);
                    }
                    else if (!st.Popped) gm.gordoEatCount = eaten;
                    if (st.Popped)
                    {
                        gm.SetSeen(false);
                        if (eat == null)
                        {
                            // Native HasPopped is the -1 sentinel, not a count at target.
                            // Persist the terminal model even when its scene object is absent.
                            gm.gordoEatCount = -1;
                            if (Mod.IsHost && GordoRewards.HasPending(id)) QueueGordo(id, st, "reward-component");
                            else _pendingGordos.Remove(id);
                            return;
                        }
                        if (!_gordoBurstsStarted.Contains(id) && !wasPopped)
                        {
                            // Native immediate completion emits the event, grants rewards and
                            // removes the live Gordo. Only the host can grant rewards.
                            _gordoBurstsStarted.Add(id);
                            eat.ImmediateReachedTarget();
                        }
                        else if (Mod.IsHost && GordoRewards.HasPending(id) && eat._rewards != null)
                            eat._rewards.GiveRewards();
                        gm.gordoEatCount = -1;
                        if (gm.gameObj != null && gm.gameObj.activeSelf) gm.gameObj.SetActive(false);
                        if (Mod.IsHost && GordoRewards.HasPending(id)) QueueGordo(id, st, "rewards");
                        else _pendingGordos.Remove(id);
                        Mod.Diagnostics?.Record("world", "gordo-burst-applied", $"id={id}; eaten={eaten}; required={gm.targetCount}; already={eat.HasPopped()}");
                    }
                    else if (gm.gameObj != null && !gm.gameObj.activeSelf) gm.gameObj.SetActive(true);              // alive on the host: re-activate
                }
                if (!st.Popped) _pendingGordos.Remove(id);
            }
            catch (Exception e)
            {
                QueueGordo(id, st, "apply-failed");
                Mod.Diagnostics?.Record("world", "gordo-apply-failed", $"id={id}; popped={st.Popped}; completion-started={_gordoBurstsStarted.Contains(id)}; error={e.GetType().Name}");
                Rl.Warn("gordo-apply", "gordo apply failed: " + e.Message);
            }
        }
        static void QueueGordo(string id, GordoSync.State st, string waitingFor)
        {
            bool first = !_pendingGordos.ContainsKey(id);
            _pendingGordos[id] = new GordoSync.State { Eaten = st.Eaten, Required = st.Required, Type = st.Type, Popped = st.Popped, Seen = st.Seen };
            if (first) Mod.Diagnostics?.Record("world", "gordo-state-queued", $"id={id}; waiting={waitingFor}; popped={st.Popped}");
        }
        static double _nextGordoRetry;
        static void PumpGordos()
        {
            double now = Time.unscaledTimeAsDouble;
            if (_pendingGordos.Count == 0 || now < _nextGordoRetry) return;
            _nextGordoRetry = now + 0.25;
            foreach (var kv in _pendingGordos.ToArray()) ApplyGordo(kv.Key, kv.Value);
        }
        /// <summary>Host: pays shared money (expedition rewards) through the native balance, so it is published like any host income and shown with the native notice.</summary>
        public static bool HostGrantMoney(int amount)
        {
            try
            {
                if (!Mod.IsHost || amount <= 0) return false;
                var ps = SceneContext.Instance?.PlayerState; var def = CurrencyById(Market.SaleCurrency); if (ps == null || def == null) return false;
                ps.AddCurrency(def.Cast<ICurrency>(), amount, true, null); return true;
            }
            catch (Exception e) { Rl.Warn("grant", "reward payment failed: " + e.Message); return false; }
        }
        public static string GordoKey(GordoModel model)
        {
            var g = GameState.Model; if (g?.gordos == null || model == null) return null;
            foreach (var kv in g.gordos) if (kv.Value != null && kv.Value.Pointer == model.Pointer) return kv.Key; return null;
        }
        /// <summary>A native feed happened locally (after DoEat): the host publishes, a guest asks the host to count it.</summary>
        public static void OnNativeGordoBurst(string id)
        {
            var g = GameState.Model;
            if (!Mod.IsHost || string.IsNullOrEmpty(id) || Gordos == null ||
                g?.gordos == null || !g.gordos.TryGetValue(id, out var model) || model == null) return;
            // The native event precedes reward delivery. Track that completion
            // as started so recovery cannot run its event/meta reward twice.
            _gordoBurstsStarted.Add(id); GordoRewards.MarkPending(id);
            Gordos.HostLocal(id, model.gordoEatCount, model.targetCount,
                ActorTypes.IdOf(model.identifiableType), true);
            if (Gordos.Gordos.TryGetValue(id, out var state)) QueueGordo(id, state, "native-completion");
            Mod.Diagnostics?.Record("world", "gordo-native-burst-published", $"id={id}; authority=host");
        }
        public static void OnLocalGordoFeed(GordoEat eat, GameObject food, long priorFoodActorId = 0, int foodType = 0, int nativeUnits = 1)
        {
            if (!Mod.IsNetworkActive || Applying.Active || eat == null || Gordos == null) return;
            var gm = eat.GordoModel; var id = GordoKey(gm); if (id == null) return;
            int type = ActorTypes.IdOf(gm.identifiableType), eaten = eat.GetEatenCount(), required = eat.GetTargetCount();
            long foodActorId = priorFoodActorId != 0 ? priorFoodActorId : ActorLink.IdOf(food);
            // 2.1 recap / Gordo Feast: the seat whose food it was counts it (a host's native bite of a guest's food replica is not a host feed)
            bool ownFood = foodActorId == 0 || Mod.Actors == null || !Mod.Actors.Table.TryGet(foodActorId, out var fe) || fe.Owner == Mod.LocalId;
            if (ownFood) Coop.CoopRuntime.Report(Lycoreia.Coop.CoopStat.GordoFood, Math.Max(1, nativeUnits));
            Mod.Diagnostics?.Record("world", "gordo-feed-observed", $"id={id}; food={foodActorId}; eaten={eaten}; required={required}; authority={(Mod.IsHost ? "host" : "intent")}");
            if (Mod.IsHost)
            {
                int canonical = Gordos.HostLocal(id, eaten, required, type, gm.HasPopped(), foodActorId, nativeUnits);
                if (!gm.HasPopped() && eat.GetEatenCount() != canonical) using (Applying.Enter()) eat.SetEatenCount(canonical);
                return;
            }
            Mod.Node.SendToHost(new GordoFeed { Id = id, RequestId = Guid.NewGuid().ToString("N"), FoodActorId = foodActorId, NewFoodCount = Math.Min(eaten, required), RequiredFoodCount = required, GordoType = type, FoodType = foodType });
            if (gm.HasPopped() || (required > 0 && eaten >= required)) Mod.Node.SendToHost(new GordoBurst { Id = id, EatenCount = eaten, RequiredFoodCount = required, GordoType = type });
        }
        /// <summary>Native GiveRewards runs only on the reward authority (the host).</summary>
        public static bool MayGiveRewards() => !Mod.IsNetworkActive || Mod.IsHost;

        // ------------------------------------------------------------------ frame pump
        public sealed class Pump : IFramePump
        {
            public string Name => "world";
            double _nextPendingRetry;
            void IFramePump.Pump()
            {
                if (!Ready || !Mod.IsNetworkActive || !GameState.Ready || GameState.Loading) return; double now = Time.unscaledTimeAsDouble;
                if (now >= _nextPendingRetry)
                {
                    _nextPendingRetry = now + 0.25;
                    Switches.RetryPending(); Doors.RetryPending(); Slots.RetryPending();
                    Depositors.RetryPending(); Barriers.RetryPending();
                }
            PumpTime(now); PumpCurrency(now); PumpStageB2(now); EventAdapter.Pump(); PumpPedia(); PumpGordos();
            }
        }
    }
}

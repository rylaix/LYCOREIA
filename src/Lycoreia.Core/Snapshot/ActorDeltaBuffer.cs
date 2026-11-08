// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Lycoreia.Snapshot
{
    /// <summary>
    /// Guest-side delta buffering around the actor snapshot: generations, revision watermark, applied-delta journal,
    /// overflow handling and replay order. Game specifics are injected.
    /// </summary>
    public sealed class ActorDeltaBuffer
    {
        public const int MaximumJournalEvents = 131_072, MaximumJournalTransforms = 131_072;

        readonly struct JournalEntry
        {
            public readonly ActorDeltaKind Kind; public readonly long ActorId, Revision; public readonly int Order; public readonly Func<bool> Apply;
            public JournalEntry(ActorDeltaKind k, long a, long r, int o, Func<bool> ap) { Kind = k; ActorId = a; Revision = r; Order = o; Apply = ap; }
        }

        readonly ActorSnapshotDeltaQueue<Func<bool>> _queue;
        readonly List<JournalEntry> _events = new List<JournalEntry>();
        readonly HashSet<ActorDeltaJournalKey> _eventKeys = new HashSet<ActorDeltaJournalKey>();
        readonly Dictionary<long, JournalEntry> _latestTransform = new Dictionary<long, JournalEntry>();
        readonly Dictionary<long, ActorDeltaRevisionState> _applied = new Dictionary<long, ActorDeltaRevisionState>();
        readonly int _maxEvents, _maxTransforms;
        int _order; bool _truncated; long _highestEvicted;
        bool _invalidated, _hasCompleted, _hasActiveRev, _activePacketReceived;
        long _completedRev, _activeRev, _activeGen, _latestSync, _activeSync, _closedSync, _activeSnapSync;

        public Func<long, bool> ActorKnown = _ => false;
        public Action RequestResync = () => { };
        public Action<string> Info = _ => { }, Warn = _ => { }, Error = _ => { };

        public ActorDeltaBuffer(int maxPending = 262_144, int maxEvents = MaximumJournalEvents, int maxTransforms = MaximumJournalTransforms)
        { _queue = new ActorSnapshotDeltaQueue<Func<bool>>(maxPending); _maxEvents = maxEvents; _maxTransforms = maxTransforms; }

        public bool IsBuffering => _queue.IsBuffering;
        public bool Invalidated => _invalidated;
        public long ActiveGeneration => _activeGen;
        public long LatestSyncId => _latestSync;
        public bool HasCompletedWatermark => _hasCompleted;
        public long CompletedWatermark => _completedRev;

        public static bool IsActorDelta(ActorDeltaKind k) => true;

        // ---------------------------------------------------------------- barriers
        public long BeginSync(long syncId = 0)
        {
            if (syncId > 0 && !ActorSyncGenerationPolicy.ShouldBeginSync(syncId, _latestSync, _closedSync)) return 0;
            ClearInvalidated();
            if (syncId > 0)
            {
                if (syncId > _latestSync)
                {
                    _latestSync = syncId; _activeSync = syncId;
                    if (_activeGen != 0 && _queue.IsCurrent(_activeGen)) { _activeGen = _queue.BeginSnapshot(); _activeSnapSync = syncId; }
                    else { _activeGen = 0; _activeSnapSync = 0; }
                    _activePacketReceived = false; _hasActiveRev = false; _activeRev = 0;
                }
                else _activeSync = syncId;
            }
            bool was = _queue.IsBuffering; long g = _queue.Begin();
            if (!was) Seed();
            return g;
        }

        public long BeginSnapshot(long syncId = 0)
        {
            if (syncId <= 0)
            {
                if (_invalidated) { Warn("InitialActors ignored because its delta buffer overflowed; waiting for a fresh resync snapshot."); return 0; }
                bool w = _queue.IsBuffering; _activeGen = _queue.BeginSnapshot(); _hasActiveRev = false; _activeRev = 0;
                if (!w) Seed(); return _activeGen;
            }
            if (!ActorSyncGenerationPolicy.ShouldBeginSnapshot(syncId, _latestSync, _closedSync, _activeSnapSync, _activePacketReceived)) return 0;
            if (_invalidated) ClearInvalidated();
            if (syncId > _latestSync) { _latestSync = syncId; _activeSync = syncId; }
            if (_activeSnapSync == syncId && _activeGen != 0 && _queue.IsCurrent(_activeGen) && !_activePacketReceived)
            { _activePacketReceived = true; _hasActiveRev = false; _activeRev = 0; return _activeGen; }
            _activeSync = syncId; _activeSnapSync = syncId; _activePacketReceived = true;
            bool was = _queue.IsBuffering; _activeGen = _queue.BeginSnapshot(); _hasActiveRev = false; _activeRev = 0;
            if (!was) Seed();
            return _activeGen;
        }

        public bool IsCurrent(long gen) => _queue.IsCurrent(gen);

        public void SetSnapshotRevision(long gen, bool has, long rev)
        {
            if (!_queue.IsCurrent(gen)) return;
            if (has && ActorDeltaJournalPolicy.ShouldAbortForJournalLoss(_truncated, _highestEvicted, rev))
            { Invalidate($"journal lost actor revision {_highestEvicted} newer than snapshot watermark {rev}"); return; }
            int stale = _queue.SetSnapshotRevision(gen, has, rev);
            _activeGen = gen; _hasActiveRev = has; _activeRev = rev;
            if (stale > 0) Info($"InitialActors watermark discarded {stale} stale actor deltas.");
        }

        // ---------------------------------------------------------------- live deltas
        /// <summary>
        /// Called by every actor change handler. Returns true when the change was consumed (buffered or dropped as covered);
        /// false when the caller must apply it now.
        /// </summary>
        public bool TryDefer(ActorDeltaKind kind, long actorId, bool hasRevision, long revision, Func<bool> apply, string transferOwner = null)
        {
            if (_hasCompleted && hasRevision && ActorRevisionOrderingPolicy.IsCoveredBySnapshot(true, _completedRev, true, revision)) return true;
            Func<bool> guarded = () => ApplyOrdered(kind, actorId, hasRevision, revision, transferOwner, apply);
            if (Remember(kind, actorId, hasRevision, revision, guarded)) return true;
            if (_queue.TryEnqueue(guarded, hasRevision, revision, out var overflow))
            {
                if (overflow.Length > 0)
                {
                    _invalidated = true;
                    Error($"Actor snapshot delta buffer reached its limit ({overflow.Length}); aborting that snapshot and replaying only mutations intercepted after the barrier before requesting a fresh snapshot.");
                    Replay(overflow); RequestResync();
                }
                return true;
            }
            return false;
        }

        bool ApplyOrdered(ActorDeltaKind kind, long actorId, bool hasRevision, long revision, string transferOwner, Func<bool> apply)
        {
            if (!hasRevision || actorId == 0) return apply();
            if (!ShouldApply(kind, actorId, revision)) return false;
            _applied.TryGetValue(actorId, out var cur);
            if (kind == ActorDeltaKind.ActorDestroy && !ActorKnown(actorId))
            { _applied[actorId] = ActorDeltaRevisionPolicy.Record(cur, kind, revision, null); return true; }
            bool ok = apply();
            if (ok) _applied[actorId] = ActorDeltaRevisionPolicy.Record(cur, kind, revision, kind == ActorDeltaKind.ActorTransfer ? transferOwner : null);
            return ok;
        }

        public void RecordApplied(ActorDeltaKind kind, long actorId, long revision, string ownerId = null)
        {
            if (actorId == 0 || revision <= 0) return;
            _applied.TryGetValue(actorId, out var st); _applied[actorId] = ActorDeltaRevisionPolicy.Record(st, kind, revision, ownerId);
        }
        public bool ShouldApply(ActorDeltaKind kind, long actorId, long revision)
        {
            if (actorId == 0 || revision <= 0) return true;
            _applied.TryGetValue(actorId, out var st);
            return ActorDeltaRevisionPolicy.ShouldApply(st, kind, revision, ActorKnown(actorId));
        }
        public string OwnerForSpawn(long actorId, long revision, string fallback)
            => _applied.TryGetValue(actorId, out var st) && ActorDeltaRevisionPolicy.HasNewerTransferOwner(st, revision)
                ? ActorDeltaRevisionPolicy.ResolveOwnerForSpawn(st, revision, fallback) : fallback;

        // ---------------------------------------------------------------- completion
        public void Complete(long gen)
        {
            if (!_queue.Complete(gen, out var replay)) return;
            if (gen == _activeGen)
            {
                if (_hasActiveRev) { _hasCompleted = true; _completedRev = _activeRev; PruneJournal(_activeRev); }
                _applied.Clear();
            }
            if (gen == _activeGen && _activeSnapSync > 0 && _activeSnapSync == _activeSync) _closedSync = _activeSnapSync;
            ResetActive();
            Info($"InitialActors applied; replaying {replay.Length} buffered actor mutations.");
            Replay(replay);
        }

        public void Abort(long gen)
        {
            if (!_queue.Abort(gen, out var replay)) return;
            if (gen == _activeGen)
            {
                if (_activeSnapSync > 0 && _activeSnapSync == _activeSync) _closedSync = _activeSnapSync;
                ResetActive();
            }
            Warn($"InitialActors snapshot aborted; replaying {replay.Length} actor mutations without advancing the snapshot watermark.");
            Replay(replay);
        }

        public void Cancel()
        {
            _queue.Cancel(); _events.Clear(); _eventKeys.Clear(); _latestTransform.Clear(); _applied.Clear();
            _truncated = false; _highestEvicted = 0; _invalidated = false; _hasCompleted = false; _completedRev = 0;
            ResetActive(); _latestSync = 0; _activeSync = 0; _closedSync = 0; _order = 0;
        }

        void ResetActive() { _hasActiveRev = false; _activeRev = 0; _activeGen = 0; _activeSnapSync = 0; _activePacketReceived = false; }
        void ClearInvalidated() { if (!_invalidated) return; _queue.Cancel(); _invalidated = false; ResetActive(); }

        static void Replay(Func<bool>[] items)
        {
            foreach (var f in items) { try { f(); } catch (Exception e) { Log.Error("replayed actor change failed", e); } }
        }

        // ---------------------------------------------------------------- journal
        bool Remember(ActorDeltaKind kind, long actorId, bool hasRevision, long revision, Func<bool> apply)
        {
            if (!hasRevision) return false;
            var entry = new JournalEntry(kind, actorId, revision, _order++, apply);
            if (kind == ActorDeltaKind.ActorUpdate && actorId != 0)
            {
                if (_latestTransform.TryGetValue(actorId, out var ex) && !ActorDeltaJournalPolicy.ShouldReplaceTransform(ex.Revision, revision)) return true;
                _latestTransform[actorId] = entry;
                if (_latestTransform.Count > _maxTransforms)
                {
                    long oldId = 0; var old = default(JournalEntry); int oldOrder = int.MaxValue;
                    foreach (var kv in _latestTransform) if (kv.Value.Order < oldOrder) { oldId = kv.Key; old = kv.Value; oldOrder = kv.Value.Order; }
                    _latestTransform.Remove(oldId); Lost(old.Revision);
                }
                return false;
            }
            if (!_eventKeys.Add(new ActorDeltaJournalKey(kind, actorId, revision))) return true;
            if ((kind == ActorDeltaKind.ActorSpawn || kind == ActorDeltaKind.ActorDestroy) && _latestTransform.TryGetValue(actorId, out var lt) &&
                ActorDeltaJournalPolicy.ShouldClearTransformOnLifecycle(lt.Revision, revision)) _latestTransform.Remove(actorId);
            _events.Add(entry);
            if (_events.Count <= _maxEvents) return false;
            var ev = _events[0]; _eventKeys.Remove(new ActorDeltaJournalKey(ev.Kind, ev.ActorId, ev.Revision)); _events.RemoveAt(0); Lost(ev.Revision);
            return false;
        }
        void Lost(long rev) { _truncated = true; _highestEvicted = Math.Max(_highestEvicted, rev); }

        void Seed()
        {
            if (!_queue.IsBuffering || (_events.Count == 0 && _latestTransform.Count == 0)) return;
            foreach (var e in _events.Concat(_latestTransform.Values).OrderBy(x => x.Order).ToArray())
            {
                if (!_queue.TryEnqueue(e.Apply, true, e.Revision, out var overflow, alreadyApplied: true)) break;
                if (overflow.Length == 0) continue;
                _invalidated = true;
                Error($"Actor pre-sync journal exceeded the delta buffer ({overflow.Length}); aborting this snapshot and replaying retained mutations before requesting a fresh snapshot.");
                Replay(overflow); RequestResync(); break;
            }
        }

        void PruneJournal(long through)
        {
            for (int i = _events.Count - 1; i >= 0; i--)
            {
                var e = _events[i];
                if (ActorDeltaJournalPolicy.ShouldReplay(true, through, true, e.Revision)) continue;
                _eventKeys.Remove(new ActorDeltaJournalKey(e.Kind, e.ActorId, e.Revision)); _events.RemoveAt(i);
            }
            foreach (var id in _latestTransform.Where(p => !ActorDeltaJournalPolicy.ShouldReplay(true, through, true, p.Value.Revision)).Select(p => p.Key).ToArray()) _latestTransform.Remove(id);
            if (_truncated && _highestEvicted <= through) { _truncated = false; _highestEvicted = 0; }
        }

        void Invalidate(string reason)
        {
            _invalidated = true; _queue.Cancel(); ResetActive();
            Error($"Actor snapshot aborted because {reason}; requesting another snapshot."); RequestResync();
        }
        public int JournalEvents => _events.Count;
        public int JournalTransforms => _latestTransform.Count;
    }
}

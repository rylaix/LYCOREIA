// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using Lycoreia.Net;
using Lycoreia.Net.Messages;

namespace Lycoreia.Snapshot
{
    /// <summary>A fully received actor snapshot.</summary>
    public sealed class ActorSnapshot
    {
        public long SyncId, StartingActorId; public double WorldTime; public bool HasRevision; public long WorldRevision;
        public readonly List<ActorRecord> Actors = new List<ActorRecord>();
    }

    /// <summary>Collects pages of one sync id; a newer sync id replaces an incomplete older snapshot.</summary>
    public sealed class ActorPageAssembler
    {
        long _sync; int _count; readonly Dictionary<int, InitialActors> _pages = new Dictionary<int, InitialActors>();
        public long CurrentSyncId => _sync;
        public int PagesHeld => _pages.Count;

        /// <summary>Returns the assembled snapshot when the last missing page arrived.</summary>
        public ActorSnapshot Add(InitialActors page)
        {
            if (page.SyncId < _sync) return null;   // older than what we already collect
            if (page.SyncId > _sync || page.PageCount != _count) { _sync = page.SyncId; _count = page.PageCount; _pages.Clear(); }
            if (page.Page < 0 || page.Page >= _count || _pages.ContainsKey(page.Page)) return null;
            var copy = new InitialActors { SyncId = page.SyncId, Page = page.Page, PageCount = page.PageCount, StartingActorId = page.StartingActorId, WorldTime = page.WorldTime, WorldRevision = page.WorldRevision };
            copy.Actors.AddRange(page.Actors); _pages[page.Page] = copy;
            if (_pages.Count != _count) return null;
            var snap = new ActorSnapshot { SyncId = _sync };
            var first = _pages[0]; snap.StartingActorId = first.StartingActorId; snap.WorldTime = first.WorldTime; snap.HasRevision = first.WorldRevision.Has; snap.WorldRevision = first.WorldRevision.Value;
            for (int i = 0; i < _count; i++) snap.Actors.AddRange(_pages[i].Actors);
            _pages.Clear(); _count = 0;
            return snap;
        }
        public void Reset() { _pages.Clear(); _sync = 0; _count = 0; }
    }

    public enum ApplyOutcome { Success, RuntimeFailure, Stale }

    /// <summary>Game side of applying a snapshot (wait for player, clear table, destroy internally, spawn). Completes asynchronously.</summary>
    public interface IActorSnapshotApplier { void Apply(ActorSnapshot snapshot, Func<bool> stillCurrent, Action<ApplyOutcome> done); }

    /// <summary>Guest orchestration: barriers, assembling, apply with retries, abort semantics.</summary>
    public sealed class GuestSnapshotController
    {
        public const int MaxAutomaticRetries = 2;
        readonly Session _session; public readonly ActorDeltaBuffer Buffer; public readonly ActorPageAssembler Assembler = new ActorPageAssembler();
        public IActorSnapshotApplier Applier;
        public int Failures { get; private set; }
        public bool Applied { get; private set; }            // join is complete: snapshot applied and buffer drained
        public event Action SnapshotApplied, SnapshotAborted;
        public event Action<ActorSnapshot> SnapshotReceived;
        public int ResyncRequests { get; private set; }

        public GuestSnapshotController(Session s, ActorDeltaBuffer buffer = null)
        {
            _session = s; Buffer = buffer ?? new ActorDeltaBuffer();
            Buffer.RequestResync = () => RequestResync(true);
            Buffer.Info = m => Log.Info(m); Buffer.Warn = m => Log.Warn(m); Buffer.Error = m => Log.Error(m);
            s.Node.PeerAdded += p => { if (p.IsHost) { Buffer.Cancel(); Assembler.Reset(); Failures = 0; Applied = false; Buffer.BeginSync(); } };
            s.ApproveApplied += a => { if (a.SyncId > 0) Buffer.BeginSync(a.SyncId); };
            s.ClientDisconnected += _ => { Buffer.Cancel(); Assembler.Reset(); Applied = false; };
            s.Node.On<InitialActors>(Side.Guest, (c, m) => OnPage(m));
        }

        public void RequestResync(bool automatic = false)
        {
            ResyncRequests++;
            if (_session.IsGuest) _session.Node.SendToHost(new ResyncRequest { Automatic = automatic });
        }

        void OnPage(InitialActors page)
        {
            var snap = Assembler.Add(page); if (snap == null) return;
            long gen = Buffer.BeginSnapshot(snap.SyncId);
            if (gen == 0) { Log.Debug($"InitialActors for sync {snap.SyncId} ignored (superseded or closed)"); return; }
            Buffer.SetSnapshotRevision(gen, snap.HasRevision, snap.WorldRevision);
            if (!Buffer.IsCurrent(gen)) return; // journal loss invalidated it; a resync was requested
            SnapshotReceived?.Invoke(snap);
            if (Applier == null) { Buffer.Complete(gen); Applied = true; SnapshotApplied?.Invoke(); return; }
            Applier.Apply(snap, () => Buffer.IsCurrent(gen) && _session.IsGuest, outcome => Finish(gen, outcome));
        }

        void Finish(long gen, ApplyOutcome outcome)
        {
            if (!Buffer.IsCurrent(gen)) return;
            switch (outcome)
            {
                case ApplyOutcome.Success:
                    Failures = 0; Buffer.Complete(gen); Applied = true; SnapshotApplied?.Invoke(); break;
                case ApplyOutcome.RuntimeFailure:
                    Failures++;
                    if (InitialActorSnapshotPolicy.ShouldAutomaticallyRetry(Failures, MaxAutomaticRetries)) { Log.Warn($"snapshot failed ({Failures}); requesting resync"); RequestResync(true); }
                    else { Log.Error("snapshot failed repeatedly; aborting without advancing the watermark"); Buffer.Abort(gen); Failures = 0; SnapshotAborted?.Invoke(); }
                    break;
                case ApplyOutcome.Stale: break;
            }
        }
    }
}

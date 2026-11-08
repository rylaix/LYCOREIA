// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Lycoreia.Net;
using Lycoreia.Net.Messages;

namespace Lycoreia.Actors
{
    public enum CapturePhase { Preparing, Approved }
    public enum CaptureAskResult { Queued, AlreadyPending, Suppressed, GaveUp, Full }
    public enum CaptureActionKind { SendPrepare, SendCommit, SendAbort, GaveUp }

    public sealed class ClientCapture
    {
        public long ActorId; public Guid RequestId; public string ExpectedOwner; public bool Consumes; public CapturePhase Phase;
        public double CreatedAt, ApprovedAt, NextSendAt, FirstCommitAt, NativeConfirmedAt; public int PrepareSends, CommitSends;
        public bool DestroyObserved, CommitSent, ConsumeInProgress, PermitArmed, NativeConfirmed; public double PermitUntil;
        /// <summary>Nothing can be undone any more: a cancel or a resync must leave this capture alone.</summary>
        public bool Irreversible => DestroyObserved || CommitSent || ConsumeInProgress || PermitArmed;
    }
    public readonly struct CaptureAction { public readonly CaptureActionKind Kind; public readonly ClientCapture Capture; public readonly string Reason; public CaptureAction(CaptureActionKind k, ClientCapture c, string r = "") { Kind = k; Capture = c; Reason = r; } }

    /// <summary>Guest side of the capture protocol (retries every 1.5 s: prepare 3, commit 16; timeouts 8 s / 30 s; per-actor abort only).</summary>
    public sealed class GuestCaptureMachine
    {
        public const double ApprovalSeconds = 30, HeldCommitDelay = 0.3, PermitSeconds = 2, CandidateSeconds = 2.5;
        public readonly CaptureClientState Denials = new CaptureClientState();
        readonly Dictionary<long, ClientCapture> _pending = new Dictionary<long, ClientCapture>();
        readonly Dictionary<Guid, double> _completed = new Dictionary<Guid, double>(); readonly Queue<Guid> _completedOrder = new Queue<Guid>();
        readonly Dictionary<long, (double at, int scene)> _candidates = new Dictionary<long, (double, int)>();
        public int PendingCount => _pending.Count; public int CompletedCount => _completed.Count;
        public ClientCapture Get(long actorId) => _pending.TryGetValue(actorId, out var c) ? c : null;
        public List<long> PendingIds() => _pending.Keys.ToList();
        /// <summary>Native removal is part of this consume until its host result arrives;
        /// nested model hooks and reconciliation must not publish another destroy.</summary>
        public bool OwnsConsumeRemoval(long id) => _pending.TryGetValue(id, out var c) &&
            c.Phase == CapturePhase.Approved && c.Consumes &&
            (c.ConsumeInProgress || c.PermitArmed || c.DestroyObserved || c.NativeConfirmed || c.CommitSent);

        public CaptureAskResult Ask(long actorId, string expectedOwner, bool consumes, double now, out ClientCapture c)
        {
            if (_pending.TryGetValue(actorId, out c)) return CaptureAskResult.AlreadyPending;
            if (Denials.ShouldSuppress(actorId, now)) return CaptureAskResult.Suppressed;
            if (!Denials.CanRetryAfterDenial(actorId)) return CaptureAskResult.GaveUp;
            if (_pending.Count >= CaptureClientState.MaxPending) return CaptureAskResult.Full;
            c = new ClientCapture { ActorId = actorId, RequestId = Guid.NewGuid(), ExpectedOwner = expectedOwner ?? "", Consumes = consumes, CreatedAt = now, NextSendAt = now };
            _pending[actorId] = c; return CaptureAskResult.Queued;
        }

        public ActorTransfer PrepareMessage(ClientCapture c, string me) => new ActorTransfer { ActorId = c.ActorId, CaptureIntent = true, ConsumesActor = c.Consumes, CaptureRequestId = c.RequestId, NewOwner = me, ExpectedPreviousOwner = c.ExpectedOwner, HasExpected = true };
        public ActorTransfer CommitMessage(ClientCapture c, string me) => new ActorTransfer { ActorId = c.ActorId, CaptureCommit = true, ConsumesActor = c.Consumes, CaptureRequestId = c.RequestId, NewOwner = me, ExpectedPreviousOwner = c.ExpectedOwner, HasExpected = true };

        bool CommitReady(ClientCapture c, double now) => c.Phase == CapturePhase.Approved && c.NativeConfirmed && !c.ConsumeInProgress && (c.Consumes || now >= c.NativeConfirmedAt + HeldCommitDelay);

        /// <summary>Drives sends, retries and timeouts. The caller sends the messages the actions describe.</summary>
        public List<CaptureAction> Pump(double now)
        {
            var acts = new List<CaptureAction>(); PruneCompleted(now); Denials.Prune(now);
            foreach (var c in _pending.Values.ToList())
            {
                if (c.PermitArmed && now > c.PermitUntil)
                {
                    c.PermitArmed = false;
                    if (!c.DestroyObserved && !c.NativeConfirmed) c.ConsumeInProgress = false;
                }
                if (c.Phase == CapturePhase.Preparing)
                {
                    if (now - c.CreatedAt > CaptureClientState.PrepareTimeoutSeconds) { _pending.Remove(c.ActorId); acts.Add(new CaptureAction(CaptureActionKind.GaveUp, c, "prepare timeout")); continue; }
                    if (now >= c.NextSendAt && c.PrepareSends < CaptureClientState.MaxPrepareRetries) { c.PrepareSends++; c.NextSendAt = now + CaptureClientState.RetryEverySeconds; acts.Add(new CaptureAction(CaptureActionKind.SendPrepare, c)); }
                    continue;
                }
                if (CommitReady(c, now))
                {
                    if (c.CommitSent && now - c.FirstCommitAt > CaptureClientState.CommitTimeoutSeconds) { _pending.Remove(c.ActorId); acts.Add(new CaptureAction(CaptureActionKind.GaveUp, c, "commit timeout")); continue; }
                    if (now >= c.NextSendAt && c.CommitSends < CaptureClientState.MaxCommitRetries)
                    {
                        if (!c.CommitSent) { c.CommitSent = true; c.FirstCommitAt = now; }
                        c.CommitSends++; c.NextSendAt = now + CaptureClientState.RetryEverySeconds; acts.Add(new CaptureAction(CaptureActionKind.SendCommit, c));
                    }
                    else if (c.CommitSends >= CaptureClientState.MaxCommitRetries && now - c.FirstCommitAt > CaptureClientState.CommitTimeoutSeconds) { _pending.Remove(c.ActorId); acts.Add(new CaptureAction(CaptureActionKind.GaveUp, c, "commit retries exhausted")); }
                    continue;
                }
                if (!c.Irreversible && now - c.ApprovedAt > ApprovalSeconds) { _pending.Remove(c.ActorId); acts.Add(new CaptureAction(CaptureActionKind.SendAbort, c, "approval expired")); }   // the reservation expires on the host too
            }
            return acts;
        }

        /// <summary>A reply from the host. True when it matched a pending capture of ours.</summary>
        public bool OnReply(ActorTransfer m, double now, out ClientCapture c)
        {
            c = null; if (!_pending.TryGetValue(m.ActorId, out var p) || p.RequestId != m.CaptureRequestId) return false;
            c = p;
            if (m.CaptureCommit)
            {
                _pending.Remove(m.ActorId); if (m.CaptureApproved) Complete(p.RequestId, now); else Denials.OnDenied(m.ActorId, now); return true;
            }
            if (!m.CaptureIntent) return false;
            if (m.CaptureApproved) { if (p.Phase == CapturePhase.Preparing) { p.Phase = CapturePhase.Approved; p.ApprovedAt = now; p.NextSendAt = now; Denials.OnApproved(m.ActorId); } }
            else { _pending.Remove(m.ActorId); Denials.OnDenied(m.ActorId, now); }
            return true;
        }

        void Complete(Guid id, double now)
        {
            if (_completed.Count >= CaptureClientState.CompletedCap && _completedOrder.Count > 0) _completed.Remove(_completedOrder.Dequeue());
            _completed[id] = now; _completedOrder.Enqueue(id);
        }
        void PruneCompleted(double now) { while (_completedOrder.Count > 0 && (!_completed.TryGetValue(_completedOrder.Peek(), out var t) || now - t > CaptureClientState.CompletedKeepSeconds)) _completed.Remove(_completedOrder.Dequeue()); }
        public bool WasCompleted(Guid requestId) => _completed.ContainsKey(requestId);

        /// <summary>The native capture is confirmed (consume finished locally, or a held actor is captive on the joint).</summary>
        public bool NoteNativeConfirmed(long actorId, double now)
        {
            if (!_pending.TryGetValue(actorId, out var c) || c.Phase != CapturePhase.Approved) return false;
            if (!c.NativeConfirmed) { c.NativeConfirmed = true; c.NativeConfirmedAt = now; } return true;
        }
        /// <summary>A consume may be downgraded to a plain transfer when the native vacuum only held the actor.</summary>
        public bool DowngradeToHeld(long actorId) { if (_pending.TryGetValue(actorId, out var c) && c.Phase == CapturePhase.Approved && c.Consumes && !c.CommitSent && !c.DestroyObserved) { c.Consumes = false; return true; } return false; }

        public bool AllowsHeldNativePass(long id, bool nativeStoresActor) => !nativeStoresActor &&
            _pending.TryGetValue(id, out var c) && c.Phase == CapturePhase.Approved && !c.Consumes;

        public bool CanCancel(long actorId) => _pending.TryGetValue(actorId, out var c) && !c.Irreversible;
        /// <summary>Per-actor cancel (never a world resync). Null when nothing is pending or it is irreversible.</summary>
        public CaptureAction? Cancel(long actorId, string reason)
        {
            if (!_pending.TryGetValue(actorId, out var c) || c.Irreversible) return null;
            _pending.Remove(actorId); return new CaptureAction(CaptureActionKind.SendAbort, c, reason);
        }
        /// <summary>Cancels every capture except <paramref name="keep"/> that is still unapproved or reversible.</summary>
        public List<CaptureAction> CancelAllExcept(long keep, bool onlyUnapproved, string reason)
        {
            var l = new List<CaptureAction>();
            foreach (var c in _pending.Values.ToList()) { if (c.ActorId == keep || (onlyUnapproved && c.Phase == CapturePhase.Approved)) continue; var a = Cancel(c.ActorId, reason); if (a.HasValue) l.Add(a.Value); }
            return l;
        }
        /// <summary>A world resync keeps irreversible captures and aborts the others.</summary>
        public List<CaptureAction> OnResync() => CancelAllExcept(0, false, "resync");

        /// <summary>The native consume is about to run on an approved consume capture: arm the destroy permit.</summary>
        public bool TryArmConsume(long actorId, double now)
        {
            if (!_pending.TryGetValue(actorId, out var c) || c.Phase != CapturePhase.Approved || !c.Consumes || c.CommitSent || now - c.ApprovedAt > ApprovalSeconds) return false;
            c.ConsumeInProgress = true; c.PermitArmed = true; c.PermitUntil = now + PermitSeconds; return true;
        }
        /// <summary>Allow that exact destroy only for an approved, awaiting-commit consume with an armed permit, before any commit.</summary>
        public bool AllowConsumeDestroy(long actorId, bool connected, double now, bool replayed)
        {
            if (!connected || !_pending.TryGetValue(actorId, out var c)) return false;
            if (c.Phase != CapturePhase.Approved || !c.Consumes || c.CommitSent || now - c.ApprovedAt > ApprovalSeconds) return false;
            return replayed || (c.PermitArmed && now <= c.PermitUntil);
        }
        public void ObserveDestroy(long actorId) { if (_pending.TryGetValue(actorId, out var c)) c.DestroyObserved = true; }
        /// <summary>The native consume returned. When the actor is gone it is confirmed and the commit follows.</summary>
        public void OnConsumeFinished(long actorId, bool actorRemoved, double now)
        {
            if (!_pending.TryGetValue(actorId, out var c)) return;
            c.ConsumeInProgress = false; c.PermitArmed = false;
            if (actorRemoved) { c.DestroyObserved = true; c.NativeConfirmed = true; c.NativeConfirmedAt = now; }
        }

        // ---- native vacuum candidates: valid for 2.5 s within the same scene activation
        public void NoteCandidate(long actorId, int scene, double now) => _candidates[actorId] = (now, scene);
        public bool IsFreshCandidate(long actorId, int scene, double now) => _candidates.TryGetValue(actorId, out var c) && c.scene == scene && now - c.at <= CandidateSeconds;

        public void Reset() { _pending.Clear(); _completed.Clear(); _completedOrder.Clear(); _candidates.Clear(); Denials.Reset(); }
    }
}

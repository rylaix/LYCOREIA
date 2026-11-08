// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Lycoreia.Domain;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.Owned;

namespace Lycoreia.Actors
{
    public struct CaptureFacts
    {
        public bool Live, NativeReadable, HasVacuumable, CanCapture, Captive; public int PlayerScene, ActorScene; public float PlayerToActor, MaxVacuumDistance;
    }

    public sealed class Reservation
    {
        public long ActorId; public string Player; public Guid RequestId; public string ExpectedOwner; public bool Consumes; public double ExpiresAt; public bool Committed; public long Revision;
    }

    public enum CaptureOutcome { Approved, Rejected, Replayed, Committed, CommitRejected, Aborted, AbortRefused, Released }

    /// <summary>Host side of the capture protocol: prepare, approve or reject, commit, abort. The host never changes the owner on prepare.</summary>
    public sealed class CaptureHost
    {
        public const double ReservationSeconds = 35; public const int ResultCache = 4096; public const long ResultKeepMs = 45_000;
        readonly Dictionary<long, Reservation> _byActor = new Dictionary<long, Reservation>();
        readonly RequestLedger<(bool approved, long actorId, string expected)> _prepared;
        readonly RequestLedger<(long revision, uint epoch, bool consumed)> _committed;
        readonly Func<double> _now;
        public CaptureHost(Func<double> nowSeconds, Func<long> clockMs = null)
        { _now = nowSeconds; _prepared = new RequestLedger<(bool, long, string)>(ResultCache, ResultKeepMs, clockMs); _committed = new RequestLedger<(long, uint, bool)>(ResultCache, ResultKeepMs, clockMs); }

        public bool HasReservation(long actorId) { Expire(); return _byActor.TryGetValue(actorId, out var r) && !r.Committed; }
        public Reservation ReservationFor(long actorId) { Expire(); return _byActor.TryGetValue(actorId, out var r) ? r : null; }
        public int Count => _byActor.Count;
        void Expire() { double n = _now(); foreach (var id in _byActor.Where(kv => kv.Value.ExpiresAt <= n).Select(kv => kv.Key).ToList()) _byActor.Remove(id); }
        public void Reset() { _byActor.Clear(); _prepared.Clear(); _committed.Clear(); }

        /// <summary>Authorise and reserve. Returns the outcome and the rejection reason when rejected.</summary>
        public CaptureOutcome Prepare(string authPlayer, ActorTransfer m, string currentOwner, CaptureFacts f, out CaptureRejectionReason reason)
        {
            Expire(); reason = CaptureRejectionReason.None;
            long hash = RequestLedger<int>.Hash(m.ActorId, m.ExpectedPreviousOwner, m.ConsumesActor);
            var look = _prepared.TryGet(authPlayer, m.CaptureRequestId, hash, out var cached);
            if (look == LedgerLookup.Conflict) { reason = CaptureRejectionReason.OperationMismatch; return CaptureOutcome.Rejected; }
            if (look == LedgerLookup.Replay)
            {
                if (cached.approved && _byActor.TryGetValue(m.ActorId, out var active) &&
                    !active.Committed && active.Player == authPlayer && active.RequestId == m.CaptureRequestId &&
                    active.ExpectedOwner == (currentOwner ?? "")) return CaptureOutcome.Replayed;
                // Cached approval is not a new reservation. In particular an expired
                // or aborted request must never authorise another native consume.
                reason = CaptureRejectionReason.Expired; return CaptureOutcome.Rejected;
            }
            reason = ActorCaptureTransferPolicy.GetApprovalRejectionReason(true, authPlayer, authPlayer, m.HasExpected, m.ExpectedPreviousOwner, currentOwner, m.CaptureRequestId,
                f.Live, f.NativeReadable, f.HasVacuumable, f.CanCapture, f.Captive, f.PlayerScene, f.ActorScene, f.PlayerToActor, f.MaxVacuumDistance);
            if (reason == CaptureRejectionReason.None && _byActor.TryGetValue(m.ActorId, out var busy) && busy.Player != authPlayer) reason = CaptureRejectionReason.ReservationBusy;
            if (reason != CaptureRejectionReason.None) { _prepared.Remember(authPlayer, m.CaptureRequestId, hash, (false, m.ActorId, m.ExpectedPreviousOwner)); return CaptureOutcome.Rejected; }
            _byActor[m.ActorId] = new Reservation { ActorId = m.ActorId, Player = authPlayer, RequestId = m.CaptureRequestId, ExpectedOwner = m.ExpectedPreviousOwner ?? "", Consumes = m.ConsumesActor, ExpiresAt = _now() + ReservationSeconds };
            _prepared.Remember(authPlayer, m.CaptureRequestId, hash, (true, m.ActorId, m.ExpectedPreviousOwner));
            return CaptureOutcome.Approved;
        }

        /// <summary>The host already validated and granted an unowned/asleep/attached actor.
        /// Keep the same request reserved until native consume or held confirmation commits.</summary>
        public bool ReserveGranted(string player, ActorTransfer request, string owner)
        {
            Expire();
            long hash = RequestLedger<int>.Hash(request.ActorId, request.ExpectedPreviousOwner, request.ConsumesActor);
            var lookup = _prepared.TryGet(player, request.CaptureRequestId, hash, out _);
            if (lookup != LedgerLookup.Unknown)
                return lookup == LedgerLookup.Replay && _byActor.TryGetValue(request.ActorId, out var active) &&
                    !active.Committed && active.Player == player && active.RequestId == request.CaptureRequestId && active.ExpectedOwner == (owner ?? "");
            _byActor[request.ActorId] = new Reservation { ActorId = request.ActorId, Player = player,
                RequestId = request.CaptureRequestId, ExpectedOwner = owner ?? "", Consumes = request.ConsumesActor,
                ExpiresAt = _now() + ReservationSeconds };
            _prepared.Remember(player, request.CaptureRequestId,
                RequestLedger<int>.Hash(request.ActorId, request.ExpectedPreviousOwner, request.ConsumesActor),
                (true, request.ActorId, owner ?? ""));
            return true;
        }

        /// <summary>Commit: accept only for the authenticated holder with the exact request id and unchanged owner. A consume may be downgraded to plain, never the reverse.</summary>
        public CaptureOutcome Commit(string authPlayer, ActorTransfer m, string currentOwner, bool targetEligible, out Reservation res, out bool consumes, out (long rev, uint epoch, bool consumed) replay)
        {
            Expire(); res = null; consumes = false; replay = default;
            long hash = RequestLedger<int>.Hash(m.ActorId, m.CaptureRequestId);
            if (_committed.TryGet(authPlayer, m.CaptureRequestId, hash, out replay) == LedgerLookup.Replay) return CaptureOutcome.Replayed;   // only commit retries get the final result resent
            _byActor.TryGetValue(m.ActorId, out res);
            bool ok = ActorCaptureTransferPolicy.CanCommitPreparedCapture(true, authPlayer, authPlayer, true, res?.ExpectedOwner, currentOwner, m.CaptureRequestId,
                res != null && !res.Committed && res.RequestId == m.CaptureRequestId && res.Player == authPlayer, res != null && res.ExpiresAt > _now(), targetEligible);
            if (!ok || !ActorCaptureTransferPolicy.IsCompatibleCommitKind(res.Consumes, m.ConsumesActor)) return CaptureOutcome.CommitRejected;
            consumes = res.Consumes && m.ConsumesActor; res.Committed = true; return CaptureOutcome.Committed;
        }

        public void RememberCommit(string authPlayer, ActorTransfer m, long revision, uint epoch, bool consumed)
        {
            _committed.Remember(authPlayer, m.CaptureRequestId, RequestLedger<int>.Hash(m.ActorId, m.CaptureRequestId), (revision, epoch, consumed));
            _byActor.Remove(m.ActorId);
        }
        public void Release(long actorId) => _byActor.Remove(actorId);

        public CaptureOutcome Abort(string authPlayer, CaptureAbort m)
        {
            Expire(); _byActor.TryGetValue(m.ActorId, out var r);
            bool ok = ActorCaptureTransferPolicy.CanAbortPreparedCapture(true, authPlayer, r?.Player, m.ActorId, r?.ActorId ?? 0, m.RequestId, r?.RequestId ?? Guid.Empty, r != null, r?.Committed ?? false);
            if (!ok) return CaptureOutcome.AbortRefused;
            _byActor.Remove(m.ActorId); return CaptureOutcome.Aborted;
        }
    }

    /// <summary>Guest-side bookkeeping for one actor's capture attempts: denial back-off, retry limits, cancel and resync rules.</summary>
    public sealed class CaptureClientState
    {
        public const double PrepareTimeoutSeconds = 8, CommitTimeoutSeconds = 30, RetryEverySeconds = 1.5, DenialMemorySeconds = 30; public const int MaxPrepareRetries = 3, MaxCommitRetries = 16, MaxDenialRetries = 3, MaxTrackedActors = 256, MaxPending = 256, CompletedCap = 512; public const double CompletedKeepSeconds = 60;
        readonly Dictionary<long, (int denials, double retryAfter, double at)> _denials = new Dictionary<long, (int, double, double)>();
        public int DenialCount(long id) => _denials.TryGetValue(id, out var d) ? d.denials : 0;

        /// <summary>Records a denial and returns the earliest next attempt time (0, 250, 500, then 1000 ms back-off).</summary>
        public double OnDenied(long id, double now)
        {
            Prune(now);
            _denials.TryGetValue(id, out var d); d.denials = Math.Min(d.denials + 1, 1000);
            d.retryAfter = now + ActorCaptureTransferPolicy.GetCaptureDenialRetryDelayMilliseconds(d.denials) / 1000.0; d.at = now;
            if (_denials.Count >= MaxTrackedActors && !_denials.ContainsKey(id)) _denials.Remove(_denials.OrderBy(kv => kv.Value.at).First().Key);
            _denials[id] = d; return d.retryAfter;
        }
        public bool ShouldSuppress(long id, double now) => _denials.TryGetValue(id, out var d) && ActorCaptureTransferPolicy.ShouldSuppressCaptureRetry((float)now, (float)d.retryAfter, true);
        public bool CanRetryAfterDenial(long id) => DenialCount(id) <= MaxDenialRetries;
        public void OnApproved(long id) => _denials.Remove(id);
        public void Prune(double now) { foreach (var id in _denials.Where(kv => now - kv.Value.at > DenialMemorySeconds).Select(kv => kv.Key).ToList()) _denials.Remove(id); }
        public void Reset() => _denials.Clear();
    }
}

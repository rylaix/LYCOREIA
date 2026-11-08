// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Owned;

/// <summary>Pure sequencing rule for authoritative shop snapshots and corrections.</summary>
public static class ShopSnapshotRevisionPolicy
{
    internal static bool TryAdvance(ulong currentRevision, out ulong nextRevision)
    {
        if (currentRevision == ulong.MaxValue)
        {
            nextRevision = currentRevision;
            return false;
        }

        nextRevision = currentRevision + 1;
        return nextRevision != 0;
    }

    internal static bool ShouldAccept(Guid currentSession, ulong currentRevision,
        Guid incomingSession, ulong incomingRevision)
        => incomingSession != Guid.Empty && incomingRevision > 0 &&
           (incomingSession != currentSession || incomingRevision > currentRevision);

    internal static bool VerifyRules()
    {
        var session = Guid.Parse("7474c45b-5ca6-41eb-8dd4-293a135ef028");
        var nextSession = Guid.Parse("85f37b94-462d-4cb4-9eaa-d3c0ba8d8e82");
        return !ShouldAccept(session, 7, session, 7)
               && TryAdvance(7, out var correctionRevision)
               && correctionRevision == 8
               && ShouldAccept(session, 7, session, correctionRevision)
               && !ShouldAccept(session, 8, session, correctionRevision)
               && ShouldAccept(session, 8, nextSession, 1)
               && !TryAdvance(ulong.MaxValue, out var exhaustedRevision)
               && exhaustedRevision == ulong.MaxValue;
    }
}

/// <summary>
/// Retains the latest full-state publication reason until capture/send succeeds.
/// A snapshot contains current state, so coalescing several failed publications is safe.
/// </summary>
public sealed class ShopSnapshotPublishRetryState
{
    private string pendingReason;
    private float retryAt;

    internal void Schedule(string reason, float retryAtTime)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return;

        pendingReason = reason;
        retryAt = retryAtTime;
    }

    internal bool TryTake(float now, out string reason)
    {
        if (pendingReason == null || now < retryAt)
        {
            reason = string.Empty;
            return false;
        }

        reason = pendingReason;
        Clear();
        return true;
    }

    internal void Clear()
    {
        pendingReason = null;
        retryAt = 0;
    }

    internal static bool VerifyRules()
    {
        var state = new ShopSnapshotPublishRetryState();
        state.Schedule("purchase", 2f);
        if (state.TryTake(1.99f, out _) || !state.TryTake(2f, out var first) || first != "purchase")
            return false;

        // Model a failed capture after the retry is claimed: the caller must
        // enqueue it again and keep it pending until a later successful capture.
        state.Schedule(first, 3f);
        if (state.TryTake(2.99f, out _) || !state.TryTake(3f, out var retried) || retried != first)
            return false;

        state.Schedule(retried, 4f);
        state.Schedule("restock", 5f);
        return !state.TryTake(4.99f, out _) &&
               state.TryTake(5f, out var coalesced) && coalesced == "restock" &&
               !state.TryTake(5f, out _);
    }
}

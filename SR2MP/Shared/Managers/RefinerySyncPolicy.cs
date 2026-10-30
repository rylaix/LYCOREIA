namespace SR2MP.Shared.Managers;

internal enum RefineryRequestLookup
{
    Invalid,
    New,
    Duplicate,
    ConflictingDuplicate
}

/// <summary>
/// Pure rules for applying refinery count deltas and suppressing repeated client
/// requests. Counts use the game's signed integer representation; arithmetic is
/// widened before checking the int limit to avoid overflow.
/// </summary>
internal static class RefineryMutationPolicy
{
    internal const int MaximumCount = int.MaxValue;

    internal static bool IsValidRequest(
        string? playerId,
        Guid requestId,
        int itemId,
        int delta,
        long revision,
        int itemCount = 0)
        => !string.IsNullOrWhiteSpace(playerId) && requestId != Guid.Empty &&
           itemId != -1 && delta != 0 && revision == 0 && itemCount == 0;

    internal static bool TryApplyDelta(int currentCount, int delta, out int newCount)
    {
        var candidate = (long)currentCount + delta;
        if (currentCount < 0 || currentCount > MaximumCount || delta == 0 ||
            candidate < 0 || candidate > MaximumCount)
        {
            newCount = currentCount;
            return false;
        }

        newCount = (int)candidate;
        return true;
    }

    internal static bool TryApplyNewRequest(
        RefineryRequestLookup lookup,
        int currentCount,
        int delta,
        out int newCount)
    {
        newCount = currentCount;
        return lookup == RefineryRequestLookup.New && TryApplyDelta(currentCount, delta, out newCount);
    }

    internal static bool IsRepresentableCount(int count)
        => count is >= 0 and <= MaximumCount;

    internal static bool VerifyRules()
    {
        var ledger = new RefineryRequestLedger();
        var first = Guid.Parse("a92e65f1-b4a7-4277-89cf-413003e706e5");
        var second = Guid.Parse("2480d095-4a40-49b4-bae0-8455434fec5e");
        var count = 0;

        if (!IsValidRequest("player-a", first, itemId: 23, delta: 1, revision: 0) ||
            IsValidRequest(null, first, itemId: 23, delta: 1, revision: 0) ||
            IsValidRequest("player-a", Guid.Empty, itemId: 23, delta: 1, revision: 0) ||
            IsValidRequest("player-a", first, itemId: -1, delta: 1, revision: 0) ||
            IsValidRequest("player-a", first, itemId: 23, delta: 0, revision: 0) ||
            IsValidRequest("player-a", first, itemId: 23, delta: 1, revision: 1) ||
            IsValidRequest("player-a", first, itemId: 23, delta: 1, revision: 0, itemCount: 1))
            return false;

        // Two players can submit the same +1 against the same initial count; the
        // host processes both deltas serially and retains both contributions.
        var firstLookup = ledger.Observe("player-a", first, itemId: 23, delta: 1);
        if (!TryApplyNewRequest(firstLookup, count, 1, out var firstCount) || firstCount != 1)
            return false;
        count = firstCount;

        var duplicateLookup = ledger.Observe("player-a", first, itemId: 23, delta: 1);
        if (TryApplyNewRequest(duplicateLookup, count, 1, out var duplicateCount) || duplicateCount != count)
            return false;

        // Reusing the first ID with another payload is rejected. A second player's
        // unique request is still accepted against the first request's committed count.
        var secondLookup = ledger.Observe("player-b", second, itemId: 23, delta: 1);
        if (!TryApplyNewRequest(secondLookup, count, 1, out var secondCount) || secondCount != 2 ||
            ledger.Observe("player-a", first, itemId: 24, delta: 1) != RefineryRequestLookup.ConflictingDuplicate ||
            ledger.Observe("player-b", first, itemId: 23, delta: 1) != RefineryRequestLookup.New)
            return false;
        count = secondCount;

        return count == 2 &&
               TryApplyDelta(0, MaximumCount, out var atLimit) && atLimit == MaximumCount &&
               !TryApplyDelta(MaximumCount, 1, out _) &&
               !TryApplyDelta(0, -1, out _) &&
               !TryApplyDelta(10, 0, out _) &&
               !TryApplyDelta(-1, 1, out _) &&
               !IsRepresentableCount(-1) &&
               VerifyRevisionOrdering();
    }

    private static bool VerifyRevisionOrdering()
    {
        var host = new RefineryRevisionTracker();
        var client = new RefineryRevisionTracker();
        return host.Next() == 1 && host.Next() == 2 &&
               client.TryApplySnapshot(2) && !client.TryApplySnapshot(1) &&
               !client.TryAcceptUpdate(2) && client.TryAcceptUpdate(3) &&
               !client.TryAcceptUpdate(1) && client.Current == 3 &&
               host.Current == 2;
    }
}

/// <summary>Per-host-session ledger for idempotent count delta requests.</summary>
internal sealed class RefineryRequestLedger
{
    private readonly Dictionary<(string PlayerId, Guid RequestId), Payload> requests = new();

    internal RefineryRequestLookup Observe(string? playerId, Guid requestId, int itemId, int delta)
    {
        if (string.IsNullOrWhiteSpace(playerId) || requestId == Guid.Empty || itemId == -1 || delta == 0)
            return RefineryRequestLookup.Invalid;

        var key = (playerId, requestId);
        if (requests.TryGetValue(key, out var existing))
            return existing == new Payload(itemId, delta)
                ? RefineryRequestLookup.Duplicate
                : RefineryRequestLookup.ConflictingDuplicate;

        requests.Add(key, new Payload(itemId, delta));
        return RefineryRequestLookup.New;
    }

    internal void ClearPlayer(string playerId)
    {
        foreach (var key in requests.Keys.Where(key => key.PlayerId == playerId).ToArray())
            requests.Remove(key);
    }

    internal void Clear() => requests.Clear();

    private readonly record struct Payload(int ItemId, int Delta);
}

/// <summary>Monotonic world revision shared by host mutations and snapshots.</summary>
internal sealed class RefineryRevisionTracker
{
    internal long Current { get; private set; }

    internal long Next()
    {
        if (Current == long.MaxValue)
            throw new InvalidOperationException("Refinery revision exhausted.");

        return ++Current;
    }

    internal bool CanApplySnapshot(long revision)
        => revision >= 0 && revision >= Current;

    internal bool TryApplySnapshot(long revision)
    {
        if (!CanApplySnapshot(revision))
            return false;

        Current = revision;
        return true;
    }

    internal void ApplySnapshot(long revision)
    {
        if (!CanApplySnapshot(revision))
            throw new InvalidOperationException("Cannot apply a stale refinery snapshot.");

        Current = revision;
    }

    internal bool TryAcceptUpdate(long revision)
    {
        if (revision <= Current)
            return false;

        Current = revision;
        return true;
    }

    internal bool CanAcceptUpdate(long revision)
        => revision > Current;

    internal void Clear() => Current = 0;
}

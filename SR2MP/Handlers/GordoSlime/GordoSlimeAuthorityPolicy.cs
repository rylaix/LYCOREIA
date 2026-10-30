namespace SR2MP.Handlers.GordoSlime;

internal enum GordoFeedDecision
{
    Reject,
    DuplicateSnapshot,
    CommitOne
}

/// <summary>Host-side rules for guest feed requests and burst commits.</summary>
internal static class GordoSlimeAuthorityPolicy
{
    internal static bool CanAcceptSeen(bool authenticated, bool knownGordo, bool typeMatches, bool targetMatches)
        => authenticated && knownGordo && typeMatches && targetMatches;

    internal static GordoFeedDecision DecideFeed(string? gordoId, string? requestId, long actorId,
        int currentFoodCount, int requiredFoodCount, int claimedRequiredFoodCount, int claimedFoodCount,
        bool alreadyPopped, bool actorAlreadyAccepted, int actorAcceptedCount, bool requestAlreadyAccepted)
    {
        if (string.IsNullOrWhiteSpace(gordoId) || string.IsNullOrWhiteSpace(requestId) || actorId == 0 ||
            currentFoodCount < 0 || requiredFoodCount <= 0 || claimedRequiredFoodCount != requiredFoodCount ||
            claimedFoodCount <= 0 || claimedFoodCount > requiredFoodCount)
            return GordoFeedDecision.Reject;

        // Both the actor identity and request ID are dedup keys. Counts confirm the
        // host has already incorporated a duplicate event; they never drive a jump.
        if (actorAlreadyAccepted || requestAlreadyAccepted)
            return GordoFeedDecision.DuplicateSnapshot;

        return !alreadyPopped && currentFoodCount < requiredFoodCount
            ? GordoFeedDecision.CommitOne
            : GordoFeedDecision.Reject;
    }

    internal static int CanonicalCountForDuplicateRequest(int currentFoodCount, int actorAcceptedCount)
        => Math.Max(Math.Max(0, currentFoodCount), Math.Max(0, actorAcceptedCount));

    internal static bool HostCountCoversSubmittedCount(int currentFoodCount, int submittedFoodCount)
        => currentFoodCount >= 0 && submittedFoodCount > 0 && currentFoodCount >= submittedFoodCount;

    internal static int ReconcileHostLocalDuplicate(int observedFoodCount, int actorAcceptedCount,
        int requiredFoodCount)
    {
        if (requiredFoodCount <= 0)
            return 0;

        // DoEat has already applied its local +1 before the postfix runs. Remove
        // that duplicate step, while preserving any other intervening accepted feed.
        return Math.Min(requiredFoodCount,
            CanonicalCountForDuplicateRequest(Math.Max(0, observedFoodCount - 1), actorAcceptedCount));
    }

    internal static bool CanAcceptBurst(string? gordoId, int currentFoodCount, int requiredFoodCount,
        int claimedFoodCount, int claimedRequiredFoodCount, bool alreadyPopped)
        => !string.IsNullOrWhiteSpace(gordoId) && requiredFoodCount > 0 &&
           currentFoodCount >= requiredFoodCount && claimedFoodCount >= requiredFoodCount &&
           claimedRequiredFoodCount == requiredFoodCount && !alreadyPopped;

#if SR2MP_TEST
    internal static bool VerifyRules()
    {
        return CanAcceptSeen(true, true, true, true)
               && !CanAcceptSeen(false, true, true, true)
               && !CanAcceptSeen(true, false, true, true)
               && !CanAcceptSeen(true, true, false, true)
               && !CanAcceptSeen(true, true, true, false)
               && DecideFeed("gordo-a", "request-a", 123, 0, 4, 4, 1, false, false, 0, false)
                  == GordoFeedDecision.CommitOne
               && DecideFeed("gordo-a", "request-a", 123, 4, 4, 4, 5, false, false, 0, false)
                  == GordoFeedDecision.Reject
               && DecideFeed("gordo-a", "request-a", 123, 0, 4, 1, 1, false, false, 0, false)
                  == GordoFeedDecision.Reject
               && DecideFeed("gordo-a", "", 123, 0, 4, 4, 1, false, false, 0, false)
                  == GordoFeedDecision.Reject
               && DecideFeed("gordo-a", "request-a", 0, 0, 4, 4, 1, false, false, 0, false)
                  == GordoFeedDecision.Reject
               && DecideFeed("gordo-a", "request-a", 123, 4, 4, 4, 4, false, true, 4, false)
                  == GordoFeedDecision.DuplicateSnapshot
               && DecideFeed("gordo-a", "request-a", 123, 1, 4, 4, 1, false, false, 0, true)
                  == GordoFeedDecision.DuplicateSnapshot
               && CanonicalCountForDuplicateRequest(currentFoodCount: 1, actorAcceptedCount: 1) == 1
               && HostCountCoversSubmittedCount(currentFoodCount: 1, submittedFoodCount: 1)
               && !HostCountCoversSubmittedCount(currentFoodCount: 1, submittedFoodCount: 2)
               && ReconcileHostLocalDuplicate(observedFoodCount: 2, actorAcceptedCount: 1,
                   requiredFoodCount: 4) == 1
               && ReconcileHostLocalDuplicate(observedFoodCount: 3, actorAcceptedCount: 1,
                   requiredFoodCount: 4) == 2
               && CanAcceptBurst("gordo-a", 4, 4, 4, 4, false)
               && !CanAcceptBurst("gordo-a", 3, 4, 4, 4, false)
               && !CanAcceptBurst("gordo-a", 4, 4, 4, 1, false)
               && !CanAcceptBurst("gordo-a", 4, 4, 4, 4, true);
    }
#endif
}

internal sealed class GordoFeedRequestLedger
{
    private readonly HashSet<(string GordoId, string RequestId)> accepted = new();
    private readonly Queue<(string GordoId, string RequestId)> insertionOrder = new();
    private readonly int capacity;

    internal GordoFeedRequestLedger(int capacity = 4096)
        => this.capacity = Math.Max(1, capacity);

    internal bool TryAccept(string gordoId, string requestId)
    {
        if (string.IsNullOrWhiteSpace(gordoId) || string.IsNullOrWhiteSpace(requestId))
            return false;

        var key = (gordoId, requestId);
        if (!accepted.Add(key))
            return false;

        insertionOrder.Enqueue(key);
        while (insertionOrder.Count > capacity)
            accepted.Remove(insertionOrder.Dequeue());
        return true;
    }

    internal bool Contains(string gordoId, string requestId)
        => !string.IsNullOrWhiteSpace(gordoId) && !string.IsNullOrWhiteSpace(requestId) &&
           accepted.Contains((gordoId, requestId));

    internal void Clear()
    {
        accepted.Clear();
        insertionOrder.Clear();
    }

#if SR2MP_TEST
    internal static bool VerifyRules()
    {
        var ledger = new GordoFeedRequestLedger(2);
        return ledger.TryAccept("gordo-a", "request-1")
               && !ledger.TryAccept("gordo-a", "request-1")
               && ledger.TryAccept("gordo-b", "request-1")
               && ledger.TryAccept("gordo-a", "request-2")
               && ledger.TryAccept("gordo-a", "request-1")
               && !ledger.TryAccept("", "request-3");
    }
#endif
}

internal sealed class GordoFeedActorLedger
{
    private readonly Dictionary<(string GordoId, long ActorId), int> accepted = new();
    private readonly Queue<(string GordoId, long ActorId)> insertionOrder = new();
    private readonly int capacity;

    internal GordoFeedActorLedger(int capacity = 4096)
        => this.capacity = Math.Max(1, capacity);

    internal bool TryGetAcceptedCount(string gordoId, long actorId, out int acceptedCount)
    {
        acceptedCount = 0;
        return actorId != 0 && !string.IsNullOrWhiteSpace(gordoId) &&
               accepted.TryGetValue((gordoId, actorId), out acceptedCount);
    }

    internal bool TryAccept(string gordoId, long actorId, int canonicalFoodCount, out int acceptedCount)
    {
        acceptedCount = 0;
        if (actorId == 0 || string.IsNullOrWhiteSpace(gordoId) || canonicalFoodCount <= 0)
            return false;

        var key = (gordoId, actorId);
        if (accepted.TryGetValue(key, out acceptedCount))
            return false;

        acceptedCount = canonicalFoodCount;
        accepted.Add(key, canonicalFoodCount);
        insertionOrder.Enqueue(key);
        while (insertionOrder.Count > capacity)
            accepted.Remove(insertionOrder.Dequeue());
        return true;
    }

    internal void Clear()
    {
        accepted.Clear();
        insertionOrder.Clear();
    }

#if SR2MP_TEST
    internal static bool VerifyOrderings()
    {
        const string gordoId = "gordo-ordering";
        const long foodActorId = 4242;
        const int target = 4;

        // Host-first: the host DoEat postfix records its native increment; the
        // guest packet for the same food must return that count unchanged.
        var hostFirst = new GordoFeedActorLedger(4);
        var hostCount = 1;
        if (!hostFirst.TryAccept(gordoId, foodActorId, hostCount, out _) ||
            !hostFirst.TryGetAcceptedCount(gordoId, foodActorId, out var previousHostCount) ||
            GordoSlimeAuthorityPolicy.DecideFeed(gordoId, "guest-host-first", foodActorId,
                hostCount, target, target, claimedFoodCount: 1, alreadyPopped: false,
                actorAlreadyAccepted: true, actorAcceptedCount: previousHostCount,
                requestAlreadyAccepted: false) !=
            GordoFeedDecision.DuplicateSnapshot ||
            GordoSlimeAuthorityPolicy.CanonicalCountForDuplicateRequest(hostCount, previousHostCount) != 1)
            return false;

        // Guest-first: host accepts one new actor and advances once. When the
        // host's later native DoEat runs for that same actor, its postfix rolls
        // back only that duplicate local increment.
        var guestFirst = new GordoFeedActorLedger(4);
        hostCount = 0;
        var decision = GordoSlimeAuthorityPolicy.DecideFeed(gordoId, "guest-first", foodActorId,
            hostCount, target, target, claimedFoodCount: 1, alreadyPopped: false,
            actorAlreadyAccepted: false, actorAcceptedCount: 0, requestAlreadyAccepted: false);
        if (decision != GordoFeedDecision.CommitOne ||
            !guestFirst.TryAccept(gordoId, foodActorId, ++hostCount, out _))
            return false;

        var hostNativeObservedCount = hostCount + 1;
        if (!guestFirst.TryGetAcceptedCount(gordoId, foodActorId, out var previousGuestCount) ||
            guestFirst.TryAccept(gordoId, foodActorId, hostNativeObservedCount, out _) ||
            GordoSlimeAuthorityPolicy.ReconcileHostLocalDuplicate(
                hostNativeObservedCount, previousGuestCount, target) != hostCount)
            return false;

        if (!guestFirst.TryAccept(gordoId, -foodActorId, canonicalFoodCount: 1, out _) ||
            !guestFirst.TryGetAcceptedCount(gordoId, -foodActorId, out var signedActorCount) ||
            signedActorCount != 1)
            return false;

        // A distinct guest food remains a new event even when its submitted local
        // count equals a count already produced by the host.
        return GordoSlimeAuthorityPolicy.DecideFeed(gordoId, "guest-new-food", foodActorId + 1,
                   currentFoodCount: 1, requiredFoodCount: target, claimedRequiredFoodCount: target,
                   claimedFoodCount: 1, alreadyPopped: false, actorAlreadyAccepted: false,
                   actorAcceptedCount: 0, requestAlreadyAccepted: false) == GordoFeedDecision.CommitOne;
    }
#endif
}

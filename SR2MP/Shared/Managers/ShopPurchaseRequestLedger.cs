namespace SR2MP.Shared.Managers;

/// <summary>
/// Bounded idempotency cache for host decisions on shop purchase requests.
/// A request ID is bound to the authenticated player and the complete purchase
/// intent so retries cannot change the item, count, or sender.
/// </summary>
internal sealed class ShopPurchaseRequestLedger
{
    private readonly int capacity;
    private readonly Dictionary<Guid, Decision> decisions = new();
    private readonly Queue<Guid> order = new();

    internal ShopPurchaseRequestLedger(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        this.capacity = capacity;
    }

    internal ShopPurchaseRequestLookup TryGetDecision(
        string? authenticatedPlayerId,
        Guid requestId,
        string? shopAssetTag,
        string? itemAssetGuid,
        int count,
        out bool accepted)
    {
        accepted = false;
        if (!IsValidDecisionIdentity(authenticatedPlayerId, requestId, shopAssetTag, itemAssetGuid, count))
            return ShopPurchaseRequestLookup.Invalid;

        if (!decisions.TryGetValue(requestId, out var decision))
            return ShopPurchaseRequestLookup.Missing;

        if (!decision.Matches(authenticatedPlayerId!, shopAssetTag!, itemAssetGuid!, count))
            return ShopPurchaseRequestLookup.ConflictingDuplicate;

        accepted = decision.Accepted;
        return ShopPurchaseRequestLookup.Duplicate;
    }

    internal void Remember(string playerId, Guid requestId, string shopAssetTag,
        string itemAssetGuid, int count, bool accepted)
    {
        if (!IsValidDecisionIdentity(playerId, requestId, shopAssetTag, itemAssetGuid, count) ||
            decisions.ContainsKey(requestId))
            return;

        decisions.Add(requestId,
            new Decision(playerId, new PurchasePayloadFingerprint(shopAssetTag, itemAssetGuid, count), accepted));
        order.Enqueue(requestId);
        while (order.Count > capacity)
            decisions.Remove(order.Dequeue());
    }

    internal void Clear()
    {
        decisions.Clear();
        order.Clear();
    }

    internal static bool IsValidRequest(Guid requestId, string? shopAssetTag,
        string? itemAssetGuid, int count, int maxCount)
        => requestId != Guid.Empty &&
           count is >= 1 && count <= maxCount &&
           !string.IsNullOrWhiteSpace(shopAssetTag) &&
           !string.IsNullOrWhiteSpace(itemAssetGuid);

    private static bool IsValidDecisionIdentity(string? playerId, Guid requestId,
        string? shopAssetTag, string? itemAssetGuid, int count)
        => !string.IsNullOrWhiteSpace(playerId) && requestId != Guid.Empty && count >= 1 &&
           !string.IsNullOrWhiteSpace(shopAssetTag) &&
           !string.IsNullOrWhiteSpace(itemAssetGuid);

    internal static bool VerifyRules()
    {
        if (!ShopSnapshotRevisionPolicy.VerifyRules() || !ShopSnapshotPublishRetryState.VerifyRules())
            return false;

        var ledger = new ShopPurchaseRequestLedger(2);
        var first = Guid.Parse("1a884ab3-3f1f-447b-ae84-af7a310e7e1b");
        var second = Guid.Parse("28f8c5df-699c-49ce-86bd-9cacdfcdca90");
        var third = Guid.Parse("37ee7e48-1146-487c-845f-750d7130824a");

        if (!IsValidRequest(first, "shop", "item", 1, 1000) ||
            IsValidRequest(Guid.Empty, "shop", "item", 1, 1000) ||
            IsValidRequest(first, "shop", "item", 0, 1000) ||
            IsValidRequest(first, "shop", "item", 1001, 1000) ||
            IsValidRequest(first, " ", "item", 1, 1000) ||
            IsValidRequest(first, "shop", null, 1, 1000) ||
            IsValidDecisionIdentity(" ", first, "shop", "item", 1) ||
            IsValidDecisionIdentity("player-a", Guid.Empty, "shop", "item", 1) ||
            IsValidDecisionIdentity("player-a", first, "shop", "item", 0))
            return false;

        ledger.Remember("player-a", first, "shop", "item", 1, accepted: true);
        if (ledger.TryGetDecision("player-a", first, "shop", "item", 1, out var firstAccepted) !=
                ShopPurchaseRequestLookup.Duplicate || !firstAccepted ||
            ledger.TryGetDecision("player-a", second, "shop", "item", 1, out _) !=
                ShopPurchaseRequestLookup.Missing)
            return false;

        // Reusing one GUID with a different sender or purchase payload must never
        // replay the earlier acceptance as if it belonged to the new request.
        if (ledger.TryGetDecision("player-b", first, "shop", "item", 1, out var conflictingDecision) !=
                ShopPurchaseRequestLookup.ConflictingDuplicate ||
            conflictingDecision ||
            ledger.TryGetDecision("player-a", first, "other-shop", "item", 1, out _) !=
                ShopPurchaseRequestLookup.ConflictingDuplicate ||
            ledger.TryGetDecision("player-a", first, "shop", "other-item", 1, out _) !=
                ShopPurchaseRequestLookup.ConflictingDuplicate ||
            ledger.TryGetDecision("player-a", first, "shop", "item", 2, out _) !=
                ShopPurchaseRequestLookup.ConflictingDuplicate)
            return false;

        ledger.Remember("player-a", second, "shop", "item", 1, accepted: false);
        if (ledger.TryGetDecision("player-a", second, "shop", "item", 1, out var rejected) !=
                ShopPurchaseRequestLookup.Duplicate || rejected)
            return false;

        ledger.Remember("player-a", third, "shop", "item", 1, accepted: true);
        return ledger.TryGetDecision("player-a", first, "shop", "item", 1, out _) ==
                   ShopPurchaseRequestLookup.Missing &&
               ledger.TryGetDecision("player-a", second, "shop", "item", 1, out rejected) ==
                   ShopPurchaseRequestLookup.Duplicate && !rejected &&
               ledger.TryGetDecision("player-a", third, "shop", "item", 1, out var thirdAccepted) ==
                   ShopPurchaseRequestLookup.Duplicate && thirdAccepted;
    }

    private readonly record struct Decision(
        string AuthenticatedPlayerId,
        PurchasePayloadFingerprint PayloadFingerprint,
        bool Accepted)
    {
        internal bool Matches(string playerId, string shopAssetTag, string itemAssetGuid, int count)
            => string.Equals(AuthenticatedPlayerId, playerId, StringComparison.Ordinal) &&
               PayloadFingerprint.Matches(shopAssetTag, itemAssetGuid, count);
    }

    /// <summary>
    /// Exact structural fingerprint of the caller supplied purchase payload.
    /// Keeping the original identity fields avoids hash collisions while binding
    /// retries to the same shop, item and count.
    /// </summary>
    private readonly record struct PurchasePayloadFingerprint(
        string ShopAssetTag,
        string ItemAssetGuid,
        int Count)
    {
        internal bool Matches(string shopAssetTag, string itemAssetGuid, int count)
            => string.Equals(ShopAssetTag, shopAssetTag, StringComparison.Ordinal) &&
               string.Equals(ItemAssetGuid, itemAssetGuid, StringComparison.Ordinal) &&
               Count == count;
    }
}

internal enum ShopPurchaseRequestLookup
{
    Invalid,
    Missing,
    Duplicate,
    ConflictingDuplicate
}

namespace SR2MP.Shared.Managers;

/// <summary>
/// Bounded per-player request cache for host-authoritative upgrade purchases.
/// Prevents retries of the same request from invoking the native transaction twice.
/// </summary>
internal sealed class UpgradePurchaseRequestLedger
{
    private readonly int capacity;
    private readonly Dictionary<(string PlayerId, Guid RequestId), Decision> decisions = new();
    private readonly Queue<(string PlayerId, Guid RequestId)> order = new();

    internal UpgradePurchaseRequestLedger(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        this.capacity = capacity;
    }

    internal UpgradePurchaseRequestLookup TryGetDecision(
        string playerId,
        Guid requestId,
        byte upgradeId,
        sbyte expectedLevel,
        out bool accepted)
    {
        if (!decisions.TryGetValue((playerId, requestId), out var decision))
        {
            accepted = false;
            return UpgradePurchaseRequestLookup.Missing;
        }

        accepted = decision.Accepted;
        return decision.UpgradeId == upgradeId && decision.ExpectedLevel == expectedLevel
            ? UpgradePurchaseRequestLookup.Duplicate
            : UpgradePurchaseRequestLookup.ConflictingDuplicate;
    }

    internal void Remember(string playerId, Guid requestId, byte upgradeId, sbyte expectedLevel, bool accepted)
    {
        if (string.IsNullOrWhiteSpace(playerId) || requestId == Guid.Empty ||
            decisions.ContainsKey((playerId, requestId)))
            return;

        var key = (playerId, requestId);
        decisions.Add(key, new Decision(upgradeId, expectedLevel, accepted));
        order.Enqueue(key);
        while (order.Count > capacity)
            decisions.Remove(order.Dequeue());
    }

    internal void Clear()
    {
        decisions.Clear();
        order.Clear();
    }

    internal static bool IsValidRequest(string? playerId, Guid requestId, sbyte expectedLevel)
        => !string.IsNullOrWhiteSpace(playerId) && requestId != Guid.Empty && expectedLevel >= 0;

    internal static bool VerifyRules()
    {
        var ledger = new UpgradePurchaseRequestLedger(3);
        const string playerA = "player-a";
        const string playerB = "player-b";
        var first = Guid.Parse("c4c87c9e-0a73-4cc4-9f45-03c4dc351a20");
        var second = Guid.Parse("c90995b0-5c8f-4c94-9f8d-9104994073a6");
        var third = Guid.Parse("d89b9dd1-8544-45d0-8410-5d5ca1ad6c9e");

        if (!IsValidRequest(playerA, first, 0) ||
            IsValidRequest(null, first, 0) ||
            IsValidRequest(playerA, Guid.Empty, 0) ||
            IsValidRequest(playerA, first, -1))
            return false;

        ledger.Remember(playerA, first, upgradeId: 12, expectedLevel: 0, accepted: true);
        ledger.Remember(playerA, first, upgradeId: 12, expectedLevel: 0, accepted: false);
        ledger.Remember(playerB, first, upgradeId: 12, expectedLevel: 0, accepted: false);
        if (ledger.TryGetDecision(playerA, first, 12, 0, out var accepted) != UpgradePurchaseRequestLookup.Duplicate || !accepted ||
            ledger.TryGetDecision(playerA, first, 13, 0, out _) != UpgradePurchaseRequestLookup.ConflictingDuplicate ||
            ledger.TryGetDecision(playerA, first, 12, 1, out _) != UpgradePurchaseRequestLookup.ConflictingDuplicate ||
            ledger.TryGetDecision(playerB, first, 12, 0, out var rejected) != UpgradePurchaseRequestLookup.Duplicate || rejected ||
            ledger.TryGetDecision(playerA, second, 12, 0, out _) != UpgradePurchaseRequestLookup.Missing)
            return false;

        ledger.Remember(playerA, second, upgradeId: 12, expectedLevel: 0, accepted: false);
        ledger.Remember(playerA, third, upgradeId: 13, expectedLevel: 1, accepted: true);
        return ledger.TryGetDecision(playerA, first, 12, 0, out _) == UpgradePurchaseRequestLookup.Missing &&
               ledger.TryGetDecision(playerB, first, 12, 0, out rejected) == UpgradePurchaseRequestLookup.Duplicate && !rejected &&
               ledger.TryGetDecision(playerA, second, 12, 0, out rejected) == UpgradePurchaseRequestLookup.Duplicate && !rejected &&
               ledger.TryGetDecision(playerA, third, 13, 1, out accepted) == UpgradePurchaseRequestLookup.Duplicate && accepted;
    }

    private readonly record struct Decision(byte UpgradeId, sbyte ExpectedLevel, bool Accepted);
}

internal enum UpgradePurchaseRequestLookup
{
    Missing,
    Duplicate,
    ConflictingDuplicate
}

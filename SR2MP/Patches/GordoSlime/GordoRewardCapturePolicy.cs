namespace SR2MP.Patches.GordoSlime;

/// <summary>Pure rules for stable Gordo reward indexing and failed payout recovery.</summary>
internal static class GordoRewardCapturePolicy
{
    internal const int IndexingAttempts = 5;
    internal const float IndexingRetryDelaySeconds = 0.25f;
    internal const float FailureReconciliationDelaySeconds = 1f;

    internal static GordoRewardFailureOutcome ClassifyFailureReconciliation(bool scanSucceeded,
        int matchingRewardCount)
        => scanSucceeded && matchingRewardCount == 0
            ? GordoRewardFailureOutcome.SafeToRetry
            : GordoRewardFailureOutcome.Uncertain;

    internal static GordoRewardInstanceIdentity GetOrAssignIdentity(
        IDictionary<int, GordoRewardInstanceIdentity> identitiesByInstance,
        IDictionary<int, int> nextOrdinalByPrefab,
        int instanceId,
        int prefabIndex)
    {
        if (identitiesByInstance.TryGetValue(instanceId, out var existing))
            return existing.PrefabIndex == prefabIndex ? existing : new GordoRewardInstanceIdentity(-1, -1);

        nextOrdinalByPrefab.TryGetValue(prefabIndex, out var ordinal);
        identitiesByInstance.Add(instanceId, new GordoRewardInstanceIdentity(prefabIndex, ordinal));
        nextOrdinalByPrefab[prefabIndex] = ordinal + 1;
        return new GordoRewardInstanceIdentity(prefabIndex, ordinal);
    }

    internal static float RecoveryDelaySeconds(int failedAttempts)
        => failedAttempts switch
        {
            <= 1 => 1f,
            2 => 2f,
            3 => 4f,
            4 => 8f,
            _ => 15f
        };

    internal static bool VerifyRules()
    {
        var identities = new Dictionary<int, GordoRewardInstanceIdentity>();
        var nextOrdinals = new Dictionary<int, int>();
        var first = GetOrAssignIdentity(identities, nextOrdinals, 100, 2);
        var second = GetOrAssignIdentity(identities, nextOrdinals, 101, 2);
        var late = GetOrAssignIdentity(identities, nextOrdinals, 99, 2);
        var firstAgain = GetOrAssignIdentity(identities, nextOrdinals, 100, 2);
        var mismatchedPrefab = GetOrAssignIdentity(identities, nextOrdinals, 100, 3);

        return first == new GordoRewardInstanceIdentity(2, 0)
               && second == new GordoRewardInstanceIdentity(2, 1)
               && late == new GordoRewardInstanceIdentity(2, 2)
               && firstAgain == first
               && mismatchedPrefab == new GordoRewardInstanceIdentity(-1, -1)
               && ClassifyFailureReconciliation(scanSucceeded: true, matchingRewardCount: 0) ==
               GordoRewardFailureOutcome.SafeToRetry
               && ClassifyFailureReconciliation(scanSucceeded: true, matchingRewardCount: 1) ==
               GordoRewardFailureOutcome.Uncertain
               && ClassifyFailureReconciliation(scanSucceeded: false, matchingRewardCount: 0) ==
               GordoRewardFailureOutcome.Uncertain
               && RecoveryDelaySeconds(1) < RecoveryDelaySeconds(2)
               && RecoveryDelaySeconds(5) == RecoveryDelaySeconds(50);
    }
}

internal readonly record struct GordoRewardInstanceIdentity(int PrefabIndex, int RewardOrdinal);

internal enum GordoRewardFailureOutcome
{
    SafeToRetry,
    Uncertain
}

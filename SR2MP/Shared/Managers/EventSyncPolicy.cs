namespace SR2MP.Shared.Managers;

/// <summary>
/// Pure count reconciliation used by live event deltas. Wire counts are absolute
/// targets, while EventDirector.RaiseEvent accepts an increment.
/// </summary>
internal static class EventSyncPolicy
{
    internal const int MaxAppliedIncrement = 10_000;
    internal const string ZoneUnlockedEventKey = "zoneUnlocked";
    internal const int UnlockedZoneCount = 1;

    internal static bool IsZoneUnlockedEvent(string? eventKey)
        => string.Equals(eventKey, ZoneUnlockedEventKey, StringComparison.Ordinal);

    internal static int NormalizeTargetCount(string? eventKey, int targetCount)
    {
        if (IsZoneUnlockedEvent(eventKey))
            return targetCount > 0 ? UnlockedZoneCount : 0;

        return Math.Max(0, targetCount);
    }

    internal static bool IsValidZoneDataKey(string? dataKey)
        => !string.IsNullOrWhiteSpace(dataKey)
           && dataKey.Length <= 128
           && !dataKey.Any(char.IsControl);

    internal static bool ShouldApplyZoneUnlock(int currentCount, int requestedCount)
        => currentCount <= 0 && requestedCount > 0;

    internal static bool ShouldIncludeZoneUnlockSnapshot(int count)
        => count > 0;

    internal static bool CanApplyZoneUnlockState(bool eventModelReady)
        => eventModelReady;

    internal static int GetIncrement(int currentCount, int targetCount)
    {
        if (currentCount < 0 || targetCount <= currentCount)
            return 0;

        return Math.Min(targetCount - currentCount, MaxAppliedIncrement);
    }

    internal static bool VerifyRules()
        => GetIncrement(0, 1) == 1
           && GetIncrement(4, 7) == 3
           && GetIncrement(7, 7) == 0
           && GetIncrement(8, 3) == 0
           && GetIncrement(0, 0) == 0
           && GetIncrement(0, -1) == 0
           && GetIncrement(-1, 3) == 0
           && GetIncrement(0, int.MaxValue) == MaxAppliedIncrement;

    internal static bool VerifyZoneUnlockRules()
        => IsZoneUnlockedEvent("zoneUnlocked")
           && !IsZoneUnlockedEvent("fogRevealed")
           && NormalizeTargetCount("zoneUnlocked", 99) == UnlockedZoneCount
           && NormalizeTargetCount("zoneUnlocked", 0) == 0
           && NormalizeTargetCount("objectDiscovered", 99) == 99
           && IsValidZoneDataKey("conservatory")
           && !IsValidZoneDataKey(" ")
           && !IsValidZoneDataKey(new string('z', 129))
           && !IsValidZoneDataKey("zone\nname")
           && ShouldApplyZoneUnlock(0, 1)
           && !ShouldApplyZoneUnlock(1, 1)
           && !ShouldApplyZoneUnlock(2, 1)
           && !ShouldApplyZoneUnlock(0, 0)
           && ShouldIncludeZoneUnlockSnapshot(1)
           && CanApplyZoneUnlockState(eventModelReady: true)
           && !CanApplyZoneUnlockState(eventModelReady: false)
           && ShouldIncludeZoneUnlockSnapshot(99)
           && !ShouldIncludeZoneUnlockSnapshot(0)
           && !ShouldIncludeZoneUnlockSnapshot(-1);
}

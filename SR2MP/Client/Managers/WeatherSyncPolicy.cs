namespace SR2MP.Client.Managers;

/// <summary>
/// Pure ordering and reconciliation rules shared by the weather and world-time
/// handlers. Revisions are strictly increasing within one host process/session.
/// </summary>
internal static class WeatherSyncPolicy
{
    internal static bool IsNewRevision(long incoming, long latestReceived)
        => incoming > latestReceived;

    internal static bool CanApplyRevision(long candidate, long latestReceived, long lastApplied)
        => candidate == latestReceived && candidate > lastApplied;

    internal static bool ShouldReplacePending(long incoming, long pending, long lastApplied)
        => incoming > pending && incoming > lastApplied;

    internal static bool ShouldRunForecast(bool started) => started;

    internal static bool ShouldCorrectWorldTime(double authoritativeTime, double localTime)
        => double.IsFinite(authoritativeTime)
           && (!double.IsFinite(localTime) || authoritativeTime != localTime);

    internal static bool VerifyRules()
        => IsNewRevision(8, 7)
           && !IsNewRevision(7, 8)
           && CanApplyRevision(8, 8, 7)
           && !CanApplyRevision(7, 8, 6)
           && !CanApplyRevision(8, 8, 8)
           && ShouldReplacePending(8, 7, 6)
           && !ShouldReplacePending(7, 8, 6)
           && !ShouldReplacePending(8, 7, 8)
           && ShouldRunForecast(true)
           && !ShouldRunForecast(false)
           && ShouldCorrectWorldTime(20, 10)
           && ShouldCorrectWorldTime(10, 20)
           && !ShouldCorrectWorldTime(10, 10)
           && !ShouldCorrectWorldTime(double.NaN, 10)
           && ShouldCorrectWorldTime(10, double.PositiveInfinity);
}

namespace SR2MP.Shared.Utils;

/// <summary>Pure threshold policy for the post-approval host liveness watchdog.</summary>
internal static class ServerHeartbeatTimeoutPolicy
{
    internal static bool IsExpired(long lastHeartbeatTicks, long nowTicks, long timeoutTicks)
        => lastHeartbeatTicks <= 0 || nowTicks - lastHeartbeatTicks >= timeoutTicks;

#if SR2MP_TEST
    internal static bool VerifyRules()
        => !IsExpired(100, 199, 100) && IsExpired(100, 200, 100) &&
           IsExpired(0, 10, 100) && !IsExpired(200, 150, 100);
#endif
}

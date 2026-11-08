// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Owned;

/// <summary>Pure threshold policy for the post-approval host liveness watchdog.</summary>
public static class ServerHeartbeatTimeoutPolicy
{
    internal static bool IsExpired(long lastHeartbeatTicks, long nowTicks, long timeoutTicks)
        => lastHeartbeatTicks <= 0 || nowTicks - lastHeartbeatTicks >= timeoutTicks;

#if LYC_TEST
    internal static bool VerifyRules()
        => !IsExpired(100, 199, 100) && IsExpired(100, 200, 100) &&
           IsExpired(0, 10, 100) && !IsExpired(200, 150, 100);
#endif
}

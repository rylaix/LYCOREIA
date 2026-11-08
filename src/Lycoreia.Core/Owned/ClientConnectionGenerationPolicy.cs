// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Owned;

/// <summary>Rejects receive and timer callbacks that belong to a closed attempt.</summary>
public static class ClientConnectionGenerationPolicy
{
    internal static bool IsCurrent(long callbackGeneration, long activeGeneration)
        => callbackGeneration > 0 && callbackGeneration == activeGeneration;

    internal static bool ShouldScheduleReceiveFailure(bool isConnected, long callbackGeneration,
        long activeGeneration)
        => isConnected && IsCurrent(callbackGeneration, activeGeneration);

#if LYC_TEST
    internal static bool VerifyDenialRetryRules()
    {
        const long firstAttempt = 4;
        const long retryAttempt = 5;
        return ShouldScheduleReceiveFailure(true, firstAttempt, firstAttempt) &&
               !ShouldScheduleReceiveFailure(false, firstAttempt, firstAttempt) &&
               !ShouldScheduleReceiveFailure(true, firstAttempt, retryAttempt) &&
               ShouldScheduleReceiveFailure(true, retryAttempt, retryAttempt) &&
               !ShouldScheduleReceiveFailure(true, 0, retryAttempt);
    }
#endif
}

namespace SR2MP.Shared.Utils;

/// <summary>Rejects receive and timer callbacks that belong to a closed attempt.</summary>
internal static class ClientConnectionGenerationPolicy
{
    internal static bool IsCurrent(long callbackGeneration, long activeGeneration)
        => callbackGeneration > 0 && callbackGeneration == activeGeneration;

    internal static bool ShouldScheduleReceiveFailure(bool isConnected, long callbackGeneration,
        long activeGeneration)
        => isConnected && IsCurrent(callbackGeneration, activeGeneration);

#if SR2MP_TEST
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

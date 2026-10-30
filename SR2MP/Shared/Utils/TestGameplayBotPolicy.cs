using SR2MP.Shared.Managers;

namespace SR2MP.Shared.Utils;

/// <summary>
/// Pure success gates for game-action probes. The probe cannot call capture
/// successful from ownership alone, nor consume successful while either client
/// still has the actor model or actor component tracked.
/// </summary>
internal static class TestGameplayBotPolicy
{
    internal static bool IsDesignatedCaptureCandidate(long targetActorId, long candidateActorId,
        string? identifiableTypeName)
        => targetActorId > 0 && candidateActorId == targetActorId &&
           string.Equals(identifiableTypeName, "Chick", StringComparison.OrdinalIgnoreCase);

    internal static bool IsNativeHeldStateValid(bool heldReferenceMatches)
        => heldReferenceMatches;

    internal static bool CanReportCapturePass(
        bool guestActorPresent,
        bool guestModelPresent,
        bool hostActorPresent,
        bool hostModelPresent,
        bool guestAmmoIncrementConfirmed)
        => !guestActorPresent && !guestModelPresent && !hostActorPresent && !hostModelPresent &&
           guestAmmoIncrementConfirmed;

#if SR2MP_TEST
    internal static bool VerifyCaptureTargetSelection()
    {
        const long targetActorId = 0xC9;
        return !IsDesignatedCaptureCandidate(targetActorId, 0x65, "CarrotVeggie") &&
               !IsDesignatedCaptureCandidate(targetActorId, 0x64, "Chick") &&
               IsDesignatedCaptureCandidate(targetActorId, targetActorId, "Chick") &&
               !IsDesignatedCaptureCandidate(0, targetActorId, "Chick") &&
               !IsDesignatedCaptureCandidate(targetActorId, targetActorId, "CarrotVeggie");
    }

    internal static bool VerifyCaptureSuccessGates()
        => !IsNativeHeldStateValid(heldReferenceMatches: false) &&
           IsNativeHeldStateValid(heldReferenceMatches: true) &&
           !CanReportCapturePass(false, false, true, false, true) &&
           !CanReportCapturePass(false, false, false, true, true) &&
           !CanReportCapturePass(true, false, false, false, true) &&
           !CanReportCapturePass(false, false, false, false, false) &&
           CanReportCapturePass(false, false, false, false, true) &&
           ActorCaptureTransferPolicy.CanAdvanceCaptureBotToConsume(true, true, true) &&
           !ActorCaptureTransferPolicy.CanAdvanceCaptureBotToConsume(true, true, false) &&
           ActorCaptureTransferPolicy.HasExpectedAmmoIncrement(0, 1) &&
           !ActorCaptureTransferPolicy.HasExpectedAmmoIncrement(0, 0) &&
           !ActorCaptureTransferPolicy.HasExpectedAmmoIncrement(1, 3);
#endif
}

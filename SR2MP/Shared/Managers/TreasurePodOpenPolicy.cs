namespace SR2MP.Shared.Managers;

internal enum TreasurePodOpenAction
{
    CacheUntilModel,
    MarkOpenModelOnly
}

internal enum TreasurePodOpenRequestAction
{
    Reject,
    SendOpenState,
    QueueUntilLoaded,
    WaitForCurrentActivation,
    ActivateOnHost
}

/// <summary>
/// Pure transition rules for applying an authoritative pod-open state.
/// Remote state must never replay TreasurePod.Activate(), because that method
/// produces the pod's one-time rewards as well as changing its model.
/// </summary>
internal static class TreasurePodOpenPolicy
{
    internal static TreasurePodOpenAction DecideOpenAction(bool hasModel)
        => hasModel
            ? TreasurePodOpenAction.MarkOpenModelOnly
            : TreasurePodOpenAction.CacheUntilModel;

    internal static bool IsMatchingClientOpenRequest(uint pendingRequestId, uint responseRequestId)
        => pendingRequestId != 0 && responseRequestId != 0 && pendingRequestId == responseRequestId;

    internal static bool ShouldCommitAfterActivationException(bool modelIsOpen)
        => modelIsOpen;

    internal static bool VerifyRules()
        => DecideOpenAction(hasModel: false) == TreasurePodOpenAction.CacheUntilModel
           && DecideOpenAction(hasModel: true) == TreasurePodOpenAction.MarkOpenModelOnly
           && IsMatchingClientOpenRequest(7, 7)
           && !IsMatchingClientOpenRequest(7, 8)
           && !IsMatchingClientOpenRequest(0, 0)
           && ShouldCommitAfterActivationException(true)
           && !ShouldCommitAfterActivationException(false)
           && DecideHostRequest(true, true, true, false, false, false, true) ==
           TreasurePodOpenRequestAction.ActivateOnHost
           && DecideHostRequest(true, true, true, false, false, false, false) ==
           TreasurePodOpenRequestAction.QueueUntilLoaded
           && DecideHostRequest(true, true, true, true, false, false, true) ==
           TreasurePodOpenRequestAction.SendOpenState
           && DecideHostRequest(true, true, true, false, true, false, true) ==
           TreasurePodOpenRequestAction.WaitForCurrentActivation
           && DecideHostRequest(true, true, true, false, false, true, true) ==
           TreasurePodOpenRequestAction.Reject
           && DecideHostRequest(true, false, true, false, false, false, true) ==
           TreasurePodOpenRequestAction.Reject
           && DecideHostRequest(true, true, false, false, false, false, true) ==
           TreasurePodOpenRequestAction.Reject
           && DecideHostRequest(false, true, true, false, false, false, true) ==
           TreasurePodOpenRequestAction.Reject;

    internal static TreasurePodOpenRequestAction DecideHostRequest(
        bool isHost,
        bool authenticated,
        bool validPod,
        bool alreadyOpen,
        bool activationInProgress,
        bool outcomeUncertain,
        bool loaded)
    {
        if (!isHost || !authenticated || !validPod || outcomeUncertain)
            return TreasurePodOpenRequestAction.Reject;
        if (alreadyOpen)
            return TreasurePodOpenRequestAction.SendOpenState;
        if (activationInProgress)
            return TreasurePodOpenRequestAction.WaitForCurrentActivation;
        return loaded
            ? TreasurePodOpenRequestAction.ActivateOnHost
            : TreasurePodOpenRequestAction.QueueUntilLoaded;
    }
}

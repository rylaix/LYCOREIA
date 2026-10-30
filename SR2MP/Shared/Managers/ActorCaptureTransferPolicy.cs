namespace SR2MP.Shared.Managers;

internal enum CaptureRejectionReason : byte
{
    None,
    Identity,
    MissingActor,
    MissingOwner,
    OwnerChanged,
    SceneMismatch,
    UnreadableNativeState,
    NotVacuumable,
    NativeIneligible,
    AlreadyCaptive,
    OutOfRange,
    ReservationBusy,
    Expired,
    OperationMismatch
}

/// <summary>
/// Pure policy for the host's preflight actor-capture decision. This keeps the
/// authorization rules testable without loading Unity or IL2CPP objects.
/// </summary>
internal static class ActorCaptureTransferPolicy
{
    internal static bool MatchesCaptureOperation(bool packetConsumesActor, bool pendingConsumesActor)
        => packetConsumesActor == pendingConsumesActor;

    internal static bool CanMergePendingCaptureJoint(bool existingIsCapture, bool incomingIsCapture)
        => existingIsCapture && incomingIsCapture;

    internal static bool ShouldAbortCaptureOperationMismatch(bool isCommit, bool awaitingApproval)
        => !isCommit && awaitingApproval;

    internal static int GetCaptureDenialRetryDelayMilliseconds(int denialCount)
        => denialCount <= 0 ? 0 : denialCount == 1 ? 250 : denialCount == 2 ? 500 : 1000;

    internal static bool ShouldSuppressCaptureRetry(float now, float retryAfter, bool sameContext)
        => sameContext && float.IsFinite(now) && float.IsFinite(retryAfter) && now < retryAfter;

    internal static bool MatchesCaptureDenialContext(string? expectedOwnerId, string? currentOwnerId,
        int expectedSceneGroupId, int currentSceneGroupId, long expectedActivationId, long currentActivationId)
        => string.Equals(expectedOwnerId, currentOwnerId, StringComparison.Ordinal) &&
           expectedSceneGroupId == currentSceneGroupId && expectedActivationId != 0 &&
           expectedActivationId == currentActivationId;

    internal static bool CanPermitApprovedConsumeDestroy(bool guestConnected, bool matchingTarget,
        bool awaitingCommit, bool consumeOperation, bool expired, bool commitSent,
        bool replaying, bool waitingForNativeConsume)
        => guestConnected && matchingTarget && awaitingCommit && consumeOperation &&
           !expired && !commitSent && (replaying || waitingForNativeConsume);

    internal static bool HasConfirmedConsumeRemoval(bool objectPresent, bool componentPresent, bool modelPresent)
        => !objectPresent && !componentPresent && !modelPresent;

    internal static bool ShouldApplyOwnerState(bool isCaptureIntent, bool captureApproved, bool captureCommit)
        => !isCaptureIntent || (captureApproved && captureCommit);

    internal static string? ResolveActorOwner(bool hasNetworkActor, string? networkOwnerId,
        bool hasCachedOwner, string? cachedOwnerId)
        => !string.IsNullOrWhiteSpace(networkOwnerId)
            ? networkOwnerId
            : hasCachedOwner
                ? cachedOwnerId ?? string.Empty
                : hasNetworkActor ? string.Empty : null;

    internal static bool ShouldSendCaptureAbortOnExpiry(Guid requestId)
        => requestId != Guid.Empty;

    internal static bool CanPassCaptureWithoutActorId(
        bool serverRunning,
        bool hasNetworkActor,
        string? currentOwnerId,
        string? localPlayerId)
        => serverRunning || !hasNetworkActor ||
           string.Equals(currentOwnerId, localPlayerId, StringComparison.Ordinal);

    internal static bool MatchesPendingRequest(Guid pendingRequestId, Guid responseRequestId)
        => pendingRequestId != Guid.Empty && pendingRequestId == responseRequestId;

    /// <summary>
    /// A commit may match its approval exactly, or downgrade an approved consume to a plain ownership transfer
    /// (the native vacuum held the object instead of consuming it). Never the reverse: that would destroy an
    /// actor the host only reserved for a non-destructive capture.
    /// </summary>
    internal static bool IsCompatibleCommitKind(bool approvedConsumes, bool commitConsumes)
        => approvedConsumes == commitConsumes || (approvedConsumes && !commitConsumes);

    internal static bool IsSameCaptureRequest(
        Guid cachedRequestId,
        Guid incomingRequestId,
        long cachedActorId,
        long incomingActorId,
        string? cachedExpectedOwnerId,
        string? incomingExpectedOwnerId)
        => cachedRequestId != Guid.Empty && cachedRequestId == incomingRequestId &&
           cachedActorId == incomingActorId &&
           string.Equals(cachedExpectedOwnerId ?? string.Empty, incomingExpectedOwnerId ?? string.Empty,
               StringComparison.Ordinal);

    internal static bool CanReplayCachedApproval(
        bool cachedApproval,
        string? cachedOwnerId,
        string? currentOwnerId,
        string? authenticatedPlayerId)
        => cachedApproval &&
           !string.IsNullOrWhiteSpace(authenticatedPlayerId) &&
           string.Equals(cachedOwnerId, authenticatedPlayerId, StringComparison.Ordinal) &&
           string.Equals(currentOwnerId, authenticatedPlayerId, StringComparison.Ordinal);

    internal static bool CanExecutePreparedCapture(
        bool isApproved,
        bool matchingPendingRequest,
        bool awaitingApproval,
        bool pendingNotExpired,
        bool expectedOwnerMatches,
        bool nativeTargetAvailable)
        => isApproved && matchingPendingRequest && awaitingApproval && pendingNotExpired &&
           expectedOwnerMatches && nativeTargetAvailable;

    internal static bool CanCommitPreparedCapture(
        bool authenticatedEndpoint,
        string? authenticatedPlayerId,
        string? claimedOwnerId,
        bool hasExpectedOwner,
        string? expectedOwnerId,
        string? currentOwnerId,
        Guid requestId,
        bool reservationMatches,
        bool reservationNotExpired,
        bool targetStillEligible)
        => authenticatedEndpoint &&
           !string.IsNullOrWhiteSpace(authenticatedPlayerId) &&
           string.Equals(authenticatedPlayerId, claimedOwnerId, StringComparison.Ordinal) &&
           hasExpectedOwner &&
           string.Equals(expectedOwnerId ?? string.Empty, currentOwnerId ?? string.Empty,
               StringComparison.Ordinal) && targetStillEligible &&
           requestId != Guid.Empty && reservationMatches && reservationNotExpired;

    internal static bool HasConfirmedNativeCapture(bool isCaptive, bool jointConnectedToTarget)
        => isCaptive && jointConnectedToTarget;

    internal static bool ShouldRequestPredictedCapture(
        bool clientConnected,
        bool hostRunning,
        bool vacuumPressed,
        bool vacuumModeIsVac,
        bool targetHasRemoteOwner,
        bool targetCanCapture,
        bool targetIsCaptive,
        float targetDistance,
        float maximumVacuumDistance)
        => clientConnected && !hostRunning && vacuumPressed && vacuumModeIsVac &&
           targetHasRemoteOwner && targetCanCapture && !targetIsCaptive &&
           float.IsFinite(targetDistance) && float.IsFinite(maximumVacuumDistance) &&
           targetDistance >= 0f && maximumVacuumDistance > 0f && targetDistance <= maximumVacuumDistance;

    internal static bool ShouldRetainPredictedCapture(bool vacuumActive, bool actorValid,
        bool hasApprovedCapture)
        => vacuumActive && actorValid && hasApprovedCapture;

    internal static bool ShouldUseConsumePreflight(bool actorIsStoredInAmmo)
        => actorIsStoredInAmmo;

    internal static bool IsFreshNativeVacuumCandidate(float now, float observedAt, float maximumAge,
        long observedActivationId, long currentActivationId)
        => float.IsFinite(now) && float.IsFinite(observedAt) && float.IsFinite(maximumAge) &&
           maximumAge >= 0f && now >= observedAt && now - observedAt <= maximumAge &&
           observedActivationId != 0 && observedActivationId == currentActivationId;

    internal static bool CanCancelPendingCapture(bool localDestroyObserved, bool commitSent,
        bool nativeConsumeInProgress, bool destroyPermitArmed, float now, float permitUntil,
        bool destroyAttemptAccepted, float destroyAttemptUntil)
        => !localDestroyObserved && !commitSent && !nativeConsumeInProgress &&
           !(destroyPermitArmed && float.IsFinite(now) && float.IsFinite(permitUntil) && now <= permitUntil) &&
           !(destroyAttemptAccepted && float.IsFinite(now) && float.IsFinite(destroyAttemptUntil) &&
             now <= destroyAttemptUntil);

    /// <summary>
    /// A reversible cancel of an actor that is still fully present locally only
    /// needs the per-actor abort; the owner's samples correct its pose. A world
    /// resync would abort every other in-flight pull of a mass vacuum.
    /// </summary>
    internal static bool ShouldResyncAfterCancel(bool awaitingCommit, bool targetObjectPresent,
        bool targetComponentPresent, bool worldModelPresent)
        => awaitingCommit && !(targetObjectPresent && targetComponentPresent && worldModelPresent);

    /// <summary>
    /// A world resync must not abort a capture whose native consume may already
    /// be irreversible; it keeps its commit/ACK path instead.
    /// </summary>
    internal static bool ShouldPreserveCaptureAcrossResync(bool commitSent, bool localDestroyObserved,
        bool nativeConsumeInProgress, float now, bool destroyAttemptAccepted, float destroyAttemptUntil)
        => commitSent || localDestroyObserved || nativeConsumeInProgress ||
           (destroyAttemptAccepted && float.IsFinite(now) && float.IsFinite(destroyAttemptUntil) &&
            now <= destroyAttemptUntil);

    internal static bool CanAbortPreparedCapture(
        bool authenticatedPlayer,
        string? authenticatedPlayerId,
        string? reservationPlayerId,
        long requestedActorId,
        long reservedActorId,
        Guid requestId,
        Guid reservedRequestId,
        bool isPreparedApproval,
        bool alreadyCommitted)
        => authenticatedPlayer &&
           !string.IsNullOrWhiteSpace(authenticatedPlayerId) &&
           string.Equals(authenticatedPlayerId, reservationPlayerId, StringComparison.Ordinal) &&
           requestedActorId != 0 && requestedActorId == reservedActorId &&
           requestId != Guid.Empty && requestId == reservedRequestId &&
           isPreparedApproval && !alreadyCommitted;

    internal static bool CanSendCaptureDestroyAfterCommit(
        bool acceptedCommit,
        bool matchingPendingRequest,
        bool wasConsumeOperation,
        bool localDestroyObserved)
        => acceptedCommit && matchingPendingRequest && wasConsumeOperation && localDestroyObserved;

    internal static bool CanAdvanceCaptureBotToConsume(
        bool hostConfirmedOwnership,
        bool localActorAvailable,
        bool vanillaHeldStateMatches)
        => hostConfirmedOwnership && localActorAvailable && vanillaHeldStateMatches;

    internal static bool HasExpectedAmmoIncrement(int countBefore, int countAfter)
        => countBefore >= 0 && countBefore < int.MaxValue && countAfter == countBefore + 1;

    internal static bool IsPendingExpired(float now, float requestedAt, float timeout)
        => float.IsFinite(now) && float.IsFinite(requestedAt) && float.IsFinite(timeout) && timeout >= 0f &&
           now - requestedAt >= timeout;

    internal static bool CanRetryPendingCapture(
        float now,
        float requestedAt,
        float timeout,
        float nextRetryAt,
        int retryCount,
        int maximumRetries)
        => float.IsFinite(now) && float.IsFinite(requestedAt) && float.IsFinite(timeout) && timeout >= 0f &&
           float.IsFinite(nextRetryAt) && now - requestedAt < timeout && now >= nextRetryAt &&
           retryCount >= 0 && maximumRetries >= 0 && retryCount < maximumRetries;

    internal static bool CanApprove(
        bool authenticatedEndpoint,
        string? authenticatedPlayerId,
        string? claimedOwnerId,
        bool hasExpectedOwner,
        string? expectedOwnerId,
        string? currentOwnerId,
        Guid requestId,
        bool actorIsLive,
        bool targetHasVacuumable,
        bool targetCanCapture,
        bool targetIsCaptive,
        int playerSceneGroup,
        int actorSceneGroup,
        float playerToActorDistance,
        float maximumVacuumAcquisitionDistance)
    {
        return GetApprovalRejectionReason(
            authenticatedEndpoint, authenticatedPlayerId, claimedOwnerId, hasExpectedOwner,
            expectedOwnerId, currentOwnerId, requestId, actorExists: actorIsLive,
            nativeStateReadable: actorIsLive, targetHasVacuumable, targetCanCapture,
            targetIsCaptive, playerSceneGroup, actorSceneGroup, playerToActorDistance,
            maximumVacuumAcquisitionDistance) == CaptureRejectionReason.None;
    }

    internal static CaptureRejectionReason GetApprovalRejectionReason(
        bool authenticatedEndpoint,
        string? authenticatedPlayerId,
        string? claimedOwnerId,
        bool hasExpectedOwner,
        string? expectedOwnerId,
        string? currentOwnerId,
        Guid requestId,
        bool actorExists,
        bool nativeStateReadable,
        bool targetHasVacuumable,
        bool targetCanCapture,
        bool targetIsCaptive,
        int playerSceneGroup,
        int actorSceneGroup,
        float playerToActorDistance,
        float maximumVacuumAcquisitionDistance)
    {
        if (!authenticatedEndpoint || string.IsNullOrWhiteSpace(authenticatedPlayerId) ||
            !string.Equals(authenticatedPlayerId, claimedOwnerId, StringComparison.Ordinal))
            return CaptureRejectionReason.Identity;
        if (requestId == Guid.Empty)
            return CaptureRejectionReason.OperationMismatch;
        if (!hasExpectedOwner)
            return CaptureRejectionReason.MissingOwner;
        if (!string.Equals(expectedOwnerId ?? string.Empty, currentOwnerId ?? string.Empty,
                StringComparison.Ordinal))
            return CaptureRejectionReason.OwnerChanged;
        if (!actorExists)
            return CaptureRejectionReason.MissingActor;
        if (!nativeStateReadable)
            return CaptureRejectionReason.UnreadableNativeState;
        if (!targetHasVacuumable)
            return CaptureRejectionReason.NotVacuumable;
        if (!targetCanCapture)
            return CaptureRejectionReason.NativeIneligible;
        if (targetIsCaptive)
            return CaptureRejectionReason.AlreadyCaptive;
        if (playerSceneGroup < 0 || actorSceneGroup < 0 || playerSceneGroup != actorSceneGroup)
            return CaptureRejectionReason.SceneMismatch;
        if (!float.IsFinite(playerToActorDistance) || playerToActorDistance < 0f ||
            !float.IsFinite(maximumVacuumAcquisitionDistance) || maximumVacuumAcquisitionDistance <= 0f ||
            playerToActorDistance > maximumVacuumAcquisitionDistance)
            return CaptureRejectionReason.OutOfRange;

        return CaptureRejectionReason.None;
    }

    internal static float GetMaximumVacuumAcquisitionDistance(float nativeMaxVacDist)
    {
        if (!float.IsFinite(nativeMaxVacDist) || nativeMaxVacDist <= 0f)
            return float.NaN;

        // Player and actor state are sampled independently. Keep this bounded
        // network-sampling allowance separate from native terminal CaptureDist.
        var maximumDistance = nativeMaxVacDist + 3f;
        return float.IsFinite(maximumDistance) ? maximumDistance : float.NaN;
    }

#if SR2MP_TEST
    internal static bool VerifyRules()
    {
        if (!IsCompatibleCommitKind(true, true) || !IsCompatibleCommitKind(false, false) ||
            !IsCompatibleCommitKind(true, false) || IsCompatibleCommitKind(false, true))
            return false;
        var id = Guid.Parse("dce4652e-66ca-4c1e-84e5-e14706786f90");
        bool Allows(bool authenticated = true, string player = "PLAYER_GUEST", string claimant = "PLAYER_GUEST",
            bool hasExpectedOwner = true, string expectedOwner = "PLAYER_HOST", string currentOwner = "PLAYER_HOST",
            Guid? requestId = null, bool actorLive = true, bool hasVacuumable = true,
            bool canCapture = true, bool isCaptive = false, int playerScene = 4, int actorScene = 4,
            float distance = 8f, float range = 10f)
            => CanApprove(authenticated, player, claimant, hasExpectedOwner, expectedOwner, currentOwner,
                requestId ?? id, actorLive, hasVacuumable, canCapture, isCaptive,
                playerScene, actorScene, distance, range);

        return Allows()
               && MatchesCaptureOperation(packetConsumesActor: true, pendingConsumesActor: true)
               && MatchesCaptureOperation(packetConsumesActor: false, pendingConsumesActor: false)
               && !MatchesCaptureOperation(packetConsumesActor: true, pendingConsumesActor: false)
               && !MatchesCaptureOperation(packetConsumesActor: false, pendingConsumesActor: true)
               && CanMergePendingCaptureJoint(existingIsCapture: true, incomingIsCapture: true)
               && !CanMergePendingCaptureJoint(existingIsCapture: true, incomingIsCapture: false)
               && !CanMergePendingCaptureJoint(existingIsCapture: false, incomingIsCapture: true)
               && !CanMergePendingCaptureJoint(existingIsCapture: false, incomingIsCapture: false)
               && !Allows(authenticated: false)
               && !Allows(player: "PLAYER_OTHER")
               && !Allows(hasExpectedOwner: false)
               && !Allows(expectedOwner: "PLAYER_STALE")
               && !Allows(actorLive: false)
               && !Allows(hasVacuumable: false)
               && !Allows(canCapture: false)
               && !Allows(isCaptive: true)
               && !Allows(playerScene: 3)
               && !Allows(actorScene: 5)
            && Allows(distance: 10f, range: 10f)
            && !Allows(distance: 10.01f, range: 10f)
            && !Allows(distance: -0.01f)
               && !Allows(distance: float.NaN)
               && !Allows(range: 0f)
            && !Allows(range: float.NaN)
            && !Allows(range: float.PositiveInfinity)
            && float.IsNaN(GetMaximumVacuumAcquisitionDistance(0f))
            && float.IsNaN(GetMaximumVacuumAcquisitionDistance(float.NaN))
            && float.IsNaN(GetMaximumVacuumAcquisitionDistance(float.PositiveInfinity))
            && GetMaximumVacuumAcquisitionDistance(20f) == 23f
               && !Allows(requestId: Guid.Empty)
               && ShouldApplyOwnerState(isCaptureIntent: false, captureApproved: false, captureCommit: false)
               && ShouldApplyOwnerState(isCaptureIntent: false, captureApproved: false, captureCommit: true)
               && !ShouldApplyOwnerState(isCaptureIntent: true, captureApproved: true, captureCommit: false)
               && ShouldApplyOwnerState(isCaptureIntent: true, captureApproved: true, captureCommit: true)
               && !ShouldApplyOwnerState(isCaptureIntent: true, captureApproved: false, captureCommit: true)
               && CanExecutePreparedCapture(true, true, true, true, true, true)
               && !CanExecutePreparedCapture(true, true, true, false, true, true)
               && !CanExecutePreparedCapture(true, false, true, true, true, true)
               && !CanExecutePreparedCapture(true, true, false, true, true, true)
               && !CanExecutePreparedCapture(true, true, true, true, false, true)
               && !CanExecutePreparedCapture(true, true, true, true, true, false)
               && CanCommitPreparedCapture(true, "PLAYER_GUEST", "PLAYER_GUEST", true,
                   "PLAYER_HOST", "PLAYER_HOST", id, true, true, true)
               && !CanCommitPreparedCapture(true, "PLAYER_GUEST", "PLAYER_OTHER", true,
                   "PLAYER_HOST", "PLAYER_HOST", id, true, true, true)
               && !CanCommitPreparedCapture(true, "PLAYER_GUEST", "PLAYER_GUEST", true,
                   "PLAYER_HOST", "PLAYER_OTHER", id, true, true, true)
               && !CanCommitPreparedCapture(true, "PLAYER_GUEST", "PLAYER_GUEST", true,
                   "PLAYER_HOST", "PLAYER_HOST", id, false, true, true)
               && !CanCommitPreparedCapture(true, "PLAYER_GUEST", "PLAYER_GUEST", true,
                   "PLAYER_HOST", "PLAYER_HOST", id, true, false, true)
               && !CanCommitPreparedCapture(true, "PLAYER_GUEST", "PLAYER_GUEST", true,
                   "PLAYER_HOST", "PLAYER_HOST", id, true, true, false)
               && HasConfirmedNativeCapture(isCaptive: true, jointConnectedToTarget: true)
               && !HasConfirmedNativeCapture(isCaptive: false, jointConnectedToTarget: true)
               && !HasConfirmedNativeCapture(isCaptive: true, jointConnectedToTarget: false)
               && ShouldRequestPredictedCapture(true, false, true, true, true, true, false, 1.5f, 1.5f)
               && !ShouldRequestPredictedCapture(false, false, true, true, true, true, false, 1f, 1.5f)
               && !ShouldRequestPredictedCapture(true, true, true, true, true, true, false, 1f, 1.5f)
               && !ShouldRequestPredictedCapture(true, false, false, true, true, true, false, 1f, 1.5f)
               && !ShouldRequestPredictedCapture(true, false, true, false, true, true, false, 1f, 1.5f)
               && !ShouldRequestPredictedCapture(true, false, true, true, false, true, false, 1f, 1.5f)
               && !ShouldRequestPredictedCapture(true, false, true, true, true, false, false, 1f, 1.5f)
               && !ShouldRequestPredictedCapture(true, false, true, true, true, true, true, 1f, 1.5f)
               && !ShouldRequestPredictedCapture(true, false, true, true, true, true, false, 1.51f, 1.5f)
               && !ShouldRequestPredictedCapture(true, false, true, true, true, true, false, float.NaN, 1.5f)
               && CanAbortPreparedCapture(true, "PLAYER_GUEST", "PLAYER_GUEST", 12, 12,
                   id, id, true, false)
               && !CanAbortPreparedCapture(false, "PLAYER_GUEST", "PLAYER_GUEST", 12, 12,
                   id, id, true, false)
               && !CanAbortPreparedCapture(true, "PLAYER_OTHER", "PLAYER_GUEST", 12, 12,
                   id, id, true, false)
               && !CanAbortPreparedCapture(true, "PLAYER_GUEST", "PLAYER_GUEST", 12, 13,
                   id, id, true, false)
               && !CanAbortPreparedCapture(true, "PLAYER_GUEST", "PLAYER_GUEST", 12, 12,
                   id, Guid.Empty, true, false)
               && !CanAbortPreparedCapture(true, "PLAYER_GUEST", "PLAYER_GUEST", 12, 12,
                   id, id, false, false)
               && !CanAbortPreparedCapture(true, "PLAYER_GUEST", "PLAYER_GUEST", 12, 12,
                   id, id, true, true)
               && CanSendCaptureDestroyAfterCommit(true, true, true, true)
               && !CanSendCaptureDestroyAfterCommit(false, true, true, true)
               && !CanSendCaptureDestroyAfterCommit(true, false, true, true)
               && !CanSendCaptureDestroyAfterCommit(true, true, false, true)
               && !CanSendCaptureDestroyAfterCommit(true, true, true, false)
               && CanRetryPendingCapture(now: 5f, requestedAt: 0f, timeout: 8f,
                   nextRetryAt: 4f, retryCount: 1, maximumRetries: 3)
               && !CanRetryPendingCapture(now: 3.99f, requestedAt: 0f, timeout: 8f,
                   nextRetryAt: 4f, retryCount: 1, maximumRetries: 3)
               && !CanRetryPendingCapture(now: 5f, requestedAt: 0f, timeout: 8f,
                   nextRetryAt: 4f, retryCount: 3, maximumRetries: 3)
               && !CanRetryPendingCapture(now: 9f, requestedAt: 0f, timeout: 8f,
                   nextRetryAt: 4f, retryCount: 1, maximumRetries: 3)
               && !CanRetryPendingCapture(now: float.NaN, requestedAt: 0f, timeout: 8f,
                   nextRetryAt: 4f, retryCount: 1, maximumRetries: 3)
               && MatchesPendingRequest(id, id)
               && !MatchesPendingRequest(id, Guid.Empty)
               && !MatchesPendingRequest(id, Guid.NewGuid())
               && IsSameCaptureRequest(id, id, 12, 12, "PLAYER_HOST", "PLAYER_HOST")
               && !IsSameCaptureRequest(id, id, 12, 13, "PLAYER_HOST", "PLAYER_HOST")
               && !IsSameCaptureRequest(id, id, 12, 12, "PLAYER_HOST", "PLAYER_OTHER")
               && CanReplayCachedApproval(true, "PLAYER_GUEST", "PLAYER_GUEST", "PLAYER_GUEST")
               && !CanReplayCachedApproval(true, "PLAYER_GUEST", "PLAYER_OTHER", "PLAYER_GUEST")
               && !CanReplayCachedApproval(true, "PLAYER_GUEST", string.Empty, "PLAYER_GUEST")
               && !CanReplayCachedApproval(false, "PLAYER_GUEST", "PLAYER_GUEST", "PLAYER_GUEST")
               && IsPendingExpired(now: 18f, requestedAt: 10f, timeout: 8f)
               && !IsPendingExpired(now: 17.99f, requestedAt: 10f, timeout: 8f)
               && !IsPendingExpired(now: float.NaN, requestedAt: 10f, timeout: 8f)
               && ResolveActorOwner(true, "PLAYER_COMPONENT", true, "PLAYER_CACHE") == "PLAYER_COMPONENT"
               && ResolveActorOwner(true, string.Empty, true, "PLAYER_CACHE") == "PLAYER_CACHE"
               && ResolveActorOwner(false, null, true, "PLAYER_CACHE") == "PLAYER_CACHE"
               && ResolveActorOwner(true, string.Empty, false, null) == string.Empty
               && ResolveActorOwner(false, null, false, null) == null
               && ShouldSendCaptureAbortOnExpiry(id)
               && !ShouldSendCaptureAbortOnExpiry(Guid.Empty)
               && CanPassCaptureWithoutActorId(false, false, null, "PLAYER_GUEST")
               && CanPassCaptureWithoutActorId(true, true, "PLAYER_HOST", "PLAYER_GUEST")
               && CanPassCaptureWithoutActorId(false, true, "PLAYER_GUEST", "PLAYER_GUEST")
               && !CanPassCaptureWithoutActorId(false, true, "PLAYER_HOST", "PLAYER_GUEST")
               && !CanPassCaptureWithoutActorId(false, true, string.Empty, "PLAYER_GUEST")
               && CanAdvanceCaptureBotToConsume(true, true, true)
               && !CanAdvanceCaptureBotToConsume(true, true, false)
               && !CanAdvanceCaptureBotToConsume(true, false, true)
               && !CanAdvanceCaptureBotToConsume(false, true, true)
               && HasExpectedAmmoIncrement(0, 1)
               && HasExpectedAmmoIncrement(12, 13)
               && !HasExpectedAmmoIncrement(0, 0)
               && !HasExpectedAmmoIncrement(12, 14)
               && !HasExpectedAmmoIncrement(-1, 0)
               && !HasExpectedAmmoIncrement(int.MaxValue, int.MinValue);
    }
#endif
}

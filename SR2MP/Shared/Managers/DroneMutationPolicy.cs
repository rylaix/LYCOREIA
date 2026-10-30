namespace SR2MP.Shared.Managers;

/// <summary>
/// Pure authorization rules for the server-owned drone lease and its state updates.
/// </summary>
internal static class DroneMutationPolicy
{
    internal static bool CanAcceptClaim(
        string currentOwnerId,
        string claimantId,
        string expectedOwnerId,
        bool currentOwnerConnected,
        bool currentOwnerExplicitlyReleased)
    {
        currentOwnerId ??= string.Empty;
        claimantId ??= string.Empty;
        expectedOwnerId ??= string.Empty;

        if (string.IsNullOrWhiteSpace(claimantId))
            return false;

        // Re-announcing the current owner is idempotent and does not change authority.
        if (claimantId == currentOwnerId)
            return true;

        if (expectedOwnerId != currentOwnerId)
            return false;

        // Another connected player may take the drone only after an authenticated
        // release from the current owner. Disconnected owners cannot retain a lease.
        return string.IsNullOrEmpty(currentOwnerId) || !currentOwnerConnected ||
               currentOwnerExplicitlyReleased;
    }

    internal static bool CanAcceptRelease(string senderId, string currentOwnerId, string expectedOwnerId)
        => !string.IsNullOrWhiteSpace(senderId) &&
           string.Equals(senderId, currentOwnerId, StringComparison.Ordinal) &&
           string.Equals(currentOwnerId, expectedOwnerId, StringComparison.Ordinal);

    internal static bool ShouldHostTakeOverAfterRelease(
        bool serverRunning,
        bool releaseAccepted,
        bool hasDrone,
        bool droneHibernated)
        => serverRunning && releaseAccepted && hasDrone && !droneHibernated;

    internal static bool CanAcceptOwnedMutation(
        bool serverSide,
        bool endpointAuthenticated,
        string senderId,
        string currentOwnerId)
        => !serverSide ||
           (endpointAuthenticated && !string.IsNullOrWhiteSpace(senderId) &&
            string.Equals(senderId, currentOwnerId, StringComparison.Ordinal));

    internal static bool CanAcceptResourceMutation(
        bool serverSide,
        bool endpointAuthenticated,
        string senderId,
        long stationId,
        bool ownsExplorerStationInScene)
        => !serverSide ||
           (endpointAuthenticated && !string.IsNullOrWhiteSpace(senderId) &&
            stationId != 0 && ownsExplorerStationInScene);

    internal static bool CanInferUnknownCurrentOwner(
        string claimantId,
        string previousOwnerId,
        bool explicitlyReleased)
        => !string.IsNullOrWhiteSpace(claimantId) &&
           !string.IsNullOrWhiteSpace(previousOwnerId) &&
           (string.Equals(claimantId, previousOwnerId, StringComparison.Ordinal) || explicitlyReleased);

    internal static bool IsValidStationClaimTarget(bool modelExists, bool isDroneStation)
        => modelExists && isDroneStation;

    internal static bool ShouldDeferResourceIndex(int index, int resourceCount)
        => index >= 0 && resourceCount >= 0 && index >= resourceCount;

#if SR2MP_TEST
    internal static bool VerifyRules()
        => CanAcceptClaim("owner-a", "owner-a", "owner-a", true, false)
           && !CanAcceptClaim("owner-a", "owner-b", "owner-a", true, false)
           && CanAcceptClaim("owner-a", "owner-b", "owner-a", true, true)
           && !CanAcceptClaim("owner-b", "owner-a", "owner-a", true, false)
           && CanAcceptClaim("owner-a", "owner-b", "owner-a", false, false)
           && !CanAcceptClaim("owner-a", "owner-b", "owner-x", false, true)
           && CanAcceptClaim(string.Empty, "owner-b", string.Empty, false, false)
           && CanAcceptRelease("owner-a", "owner-a", "owner-a")
           && !CanAcceptRelease("owner-b", "owner-a", "owner-a")
           && ShouldHostTakeOverAfterRelease(true, true, true, false)
           && !ShouldHostTakeOverAfterRelease(false, true, true, false)
           && !ShouldHostTakeOverAfterRelease(true, false, true, false)
           && !ShouldHostTakeOverAfterRelease(true, true, false, false)
           && !ShouldHostTakeOverAfterRelease(true, true, true, true)
           && CanAcceptOwnedMutation(true, true, "owner-a", "owner-a")
           && !CanAcceptOwnedMutation(true, true, "owner-b", "owner-a")
           && !CanAcceptOwnedMutation(true, false, "owner-a", "owner-a")
           && CanAcceptOwnedMutation(false, false, string.Empty, string.Empty)
           && CanAcceptResourceMutation(true, true, "owner-a", 123, true)
           && !CanAcceptResourceMutation(true, true, "owner-a", 0, true)
           && !CanAcceptResourceMutation(true, true, "owner-a", 123, false)
           && !CanAcceptResourceMutation(true, false, "owner-a", 123, true)
           && CanAcceptResourceMutation(false, false, string.Empty, 0, false)
           && CanInferUnknownCurrentOwner("owner-a", "owner-a", false)
           && CanInferUnknownCurrentOwner("owner-b", "owner-a", true)
           && !CanInferUnknownCurrentOwner("owner-b", "owner-a", false)
           && !CanInferUnknownCurrentOwner("owner-b", "", true)
           && IsValidStationClaimTarget(true, true)
           && !IsValidStationClaimTarget(false, true)
           && !IsValidStationClaimTarget(true, false)
           && ShouldDeferResourceIndex(4, 4)
           && ShouldDeferResourceIndex(0, 0)
           && !ShouldDeferResourceIndex(3, 4)
           && !ShouldDeferResourceIndex(-1, 4);

#endif
}

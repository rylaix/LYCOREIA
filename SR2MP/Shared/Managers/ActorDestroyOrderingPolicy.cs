namespace SR2MP.Shared.Managers;

/// <summary>
/// Pure owner-token checks for destroy packets that can overtake an ownership
/// transfer because the transport orders each packet type independently.
/// </summary>
internal static class ActorDestroyOrderingPolicy
{
    internal static bool ShouldDefer(string? destroyOwnerId, string? currentOwnerId)
        => !string.IsNullOrWhiteSpace(destroyOwnerId) &&
           !string.Equals(destroyOwnerId, currentOwnerId ?? string.Empty, StringComparison.Ordinal);

    internal static bool CanResolve(string? destroyOwnerId, string? appliedOwnerId)
        => !string.IsNullOrWhiteSpace(destroyOwnerId) &&
           string.Equals(destroyOwnerId, appliedOwnerId, StringComparison.Ordinal);

#if SR2MP_TEST
    internal static bool VerifyRules()
        => !ShouldDefer("PLAYER_HOST", "PLAYER_HOST")
           && ShouldDefer("PLAYER_GUEST", "PLAYER_HOST")
           && !ShouldDefer(null, "PLAYER_HOST")
           && ShouldDefer("PLAYER_GUEST", null)
           && ShouldDefer("PLAYER_GUEST", string.Empty)
           && CanResolve("PLAYER_GUEST", "PLAYER_GUEST")
           && !CanResolve("PLAYER_GUEST", "PLAYER_HOST")
           && !CanResolve(null, "PLAYER_GUEST");
#endif
}

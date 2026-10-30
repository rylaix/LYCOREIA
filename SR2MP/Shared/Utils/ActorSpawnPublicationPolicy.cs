namespace SR2MP.Shared.Utils;

/// <summary>
/// Keeps actors created from a client's private save out of the host's shared world.
/// A client may publish new actors only after the host assigned its actor-ID block.
/// </summary>
internal static class ActorSpawnPublicationPolicy
{
    internal static bool CanClientPublish(long actorId, string? playerId, bool hasOwnershipSnapshot)
    {
        if (!hasOwnershipSnapshot || actorId <= 0 ||
            playerId is not { Length: 16 } ||
            !playerId.StartsWith("PLAYER_", StringComparison.Ordinal))
            return false;

        var rangeStart = (long)PlayerIdGenerator.GetPlayerIDNumber(playerId) * GlobalVariables.ActorIdOffset;
        var rangeEnd = rangeStart + GlobalVariables.ActorIdOffset;
        return actorId >= rangeStart && actorId < rangeEnd;
    }

    internal static bool IsHostActorId(long actorId)
        => actorId > 0 && actorId < GlobalVariables.ActorIdOffset;

    internal static bool VerifyRules()
    {
        const string playerId = "PLAYER_012345678";
        var start = (long)PlayerIdGenerator.GetPlayerIDNumber(playerId) * GlobalVariables.ActorIdOffset;
        var end = start + GlobalVariables.ActorIdOffset;

        return !CanClientPublish(start, playerId, hasOwnershipSnapshot: false)
               && CanClientPublish(start, playerId, hasOwnershipSnapshot: true)
               && CanClientPublish(end - 1, playerId, hasOwnershipSnapshot: true)
               && !CanClientPublish(start - 1, playerId, hasOwnershipSnapshot: true)
               && !CanClientPublish(end, playerId, hasOwnershipSnapshot: true)
               && !CanClientPublish(0, playerId, hasOwnershipSnapshot: true)
               && !CanClientPublish(start, "", hasOwnershipSnapshot: true)
               && IsHostActorId(1)
               && IsHostActorId(GlobalVariables.ActorIdOffset - 1)
               && !IsHostActorId(0)
               && !IsHostActorId(GlobalVariables.ActorIdOffset);
    }

    internal static void TraceSuppressed(string origin, long actorId)
    {
#if SR2MP_DEEPLOG || SR2MP_TEST
        ActorDiag.Log("spawn-suppressed", actorId,
            $"origin={origin}; snapshot={GlobalVariables.ActorManager.HasAuthoritativeOwnershipSnapshot}",
            ownerHint: GlobalVariables.LocalID, perActorMilliseconds: 1000);
#endif
#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") != "1")
            return;

        var role = Main.Server.IsRunning ? "host" : Main.Client.IsConnected ? "client" : "offline";
        var snapshot = GlobalVariables.ActorManager.HasAuthoritativeOwnershipSnapshot;
        SrLogger.LogWarning(
            $"[SR2MP_TEST] actor-spawn suppressed origin={origin} actor={actorId:X} " +
            $"role={role} snapshot={snapshot} owner={GlobalVariables.LocalID}");
#endif
    }
}

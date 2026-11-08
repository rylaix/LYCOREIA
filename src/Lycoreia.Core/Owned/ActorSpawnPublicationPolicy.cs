// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Owned;

/// <summary>
/// Keeps actors created from a client's private save out of the host's shared world.
/// A client may publish new actors only after the host assigned its actor-ID block.
/// </summary>
public static class ActorSpawnPublicationPolicy
{
    internal static bool CanClientPublish(long actorId, string playerId, bool hasOwnershipSnapshot)
    {
        if (!hasOwnershipSnapshot || actorId <= 0 ||
            playerId is not { Length: 16 } ||
            !playerId.StartsWith("PLAYER_", StringComparison.Ordinal))
            return false;

        var rangeStart = (long)Identity.ShortId(playerId) * Identity.ActorIdOffset;
        var rangeEnd = rangeStart + Identity.ActorIdOffset;
        return actorId >= rangeStart && actorId < rangeEnd;
    }

    internal static bool IsHostActorId(long actorId)
        => actorId > 0 && actorId < Identity.ActorIdOffset;

    internal static bool VerifyRules()
    {
        const string playerId = "PLAYER_012345678";
        var start = (long)Identity.ShortId(playerId) * Identity.ActorIdOffset;
        var end = start + Identity.ActorIdOffset;

        return !CanClientPublish(start, playerId, hasOwnershipSnapshot: false)
               && CanClientPublish(start, playerId, hasOwnershipSnapshot: true)
               && CanClientPublish(end - 1, playerId, hasOwnershipSnapshot: true)
               && !CanClientPublish(start - 1, playerId, hasOwnershipSnapshot: true)
               && !CanClientPublish(end, playerId, hasOwnershipSnapshot: true)
               && !CanClientPublish(0, playerId, hasOwnershipSnapshot: true)
               && !CanClientPublish(start, "", hasOwnershipSnapshot: true)
               && IsHostActorId(1)
               && IsHostActorId(Identity.ActorIdOffset - 1)
               && !IsHostActorId(0)
               && !IsHostActorId(Identity.ActorIdOffset);
    }

    internal static void TraceSuppressed(string origin, long actorId)
    {
#if DEEPLOG
        Log.Debug($"actor-spawn suppressed origin={origin} actor={actorId:X}");
#endif
    }
}

using System.Net;
using System.Runtime.CompilerServices;
using SR2MP.Packets.Actor;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Utils;

namespace SR2MP.Handlers.Internal;

internal abstract class BasePacketHandler<T> : IClientPacketHandler, IServerPacketHandler where T : IPacket, new()
{
    public bool IsServerSide { protected get; set; }

    /// <summary>
    /// Shared handlers receive gameplay requests on the host before the host relays
    /// them. Reject datagrams whose endpoint is not a registered connected client.
    /// On clients this handler is only reached through the connected server socket.
    /// </summary>
    protected bool RequireAuthenticatedClient(IPEndPoint? senderEndpoint)
    {
        if (!PacketMutationAuthorization.ShouldAccept(IsServerSide,
                senderEndpoint != null && Main.Server.ClientManager.TryGetClient(senderEndpoint, out var sender) &&
                sender != null && !string.IsNullOrWhiteSpace(sender.PlayerId),
                Main.Client.IsConnected))
        {
            SrLogger.LogWarning(
                $"Rejected {typeof(T).Name} from unauthenticated endpoint {senderEndpoint?.ToString() ?? "<null>"}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Binds a player-scoped request to the player identity registered for its endpoint.
    /// Endpoint authentication alone does not validate a packet's claimed PlayerId.
    /// </summary>
    protected bool RequireAuthenticatedPlayer(IPEndPoint? senderEndpoint, string? claimedPlayerId)
    {
        if (senderEndpoint != null &&
            Main.Server.ClientManager.TryGetClient(senderEndpoint, out var sender) &&
            sender != null &&
            PacketMutationAuthorization.IsPlayerIdentityMatch(sender.PlayerId, claimedPlayerId))
            return true;

        SrLogger.LogWarning(
            $"Rejected {typeof(T).Name} with claimed player '{claimedPlayerId ?? "<null>"}' " +
            $"from endpoint {senderEndpoint?.ToString() ?? "<null>"}: identity did not match the registered client.");
        return false;
    }

    // Do NOT override these! Only the ApiHandler class should be dealing with this!
    public virtual void Handle(PacketReader reader)
    {
        if (!IsServerSide)
            ProcessPacket(reader, null);
    }

    public virtual void Handle(PacketReader reader, IPEndPoint? clientEp)
    {
        if (IsServerSide)
            ProcessPacket(reader, clientEp);
    }

    private void ProcessPacket(PacketReader reader, IPEndPoint? clientEp)
    {
        var packet = reader.ReadPacket<T>();

        // Connect is the one pre-registration request. Every other packet received
        // by the server must come from a peer that completed that first step. This
        // applies the same gate to shared state handlers that do not need a custom
        // per-feature rule.
        if (IsServerSide)
        {
            if (packet.Type != PacketType.Connect && !RequireAuthenticatedClient(clientEp))
                return;
        }
        else if (!RequireAuthenticatedClient(clientEp))
        {
            return;
        }

        if (!IsServerSide && packet is ActorTransferPacket
            { CaptureIntent: true, CaptureCommit: true, CaptureConsumesActor: true } consumeResult)
        {
            // Completion ACK is protocol state even when its owner mutation is
            // stale relative to a snapshot/destroy. Resolve it before revision
            // buffering; the ordinary mutation path still enforces tombstones.
            SR2MP.Shared.Managers.NetworkActorCaptureManager.ResolveClientRequest(consumeResult);
        }

        var hasActorRevision = packet is IActorWorldRevisionPacket revisionedPacket &&
                                revisionedPacket.HasWorldRevision;
        var actorRevision = hasActorRevision && packet is IActorWorldRevisionPacket revisioned
            ? revisioned.WorldRevision
            : 0;
        var actorId = ActorSnapshotDeltaBuffer.GetActorId(packet);
        var transferOwnerId = packet is ActorTransferPacket transferPacket
            ? transferPacket.OwnerId
            : null;
        if (!IsServerSide && ActorSnapshotDeltaBuffer.TryDefer(packet.Type, actorId,
                hasActorRevision, actorRevision,
                () => ProcessAcceptedPacket(packet, clientEp), transferOwnerId))
            return;

        ProcessAcceptedPacket(packet, clientEp);
    }

    private bool ProcessAcceptedPacket(T packet, IPEndPoint? clientEp)
    {
        bool shouldSend;
        var wasHandlingPacket = HandlingPacket;

        try
        {
            shouldSend = Handle(packet, clientEp);
        }
        catch (Exception ex)
        {
            SrLogger.LogError($"Packet handler for {typeof(T).Name} threw: {ex}");
            return false;
        }
        finally
        {
            // Packet handlers can invoke game methods that trigger Harmony patches.
            // Restore the caller's state even when an older handler forgets to reset
            // the global guard or throws while applying a packet.
            HandlingPacket = wasHandlingPacket;
        }

        if (IsServerSide && shouldSend)
            PacketSender.SendToAllExcept(packet, clientEp);

        return shouldSend;
    }

    protected abstract bool Handle(T packet, IPEndPoint? clientEp);
}

/// <summary>
/// Pure role gate so the host/client acceptance rule can be self-tested without
/// loading any Unity models.
/// </summary>
internal static class PacketMutationAuthorization
{
    internal static bool ShouldAccept(bool isServerSide, bool authenticatedClientEndpoint, bool connectedToHost)
        => isServerSide ? authenticatedClientEndpoint : connectedToHost;

    internal static bool IsPlayerIdentityMatch(string? authenticatedPlayerId, string? claimedPlayerId)
        => !string.IsNullOrWhiteSpace(authenticatedPlayerId) &&
           string.Equals(authenticatedPlayerId, claimedPlayerId, StringComparison.Ordinal);

    internal static bool VerifyRules()
        => !ShouldAccept(isServerSide: true, authenticatedClientEndpoint: false, connectedToHost: true)
           && ShouldAccept(isServerSide: true, authenticatedClientEndpoint: true, connectedToHost: false)
           && !ShouldAccept(isServerSide: false, authenticatedClientEndpoint: false, connectedToHost: false)
           && ShouldAccept(isServerSide: false, authenticatedClientEndpoint: false, connectedToHost: true)
           && IsPlayerIdentityMatch("PLAYER_123", "PLAYER_123")
           && !IsPlayerIdentityMatch("PLAYER_123", "PLAYER_456")
           && !IsPlayerIdentityMatch("PLAYER_123", null)
           && !IsPlayerIdentityMatch("", "PLAYER_123");
}

internal static class PacketSender
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendPacket<T>(T packet) where T : IPacket
        => Main.Client.SendPacket(packet);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SendToAllExcept<T>(T packet, IPEndPoint? excludedEp) where T : IPacket
        => Main.Server.SendToAllExcept(packet, excludedEp);
}

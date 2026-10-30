using System.Net;
using JetBrains.Annotations;
using SR2MP.Api;
using SR2MP.Components.FX;
using SR2MP.Components.UI;
using SR2MP.Packets;
using SR2MP.Packets.Api;
using SR2MP.Packets.Actor;
using SR2MP.Packets.Player;
using SR2MP.Packets.Utils;
using SR2MP.Server.Managers;
using SR2MP.Server.Models;
using SR2MP.Shared.Managers;
using SR2MP.Shared.Utils;

namespace SR2MP.Server;

/// <summary>
/// Provides the host's interface.
/// </summary>
public sealed class SR2MPServer
{
    private readonly NetworkManager NetworkManager;
    internal readonly ClientManager ClientManager;
    internal readonly ReSyncManager ReSyncManager;

    private readonly ServerPacketManager packetManager;

    private Timer? timeoutTimer;
    private bool subscribedToQuitting;

    // Just here so that the port is viewable.

    /// <summary>
    /// Gets or sets a value that denotes the server's port.
    /// </summary>
    public int Port { get; private set; }

    /// <summary>
    /// Gets or sets a value that denotes the host player's player ID.
    /// </summary>
    public string PlayerId { get; private set; } = string.Empty;

    /// <summary>
    /// An event that is invoked when the server connects.
    /// </summary>
    public event Action? OnServerStarted;

    /// <summary>
    /// Gets the number of clients currently connected.
    /// </summary>
    public int GetClientCount => ClientManager.ClientCount;

    /// <summary>
    /// Gets a value indicating the running status of the server.
    /// </summary>
    public bool IsRunning => NetworkManager.IsRunning;

    internal SR2MPServer()
    {
        NetworkManager = new NetworkManager();
        ClientManager = new ClientManager();
        ReSyncManager = new ReSyncManager();
        packetManager = new ServerPacketManager(NetworkManager, ClientManager);

        NetworkManager.OnDataReceived += OnDataReceived;
        ClientManager.OnClientRemoved += OnClientRemoved;
        // A client that reconnects from the same address must start with fresh ordered streams on the host.
        ClientManager.OnClientAdded += client => NetworkManager.ResetReliabilityPeer(client.EndPoint);
        ClientManager.OnClientRemoved += client => NetworkManager.ResetReliabilityPeer(client.EndPoint);
    }

    internal void Start(int port, bool enableIPv6)
    {
        if (Main.Client.IsConnected)
        {
            SrLogger.LogWarning("You are already connected to a server, restart your game to host your own server");
            return;
        }

        if (NetworkManager.IsRunning)
        {
            SrLogger.LogMessage("Server is already running!");
            return;
        }

        NetworkUpgradeManager.ResetHostSession();

        try
        {
            PlayerId = DevMode ? "PLAYER_TEST_MODE" : PlayerIdGenerator.GenerateSessionPlayerId();
            Logger.SetDiagnosticHostId(PlayerId);

            packetManager.RegisterHandlers(Main.Core);
            
            if (!subscribedToQuitting)
            {
                subscribedToQuitting = true;
                Application.quitting += new Action(Close);
            }

            NetworkManager.Start(port, enableIPv6);
            Port = port;
            Logger.LogDiagnostic("session", "host-started", $"port={port}; ipv6={enableIPv6}; hostPeer={PlayerId}");
            timeoutTimer = new Timer(CheckClientTimeouts, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            InvokeServerStarted();
            MultiplayerUI.Instance.RegisterSystemMessage(
                "The world is now open to others!",
                $"SYSTEM_HOST_START_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                MultiplayerUI.SystemMessageConnect
            );
        }
        catch (Exception ex)
        {
            SrLogger.LogError($"Failed to start server: {ex}");
        }
    }
    
    private void InvokeServerStarted()
    {
        var subscribers = OnServerStarted;

        if (subscribers == null)
            return;

        foreach (var subscriber in subscribers.GetInvocationList())
        {
            try
            {
                ((Action)subscriber).Invoke();
            }
            catch (Exception ex)
            {
                var method = subscriber.Method;
                SrLogger.LogError($"OnServerStarted subscriber {method.DeclaringType?.FullName}.{method.Name} failed: {ex}");
            }
        }
    }

    private void CheckClientTimeouts(object? state)
    {
        if (!NetworkManager.IsRunning)
            return;

        var heartbeatAck = new EmptyPacket
        {
            Type = PacketType.HeartbeatAck,
            Reliability = PacketReliability.Unreliable,
            Channel = NetworkChannel.Important
        };

        foreach (var client in ClientManager.GetAllClients())
        {
            if (!client.IsTimedOut())
                SendToClient(heartbeatAck, client.EndPoint);
        }

        ClientManager.RemoveTimedOutClients();
    }

    private void OnDataReceived(byte[] data, int receivedBytes, IPEndPoint clientEp)
    {
        SrLogger.LogPacketSize($"Received {receivedBytes} bytes from Client!",
            $"Received {receivedBytes} bytes from {clientEp}.");

        ClientManager.UpdateHeartbeat(clientEp);

        try
        {
            packetManager.HandlePacket(data, receivedBytes, clientEp);
        }
        catch (Exception ex)
        {
            SrLogger.LogError($"Error handling packet from {clientEp}: {ex}");
        }
    }

    private void OnClientRemoved(ClientInfo client)
        => MainThreadDispatcher.Instance.Enqueue(() => HandleClientRemoved(client));

    private void HandleClientRemoved(ClientInfo client)
    {
        var playerId = client.PlayerId;

        var player = PlayerManager.GetPlayer(playerId);
        if (player != null)
        {
            RemoteVacuumSuctionFX.ForgetPendingPlayer(playerId);
            PlayerManager.RemovePlayer(playerId);

            if (PlayerObjects.TryGetValue(playerId, out var playerObj))
            {
                if (playerObj)
                    Object.Destroy(playerObj);

                PlayerObjects.Remove(playerId);
            }

            var leavePacket = new PlayerLeavePacket
            {
                Type = PacketType.BroadcastPlayerLeave,
                PlayerId = playerId
            };

            SendToAll(leavePacket);

            SrLogger.LogMessage($"Player {playerId} left the server");

            var leaveChatPacket = new ChatMessagePacket
            {
                Username = "SYSTEM",
                Message = $"{player.Username} left the world!",
                MessageID = $"SYSTEM_LEAVE_{playerId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                MessageType = MultiplayerUI.SystemMessageDisconnect
            };
            SendToAll(leaveChatPacket);

            MultiplayerUI.Instance.RegisterSystemMessage(
                $"{player.Username} left the world!",
                $"SYSTEM_LEAVE_HOST_{playerId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                MultiplayerUI.SystemMessageDisconnect
            );
        }

#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_LIFECYCLE") == "1")
            SrLogger.LogMessage($"[SR2MP_TEST] disconnected-player-cleanup player={playerId} playerModelPresent={player != null}");
#endif
        
        MainThreadDispatcher.Instance.Enqueue(() =>
        {
            ActorManager.AssignOwnershipOfUnowned();
            NetworkDroneManager.AssignOwnershipOfUnowned();
            NetworkGardenManager.AssignOwnershipOfUnowned();
        });
    }

    internal void Close()
    {
        if (!NetworkManager.IsRunning)
            return;

        Logger.LogDiagnostic("session", "host-stopping", $"hostPeer={PlayerId}; clients={GetClientCount}");

        var closeChatMessage = new ChatMessagePacket
        {
            Username = "SYSTEM",
            Message = "Server closed!",
            MessageID = $"SYSTEM_CLOSE_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            MessageType = MultiplayerUI.SystemMessageClose
        };
        SendToAll(closeChatMessage);

        MultiplayerUI.Instance.ClearChatMessages();
        MultiplayerUI.Instance.RegisterSystemMessage("You closed the server!", $"SYSTEM_CLOSE_SERVER_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}", MultiplayerUI.SystemMessageClose);

        try
        {
            timeoutTimer?.Dispose();
            timeoutTimer = null;

            var closePacket = new ClosePacket();

            try
            {
                SendToAll(closePacket);
            }
            catch (Exception ex)
            {
                SrLogger.LogWarning($"Failed to broadcast server close: {ex}");
            }

            foreach (var player in PlayerManager.GetAllPlayers())
            {
                var playerId = player.PlayerId;
                if (!PlayerObjects.TryGetValue(playerId, out var playerObject))
                    continue;

                if (playerObject != null)
                {
                    Object.Destroy(playerObject);
                    SrLogger.LogPacketSize($"Destroyed player object for {playerId}");
                }
                PlayerObjects.Remove(playerId);
            }

            PacketDeduplication.Clear();
            ClientManager.Clear();
            PlayerManager.Clear();
            NetworkManager.Stop();
            NetworkStringPool.Clear();
            ApiHandlers.ClearNetIds();

            SrLogger.LogMessage("Server closed");
        }
        catch (Exception ex)
        {
            SrLogger.LogError($"Error during server shutdown: {ex}");
        }
    }

    internal void SendToClient<T>(T packet, IPEndPoint endPoint) where T : IPacket
    {
        packet = ActorWorldRevision.Stamp(packet);
        PrepareAndSendToClient(packet, packet.Reliability, packet.Channel, endPoint,
            SerialiseInternalPacket<T>.Serialiser);
    }

    internal void SendCachedCaptureResult(ActorTransferPacket packet, IPEndPoint endPoint)
    {
        if (!packet.HasWorldRevision || !packet.CaptureIntent || !packet.CaptureCommit ||
            !packet.CaptureApproved)
            throw new InvalidOperationException("Only a stamped accepted capture result may be replayed.");
        // Replay the original mutation revision; a retry must not outrank a newer destroy tombstone.
#if SR2MP_TEST
        SrLogger.LogMessage($"[SR2MP_TEST] cached capture ACK replay actor={packet.ActorId.Value:X} revision={packet.WorldRevision} consume={packet.CaptureConsumesActor}");
#endif
        PrepareAndSendToClient(packet, packet.Reliability, packet.Channel, endPoint,
            SerialiseInternalPacket<ActorTransferPacket>.Serialiser);
    }

    /// <summary>
    /// Sends a custom packet to a specific client endpoint.
    /// </summary>
    /// <typeparam name="T">The type of the custom packet to send.</typeparam>
    /// <param name="data">The packet data to send.</param>
    /// <param name="endPoint">The endpoint of the client to receive the packet.</param>
    [PublicApi]
    public void SendDataToClient<T>(T data, IPEndPoint endPoint) where T : ICustomPacket
    {
        if (!ApiHandlers.CurrentNetIdMapping2.TryGetValue(data.GetType(), out var modId))
        {
            SrLogger.LogWarning($"Cannot send API packet: No ModId registered for custom packet type {data.GetType().FullName}.");
            return;
        }

        var apiHeader = new ApiPacket(data.Reliability, data.Channel, modId);
        PrepareAndSendToClient((apiHeader, data), apiHeader.Reliability, apiHeader.Channel, endPoint, SerialiseApiPacket<T>.Serialiser);
    }

    /// <summary>
    /// Sends a custom packet to a specific client.
    /// </summary>
    /// <typeparam name="T">The type of the custom packet to send.</typeparam>
    /// <param name="data">The packet data to send.</param>
    /// <param name="client">The client info of the recipient.</param>
    [PublicApi]
    public void SendDataToClient<T>(T data, ClientInfo client) where T : ICustomPacket
        => SendDataToClient(data, client.EndPoint);

    private void PrepareAndSendToClient<T>(T state, PacketReliability reliability, NetworkChannel channel,
        IPEndPoint endPoint, PacketWriterDelegate<T> writeAction)
    {
        using var writer = PacketWriter.Borrow();

        try
        {
            writeAction(writer, state);
            var data = writer.ToSpan();
            Logger.RecordPacket("out", data.Length > 0 ? data[0] : (byte)0, data.Length, 1);

            NetworkManager.Send(data, endPoint, reliability, channel);

            SrLogger.LogPacketSize($"Sent {data.Length} bytes to client at {endPoint}.");
        }
        catch (Exception ex)
        {
            SrLogger.LogError($"Failed to send packet to client {endPoint}: {ex}");
        }
    }

    internal void SendToAll<T>(T packet) where T : IPacket
    {
        packet = ActorWorldRevision.Stamp(packet);
        PrepareAndSendToAll(packet, packet.Reliability, packet.Channel, SerialiseInternalPacket<T>.Serialiser);
    }

    /// <summary>
    /// Broadcasts a custom packet to all connected clients.
    /// </summary>
    /// <typeparam name="T">The type of the custom packet to send.</typeparam>
    /// <param name="data">The packet data to send.</param>
    [PublicApi]
    public void SendDataToAll<T>(T data) where T : ICustomPacket
    {
        if (!ApiHandlers.CurrentNetIdMapping2.TryGetValue(data.GetType(), out var modId))
        {
            SrLogger.LogWarning($"Cannot send API packet: No ModId registered for custom packet type {data.GetType().FullName}.");
            return;
        }

        var apiHeader = new ApiPacket(data.Reliability, data.Channel, modId);
        PrepareAndSendToAll((apiHeader, data), apiHeader.Reliability, apiHeader.Channel, SerialiseApiPacket<T>.Serialiser);
    }

    private void PrepareAndSendToAll<T>(T state, PacketReliability reliability, NetworkChannel channel,
        PacketWriterDelegate<T> writeAction)
    {
        using var writer = PacketWriter.Borrow();

        try
        {
            writeAction(writer, state);
            var data = writer.ToSpan();
            Logger.RecordPacket("out", data.Length > 0 ? data[0] : (byte)0, data.Length, ClientManager.ClientCount > 0 ? (ushort)1 : (ushort)0);

            NetworkManager.Broadcast(data, ClientManager.GetAllClients(), reliability, channel);

            SrLogger.LogPacketSize($"Broadcasted {data.Length} bytes to all clients.");
        }
        catch (Exception ex)
        {
            SrLogger.LogError($"Failed to broadcast packet to all: {ex}");
        }
    }

    /// <summary>
    /// Broadcasts a custom packet to all clients except the specified one.
    /// </summary>
    /// <typeparam name="T">The type of the custom packet to send.</typeparam>
    /// <param name="data">The packet data to send.</param>
    /// <param name="excludedClientInfo">The client info string to exclude from the broadcast.</param>
    [PublicApi]
    public void SendDataToAllExcept<T>(T data, IPEndPoint? excludedClientInfo) where T : ICustomPacket
    {
        if (!ApiHandlers.CurrentNetIdMapping2.TryGetValue(data.GetType(), out var modId))
        {
            SrLogger.LogWarning($"Cannot send API packet: No ModId registered for custom packet type {data.GetType().FullName}.");
            return;
        }

        var apiHeader = new ApiPacket(data.Reliability, data.Channel, modId);
        PrepareAndSendToAllExcept((apiHeader, data), apiHeader.Reliability, apiHeader.Channel, SerialiseApiPacket<T>.Serialiser, excludedClientInfo);
    }

    internal void SendToAllExcept<T>(T packet, IPEndPoint? excludedClientInfo) where T : IPacket
    {
        packet = ActorWorldRevision.Stamp(packet);
        PrepareAndSendToAllExcept(packet, packet.Reliability, packet.Channel,
            SerialiseInternalPacket<T>.Serialiser, excludedClientInfo);
    }

    private void PrepareAndSendToAllExcept<T>(T state, PacketReliability reliability, NetworkChannel channel,
        PacketWriterDelegate<T> writeAction, IPEndPoint? excludedClientInfo)
    {
        using var writer = PacketWriter.Borrow();

        try
        {
            writeAction(writer, state);
            var data = writer.ToSpan();

            var sentCount = 0;

            foreach (var client in ClientManager.GetAllClients())
            {
                if (client.EndPoint.Equals(excludedClientInfo))
                    continue;

                NetworkManager.Send(data, client.EndPoint, reliability, channel);
                sentCount++;
            }

            SrLogger.LogPacketSize($"Broadcasted {data.Length} bytes to {sentCount} client(s).");
        }
        catch (Exception ex)
        {
            SrLogger.LogError($"Failed to broadcast packet: {ex}");
        }
    }

    // internal int GetPendingReliablePackets() => NetworkManager.GetPendingReliablePackets();
}

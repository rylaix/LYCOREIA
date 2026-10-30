using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Utils;
using SR2MP.Packets.World;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Drone;

[PacketHandler((byte)PacketType.DroneResourceUpdate)]
internal sealed class DroneResourceUpdateHandler : BasePacketHandler<DroneResourcePacket>
{
    protected override bool Handle(DroneResourcePacket packet, IPEndPoint? clientEndpoint)
    {
        if (Main.Server.IsRunning)
        {
            var authenticatedPlayerId = clientEndpoint != null &&
                                        Main.Server.ClientManager.TryGetClient(clientEndpoint, out var sender) &&
                                        sender != null
                ? sender.PlayerId
                : string.Empty;
            var endpointAuthenticated = !string.IsNullOrWhiteSpace(authenticatedPlayerId);

            if (!RequireAuthenticatedClient(clientEndpoint) ||
                !DroneMutationPolicy.CanAcceptResourceMutation(
                    serverSide: true,
                    endpointAuthenticated: endpointAuthenticated,
                    senderId: authenticatedPlayerId,
                    stationId: packet.StationId,
                    ownsExplorerStationInScene: NetworkDroneManager.IsOwnedExplorerStationInScene(
                        authenticatedPlayerId, packet.StationId, packet.Scene)))
            {
                SrLogger.LogWarning(
                    $"Rejected drone-resource update scene={packet.Scene} index={packet.Index} " +
                    $"sender='{authenticatedPlayerId}' endpoint={clientEndpoint?.ToString() ?? "<null>"}.");
                return false;
            }
        }

        return NetworkDroneManager.ReceiveResourceUpdate(packet);
    }
}

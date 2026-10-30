using System.Net;
using SR2MP.Components.Drone;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Drone;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Drone;

[PacketHandler((byte)PacketType.DroneUpdate)]
internal sealed class DroneUpdateHandler : BasePacketHandler<DroneUpdatePacket>
{
    protected override bool Handle(DroneUpdatePacket packet, IPEndPoint? clientEndpoint)
    {
        if (Main.Server.IsRunning)
        {
            if (!RequireAuthenticatedClient(clientEndpoint) || clientEndpoint == null ||
                !Main.Server.ClientManager.TryGetClient(clientEndpoint, out var sender) || sender == null)
            {
                SrLogger.LogWarning(
                    $"Rejected DroneUpdate for station {packet.StationId} from unauthenticated endpoint " +
                    $"{clientEndpoint?.ToString() ?? "<null>"}.");
                return false;
            }

            var currentOwnerId = NetworkDroneManager.TryGetCurrentOwner(packet.StationId, out var ownerId)
                ? ownerId
                : string.Empty;
            if (!DroneMutationPolicy.CanAcceptOwnedMutation(
                    serverSide: true,
                    endpointAuthenticated: true,
                    senderId: sender.PlayerId,
                    currentOwnerId: currentOwnerId))
            {
                SrLogger.LogWarning(
                    $"Rejected DroneUpdate for station {packet.StationId} from non-owner '{sender.PlayerId}' " +
                    $"(current owner '{currentOwnerId}').");
                return false;
            }
        }

        if (NetworkDrone.Drones.TryGetValue(packet.StationId, out var drone) && drone)
            drone.ApplyUpdate(packet);

        return true;
    }
}

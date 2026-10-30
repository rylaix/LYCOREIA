using System.Net;
using SR2MP.Components.Drone;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.World;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Drone;

[PacketHandler((byte)PacketType.DroneTask)]
internal sealed class DroneTaskHandler : BasePacketHandler<DroneProgramPacket>
{
    protected override bool Handle(DroneProgramPacket packet, IPEndPoint? clientEndpoint)
    {
        if (Main.Server.IsRunning)
        {
            if (!RequireAuthenticatedClient(clientEndpoint) || clientEndpoint == null ||
                !Main.Server.ClientManager.TryGetClient(clientEndpoint, out var sender) || sender == null)
            {
                SrLogger.LogWarning(
                    $"Rejected DroneTask for station {packet.ActorId} from unauthenticated endpoint " +
                    $"{clientEndpoint?.ToString() ?? "<null>"}.");
                return false;
            }

            var currentOwnerId = NetworkDroneManager.TryGetCurrentOwner(packet.ActorId, out var ownerId)
                ? ownerId
                : string.Empty;
            if (!DroneMutationPolicy.CanAcceptOwnedMutation(
                    serverSide: true,
                    endpointAuthenticated: true,
                    senderId: sender.PlayerId,
                    currentOwnerId: currentOwnerId))
            {
                SrLogger.LogWarning(
                    $"Rejected DroneTask for station {packet.ActorId} from non-owner '{sender.PlayerId}' " +
                    $"(current owner '{currentOwnerId}').");
                return false;
            }
        }

        return NetworkDroneManager.ReceiveDroneTask(packet);
    }
}

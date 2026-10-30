using System.Net;
using Il2CppMonomiPark.SlimeRancher.Drone;
using SR2MP.Components.Drone;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Drone;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Drone;

[PacketHandler((byte)PacketType.DroneAnimation)]
internal sealed class DroneAnimationHandler : BasePacketHandler<DroneAnimationPacket>
{
    protected override bool Handle(DroneAnimationPacket packet, IPEndPoint? clientEndpoint)
    {
        if (Main.Server.IsRunning)
        {
            if (!RequireAuthenticatedClient(clientEndpoint) || clientEndpoint == null ||
                !Main.Server.ClientManager.TryGetClient(clientEndpoint, out var sender) || sender == null)
                return false;

            var currentOwnerId = NetworkDroneManager.TryGetCurrentOwner(packet.StationId, out var ownerId)
                ? ownerId
                : string.Empty;
            if (packet.StationId == 0 || !DroneMutationPolicy.CanAcceptOwnedMutation(
                    serverSide: true,
                    endpointAuthenticated: true,
                    senderId: sender.PlayerId,
                    currentOwnerId: currentOwnerId))
            {
                SrLogger.LogWarning(
                    $"Rejected DroneAnimation for station {packet.StationId} from non-owner '{sender.PlayerId}' " +
                    $"(current owner '{currentOwnerId}').");
                return false;
            }
        }

        if (!NetworkDrone.Drones.TryGetValue(packet.StationId, out var drone) || !drone || drone.LocallyOwned)
            return true;

        var manager = drone.GetComponentInChildren<ExplorerDroneMovementManager>(true);
        if (!manager)
            return true;

        HandlingPacket = true;

        try
        {
            switch (packet.Animation)
            {
                case DroneAnimationPacket.DroneAnimation.Scan:
                    manager!.StartScan();
                    break;
                case DroneAnimationPacket.DroneAnimation.Gather:
                    manager!.StartGather();
                    break;
                case DroneAnimationPacket.DroneAnimation.Acquisition:
                    manager!.StartAcquisitionEvent();
                    break;
                case DroneAnimationPacket.DroneAnimation.StopAnimation:
                    manager!.StopCurrentAnimationState();
                    break;
            }
        }
        finally
        {
            HandlingPacket = false;
        }

        return true;
    }
}

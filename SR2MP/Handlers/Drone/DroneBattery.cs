using System.Net;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Components.Drone;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.World;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Drone;

[PacketHandler((byte)PacketType.DroneBattery)]
internal sealed class DroneBatteryHandler : BasePacketHandler<DroneBatteryPacket>
{
    protected override bool Handle(DroneBatteryPacket packet, IPEndPoint? clientEndpoint)
    {
        if (Main.Server.IsRunning)
        {
            if (!RequireAuthenticatedClient(clientEndpoint) || clientEndpoint == null ||
                !Main.Server.ClientManager.TryGetClient(clientEndpoint, out var sender) || sender == null)
            {
                SrLogger.LogWarning(
                    $"Rejected DroneBattery for station {packet.ActorId} from unauthenticated endpoint " +
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
                    $"Rejected DroneBattery for station {packet.ActorId} from non-owner '{sender.PlayerId}' " +
                    $"(current owner '{currentOwnerId}').");
                return false;
            }
        }

        var stationModel = GameState.GetIdentifiableModel(new ActorId(packet.ActorId))?.TryCast<DroneStationGadgetModel>();
        if (stationModel == null)
        {
            SrLogger.LogWarning($"DroneStationGadgetModel {packet.ActorId} not found!");
            return true;
        }

        HandlingPacket = true;
        stationModel.SetEnergy(SceneContext.Instance.TimeDirector, packet.EnergyDepletedPerHour, packet.Charge);
        HandlingPacket = false;

        return true;
    }
}

using System.Net;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Drone;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Drone;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Drone;

[PacketHandler((byte)PacketType.DroneCloudExtraction)]
internal sealed class DroneCloudExtractionHandler : BasePacketHandler<DroneCloudExtractionPacket>
{
    protected override bool Handle(DroneCloudExtractionPacket packet, IPEndPoint? clientEndpoint)
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
                    $"Rejected DroneCloudExtraction for station {packet.StationId} from non-owner '{sender.PlayerId}' " +
                    $"(current owner '{currentOwnerId}').");
                return false;
            }
        }

        var stationModel = GameState.GetIdentifiableModel(new ActorId(packet.StationId))?.TryCast<DroneStationGadgetModel>();
        if (stationModel == null)
        {
            SrLogger.LogWarning($"DroneStationGadgetModel {packet.StationId} not found!");
            return true;
        }

        var stationObj = stationModel.GetGameObject();
        var station = stationObj ? stationObj!.GetComponentInChildren<DroneStation>() : null;
        if (!station)
            return true;

        var type = packet.TypeId != -1 && ActorManager.ActorTypes.TryGetValue(packet.TypeId, out var identType)
            ? identType
            : null;

        HandlingPacket = true;
        station!.AssignCloudExtractionMode(packet.Extracting, type, packet.Count);
        HandlingPacket = false;

        return true;
    }
}

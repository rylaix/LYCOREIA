using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Loading;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Drone;

[PacketHandler((byte)PacketType.InitialDroneCloud, HandlerType.Client)]
internal sealed class InitialDroneCloudHandler : BasePacketHandler<InitialDroneCloudPacket>
{
    protected override bool Handle(InitialDroneCloudPacket packet, IPEndPoint? _)
    {
        var cloud = GameState.droneModel.GetCloudModel();
        if (cloud == null)
            return false;

        // DroneCloudModel.SetAmount rejects identifiers that are not already in
        // its supported-item table. Keep that table intact and replace amounts
        // for the supported types from the authoritative snapshot.
        List<IdentifiableType>? supportedTypes = null;
        if (cloud._amounts != null)
        {
            supportedTypes = new List<IdentifiableType>();
            foreach (var entry in cloud._amounts)
                if (entry.Key != null)
                    supportedTypes.Add(entry.Key);
        }
        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            if (supportedTypes == null)
                return false;

            foreach (var type in supportedTypes)
            {
                if (type == null)
                    continue;

                var typeId = NetworkActorManager.GetPersistentID(type);
                var amount = packet.Amounts.TryGetValue(typeId, out var authoritativeAmount)
                    ? authoritativeAmount
                    : 0;
                cloud.SetAmount(type, amount);
            }
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }
        return false;
    }
}

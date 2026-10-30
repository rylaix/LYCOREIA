using System.Net;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.World;
using SR2MP.Handlers;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Utils;
using SR2MP.Packets.World;

namespace SR2MP.Handlers.Access;

[PacketHandler((byte)PacketType.AccessDoor)]
internal sealed class AccessDoorHandler : BasePacketHandler<AccessDoorPacket>
{
    protected override bool Handle(AccessDoorPacket packet, IPEndPoint? clientEp)
    {
        if (!RequireAuthenticatedClient(clientEp))
            return false;

        if (!ProgressionPacketInputPolicy.HasIdentifier(packet.ID) ||
            !ProgressionPacketInputPolicy.IsDefinedEnum(packet.State))
            return false;

        if (!GameState.doors.TryGetValue(packet.ID, out var model))
        {
            model = new AccessDoorModel
            {
                gameObj = null,
                state = packet.State
            };

            GameState.doors.Add(packet.ID, model);
            return true;
        }

        model.state = packet.State;

        if (model.gameObj)
        {
            var wasHandlingPacket = HandlingPacket;
            HandlingPacket = true;
            try
            {
                var door = model.gameObj.GetComponent<AccessDoor>();
                if (door)
                    door.CurrState = packet.State;
            }
            finally
            {
                HandlingPacket = wasHandlingPacket;
            }
        }

        return true;
    }
}

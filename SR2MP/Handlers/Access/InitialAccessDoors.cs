using System.Net;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.World;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Loading;
using SR2MP.Packets.Utils;

namespace SR2MP.Handlers.Access;

[PacketHandler((byte)PacketType.InitialAccessDoors, HandlerType.Client)]
internal sealed class InitialAccessDoorsHandler : BasePacketHandler<InitialAccessDoorsPacket>
{
    protected override bool Handle(InitialAccessDoorsPacket packet, IPEndPoint? _)
    {
        var gameModel = GameState;
        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            foreach (var door in packet.Doors)
            {
                if (gameModel.doors.TryGetValue(door.ID, out var doorModel))
                {
                    doorModel.state = door.State;

                    if (doorModel.gameObj)
                    {
                        var doorComponent = doorModel.gameObj.GetComponent<AccessDoor>();
                        if (doorComponent)
                            doorComponent.CurrState = doorModel.state;
                    }
                }
                else
                {
                    doorModel = new AccessDoorModel
                    {
                        gameObj = null,
                        state = door.State
                    };

                    gameModel.doors.Add(door.ID, doorModel);
                }
            }
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        return false;
    }
}

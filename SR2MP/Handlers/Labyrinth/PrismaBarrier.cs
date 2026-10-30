using System.Net;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Labyrinth;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.World;
using SR2MP.Packets.Utils;

namespace SR2MP.Handlers.Labyrinth;

[PacketHandler((byte)PacketType.PrismaBarrier)]
internal sealed class PrismaBarrierHandler : BasePacketHandler<PrismaBarrierPacket>
{
    protected override bool Handle(PrismaBarrierPacket packet, IPEndPoint? clientEp)
    {
        if (!RequireAuthenticatedClient(clientEp))
            return false;

        if (string.IsNullOrWhiteSpace(packet.ID))
            return false;

        var barriers = GameState.AllPrismaBarriers();
        if (!barriers.TryGetValue(packet.ID, out var model))
        {
            // A live state can arrive before this region has registered its model.
            // Match the initial snapshot path so the state is retained for later loads.
            model = new PrismaBarrierModel(packet.ID)
            {
                _gameObj = null,
                ActivationTime = packet.ActivationTime
            };
            barriers.Add(packet.ID, model);
        }

        if (model != null)
        {
            var wasHandlingPacket = HandlingPacket;
            HandlingPacket = true;
            try
            {
                model.ActivationTime = packet.ActivationTime;
                model.NotifyParticipants();
                var gameObject = model._gameObj;
                if (gameObject)
                {
                    var barrier = gameObject!.GetComponent<PrismaBarrier>();
                    if (barrier)
                        barrier.SetActivationTime(packet.ActivationTime);
                }
            }
            finally
            {
                HandlingPacket = wasHandlingPacket;
            }
        }
        return true;
    }
}

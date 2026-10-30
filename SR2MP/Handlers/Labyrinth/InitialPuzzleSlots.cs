using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Loading;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Labyrinth;

[PacketHandler((byte)PacketType.InitialPuzzleSlots, HandlerType.Client)]
internal sealed class InitialPuzzleSlotsHandler : BasePacketHandler<InitialPuzzleSlotsPacket>
{
    protected override bool Handle(InitialPuzzleSlotsPacket packet, IPEndPoint? _)
    {
        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            foreach (var slot in packet.Slots)
                NetworkPuzzleSlotManager.ApplyState(slot.ID, slot.Filled);
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        return false;
    }
}

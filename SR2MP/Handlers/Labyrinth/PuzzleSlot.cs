using System.Net;
using SR2MP.Handlers;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.World;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Labyrinth;

[PacketHandler((byte)PacketType.PuzzleSlot)]
internal sealed class PuzzleSlotHandler : BasePacketHandler<PuzzleSlotPacket>
{
    protected override bool Handle(PuzzleSlotPacket packet, IPEndPoint? clientEp)
    {
        if (!RequireAuthenticatedClient(clientEp))
            return false;

        if (!ProgressionPacketInputPolicy.HasIdentifier(packet.ID))
            return false;

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            NetworkPuzzleSlotManager.ApplyState(packet.ID, packet.Filled);
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }
        
        return true;
    }
}

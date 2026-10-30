using System.Net;
using SR2MP.Handlers;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.World;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Labyrinth;

[PacketHandler((byte)PacketType.PlortDepositor)]
internal sealed class PlortDepositorHandler : BasePacketHandler<PlortDepositorPacket>
{
    protected override bool Handle(PlortDepositorPacket packet, IPEndPoint? clientEp)
    {
        if (!RequireAuthenticatedClient(clientEp))
            return false;

        if (!ProgressionPacketInputPolicy.HasIdentifier(packet.ID) ||
            !ProgressionPacketInputPolicy.IsNonNegativeDepositorAmount(packet.AmountDeposited))
            return false;

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            NetworkPlortDepositorManager.ApplyState(packet.ID, packet.AmountDeposited);
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        return true;
    }
}

using System.Collections;
using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Loading;
using SR2MP.Packets.Utils;

namespace SR2MP.Handlers.Refinery;

[PacketHandler((byte)PacketType.InitialRefinery, HandlerType.Client)]
internal sealed class InitialRefineryHandler : BasePacketHandler<InitialRefineryPacket>
{
    protected override bool Handle(InitialRefineryPacket packet, IPEndPoint? _)
    {
        SR2MP.Shared.Managers.NetworkRefineryManager.ReceiveSnapshot(packet);
        return false;
    }
}

using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Loading;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Gadget;

[PacketHandler((byte)PacketType.InitialTeleporterLinks, HandlerType.Client)]
internal sealed class InitialTeleporterLinksHandler : BasePacketHandler<InitialTeleporterLinksPacket>
{
    protected override bool Handle(InitialTeleporterLinksPacket packet, IPEndPoint? _)
    {
        StartCoroutine(NetworkGadgetManager.ApplyTeleporterLinkSnapshot(packet.Links));

        return false;
    }
}

using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Loading;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Sanctuary;

[PacketHandler((byte)PacketType.InitialSanctuaryState, HandlerType.Client)]
internal sealed class InitialSanctuaryStateHandler : BasePacketHandler<InitialSanctuaryStatePacket>
{
    protected override bool Handle(InitialSanctuaryStatePacket packet, IPEndPoint? clientEp)
    {
        NetworkSanctuaryManager.ApplySnapshot(packet);
        return false;
    }
}

using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Utils;
using SR2MP.Packets.World;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Sanctuary;

[PacketHandler((byte)PacketType.SanctuaryRoomState)]
internal sealed class SanctuaryRoomStateHandler : BasePacketHandler<SanctuaryRoomStatePacket>
{
    protected override bool Handle(SanctuaryRoomStatePacket packet, IPEndPoint? clientEp)
    {
        NetworkSanctuaryManager.ApplyRoomState(packet.State);
        return true;
    }
}

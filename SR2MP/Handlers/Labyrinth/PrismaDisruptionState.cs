using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Utils;
using SR2MP.Packets.World;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Labyrinth;

[PacketHandler((byte)PacketType.PrismaDisruptionState, HandlerType.Client)]
internal sealed class PrismaDisruptionStateHandler : BasePacketHandler<PrismaDisruptionStatePacket>
{
    protected override bool Handle(PrismaDisruptionStatePacket packet, IPEndPoint? _)
    {
        NetworkPrismaDisruptionManager.ApplySnapshot(packet);
        return false;
    }
}

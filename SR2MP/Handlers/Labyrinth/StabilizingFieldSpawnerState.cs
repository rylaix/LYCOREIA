using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Utils;
using SR2MP.Packets.World;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Labyrinth;

[PacketHandler((byte)PacketType.StabilizingFieldSpawnerState, HandlerType.Client)]
internal sealed class StabilizingFieldSpawnerStateHandler : BasePacketHandler<StabilizingFieldSpawnerStatePacket>
{
    protected override bool Handle(StabilizingFieldSpawnerStatePacket packet, IPEndPoint? _)
    {
        NetworkStabilizingFieldSpawnerManager.ApplySnapshot(packet);
        return false;
    }
}

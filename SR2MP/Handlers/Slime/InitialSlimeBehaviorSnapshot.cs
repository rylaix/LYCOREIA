using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Slime;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Slime;

[PacketHandler((byte)PacketType.InitialSlimeBehaviorSnapshot, HandlerType.Client)]
internal sealed class InitialSlimeBehaviorSnapshotHandler : BasePacketHandler<InitialSlimeBehaviorSnapshotPacket>
{
    protected override bool Handle(InitialSlimeBehaviorSnapshotPacket packet, IPEndPoint? clientEndpoint)
    {
        NetworkSlimeBehaviorManager.ReceiveSnapshot(packet);
        return false;
    }
}

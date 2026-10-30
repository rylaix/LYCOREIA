using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.World;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.ResourceNode;

[PacketHandler((byte)PacketType.ResourceNodePlacement, HandlerType.Client)]
internal sealed class ResourceNodePlacementHandler : BasePacketHandler<ResourceNodePlacementPacket>
{
    protected override bool Handle(ResourceNodePlacementPacket packet, IPEndPoint? _)
    {
        if (packet.Node == null || !ResourceNodeSyncPolicy.IsValidNodeId(packet.Node.ID))
            return false;

        ResourceNodeManager.ApplyPlacement(packet.Node);
        return true;
    }
}

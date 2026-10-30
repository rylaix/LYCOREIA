using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.World;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.ResourceNode;

[PacketHandler((byte)PacketType.ResourceNode)]
internal sealed class ResourceNodeHandler : BasePacketHandler<ResourceNodePacket>
{
    protected override bool Handle(ResourceNodePacket packet, IPEndPoint? _)
    {
        if (!ResourceNodeSyncPolicy.IsValidNodeId(packet.NodeId))
            return false;

        ResourceNodeManager.ApplyState(packet.NodeId, (Il2Cpp.ResourceNode.NodeState)packet.State);
        return true;
    }
}

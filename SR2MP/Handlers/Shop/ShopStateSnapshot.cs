using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Shop;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Shop;

[PacketHandler((byte)PacketType.ShopStateSnapshot, HandlerType.Client)]
internal sealed class ShopStateSnapshotHandler : BasePacketHandler<ShopStateSnapshotPacket>
{
    protected override bool Handle(ShopStateSnapshotPacket packet, IPEndPoint? _)
    {
        NetworkShopManager.ApplySnapshot(packet);
        return false;
    }
}

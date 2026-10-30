using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Shop;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Shop;

[PacketHandler((byte)PacketType.ShopPurchaseResult, HandlerType.Client)]
internal sealed class ShopPurchaseResultHandler : BasePacketHandler<ShopPurchaseResultPacket>
{
    protected override bool Handle(ShopPurchaseResultPacket packet, IPEndPoint? _)
    {
        NetworkShopManager.HandlePurchaseResult(packet);
        return false;
    }
}

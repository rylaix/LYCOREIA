using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Shop;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Shop;

[PacketHandler((byte)PacketType.ShopPurchaseRequest, HandlerType.Server)]
internal sealed class ShopPurchaseRequestHandler : BasePacketHandler<ShopPurchaseRequestPacket>
{
    protected override bool Handle(ShopPurchaseRequestPacket packet, IPEndPoint? clientEp)
        => NetworkShopManager.HandlePurchaseRequest(packet, clientEp);
}

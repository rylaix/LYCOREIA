using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Economy;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Currency;

[PacketHandler((byte)PacketType.PlortSold, HandlerType.Server)]
internal sealed class PlortSoldHandler : BasePacketHandler<PlortSoldPacket>
{
    protected override bool Handle(PlortSoldPacket packet, IPEndPoint? clientEp)
    {
        if (!RequireAuthenticatedClient(clientEp))
            return false;

        NetworkMarketManager.ApplyGuestSale(packet);
        return false; // host broadcasts canonical prices instead of relaying the sale
    }
}

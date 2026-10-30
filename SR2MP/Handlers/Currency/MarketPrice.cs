using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Economy;
using SR2MP.Packets.Utils;

namespace SR2MP.Handlers.Currency;

[PacketHandler((byte)PacketType.MarketPriceChange, HandlerType.Client)]
internal sealed class MarketPriceHandler : BasePacketHandler<MarketPricePacket>
{
    protected override bool Handle(MarketPricePacket packet, IPEndPoint? _)
    {
        var scene = SceneContext.Instance;
        var economy = scene ? scene.PlortEconomyDirector : null;
        var entries = economy?._currValueMap?._entries;
        if (entries == null || packet.Prices == null || packet.Prices.Length != entries.Length)
        {
            SrLogger.LogWarning(
                $"Ignoring market-price snapshot with {packet.Prices?.Length ?? -1} values; " +
                $"the local economy has {entries?.Length ?? -1} entries.");
            return false;
        }

        var i = 0;

        foreach (var price in entries)
        {
            if (price.value != null)
                (price.value.CurrValue, price.value.PrevValue) = packet.Prices[i];

            i++;
        }

        try
        {
            MarketUIInstance?.EconUpdate();
        } catch { /* ignored */ }
        return false;
    }
}

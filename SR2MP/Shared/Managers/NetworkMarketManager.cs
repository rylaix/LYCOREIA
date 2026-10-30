using SR2MP.Packets.Economy;

namespace SR2MP.Shared.Managers;

/// <summary>
/// Plort market saturation is host-authoritative. Guests apply a sale locally for instant
/// feedback and report it; the host applies it to the saved market and broadcasts the
/// resulting prices, coalesced to at most one snapshot per interval.
/// </summary>
internal static class NetworkMarketManager
{
    private const float BroadcastInterval = 0.5f;
    private static bool pricesDirty;
    private static float nextBroadcastAt;

    /// <summary>RegisterSold postfix for a local (non-network) sale.</summary>
    internal static void OnLocalSale(IdentifiableType type, int count, int price)
    {
        if (!Main.IsNetworkActive)
            return;
        if (Main.Server.IsRunning)
        {
            pricesDirty = true;
            return;
        }

        if (!Main.Client.IsConnected || count <= 0)
            return;

        int itemId;
        try
        {
            itemId = NetworkActorManager.GetPersistentID(type);
        }
        catch
        {
            return;
        }

        Main.Client.SendPacket(new PlortSoldPacket { ItemId = itemId, Count = count, Price = price });
        Logger.LogDiagnostic("market", "sale-sent", $"item={itemId}; count={count}; price={price}");
    }

    /// <summary>Host: apply a guest sale to the authoritative market.</summary>
    internal static void ApplyGuestSale(PlortSoldPacket packet)
    {
        if (!Main.Server.IsRunning || packet.Count <= 0 || packet.Count > 10_000)
            return;

        var scene = SceneContext.Instance;
        var economy = scene ? scene.PlortEconomyDirector : null;
        if (!economy || !ActorManager.ActorTypes.TryGetValue(packet.ItemId, out var type) || type == null)
            return;

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            economy!.RegisterSold(type, packet.Count, packet.Price);
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"Could not apply guest plort sale {type.name} x{packet.Count}: {ex.Message}");
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        pricesDirty = true;
        Logger.LogDiagnostic("market", "guest-sale-applied", $"item={type.name}; count={packet.Count}");
    }

    /// <summary>Main-thread pump: host broadcasts changed prices.</summary>
    internal static void Pump()
    {
        if (!pricesDirty || !Main.Server.IsRunning)
        {
            pricesDirty &= Main.Server.IsRunning;
            return;
        }

        var now = UnityEngine.Time.unscaledTime;
        if (now < nextBroadcastAt)
            return;

        var prices = MarketPricesArray;
        if (prices == null)
            return;

        pricesDirty = false;
        nextBroadcastAt = now + BroadcastInterval;
        Main.Server.SendToAll(new MarketPricePacket { Prices = prices });
    }
}

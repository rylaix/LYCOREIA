using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Shop;
using Il2CppMonomiPark.SlimeRancher.Shop.Runtime;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Shop;

[HarmonyPatch(typeof(ShopDirector), nameof(ShopDirector.TrackItemPurchased))]
internal static class ShopPurchasePatch
{
    private static void Postfix(ShopRuntimeItem item, int count)
    {
        if (HandlingPacket || item == null || count <= 0)
            return;

        if (Main.Server.IsRunning)
            NetworkShopManager.PublishSnapshot("host purchase");
        else if (Main.Client.IsConnected)
            NetworkShopManager.RequestPurchase(item, count);
    }
}

[HarmonyPatch(typeof(ShopRuntime), "HandleShopRestock")]
internal static class ShopRestockPatch
{
    private static bool Prefix()
    {
        // The host advances shop state and publishes the resulting absolute snapshot.
        // Clients wait for that snapshot instead of locally advancing a different seed.
        return Main.Server.IsRunning || !Main.Client.IsConnected;
    }

    private static void Postfix()
        => NetworkShopManager.QueueSnapshotAfterNextFrame("host restock");
}

[HarmonyPatch(typeof(ShopDirector), "Update")]
internal static class ShopSnapshotPumpPatch
{
    private static void Postfix()
        => NetworkShopManager.PumpPendingSnapshot();
}

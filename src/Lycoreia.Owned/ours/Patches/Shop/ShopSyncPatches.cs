// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Shop;
using Il2CppMonomiPark.SlimeRancher.Shop.Runtime;
using Lycoreia.Owned.Shared.Managers;

namespace Lycoreia.Owned.Patches.Shop;

[HarmonyPatch(typeof(ShopDirector), nameof(ShopDirector.TrackItemPurchased))]
internal static class ShopPurchasePatch
{
    private static void Postfix(ShopRuntimeItem item, int count)
    {
        if (HandlingPacket || item == null || count <= 0)
            return;

        if (ModFacade.Server.IsRunning)
            NetworkShopManager.QueueSnapshotAfterNextFrame("host purchase");
        else if (ModFacade.Client.IsConnected)
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
        return ModFacade.Server.IsRunning || !ModFacade.Client.IsConnected;
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

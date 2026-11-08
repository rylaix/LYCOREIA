// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Shop.Runtime;
using Lycoreia.Owned.Shared.Managers;

namespace Lycoreia.Owned.Patches.Shop;

[HarmonyPatch(typeof(ShopRuntimeTransactor), nameof(ShopRuntimeTransactor.Purchase))]
internal static class AuthoritativeShopPurchasePatch
{
    private static bool Prefix(
        ShopRuntimeItem item,
        int count,
        Il2CppSystem.Guid uiSessionId,
        ref bool __result)
    {
        _ = uiSessionId;
        if (!ModFacade.Client.IsConnected || ModFacade.Server.IsRunning || HandlingPacket)
            return true;

        // A guest submits intent only. Its local transactor must not charge currency,
        // consume components or record stock before the host decides the request.
        __result = NetworkShopManager.RequestPurchase(item, count);
        return false;
    }
}

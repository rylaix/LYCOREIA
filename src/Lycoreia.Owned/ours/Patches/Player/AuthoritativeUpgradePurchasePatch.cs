// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.UI.Fabricator;
using Lycoreia.Owned.Shared.Managers;

namespace Lycoreia.Owned.Patches.Player;

[HarmonyPatch(typeof(PlayerUpgradeFabricatableItem),
    "MonomiPark_SlimeRancher_UI_Fabricator_IFabricatableItem_FabricateAndSpendCost")]
internal static class AuthoritativeUpgradePurchasePatch
{
    private static bool Prefix(
        PlayerUpgradeFabricatableItem __instance,
        int count,
        ref FabricationBlockedReason __result)
    {
        if (!ModFacade.Client.IsConnected || ModFacade.Server.IsRunning || HandlingPacket)
            return true;

        // Guests submit intent before the game's native fabricator spends currency
        // or components. The host executes this native transaction after validation.
        __result = NetworkUpgradePurchaseManager.RequestClientPurchase(__instance, count)
            ? FabricationBlockedReason.NONE
            : FabricationBlockedReason.OPERATION_FAILED;
        return false;
    }
}

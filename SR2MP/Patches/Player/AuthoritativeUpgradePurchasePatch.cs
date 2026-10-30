using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.UI.Fabricator;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Player;

[HarmonyPatch(typeof(PlayerUpgradeFabricatableItem),
    "MonomiPark_SlimeRancher_UI_Fabricator_IFabricatableItem_FabricateAndSpendCost")]
internal static class AuthoritativeUpgradePurchasePatch
{
    private static bool Prefix(
        PlayerUpgradeFabricatableItem __instance,
        int count,
        ref FabricationBlockedReason __result)
    {
        if (!Main.Client.IsConnected || Main.Server.IsRunning || HandlingPacket)
            return true;

        // Guests submit intent before the game's native fabricator spends currency
        // or components. The host executes this native transaction after validation.
        __result = NetworkUpgradePurchaseManager.RequestClientPurchase(__instance, count)
            ? FabricationBlockedReason.NONE
            : FabricationBlockedReason.OPERATION_FAILED;
        return false;
    }
}

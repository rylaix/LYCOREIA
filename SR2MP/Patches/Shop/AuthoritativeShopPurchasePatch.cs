using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Shop.Runtime;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Shop;

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
        if (!Main.Client.IsConnected || Main.Server.IsRunning || HandlingPacket)
            return true;

        // A guest submits intent only. Its local transactor must not charge currency,
        // consume components or record stock before the host decides the request.
        __result = NetworkShopManager.RequestPurchase(item, count);
        return false;
    }
}

using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Economy;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Economy;

[HarmonyPatch(typeof(PlortEconomyDirector), nameof(PlortEconomyDirector.RegisterSold))]
internal static class PlortSoldPatch
{
    public static void Postfix(IdentifiableType id, int count, int price)
    {
        if (HandlingPacket || id == null)
            return;

        NetworkMarketManager.OnLocalSale(id, count, price);
    }
}

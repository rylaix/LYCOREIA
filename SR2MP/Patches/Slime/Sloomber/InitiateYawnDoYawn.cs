using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Slime.Slumber;

namespace SR2MP.Patches.Slime.Sloomber;

[HarmonyPatch(typeof(InitiateYawn), nameof(InitiateYawn.DoYawn))]
internal static class InitiateYawnDoYawn
{
    public static void Postfix(InitiateYawn __instance)
    {
        SloomberYawnSync.End(__instance);
    }
}

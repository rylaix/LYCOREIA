using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Slime.Tangle;
using SR2MP.Patches.Slime;

namespace SR2MP.Patches.Slime.Tangle;

[HarmonyPatch(typeof(TangleSpawnBud), nameof(TangleSpawnBud.Selected))]
internal static class TangleSpawnBudSelected
{
    public static bool Prefix(TangleSpawnBud __instance) => SlimeBehaviorAuthority.ShouldRun(__instance);
}

[HarmonyPatch(typeof(TangleSpawnBud), nameof(TangleSpawnBud.Action))]
internal static class TangleSpawnBudAction
{
    public static bool Prefix(TangleSpawnBud __instance) => SlimeBehaviorAuthority.ShouldRun(__instance);
}

[HarmonyPatch(typeof(TangleSpawnBud), nameof(TangleSpawnBud.Deselected))]
internal static class TangleSpawnBudDeselected
{
    public static bool Prefix(TangleSpawnBud __instance) => SlimeBehaviorAuthority.ShouldRun(__instance);
}

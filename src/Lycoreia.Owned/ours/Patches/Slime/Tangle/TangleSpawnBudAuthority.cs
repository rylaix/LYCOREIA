// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Slime.Tangle;
using Lycoreia.Owned.Patches.Slime;

namespace Lycoreia.Owned.Patches.Slime.Tangle;

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

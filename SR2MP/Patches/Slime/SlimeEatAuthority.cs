using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Slime;

namespace SR2MP.Patches.Slime;

/// <summary>
/// Slime eating is a world mutation: it can destroy food, create plorts, and
/// transform largos. Run the collision and chomp pipeline only on the slime
/// actor's owner so remote Tarr copies cannot independently consume the same
/// actor and repeatedly request a non-owner destroy.
/// </summary>
[HarmonyPatch]
internal static class SlimeEatAuthority
{
    [HarmonyTargetMethods]
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(SlimeEat), nameof(SlimeEat.ProcessCollisionEnter));
        yield return AccessTools.Method(typeof(SlimeEat), nameof(SlimeEat.MaybeChomp));
        yield return AccessTools.Method(typeof(SlimeEat), nameof(SlimeEat.FinishChomp));
        yield return AccessTools.Method(typeof(SlimeEat), nameof(SlimeEat.EatAndTransform));
        yield return AccessTools.Method(typeof(SlimeEat), nameof(SlimeEat.EatAndProduce));
    }

    private static bool Prefix(SlimeEat __instance)
        => SlimeBehaviorAuthority.ShouldRun(__instance);
}

[HarmonyPatch(typeof(TarrGrowthTentacleGrapple), nameof(TarrGrowthTentacleGrapple.Update))]
internal static class TarrGrowthTentacleAuthority
{
    public static bool Prefix(TarrGrowthTentacleGrapple __instance)
    {
        var runNative = SlimeBehaviorAuthority.ShouldRun(__instance);
        TarrGrappleTrace.Log(__instance, "update-before", 7, result: runNative);
        return runNative;
    }

    public static void Postfix(TarrGrowthTentacleGrapple __instance)
        => TarrGrappleTrace.Log(__instance, "update-after", 8);
}

using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Slime;
using SR2MP.Shared.Utils;

namespace SR2MP.Patches.Actor;

/// <summary>
/// Diagnostics only: the tester saw many radiant ("sparkle") slimes while the host was frozen, with
/// no radiant spawn in the logs. Records every slime that turns radiant and whether a packet did it.
/// </summary>
[HarmonyPatch(typeof(SlimeRadiant), nameof(SlimeRadiant.SetRadiant), new Type[0])]
internal static class RadiantDiagPatch
{
    public static void Postfix(SlimeRadiant __instance)
    {
#if SR2MP_DEEPLOG || SR2MP_TEST
        if (!Main.IsNetworkActive || !__instance)
            return;

        var actor = __instance.GetComponent<IdentifiableActor>();
        if (actor)
            ActorDiag.Log("radiant-set", actor.GetActorId().Value,
                $"fromPacket={HandlingPacket}", perActorMilliseconds: 10_000);
#endif
    }
}

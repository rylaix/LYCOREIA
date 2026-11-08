// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Slime;
using Lycoreia.Owned.Shared.Utils;

namespace Lycoreia.Owned.Patches.Actor;

/// <summary>
/// Diagnostics only: the tester saw many radiant ("sparkle") slimes while the host was frozen, with
/// no radiant spawn in the logs. Records every slime that turns radiant and whether a packet did it.
/// </summary>
[HarmonyPatch(typeof(SlimeRadiant), nameof(SlimeRadiant.SetRadiant), new Type[0])]
internal static class RadiantDiagPatch
{
    public static void Postfix(SlimeRadiant __instance)
    {
#if LYCOREIA_DEEPLOG || LYCOREIA_TEST
        if (!ModFacade.IsNetworkActive || !__instance)
            return;

        var actor = __instance.GetComponent<IdentifiableActor>();
        if (actor)
            ActorDiag.Log("radiant-set", actor.GetActorId().Value,
                $"fromPacket={HandlingPacket}", perActorMilliseconds: 10_000);
#endif
    }
}

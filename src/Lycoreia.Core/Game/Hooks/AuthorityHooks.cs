// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Lycoreia.Net;
using Il2CppMonomiPark.SlimeRancher.Slime;
using Il2CppMonomiPark.SlimeRancher.Slime.Dervish;
using UnityEngine;

namespace Lycoreia.Game.Hooks
{
    /// <summary>
    /// Behaviour side effects run only on the actor's owner. A replica created while a received message is being applied, and not yet
    /// in the actor table, must not run its first behaviour tick either. (The eat pipeline, Tarr grapple and Tangle bud are gated by the
    /// owned SlimeEatAuthority / TangleSpawnBudAuthority files; GroundVine, StalkConsumable and InitiateYawn by their owned files.)
    /// </summary>
    public static class BehaviourAuthority
    {
        public static bool ShouldRun(Component c)
        {
            try
            {
                if (Lycoreia.Owned.Shared.Managers.NetworkSlimeBehaviorManager.ApplyingState) return true;
                if (!Mod.IsNetworkActive || c == null) return true;
                var l = ActorLink.Of(c); if (l) return l.LocallyOwned;
                return !Applying.Active;
            }
            catch (Exception e) { HookGuard.Fail("behaviour-authority", e); return true; }
        }
    }

    [HarmonyPatch]
    static class OwnerOnlySideEffects
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(DervishSlimeSpin), nameof(DervishSlimeSpin.Action));
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(SlimeRadiant))) if (m.Name == nameof(SlimeRadiant.SetRadiant)) yield return m;
            yield return AccessTools.Method(typeof(ProducePlortsOnHit), nameof(ProducePlortsOnHit.ProducePlort));
            yield return AccessTools.Method(typeof(LuckySlimeProduceCoins), nameof(LuckySlimeProduceCoins.ProduceCoins));
            yield return AccessTools.Method(typeof(GiantEggBreakOnImpact), nameof(GiantEggBreakOnImpact.BreakOpen));
            yield return AccessTools.Method(typeof(SpawnGiantEgg), nameof(SpawnGiantEgg.CreateGiantEgg));
        }
        static bool Prefix(Component __instance) => BehaviourAuthority.ShouldRun(__instance);
    }
}

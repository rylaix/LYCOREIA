// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Lycoreia.Actors;
using UnityEngine;

namespace Lycoreia.Game.Hooks
{
    [HarmonyPatch]
    static class SlimeFoodAuthorityHook
    {
        static readonly HashSet<long> _logged = new HashSet<long>();

        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var m in typeof(SlimeEat).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (m.Name != nameof(SlimeEat.MaybeChomp) && m.Name != nameof(SlimeEat.MaybeSpinAndChomp) && m.Name != nameof(SlimeEat.FinishChomp) &&
                    m.Name != nameof(SlimeEat.EatAndProduce) && m.Name != nameof(SlimeEat.EatAndTransform) && m.Name != nameof(SlimeEat.EatImmediate)) continue;
                var p = m.GetParameters(); if (p.Length > 0 && p[0].ParameterType == typeof(GameObject)) yield return m;
            }
        }

        /// <summary>While above 0, a host slime is eating another seat's food: DestroyActorHook consumes it for everyone instead of suppressing.</summary>
        [ThreadStatic] internal static int HostConsumeDepth;

        static bool Destroys(MethodBase m) => m != null && (m.Name == nameof(SlimeEat.EatAndProduce) || m.Name == nameof(SlimeEat.EatAndTransform) || m.Name == nameof(SlimeEat.EatImmediate));

        /// <summary>__0 is the food in every target (obj / chomping / target).</summary>
        static bool Prefix(SlimeEat __instance, GameObject __0, MethodBase __originalMethod, out bool __state)
        {
            __state = false;
            try
            {
                if (!HookGuard.Active || __0 == null) return true;
                var food = ActorLink.Of(__0);
                // a slime as food (Tarr) is never consumed for another seat: ranch and expansion slimes are never deleted by the mod
                bool reserved = food && Mod.IsHost && (Mod.Actors.Captures.HasReservation(food.Id) || __0.GetComponentInParent<SlimeEat>() != null || GameActorWorld.IsRanchSafeSlime(__0));
                var verdict = FoodAuthority.Decide(true, Mod.IsHost, food, food && food.LocallyOwned, reserved);
                if (verdict == FoodAuthority.Verdict.Eat) return true;
                if (verdict == FoodAuthority.Verdict.EatHostConsumes) { if (Destroys(__originalMethod)) { HostConsumeDepth++; __state = true; } return true; }
                ActorRuntime.NoteActivity(__0);                                                         // ask the arbiter for the food instead
                if (_logged.Count < 4096 && _logged.Add(food.Id))
                    Mod.Diagnostics?.Record("actors", "eat-foreign-food-skipped", $"food={food.Id}; food-owner={food.CurrentOwnerId}; slime={ActorLink.IdOf(__instance.gameObject)}; step={__originalMethod?.Name}");
                return false;                                                                            // MaybeChomp/MaybeSpinAndChomp then return false (no chomp)
            }
            catch (Exception e) { HookGuard.Fail("SlimeEat.food", e); return true; }
        }

        static Exception Finalizer(Exception __exception, bool __state) { if (__state && HostConsumeDepth > 0) HostConsumeDepth--; return __exception; }
    }
}

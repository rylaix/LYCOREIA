// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using HarmonyLib;
using Il2Cpp;
using Lycoreia.Actors;
using System;

namespace Lycoreia.Game.Hooks
{
    static class AnimalAuthority
    {
        public static bool Authorized(Reproduce animal)
        {
            if (!Mod.IsNetworkActive) return true;
            var model = animal?._model;
            if (model != null && Mod.Actors != null && Mod.Actors.Table.TryGet(model.actorId.Value, out var entry))
                return entry.Owner == Mod.LocalId;
            return BehaviourAuthority.ShouldRun(animal);
        }
    }

    [HarmonyPatch(typeof(Reproduce), nameof(Reproduce.RegistryUpdate))]
    static class AnimalReproductionHook
    {
        static bool Prefix(Reproduce __instance)
        {
            try
            {
            if (!AnimalSimulationPolicy.MayReproduce(Mod.IsNetworkActive,
                AnimalAuthority.Authorized(__instance), __instance._regionMember?._hibernating == true, GameState.Loading)) return false;
            if (!Mod.IsNetworkActive) return true;
            var model = __instance._model; var td = __instance._timeDir;
            if (model == null || td == null || model.nextReproduceTime > td.WorldTime()) return true;
            var p = GameActorWorld.W(__instance.transform.position);
            bool onRanch = GameActorWorld.Ranch.Contains(p) == true;
            int population = GameActorWorld.CountWithin(e => ActorTypes.TryGetType(e.TypeId, out var t) && GameActorWorld.IsChicken(t), p, ChickenCap.Radius);
            if (AnimalSimulationPolicy.MayBreedAtPopulation(population, onRanch)) return true;
            model.nextReproduceTime = td.HoursFromNow(__instance.ReproducePeriod());
            return false;
            }
            catch (Exception e) { HookGuard.Fail("reproduction-authority", e); return false; }
        }

        static void Postfix(Reproduce __instance)
        {
            if (!Mod.IsNetworkActive || GameState.Loading || __instance._regionMember?._hibernating == true || !AnimalAuthority.Authorized(__instance)) return;
            try
            {
                var model = __instance._model; var td = __instance._timeDir;
                if (model == null || td == null || model.nextReproduceTime > td.WorldTime()) return;
                double now = td.WorldTime();
                model.nextReproduceTime = AnimalSimulationPolicy.NextDeadline(model.nextReproduceTime, now,
                    td.HoursFromNow(__instance.ReproducePeriod()) - now);
            }
            catch (Exception e) { HookGuard.Fail("reproduction-deadline", e); }
        }
    }

    [HarmonyPatch(typeof(Reproduce), nameof(Reproduce.CreateChick))]
    static class AnimalOffspringAuthorityHook
    {
        static bool Prefix(Reproduce __instance)
            => AnimalSimulationPolicy.MayReproduce(Mod.IsNetworkActive,
                AnimalAuthority.Authorized(__instance), __instance._regionMember?._hibernating == true, GameState.Loading);
    }
}

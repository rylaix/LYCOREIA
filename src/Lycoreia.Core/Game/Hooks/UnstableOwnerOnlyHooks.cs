// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Labyrinth;
using UnityEngine;

namespace Lycoreia.Game.Hooks
{
    static class UnstableOwnerOnly
    {
        static readonly HashSet<long> _logged = new HashSet<long>();

        /// <summary>True when this seat may run the native transform of the actor on go.</summary>
        internal static bool MayRun(GameObject go, string step)
        {
            if (!HookGuard.Active || go == null) return true;
            var l = ActorLink.Of(go);
            bool skip = Lycoreia.Owned.ActorDestroyPolicy.ShouldSkipTimedTransform(true, l, l && l.LocallyOwned);
            if (skip && _logged.Count < 4096 && _logged.Add(l.Id))
                Mod.Diagnostics?.Record("actors", "unstable-replica-skipped", $"id={l.Id}; owner={l.CurrentOwnerId}; step={step}");
            return !skip;
        }
    }

    [HarmonyPatch(typeof(DisruptableResource), nameof(DisruptableResource.Update))]
    static class DisruptableResourceOwnerOnlyHook
    {
        static bool Prefix(DisruptableResource __instance)
        {
            try { return UnstableOwnerOnly.MayRun(__instance != null ? __instance.gameObject : null, "become-unstable"); }
            catch (Exception e) { HookGuard.Fail("DisruptableResource.Update", e); return true; }
        }
    }

    [HarmonyPatch(typeof(UnstableResource), nameof(UnstableResource.Update))]
    static class UnstableResourceOwnerOnlyHook
    {
        static bool Prefix(UnstableResource __instance)
        {
            try { return UnstableOwnerOnly.MayRun(__instance != null ? __instance.gameObject : null, "burst"); }
            catch (Exception e) { HookGuard.Fail("UnstableResource.Update", e); return true; }
        }
    }

    [HarmonyPatch(typeof(PrismaStabilizerController), nameof(PrismaStabilizerController.AttemptToStabilizeObject))]
    static class StabilizeAttemptOwnerOnlyHook
    {
        static bool Prefix(GameObject obj, ref bool __result)
        {
            try { if (UnstableOwnerOnly.MayRun(obj, "stabilize-attempt")) return true; __result = false; return false; }
            catch (Exception e) { HookGuard.Fail("PrismaStabilizerController.AttemptToStabilizeObject", e); return true; }
        }
    }

    /// <summary>Ownership can move between the queueing trigger and the controller's Update.</summary>
    [HarmonyPatch(typeof(PrismaStabilizerController), nameof(PrismaStabilizerController.StabilizeObject))]
    static class StabilizeOwnerOnlyHook
    {
        static bool Prefix(GameObject unstableObj)
        {
            try { return UnstableOwnerOnly.MayRun(unstableObj, "stabilize"); }
            catch (Exception e) { HookGuard.Fail("PrismaStabilizerController.StabilizeObject", e); return true; }
        }
    }
}

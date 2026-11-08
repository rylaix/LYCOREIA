// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Player.PlayerItems;
using Lycoreia.Owned.Shared.Managers;

namespace Lycoreia.Owned.Patches.Actor;

[HarmonyPatch(typeof(VacuumItem), "UpdateTargetingInfo")]
internal static class TraceVacuumTargetingInfo
{
    private static float nextTraceAt;

    public static void Postfix(VacuumItem __instance)
    {
#if LYCOREIA_TEST
        if (Environment.GetEnvironmentVariable("LYCOREIA_TEST_TRACE_VAC_STATE") != "1" ||
            !__instance._vacPressed || UnityEngine.Time.unscaledTime < nextTraceAt)
            return;

        nextTraceAt = UnityEngine.Time.unscaledTime + 1f;
        var actor = LocalActorVacuumPredictionManager.ActiveActor;
        var origin = __instance.VacOrigin;
        var vacuumHit = DescribeRayHit(__instance, origin, "VAC_RAY_MASK", actor);
        var targetingHit = DescribeRayHit(__instance, origin, "TARGETING_RAY_MASK", actor);
        OwnedLog.LogMessage(
            $"[LYCOREIA_TEST] native-vacuum-targeting actor={(actor ? actor.ActorId.Value.ToString("X") : "<none>")} " +
            $"vacuumHit={vacuumHit} targetingHit={targetingHit}");
#endif
    }

#if LYCOREIA_TEST
    private static string DescribeRayHit(VacuumItem vacuumItem, GameObject? origin,
        string maskName, Lycoreia.Game.ActorLink actor)
    {
        if (!origin)
            return "<no-origin>";

        try
        {
            var maskProperty = AccessTools.Property(typeof(VacuumItem), maskName);
            var rawMask = maskProperty?.GetValue(null) ?? AccessTools.Field(typeof(VacuumItem), maskName)?.GetValue(null);
            if (rawMask == null)
                return "<no-mask>";

            var mask = Convert.ToInt32(rawMask);
            if (!Physics.Raycast(origin.transform.position, origin.transform.up.normalized, out var hit,
                    vacuumItem.MaxVacDist, mask, QueryTriggerInteraction.Ignore) || !hit.collider)
                return "<clear>";

            var transform = hit.collider.transform;
            var matchesActor = actor &&
                (transform == actor.transform || transform.IsChildOf(actor.transform));
            return $"{(matchesActor ? "<predicted-actor>" : hit.collider.name)}:" +
                   $"layer={hit.collider.gameObject.layer}:{LayerMask.LayerToName(hit.collider.gameObject.layer)} " +
                   $"distance={hit.distance:F2}";
        }
        catch (Exception exception)
        {
            return $"<{exception.GetType().Name}>";
        }
    }
#endif
}

[HarmonyPatch(typeof(VacuumItem), "TryGetVacuumableForCollider")]
internal static class TraceVacuumColliderCandidate
{
    private static float nextTraceAt;

    public static void Prefix(GameObject __0)
    {
#if LYCOREIA_TEST
        if (Environment.GetEnvironmentVariable("LYCOREIA_TEST_TRACE_VAC_STATE") != "1" ||
            !__0 || UnityEngine.Time.unscaledTime < nextTraceAt)
            return;

        var actor = LocalActorVacuumPredictionManager.ActiveActor;
        if (!actor || !actor.gameObject.activeInHierarchy)
            return;

        var candidate = __0.transform;
        if (candidate != actor.transform && !candidate.IsChildOf(actor.transform))
            return;

        nextTraceAt = UnityEngine.Time.unscaledTime + 0.25f;
        OwnedLog.LogMessage(
            $"[LYCOREIA_TEST] native-vacuum-candidate actor={actor.ActorId.Value:X} " +
            $"collider={__0.name} position={__0.transform.position}");
#endif
    }
}

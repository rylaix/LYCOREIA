using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Slime;
using SR2MP.Components.Actor;
using SR2MP.Shared.Managers;
using System;
using UnityEngine;

namespace SR2MP.Patches.Slime;

/// <summary>
/// Bounded observational hooks for the native Tarr grapple path. These hooks
/// never invoke native methods or change their arguments/results.
/// </summary>
[HarmonyPatch(typeof(TarrGrowthTentacleGrapple), nameof(TarrGrowthTentacleGrapple.FindNearestSlime))]
internal static class TraceTarrFindNearestSlime
{
    public static void Prefix(TarrGrowthTentacleGrapple __instance, float __0, float __1)
        => TarrGrappleTrace.Log(__instance, "find-nearest-before", 0, __0, __1);

    public static void Postfix(TarrGrowthTentacleGrapple __instance, GameObject __result)
        => TarrGrappleTrace.Log(__instance, "find-nearest-after", 1, value: __result);
}

[HarmonyPatch(typeof(TarrGrowthTentacleGrapple), nameof(TarrGrowthTentacleGrapple.GetGrappleTarget))]
internal static class TraceTarrGetGrappleTarget
{
    public static void Postfix(TarrGrowthTentacleGrapple __instance, GameObject __result)
        => TarrGrappleTrace.Log(__instance, "get-target", 2, value: __result);
}

[HarmonyPatch(typeof(TarrGrowthTentacleGrapple), nameof(TarrGrowthTentacleGrapple.MaybeGrapple))]
internal static class TraceTarrMaybeGrapple
{
    public static void Prefix(TarrGrowthTentacleGrapple __instance, GameObject __0)
        => TarrGrappleTrace.Log(__instance, "maybe-grapple-before", 3, value: __0);

    public static void Postfix(TarrGrowthTentacleGrapple __instance, GameObject __0, bool __result)
        => TarrGrappleTrace.Log(__instance, "maybe-grapple-after", 4, value: __0, result: __result);
}

[HarmonyPatch(typeof(TarrGrowthTentacleGrapple), nameof(TarrGrowthTentacleGrapple.ForceBreak))]
internal static class TraceTarrForceBreak
{
    public static void Prefix(TarrGrowthTentacleGrapple __instance)
        => TarrGrappleTrace.Log(__instance, "force-break-before", 5);

    public static void Postfix(TarrGrowthTentacleGrapple __instance)
        => TarrGrappleTrace.Log(__instance, "force-break-after", 6);
}

internal static class TarrGrappleTrace
{
    private static readonly float[] NextTraceAt = new float[9];

    internal static string Object(GameObject value)
    {
        if (!value)
            return "<none>";

        var actor = value.GetComponentInParent<NetworkActor>();
        return $"name={value.name},instance={value.GetInstanceID()},actor=" +
               (actor ? actor.ActorId.Value.ToString("X") : "<none>") + ",owner=" +
               (actor ? actor.CurrentOwnerId : "<none>");
    }

    internal static void Log(TarrGrowthTentacleGrapple grapple, string eventName, int eventIndex,
        float value0 = 0f, float value1 = 0f, GameObject value = null, bool result = false)
    {
        try
        {
            if (!Logger.DiagnosticsEnabled || !grapple || eventIndex < 0 || eventIndex >= NextTraceAt.Length)
                return;

            // Fixed-size per-event throttle: no actor/instance keyed state or Logger keys accumulate.
            var now = UnityEngine.Time.unscaledTime;
            if (now < NextTraceAt[eventIndex])
                return;
            NextTraceAt[eventIndex] = now + 1f;

            var root = grapple.GetComponentInParent<NetworkActor>();
            var target = grapple._target;
            var tentacle = grapple._activeTentacle;
            var details = eventIndex switch
            {
                0 => $"arg0={value0:F2}; arg1={value1:F2}",
                1 or 2 => $"result={Object(value)}",
                3 => $"candidate={Object(value)}",
                4 => $"candidate={Object(value)}; result={result}",
                7 => $"allowNativeUpdate={result}",
                8 => "postfix-observed",
                _ => string.Empty
            };
            Logger.LogDiagnostic("tarr-grapple", eventName,
                $"grappleInstance={grapple.GetInstanceID()}; " +
                $"rootActor={(root ? root.ActorId.Value.ToString("X") : "<none>")}; " +
                $"rootOwner={(root ? root.CurrentOwnerId : "<none>")}; " +
                $"rootLocal={(root && root.LocallyOwned)}; " +
                $"target={Object(target)}; " +
                $"tentacle={Object(tentacle)}; " +
                $"tentacleActive={(tentacle && tentacle.activeInHierarchy)}; {details}",
                "tarr-grapple");
        }
        catch (System.Exception exception)
        {
            Logger.LogDiagnostic("tarr-grapple", "trace-error",
                $"event={eventName}; reason={exception.GetType().Name}:{exception.Message}",
                "tarr-grapple:trace-error", 30_000);
        }
    }
}

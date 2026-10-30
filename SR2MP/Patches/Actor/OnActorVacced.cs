using HarmonyLib;
using UnityEngine;
using SR2MP.Components.Actor;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Actor;

[HarmonyPatch(typeof(Vacuumable), nameof(Vacuumable.Capture))]
internal static class OnActorVacced
{
    public static bool Prefix(Vacuumable __instance, Joint __0)
    {
#if SR2MP_TEST
        TraceNativePullingJointUpdate.TraceCapture(__instance, __0, "capture-before");
#endif
        return NetworkActorCaptureManager.BeforeCapture(__instance, __0);
    }

    public static void Postfix(Vacuumable __instance, Joint __0)
    {
        if (__instance)
        {
            var actorId = __instance._identifiable ? __instance._identifiable.GetActorId() : default;
            var networkActor = __instance.GetComponent<NetworkActor>();
            Logger.LogDiagnostic("vacuum", "native-capture-completed",
                $"actorId={actorId.Value:X}; actor={__instance.gameObject.name}; " +
                $"owner={networkActor?.CurrentOwnerId ?? "<none>"}; captive={__instance.IsCaptive()}; " +
                $"jointConnected={__0 && __instance._body && __0.connectedBody == __instance._body}; " +
                $"position={__instance.transform.position}");
        }
#if SR2MP_TEST
        TraceNativePullingJointUpdate.TraceCapture(__instance, __0, "capture-after");
#endif
    }
}

using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Player;
using Il2CppMonomiPark.SlimeRancher.Player.PlayerItems;
using SR2MP.Components.Actor;
using SR2MP.Shared.Managers;
using SR2MP.Shared.Utils;
using UnityEngine;

namespace SR2MP.Patches.Actor;

[HarmonyPatch(typeof(VacuumItem), "VacTriggered")]
internal static class TraceVacTriggered
{
    public static void Postfix(VacuumItem __instance)
    {
        Logger.LogDiagnostic("vacuum", "triggered",
            $"mode={__instance._vacMode}; pressed={__instance._vacPressed}; active={__instance.isActiveAndEnabled}; " +
            $"maxVacDist={__instance.MaxVacDist:F2}; captureDist={__instance.CaptureDist:F2}");
#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_VAC_STATE") == "1")
            SrLogger.LogMessage(
                $"[SR2MP_TEST] native-vac-triggered mode={__instance._vacMode} " +
                $"pressed={__instance._vacPressed} active={__instance.isActiveAndEnabled}");
#endif
    }
}

[HarmonyPatch(typeof(VacuumItem), "VacCanceled")]
internal static class TraceVacCanceled
{
    public static void Postfix(VacuumItem __instance)
    {
        Logger.LogDiagnostic("vacuum", "cancelled",
            $"mode={__instance._vacMode}; pressed={__instance._vacPressed}; active={__instance.isActiveAndEnabled}");
#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_VAC_STATE") == "1")
            SrLogger.LogMessage(
                $"[SR2MP_TEST] native-vac-canceled mode={__instance._vacMode} " +
                $"pressed={__instance._vacPressed}");
#endif
    }
}

[HarmonyPatch(typeof(VacuumItem), "UpdateHeld")]
internal static class TraceVanillaVacuumUpdateHeld
{
    private static float nextTraceAt;

    public static void Prefix(VacuumItem __instance,
        Il2CppSystem.Collections.Generic.HashSet<GameObject> __0)
    {
#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_VAC_STATE") != "1" ||
            UnityEngine.Time.unscaledTime < nextTraceAt)
            return;

        nextTraceAt = UnityEngine.Time.unscaledTime + 1f;
        var target = LocalActorVacuumPredictionManager.ActiveActor;
        var targetInVac = __0 != null && target && __0.Contains(target.gameObject);
        SrLogger.LogMessage(
            $"[SR2MP_TEST] native-vacuum-update-held count={(__0 != null ? __0.Count : -1)} " +
            $"targetInVac={targetInVac} actor={(target ? target.ActorId.Value.ToString("X") : "<none>")} " +
            $"position={(target ? target.transform.position.ToString() : "<none>")}");
#endif
    }
}

[HarmonyPatch(typeof(VacuumItem), "ForceJoint", typeof(Vacuumable))]
internal static class TraceVanillaVacuumForceJoint
{
    public static void Prefix(VacuumItem __instance, Vacuumable __0)
    {
        if (__0)
        {
            var networkActor = __0.GetComponent<NetworkActor>();
            Logger.LogDiagnostic("vacuum", "force-joint-enter",
                $"actorId={(networkActor ? networkActor.ActorId.Value.ToString("X") : "<none>")}; " +
                $"owner={(networkActor ? networkActor.CurrentOwnerId : "<none>")}; " +
                $"pressed={__instance._vacPressed}; mode={__instance._vacMode}; " +
                $"actorPos={__0.transform.position.ToString("F2")}; " +
                $"captive={__0.IsCaptive()}; body={(bool)__0._body}");
        }
#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_VAC_STATE") != "1" || !__0)
            return;

        var actor = __0.GetComponent<NetworkActor>();
        var body = __0._body;
        SrLogger.LogMessage(
            $"[SR2MP_TEST] native-vacuum-force-joint actor={(actor ? actor.ActorId.Value.ToString("X") : "<none>")} " +
            $"owner={actor?.CurrentOwnerId ?? "<none>"} pressed={__instance._vacPressed} " +
            $"bodyConstraints={(body ? body.constraints.ToString() : "<none>")} " +
            $"position={__0.transform.position}");
#endif
    }
}

#if SR2MP_DEEPLOG
[HarmonyPatch(typeof(VacuumItem), "ForceJoint", typeof(Vacuumable))]
internal static class DeepLogVacuumForceJointPostfix
{
    public static void Postfix(VacuumItem __instance, Vacuumable __0)
        => DeepLogVacuumTracker.OnForceJoint(__instance, __0);
}
#endif

[HarmonyPatch(typeof(VacuumItem), "ConsumeExistingJointed")]
internal static class TraceNativePullingJointUpdate
{
    private static float nextTraceAt;
    private static int sampledFrame = -1;

    public static void Prefix(VacuumItem __instance)
        => Trace(__instance, "consume-existing-jointed-before");

    public static void Postfix(VacuumItem __instance)
        => Trace(__instance, "consume-existing-jointed-after");

    private static void Trace(VacuumItem vacuumItem, string phase)
    {
#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_VAC_JOINTS") != "1")
            return;

        if (phase == "consume-existing-jointed-before")
        {
            if (UnityEngine.Time.unscaledTime < nextTraceAt)
                return;
            nextTraceAt = UnityEngine.Time.unscaledTime + .15f;
            sampledFrame = UnityEngine.Time.frameCount;
        }
        else if (sampledFrame != UnityEngine.Time.frameCount)
            return;
        try
        {
            var actor = LocalActorVacuumPredictionManager.ActiveActor;
            var target = actor ? actor.GetComponent<Vacuumable>() : null;
            var joint = target ? target._captiveToJoint : null;
            var body = target ? target._body : null;
            var origin = vacuumItem ? vacuumItem.VacOrigin : null;
            var member = false;
            var count = -1;
            var list = vacuumItem ? vacuumItem._joints : null;
            if (list != null)
            {
                count = list.Count;
                for (var i = 0; i < count; i++)
                {
                    var listed = list[i];
                    if (listed && joint && listed.Pointer == joint.Pointer)
                        member = true;
                }
            }

            SrLogger.LogMessage(
                $"[SR2MP_TEST] vacuum-joint-update phase={phase} frame={UnityEngine.Time.frameCount} " +
                $"time={UnityEngine.Time.unscaledTime:F3} actor={(actor ? actor.ActorId.Value.ToString("X") : "<none>")} " +
                $"lock={JointId(vacuumItem ? vacuumItem.LockJoint : null)} captive={JointId(joint)} " +
                $"connectedBody={(joint && joint.connectedBody ? joint.connectedBody.GetInstanceID() : 0)} " +
                $"listCount={count} member={member} parent={TransformId(joint ? joint.transform.parent : null)} " +
                $"vacOrigin={TransformId(origin ? origin.transform : null)} " +
                $"localPosition={(joint ? joint.transform.localPosition.ToString("F3") : "<none>")} " +
                $"nozzleDistance={(origin && body ? Vector3.Distance(origin.transform.position, body.position) : float.NaN):F3} " +
                $"constraints={(body ? body.constraints.ToString() : "<none>")} " +
                $"kinematic={(body ? body.isKinematic.ToString() : "<none>")} " +
                $"velocity={(body ? body.velocity.ToString("F3") : "<none>")}");
        }
        catch (Exception exception)
        {
            SrLogger.LogWarning($"[SR2MP_TEST] vacuum-joint-update trace failed phase={phase}: {exception.GetType().Name}");
        }
#endif
    }

    internal static void TraceCapture(Vacuumable target, Joint argument, string phase)
    {
#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_VAC_JOINTS") != "1")
            return;
        try
        {
            var actor = target ? target.GetComponent<NetworkActor>() : null;
            var player = SceneContext.Instance ? SceneContext.Instance.player : null;
            var controller = player ? player.GetComponent<PlayerItemController>() : null;
            var vacuumItem = controller ? controller._vacuumItem : null;
            var lockJoint = vacuumItem ? vacuumItem.LockJoint : null;
            var captive = target ? target._captiveToJoint : null;
            var origin = vacuumItem ? vacuumItem.VacOrigin : null;
            var body = target ? target._body : null;
            var list = vacuumItem ? vacuumItem._joints : null;
            var count = list != null ? list.Count : -1;
            var argumentListed = Contains(list, argument);
            var captiveListed = Contains(list, captive);
            SrLogger.LogMessage(
                $"[SR2MP_TEST] vacuum-joint-capture phase={phase} frame={UnityEngine.Time.frameCount} " +
                $"time={UnityEngine.Time.unscaledTime:F3} actor={(actor ? actor.ActorId.Value.ToString("X") : "<none>")} " +
                $"argument={JointId(argument)} lock={JointId(lockJoint)} captive={JointId(captive)} " +
                $"argumentIsLock={(argument && lockJoint && argument.Pointer == lockJoint.Pointer)} " +
                $"argumentIsCaptive={(argument && captive && argument.Pointer == captive.Pointer)} " +
                $"argumentListed={argumentListed} captiveListed={captiveListed} listCount={count} " +
                $"argumentParent={TransformId(argument ? argument.transform.parent : null)} " +
                $"vacOrigin={TransformId(origin ? origin.transform : null)} " +
                $"argumentConnected={(argument && argument.connectedBody ? argument.connectedBody.GetInstanceID() : 0)} " +
                $"body={(body ? body.GetInstanceID() : 0)} " +
                $"nozzleDistance={(origin && body ? Vector3.Distance(origin.transform.position, body.position) : float.NaN):F3}");
        }
        catch (Exception exception)
        {
            SrLogger.LogWarning($"[SR2MP_TEST] vacuum-joint-capture trace failed phase={phase}: {exception.GetType().Name}");
        }
#endif
    }

    private static bool Contains(Il2CppSystem.Collections.Generic.List<SpringJoint>? list, Joint? joint)
    {
        if (list == null || !joint)
            return false;
        for (var i = 0; i < list.Count; i++)
        {
            var listed = list[i];
            if (listed && listed.Pointer == joint.Pointer)
                return true;
        }
        return false;
    }

    private static long JointId(Joint? joint) => joint ? joint.Pointer.ToInt64() : 0;
    private static long TransformId(Transform? transform) => transform ? transform.Pointer.ToInt64() : 0;
}

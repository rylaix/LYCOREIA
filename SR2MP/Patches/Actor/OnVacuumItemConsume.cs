using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Player.PlayerItems;
using SR2MP.Components.Actor;
using SR2MP.Components.FX;
using SR2MP.Shared.Managers;
using SR2MP.Shared.Utils;

namespace SR2MP.Patches.Actor;

[HarmonyPatch(typeof(VacuumItem), "ConsumeVacuumable", typeof(GameObject))]
internal static class OnVacuumItemConsume
{
    public static bool Prefix(VacuumItem __instance, GameObject __0, out long __state)
    {
        var runOriginal = NetworkActorCaptureManager.BeforeConsume(__instance, __0);
        __state = NetworkActorCaptureManager.TakeNativeConsumeStartedActor();
        return runOriginal;
    }

    // A finalizer (not a postfix) so an approved consume that throws natively still
    // clears its in-progress flag and replay marker for the exact actor it started.
    public static Exception? Finalizer(VacuumItem __instance, long __state, Exception? __exception)
    {
        NetworkActorCaptureManager.AfterConsume(__instance, __state, __exception != null);
        return __exception;
    }
}

[HarmonyPatch(typeof(VacuumItem), nameof(VacuumItem.Update))]
internal static class OnVacuumItemUpdate
{
    private static float nextTraceAt;
    private static float nextDevelopmentSampleAt;

    public static void Prefix(VacuumItem __instance)
    {
        RemoteVacuumSuctionFX.PublishLocalSuctionPose(__instance);
        NetworkActorCaptureManager.Pump(__instance);
        LocalActorVacuumPredictionManager.Update(__instance);
#if SR2MP_DEEPLOG
        DeepLogVacuumTracker.Update(__instance);
#endif

        if (Logger.DiagnosticsEnabled && UnityEngine.Time.unscaledTime >= nextDevelopmentSampleAt)
        {
            var actor = LocalActorVacuumPredictionManager.ActiveActor;
            if (__instance._vacPressed || __instance._vacMode != VacuumItem.VacMode.NONE || actor)
            {
                nextDevelopmentSampleAt = UnityEngine.Time.unscaledTime + .5f;
                LogDevelopmentVacuumSample(__instance, actor);
            }
        }

#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_VAC_STATE") == "1" &&
            UnityEngine.Time.unscaledTime >= nextTraceAt)
        {
            nextTraceAt = UnityEngine.Time.unscaledTime + 1f;
            var region = __instance.VacRegion;
            var target = LocalActorVacuumPredictionManager.ActiveActor;
            SrLogger.LogMessage(
                $"[SR2MP_TEST] native-vacuum-update active={__instance.isActiveAndEnabled} " +
                $"mode={__instance._vacMode} pressed={__instance._vacPressed} " +
                $"region={(region ? region.name : "<missing>")} regionActive={(region && region.activeInHierarchy)} " +
                $"predictedActor={(target ? target.ActorId.Value.ToString("X") : "<none>")} " +
                $"position={(target ? target.transform.position.ToString() : "<none>")} " +
                DescribeRegionOverlap(region, target));
        }
#endif
    }

    private static void LogDevelopmentVacuumSample(VacuumItem vacuumItem, NetworkActor? actor)
    {
        try
        {
            var origin = vacuumItem ? vacuumItem.VacOrigin : null;
            var target = actor ? actor.GetComponent<Vacuumable>() : null;
            var body = target ? target._body : null;
            var joint = target ? target._captiveToJoint : null;
            var targetPosition = actor ? actor.transform.position : Vector3.zero;
            var originPosition = origin ? origin.transform.position : Vector3.zero;
            var distance = origin && actor ? Vector3.Distance(originPosition, targetPosition) : float.NaN;
            var captureRequestId = actor
                ? NetworkActorCaptureManager.GetDiagnosticCaptureCorrelation(actor.ActorId.Value)
                : "<none>";
            var rayHit = "<none>";
            var rayHitDistance = float.NaN;
            if (origin && actor && distance > .01f && Physics.Raycast(originPosition,
                    (targetPosition - originPosition).normalized, out var hit,
                    Math.Min(distance + 1f, vacuumItem.MaxVacDist + 1f),
                    GetNativeVacuumRayMask(), QueryTriggerInteraction.Ignore))
            {
                rayHit = $"{hit.collider?.name ?? "<unknown>"}@layer{hit.collider?.gameObject.layer}";
                rayHitDistance = hit.distance;
            }

            var nearby = "<none>";
            if (actor)
            {
                var names = Physics.OverlapSphere(targetPosition, .45f, Physics.DefaultRaycastLayers,
                        QueryTriggerInteraction.Ignore)
                    .Where(collider => collider &&
                        (!collider.transform.IsChildOf(actor.transform) && collider.transform != actor.transform))
                    .Take(5)
                    .Select(collider => $"{collider.name}:L{collider.gameObject.layer}")
                    .ToArray();
                nearby = names.Length == 0 ? "<none>" : string.Join(",", names);
            }

            var player = SceneContext.Instance ? SceneContext.Instance.player : null;
            var joints = vacuumItem ? vacuumItem._joints : null;
            Logger.LogDiagnostic("vacuum", "sample",
                $"mode={vacuumItem._vacMode}; pressed={vacuumItem._vacPressed}; active={vacuumItem.isActiveAndEnabled}; " +
                $"actorId={(actor ? actor.ActorId.Value.ToString("X") : "<none>")}; requestId={captureRequestId}; " +
                $"owner={(actor ? actor.CurrentOwnerId : "<none>")}; captive={(target ? target.IsCaptive() : false)}; " +
                $"held={(vacuumItem._held && actor && vacuumItem._held == actor.gameObject)}; " +
                $"joint={(joint ? joint.Pointer.ToInt64().ToString("X") : "<none>")}; " +
                $"jointCount={(joints != null ? joints.Count : -1)}; " +
                $"connectedBody={(joint && joint.connectedBody ? joint.connectedBody.GetInstanceID() : 0)}; " +
                $"playerPos={(player ? player.transform.position.ToString("F2") : "<none>")}; " +
                $"nozzlePos={(origin ? originPosition.ToString("F2") : "<none>")}; " +
                $"actorPos={(actor ? targetPosition.ToString("F2") : "<none>")}; " +
                $"distance={distance:F2}; maxVacDist={(vacuumItem ? vacuumItem.MaxVacDist : float.NaN):F2}; " +
                $"nativeRayHit={rayHit}; nativeRayHitDistance={rayHitDistance:F2}; " +
                $"bodyVelocity={(body ? body.velocity.ToString("F2") : "<none>")}; " +
                $"nearbyColliders=[{nearby}]",
                rateKey: $"vacuum-sample:{LocalID}", minimumIntervalMilliseconds: 450);
        }
        catch (Exception exception)
        {
            Logger.LogDiagnostic("vacuum", "sample-error", $"type={exception.GetType().Name}; message={exception.Message}",
                rateKey: $"vacuum-sample-error:{LocalID}", minimumIntervalMilliseconds: 5000);
        }
    }

    private static int GetNativeVacuumRayMask()
    {
        try
        {
            var property = AccessTools.Property(typeof(VacuumItem), "VAC_RAY_MASK");
            object? value = property?.GetValue(null);
            if (property == null)
            {
                var field = AccessTools.Field(typeof(VacuumItem), "VAC_RAY_MASK");
                if (field != null)
                    value = field.GetValue(null);
            }

            return value == null ? Physics.DefaultRaycastLayers : Convert.ToInt32(value);
        }
        catch
        {
            return Physics.DefaultRaycastLayers;
        }
    }

#if SR2MP_TEST
    private static string DescribeRegionOverlap(GameObject? region, NetworkActor? actor)
    {
        if (!region || !actor)
            return "regionOverlap=<unavailable>";

        try
        {
            var regionColliders = region.GetComponentsInChildren<Collider>(true)
                .Where(collider => collider && collider.enabled && collider.gameObject.activeInHierarchy)
                .ToArray();
            var actorColliders = actor.GetComponentsInChildren<Collider>(true)
                .Where(collider => collider && collider.enabled && collider.gameObject.activeInHierarchy)
                .ToArray();
            var intersections = from regionCollider in regionColliders
                                from actorCollider in actorColliders
                                where regionCollider.bounds.Intersects(actorCollider.bounds)
                                select $"{regionCollider.name}/{actorCollider.name}";
            return $"regionColliders={regionColliders.Length} actorColliders={actorColliders.Length} " +
                   $"overlap=[{string.Join(",", intersections.Take(8))}]";
        }
        catch (Exception exception)
        {
            return $"regionOverlap=<{exception.GetType().Name}>";
        }
    }
#endif
}

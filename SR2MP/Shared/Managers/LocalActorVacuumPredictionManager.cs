using System;
using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Player.PlayerItems;
using SR2MP.Components.Actor;

namespace SR2MP.Shared.Managers;

/// <summary>
/// Remote actors are frozen and interpolated from their owner. Temporarily give
/// the local vanilla vacuum the same physics control it would have for a local
/// actor; ownership still changes only through the existing host-validated capture
/// or consume transaction.
/// </summary>
internal static class LocalActorVacuumPredictionManager
{
    private static readonly Dictionary<long, NetworkActor> predictedActors = new();
    private static NetworkActor? currentTargetActor;
    private static bool rayMaskRead;
    private static int vacuumRayMask;
#if SR2MP_TEST
    private static float nextTestPinTraceAt;
#endif

    // Kept for existing diagnostics and bot target observation. This is a
    // representative candidate/approved actor, never an authority decision.
    internal static NetworkActor? ActiveActor
        => currentTargetActor ? currentTargetActor : predictedActors.Values.FirstOrDefault();

    internal static bool BeginApprovedConsume(VacuumItem vacuumItem, NetworkActor actor)
    {
        if (!vacuumItem || !actor || actor.IsDestroyed || actor.LocallyOwned ||
            !IsLocalVacuumItem(vacuumItem) || !vacuumItem._vacPressed ||
            vacuumItem._vacMode != VacuumItem.VacMode.VAC ||
            (!IsCurrentVacuumTarget(vacuumItem, actor) &&
             !NetworkActorCaptureManager.IsFreshNativeVacuumCandidate(actor.ActorId.Value)))
            return false;

        var actorId = actor.ActorId.Value;
        if (actorId == 0)
            return false;
        predictedActors[actorId] = actor;
        actor.SetLocalVacuumPrediction(true);
        return actor.IsLocalVacuumPrediction;
    }

    internal static void EndApprovedConsume(long actorId)
    {
        if (actorId != 0 && predictedActors.Remove(actorId, out var actor) && actor)
            actor.SetLocalVacuumPrediction(false);
    }

    internal static void Clear() => ClearAllPredictions();

    internal static void PrepareTestActorForApprovedStage(VacuumItem vacuumItem, NetworkActor actor)
    {
#if SR2MP_TEST
        PinTestActorInsideVacRegion(vacuumItem, actor);
#endif
    }

    internal static void Update(VacuumItem vacuumItem)
    {
        try
        {
            if (!vacuumItem || (!Main.Server.IsRunning && !Main.Client.IsConnected) ||
                !IsLocalVacuumItem(vacuumItem) || !vacuumItem._vacPressed ||
                vacuumItem._vacMode != VacuumItem.VacMode.VAC)
            {
                NetworkActorCaptureManager.CancelClientCapturesForVacuum(
                    vacuumItem, "vacuum released or inactive");
                currentTargetActor = null;
                ClearAllPredictions();
                return;
            }

            if (!TryFindVacuumTarget(vacuumItem, out var target))
            {
                currentTargetActor = null;
                NetworkActorCaptureManager.CancelUnapprovedClientCapturesExcept(
                    vacuumItem, 0, "vacuum ray no longer selects the requested actor");
                PrunePredictions(vacuumActive: true);
                return;
            }

            var actor = target!.GetComponent<NetworkActor>();
            if (!actor || actor.IsDestroyed || actor.LocallyOwned ||
                string.IsNullOrWhiteSpace(actor.CurrentOwnerId) || actor.CurrentOwnerId == LocalID)
            {
                currentTargetActor = null;
                NetworkActorCaptureManager.CancelUnapprovedClientCapturesExcept(
                    vacuumItem, 0, "vacuum ray selected an ineligible actor");
                PrunePredictions(vacuumActive: true);
                return;
            }

            currentTargetActor = actor;
            NetworkActorCaptureManager.CancelUnapprovedClientCapturesExcept(
                vacuumItem, actor.ActorId.Value, "vacuum aim moved to another actor");
            var origin = vacuumItem.VacOrigin;
            var targetDistance = origin
                ? Vector3.Distance(origin.transform.position, target!.transform.position)
                : float.PositiveInfinity;
            if (ActorCaptureTransferPolicy.ShouldRequestPredictedCapture(
                    clientConnected: Main.Client.IsConnected,
                    hostRunning: Main.Server.IsRunning,
                    vacuumPressed: vacuumItem._vacPressed,
                    vacuumModeIsVac: vacuumItem._vacMode == VacuumItem.VacMode.VAC,
                    targetHasRemoteOwner: !actor.LocallyOwned &&
                                          !string.IsNullOrWhiteSpace(actor.CurrentOwnerId) &&
                                          actor.CurrentOwnerId != LocalID,
                    targetCanCapture: target!.CanCapture(),
                    targetIsCaptive: target.IsCaptive(),
                    targetDistance: targetDistance,
                    maximumVacuumDistance: vacuumItem.MaxVacDist))
                NetworkActorCaptureManager.RequestPredictedClientCapture(
                    vacuumItem, target, targetDistance);

#if SR2MP_TEST
            PinTestActorInsideVacRegion(vacuumItem, actor);
#endif
            PrunePredictions(vacuumActive: true);
        }
        catch (Exception exception)
        {
            currentTargetActor = null;
            ClearAllPredictions();
            SrLogger.LogWarning($"Local actor vacuum prediction failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    internal static bool IsLocalVacuumItem(VacuumItem vacuumItem)
    {
        var player = SceneContext.Instance?.Player;
        if (!player)
            return false;

        var itemController = player.GetComponent<PlayerItemController>();
        return itemController && itemController._vacuumItem == vacuumItem;
    }

    private static bool TryFindVacuumTarget(VacuumItem vacuumItem, out Vacuumable? target)
    {
        target = null;
        var origin = vacuumItem.VacOrigin;
        if (!origin || !TryGetVacuumRayMask(out var mask))
            return false;

        if (!Physics.Raycast(origin.transform.position, origin.transform.up.normalized, out var hit,
                vacuumItem.MaxVacDist, mask, QueryTriggerInteraction.Ignore) || !hit.collider)
            return false;

        target = hit.collider.GetComponentInParent<Vacuumable>();
        return target && target.gameObject.activeInHierarchy && !target.IsCaptive();
    }

    private static bool IsCurrentVacuumTarget(VacuumItem vacuumItem, NetworkActor actor)
        => actor && TryFindVacuumTarget(vacuumItem, out var target) &&
           target && target.GetComponent<NetworkActor>() == actor;

    private static void PrunePredictions(bool vacuumActive)
    {
        foreach (var (actorId, actor) in predictedActors.ToArray())
        {
            var actorValid = actor && !actor.IsDestroyed && !actor.LocallyOwned &&
                             actor.gameObject.activeInHierarchy &&
                             !string.IsNullOrWhiteSpace(actor.CurrentOwnerId) && actor.CurrentOwnerId != LocalID;
            var hasApprovedCapture = actorValid && NetworkActorCaptureManager.HasMatchingApprovedPullJoint(actorId);
            if (ActorCaptureTransferPolicy.ShouldRetainPredictedCapture(
                    vacuumActive, actorValid, hasApprovedCapture))
            {
                actor.SetLocalVacuumPrediction(true);
                continue;
            }

            if (actor)
                actor.SetLocalVacuumPrediction(false);
            predictedActors.Remove(actorId);
            if (actor && NetworkActorCaptureManager.IsWaitingForApprovedConsume(actorId))
                NetworkActorCaptureManager.CancelClientCapture(actorId,
                    "approved native vacuum pulling joint is no longer valid");
        }
    }

    private static bool TryGetVacuumRayMask(out int mask)
    {
        if (!rayMaskRead)
        {
            rayMaskRead = true;
            try
            {
                var property = AccessTools.Property(typeof(VacuumItem), "VAC_RAY_MASK");
                var value = property?.GetValue(null);
                if (value == null)
                    value = AccessTools.Field(typeof(VacuumItem), "VAC_RAY_MASK")?.GetValue(null);
                if (value != null)
                    vacuumRayMask = Convert.ToInt32(value);
            }
            catch (Exception exception)
            {
                SrLogger.LogWarning(
                    $"Could not read native vacuum ray mask: {exception.GetType().Name}: {exception.Message}");
            }
        }

        mask = vacuumRayMask;
        return mask != 0;
    }

#if SR2MP_TEST
    private static void PinTestActorInsideVacRegion(VacuumItem vacuumItem, NetworkActor actor)
    {
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_PIN_REMOTE_CAPTURE_ACTOR") != "1")
            return;

        var origin = vacuumItem.VacOrigin;
        var region = vacuumItem.VacRegion;
        if (!origin || !region)
            return;

        UnityEngine.Collider? selectedRegionCollider = null;
        var destination = origin.transform.position + origin.transform.forward;
        var targetPoint = destination;
        var bestDistance = float.MaxValue;
        foreach (var collider in region.GetComponentsInChildren<UnityEngine.Collider>(true))
        {
            if (!collider || !collider.enabled || !collider.isTrigger ||
                !collider.gameObject.activeInHierarchy)
                continue;

            var center = collider.bounds.center;
            var closestCenter = collider.ClosestPoint(center);
            var surfacePoint = collider.ClosestPoint(origin.transform.position);
            var inward = center - surfacePoint;
            var candidate = (center - closestCenter).sqrMagnitude <= 0.0001f
                ? center
                : inward.sqrMagnitude > 0.001f
                    ? surfacePoint + inward.normalized * 0.25f
                    : center;
            var distance = UnityEngine.Vector3.Distance(origin.transform.position, candidate);
            if (distance >= bestDistance)
                continue;

            selectedRegionCollider = collider;
            destination = candidate;
            targetPoint = candidate;
            bestDistance = distance;
        }

        var actorCollider = actor.GetComponentsInChildren<UnityEngine.Collider>(true)
            .Where(collider => collider && collider.enabled && !collider.isTrigger &&
                               collider.gameObject.activeInHierarchy)
            .OrderByDescending(collider => collider.bounds.extents.sqrMagnitude)
            .FirstOrDefault();
        if (actorCollider)
            destination += actor.transform.position - actorCollider.bounds.center;

        // Keep VacOrigin's player/camera-driven pose intact. This diagnostic only
        // moves the remote actor into a native trigger; rotating the nozzle toward
        // the trigger point makes the visible vacuum aim unlike ordinary play.

        var body = actor.GetComponent<UnityEngine.Rigidbody>();
        if (body)
        {
            body.position = destination;
            body.velocity = UnityEngine.Vector3.zero;
            body.angularVelocity = UnityEngine.Vector3.zero;
            body.WakeUp();
        }
        actor.transform.position = destination;

        if (Environment.GetEnvironmentVariable("SR2MP_TEST_PIN_SYNC_TRANSFORMS") == "1")
            UnityEngine.Physics.SyncTransforms();

        if (UnityEngine.Time.unscaledTime < nextTestPinTraceAt)
            return;

        nextTestPinTraceAt = UnityEngine.Time.unscaledTime + 5f;
        var regionCheck = selectedRegionCollider
            ? selectedRegionCollider.ClosestPoint(targetPoint)
            : UnityEngine.Vector3.zero;
        SrLogger.LogMessage(
            $"[SR2MP_TEST] actor-test-pin actor={actor.ActorId.Value:X} position={destination} " +
            $"origin={origin.transform.position} distance={bestDistance:F2} " +
            $"captureDist={vacuumItem.CaptureDist:F2} regionCollider={(selectedRegionCollider ? selectedRegionCollider.name : "<none>")} " +
            $"regionPoint={targetPoint} regionCheck={regionCheck}");
    }
#endif

    private static void ClearAllPredictions()
    {
        foreach (var actor in predictedActors.Values)
            if (actor)
                actor.SetLocalVacuumPrediction(false);
        predictedActors.Clear();
    }
}

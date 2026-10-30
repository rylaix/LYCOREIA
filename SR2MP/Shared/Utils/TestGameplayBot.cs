#if SR2MP_TEST
using HarmonyLib;
using System.Reflection;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Player.CharacterController;
using Il2CppMonomiPark.SlimeRancher.Player;
using Il2CppMonomiPark.SlimeRancher.Player.PlayerItems;
using SR2MP.Components.Actor;
using SR2MP.Packets.Actor;
using SR2MP.Shared.Managers;

namespace SR2MP.Shared.Utils;

/// <summary>
/// Opt-in game-action bot for disposable two-client runs. It uses the game's own
/// player/actor objects and native vacuum methods, while bypassing only camera
/// aiming and input acquisition so the networked action can be exercised repeatably.
/// </summary>
internal static class TestGameplayBot
{
    private const int Idle = 0;
    private const int WaitingToCapture = 1;
    private const int WaitingForOwnership = 2;
    private const int WaitingForDestroy = 4;
    private const int Finished = 5;
    private const int SettlingAfterCapture = 6;
    private const int WaitingForInventoryReject = 7;
    private const int SettlingInventoryReject = 8;
    private const int WaitingForInventoryRejectStatus = 9;
    private const float CaptureReleaseSettleSeconds = 1f;
    private const float FullInventoryVacuumAttemptSeconds = 3f;
    private const float FullInventoryStatusTimeoutSeconds = 15f;

    private static bool followAttempted;
    private static bool followVerified;
    private static bool followScreenshotAttempted;
    private static bool captureTargetScreenshotAttempted;
    private static float followVerifyAt;
    private static float nextCaptureCameraTraceAt;
    private static float nextCaptureMechanicsTraceAt;
    private static bool hostCaptureTargetSpawnAttempted;
    private static float hostCaptureTargetSpawnAt = -1f;
    private static int hostCaptureTargetsSpawned;
    private static readonly List<long> massVacTargetActorIds = new();
    private static readonly List<long> massVacReceivedTargetIds = new();
    private static readonly List<long> massVacOrderedTargetIds = new();
    private static bool massVacFixtureLogged;
    private static bool massVacOverlapLogged;
    private static int massVacPeakPending;
    private static int massVacPeakPulling;
    private static float massVacInputStartedAt;
    private const int MassVacPileColumns = 4;
    private const int MaxMassVacPileTargets = 16;
    // Matches native MaxVacDist: anything closer can legitimately enter the held cone.
    private const float MassVacPileClearRadius = 20f;
    private static bool massVacObserverLogged;
    private static float massVacNextAimAt;
    private static int massVacAimIndex;
    private static bool massVacInputStarted;
    private static bool massVacStatusesStarted;
    private static bool massVacFinished;
    private static bool massVacFailed;
    private static float massVacDeadline;
    private static float massVacNextStatusAt;
    private static int massVacStatusQuerySequence;
    private static int massVacInitialAmmo = -1;
    private static VacuumItem? massVacVacuumItem;
    private static Guid massVacProbeId;
    private static readonly Dictionary<long, int> massVacStatusQueries = new();
    private static readonly Dictionary<long, float> massVacStatusSentAt = new();
    private static readonly Dictionary<long, int> massVacStatusRetries = new();
    private static readonly Dictionary<long, (bool actorPresent, bool modelPresent)> massVacHostStatus = new();
    private static readonly HashSet<long> massVacBaselineNonTargetActors = new();
    private static float massVacNextDistractorCheckAt;
    private static long hostCaptureTargetActorId;
    private static long hostCaptureLastActorId;
    private static int captureRepeatCount;
    private static bool captureRepeatCountLoaded;
    private static int captureIterationsPassed;
    private static long queuedCaptureTargetActorId;
    private static bool gameplayReadyDelayStarted;
    private static float gameplayReadyAt;
    private static int capturePhase;
    private static float captureActionAt;
    private static float captureStageFallbackAt;
    private static bool captureStageFallbackAttempted;
    private static bool captureAbsenceWithoutNativeRemovalLogged;
    private static long captureActorId;
    private static long announcedCaptureTargetActorId;
    private static float captureTargetWaitDeadline;
    private static bool captureTargetWaitStarted;
    private static Guid captureProbeId;
    private static int captureStatusQueryId;
    private static bool captureStatusQueryPending;
    private static bool captureHostStatusReceived;
    private static bool captureHostActorPresent;
    private static bool captureHostModelPresent;
    private static float captureTimeoutAt;
    private static GameObject? captureActorObject;
    private static Vacuumable? captureVacuumable;
    private static VacuumItem? captureVacuumItem;
    private static IdentifiableType? captureIdentifiableType;
    private static int captureAmmoCountBefore;
    private static bool captureModeConfigLoaded;
    private static bool fullInventoryRejectionMode;
    private static bool fullInventoryFixturePrepared;
    private static bool fullInventoryInputPerformed;
    private static bool fullInventoryTargetObserved;
    private static float fullInventoryInputStartedAt;
    private static string fullInventoryEarlyReleaseReason = string.Empty;
    private static string fullInventoryFixtureTrace = "<not prepared>";
    private static int hostControlPhase;
    private static float hostControlActionAt;
    private static float hostControlDeadline;
    private static float hostControlInitialNozzleDistance;
    private static float hostControlMinimumNozzleDistance;
    private static float hostControlNextTraceAt;
    private static float hostControlStartedAt;
    private static int hostControlAmmoBefore;
    private static bool hostControlProgressChecked;
    private static bool hostControlProgressPassed;
    private static bool hostObserverPassed;
    private static bool captureAmmoResetDone;

    private static float GetCaptureStartDistance()
    {
        var text = Environment.GetEnvironmentVariable("SR2MP_TEST_CAPTURE_START_DISTANCE");
        return float.TryParse(text, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var requested) && float.IsFinite(requested)
            ? Mathf.Clamp(requested, 1.75f, 4f) : 1.75f;
    }

    private static int GetCaptureRepeatCount()
    {
        if (captureRepeatCountLoaded)
            return captureRepeatCount;

        var configuredValue = Environment.GetEnvironmentVariable("SR2MP_TEST_CAPTURE_REPEAT_COUNT");
        var requestedCount = int.TryParse(configuredValue, out var parsedCount) ? parsedCount : 1;
        captureRepeatCount = Math.Clamp(requestedCount, 1, IsMassVacPile() ? MaxMassVacPileTargets : 5);
        captureRepeatCountLoaded = true;
        SrLogger.LogMessage(
            $"[SR2MP_TEST_BOT] capture suite repeat count={captureRepeatCount} " +
            $"(SR2MP_TEST_CAPTURE_REPEAT_COUNT={configuredValue ?? "<unset>"})");
        return captureRepeatCount;
    }

    private static bool ConfigureCaptureScenario()
    {
        if (captureModeConfigLoaded)
            return capturePhase != Finished;

        captureModeConfigLoaded = true;
        fullInventoryRejectionMode =
            Environment.GetEnvironmentVariable("SR2MP_TEST_CAPTURE_FULL_INVENTORY") == "1";
        if (!fullInventoryRejectionMode)
            return true;

        var repeatCount = GetCaptureRepeatCount();
        if (repeatCount != 1)
        {
            capturePhase = Finished;
            SrLogger.LogError(
                $"[SR2MP_TEST_BOT] capture-full-inventory FAIL: this scenario requires " +
                $"SR2MP_TEST_CAPTURE_REPEAT_COUNT=1 (effective count={repeatCount})");
            return false;
        }

        var conflictingHooks = new[]
        {
            "SR2MP_TEST_BOT_REQUEST_APPROVED_STAGE",
            "SR2MP_TEST_ASSIGN_APPROVED_ACTOR_HELD_ON_CONSUME",
            "SR2MP_TEST_PIN_REMOTE_CAPTURE_ACTOR",
            "SR2MP_TEST_PIN_SYNC_TRANSFORMS",
            "SR2MP_TEST_ROTATE_VACUUM_ORIGIN",
            "SR2MP_TEST_RESYNC_ON_CAPTURE_COMMIT"
        }.Where(name => Environment.GetEnvironmentVariable(name) == "1").ToArray();
        if (conflictingHooks.Length > 0)
        {
            capturePhase = Finished;
            SrLogger.LogError(
                "[SR2MP_TEST_BOT] capture-full-inventory FAIL: natural rejection scenario cannot run with " +
                $"test hooks enabled=[{string.Join(",", conflictingHooks)}]");
            return false;
        }

        SrLogger.LogMessage(
            "[SR2MP_TEST_BOT] capture-full-inventory scenario enabled; " +
            "guest PlayerState.Ammo will be filled through native add methods on a disposable profile");
        return true;
    }

    private static string DescribeAmmoSlots(AmmoSlotManager? ammo)
    {
        if (ammo == null || ammo.Slots == null)
            return "<unavailable>";

        var slots = new List<string>();
        for (var index = 0; index < ammo.Slots.Count; index++)
        {
            var slot = ammo.Slots[index];
            if (slot == null)
            {
                slots.Add($"{index}:<null>");
                continue;
            }

            var itemName = slot.Id ? slot.Id.name : "<empty>";
            slots.Add($"{index}:{(slot.IsUnlocked ? "unlocked" : "locked")}:{itemName}={slot.Count}/{slot.MaxCount}");
        }

        return string.Join(";", slots);
    }

    private static bool TrySummarizeFullInventory(AmmoSlotManager? ammo, IdentifiableType chickType,
        out string slotTrace, out int unlockedCount, out int fullUnlockedCount,
        out bool chickCanBeAdded, out string failureReason)
    {
        slotTrace = DescribeAmmoSlots(ammo);
        unlockedCount = 0;
        fullUnlockedCount = 0;
        chickCanBeAdded = true;
        failureReason = string.Empty;
        if (ammo == null || ammo.Slots == null || !chickType)
        {
            failureReason = "guest ammo slots or Chick metadata are unavailable";
            return false;
        }

        try
        {
            for (var index = 0; index < ammo.Slots.Count; index++)
            {
                var slot = ammo.Slots[index];
                if (slot == null)
                {
                    failureReason = $"guest ammo slot {index} is null";
                    return false;
                }

                if (!slot.IsUnlocked)
                    continue;

                unlockedCount++;
                if (slot.MaxCount <= 0 || slot.Count < 0 || slot.Count > slot.MaxCount)
                {
                    failureReason =
                        $"guest ammo slot {index} has invalid count/capacity {slot.Count}/{slot.MaxCount}";
                    return false;
                }

                if (slot.Count >= slot.MaxCount)
                    fullUnlockedCount++;
            }

            chickCanBeAdded = ammo.CouldAddToAnySlot(new AmmoSlot.AmmoMetadata(chickType));
            if (unlockedCount == 0)
            {
                failureReason = "guest ammo has no unlocked slots";
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            failureReason = $"native full-inventory verification threw {exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    private static bool TryPrepareFullInventoryFixture(AmmoSlotManager? ammo, IdentifiableType chickType,
        out string failureReason)
    {
        failureReason = string.Empty;
        var chickCountBefore = CountLocalAmmo(chickType);
        var beforeTrace = DescribeAmmoSlots(ammo);
        var capacityBefore = true;
        var capacityBeforeKnown = false;
        try
        {
            if (ammo == null)
                throw new InvalidOperationException("guest ammo state is unavailable");
            capacityBefore = ammo.CouldAddToAnySlot(new AmmoSlot.AmmoMetadata(chickType));
            capacityBeforeKnown = true;
        }
        catch (Exception exception)
        {
            failureReason = $"CouldAddToAnySlot before fixture threw {exception.GetType().Name}";
            var failedFixtureTrace = DescribeAmmoSlots(ammo);
            var failedFixtureChickCount = CountLocalAmmo(chickType);
            SrLogger.LogError(
                $"[SR2MP_TEST_BOT] capture-full-inventory fixture FAIL reason={failureReason} " +
                $"slotsBefore=[{beforeTrace}] slotsAfter=[{failedFixtureTrace}] " +
                $"chickAmmoBefore={chickCountBefore} chickAmmoAfter={failedFixtureChickCount} " +
                "chickCapacityBefore=<not-checked> chickCapacityAfter=<not-checked>");
            return false;
        }

        SrLogger.LogMessage(
            $"[SR2MP_TEST_BOT] capture-full-inventory fixture begin slotsBefore=[{beforeTrace}] " +
            $"chickAmmoBefore={chickCountBefore} chickCapacityBefore={capacityBefore}");

        var wasHandlingPacket = HandlingPacket;
        var fixtureSuccess = false;
        string fixtureError = string.Empty;
        var unlockedCount = 0;
        var fullUnlockedCount = 0;
        var chickCanBeAddedAfter = true;
        var chickCapacityAfterChecked = false;
        var afterTrace = "<unavailable>";
        var chickCountAfter = -1;
        try
        {
            HandlingPacket = true;
            if (ammo == null || ammo.Slots == null || !chickType)
            {
                fixtureError = "guest ammo slots or Chick metadata are unavailable";
            }
            else
            {
                for (var index = 0; index < ammo.Slots.Count && string.IsNullOrEmpty(fixtureError); index++)
                {
                    var slot = ammo.Slots[index];
                    if (slot == null)
                    {
                        fixtureError = $"guest ammo slot {index} is null";
                        break;
                    }

                    if (!slot.IsUnlocked || slot.Count >= slot.MaxCount)
                        continue;

                    if (slot.MaxCount <= 0 || slot.Count < 0 || slot.Count > slot.MaxCount)
                    {
                        fixtureError =
                            $"guest ammo slot {index} has invalid count/capacity {slot.Count}/{slot.MaxCount}";
                        break;
                    }

                    var candidates = new List<IdentifiableType>();
                    if (slot.Id)
                        candidates.Add(slot.Id);
                    if (!candidates.Contains(chickType))
                        candidates.Add(chickType);
                    foreach (var candidate in ActorManager.ActorTypes.Values)
                    {
                        if (candidate && !candidates.Contains(candidate))
                            candidates.Add(candidate);
                    }

                    var filled = false;
                    var fillItemName = "<none>";
                    var countBeforeFill = slot.Count;
                    var candidateFailures = new List<string>();
                    foreach (var candidate in candidates)
                    {
                        try
                        {
                            var metadata = new AmmoSlot.AmmoMetadata(candidate, slot.Emotions, slot.Radiant);
                            if (!ammo.CouldAddToSlot(metadata, index, false))
                                continue;

                            var missingCount = slot.MaxCount - slot.Count;
                            var nativeAdded = ammo.MaybeAddToSpecificSlot(metadata, index, missingCount, false);
                            if (nativeAdded && slot.Count == slot.MaxCount)
                            {
                                filled = true;
                                fillItemName = candidate.name;
                                break;
                            }

                            candidateFailures.Add(
                                $"{candidate.name}:MaybeAddToSpecificSlot={nativeAdded},count={slot.Count}/{slot.MaxCount}");
                        }
                        catch (Exception exception)
                        {
                            candidateFailures.Add($"{candidate.name}:{exception.GetType().Name}");
                        }
                    }

                    if (!filled)
                    {
                        fixtureError =
                            $"could not fill unlocked guest ammo slot {index} using native MaybeAddToSpecificSlot " +
                            $"(count={slot.Count}/{slot.MaxCount}; attempts=[{string.Join("|", candidateFailures)}])";
                    }
                    else
                    {
                        SrLogger.LogMessage(
                            $"[SR2MP_TEST_BOT] capture-full-inventory fixture native slot fill index={index} " +
                            $"item={fillItemName} count={countBeforeFill}->{slot.Count}/{slot.MaxCount} " +
                            "method=MaybeAddToSpecificSlot handlingPacket=true");
                    }
                }

                var inventoryVerified = false;
                if (string.IsNullOrEmpty(fixtureError))
                {
                    inventoryVerified = TrySummarizeFullInventory(ammo, chickType, out afterTrace,
                        out unlockedCount, out fullUnlockedCount, out chickCanBeAddedAfter, out fixtureError);
                    chickCapacityAfterChecked = inventoryVerified;
                    if (!inventoryVerified && string.IsNullOrEmpty(fixtureError))
                        fixtureError = "native full-inventory postcondition could not be verified";
                }
                if (string.IsNullOrEmpty(fixtureError) && inventoryVerified &&
                    (fullUnlockedCount != unlockedCount || chickCanBeAddedAfter))
                {
                    fixtureError =
                        $"fixture postcondition failed (unlockedFull={fullUnlockedCount}/{unlockedCount}, " +
                        $"chickCapacity={chickCanBeAddedAfter})";
                }

                fixtureSuccess = string.IsNullOrEmpty(fixtureError);
            }
        }
        catch (Exception exception)
        {
            fixtureError = $"native ammo fixture threw {exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        afterTrace = DescribeAmmoSlots(ammo);
        chickCountAfter = CountLocalAmmo(chickType);
        fullInventoryFixtureTrace =
            $"slotsBefore=[{beforeTrace}] slotsAfter=[{afterTrace}] " +
            $"chickAmmoBefore={chickCountBefore} chickAmmoAfter={chickCountAfter} " +
            $"chickCapacityBefore={(capacityBeforeKnown ? capacityBefore.ToString() : "<not-checked>")} " +
            $"chickCapacityAfter={(chickCapacityAfterChecked ? chickCanBeAddedAfter.ToString() : "<not-checked>")} " +
            $"unlockedFull={fullUnlockedCount}/{unlockedCount}";
        if (!fixtureSuccess)
        {
            failureReason = fixtureError;
            SrLogger.LogError(
                $"[SR2MP_TEST_BOT] capture-full-inventory fixture FAIL reason={fixtureError} " +
                fullInventoryFixtureTrace);
            return false;
        }

        fullInventoryFixturePrepared = true;
        SrLogger.LogMessage(
            $"[SR2MP_TEST_BOT] capture-full-inventory fixture PASS {fullInventoryFixtureTrace}");
        return true;
    }

    private static bool IsCaptureActorPresent(long actorId)
    {
        if (actorId <= 0)
            return false;

        return ActorManager.Actors.ContainsKey(actorId) ||
               GameState.TryGetIdentifiableModel(new ActorId(actorId), out _);
    }

    private static bool TryFindClearCaptureTargetPosition(GameObject player, out Vector3 actorPosition,
        out string placementTrace, int massVacTargetCount = 0)
    {
        actorPosition = Vector3.zero;
        var playerPosition = player.transform.position;
        var forward = player.transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.01f)
            forward = Vector3.forward;
        forward.Normalize();
        var right = Vector3.Cross(Vector3.up, forward).normalized;
        var blockers = new List<string>();
        var requestedNozzleDistance = GetRequestedNozzleDistance();

        // Search nearby walkable ground in expanding rings. The guest test bot
        // uses the same native vacuum ray as the game for extended approaches.
        // This rejects targets whose long suction corridor crosses ranch walls,
        // stairs, or another capturable actor before the target is spawned.
        foreach (var radius in new[] { 6f, 9f, 12f, 16f, 20f, 24f, 30f, 36f, 42f, 48f })
        {
            for (var directionIndex = 0; directionIndex < 16; directionIndex++)
            {
                var angle = Mathf.PI * 2f * directionIndex / 16f;
                var direction = forward * Mathf.Cos(angle) + right * Mathf.Sin(angle);
                var probe = playerPosition + direction * radius;
                var groundOrigin = probe + Vector3.up * 10f;
                if (!Physics.Raycast(groundOrigin, Vector3.down, out var groundHit, 22f,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) ||
                    groundHit.normal.y < 0.72f || Mathf.Abs(groundHit.point.y - playerPosition.y) > 3f)
                    continue;

                var target = groundHit.point + Vector3.up * 0.55f;
                var cameraOrigin = groundHit.point + Vector3.right * 3.5f + Vector3.up * 1.9f;
                if (Physics.Linecast(cameraOrigin, target, out var blocker,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    if (blockers.Count < 8)
                        blockers.Add($"r={radius:F0},a={directionIndex * 22.5f:F1},hit={blocker.collider?.name ?? "<unknown>"}");
                    continue;
                }

                var crowdTrace = "not checked";
                if (massVacTargetCount >= 2 &&
                    TryFindObstructedMassVacLanePosition(groundHit.point + Vector3.up, massVacTargetCount,
                        out var obstructedLaneIndex))
                {
                    if (blockers.Count < 8)
                        blockers.Add($"r={radius:F0},a={directionIndex * 22.5f:F1},laneObstructed={obstructedLaneIndex}");
                    continue;
                }
                if (massVacTargetCount >= 2 &&
                    !IsMassVacCorridorClear(groundHit.point + Vector3.up, massVacTargetCount,
                        out crowdTrace))
                {
                    if (blockers.Count < 8)
                        blockers.Add($"r={radius:F0},a={directionIndex * 22.5f:F1},crowd={crowdTrace}");
                    continue;
                }

                actorPosition = groundHit.point + Vector3.up;
                var approachTrace = "near-range fixture";
                if (requestedNozzleDistance >= 4f)
                {
                    _ = GetCaptureApproachDirection(0, actorPosition, requestedNozzleDistance,
                        out approachTrace);
                    if (!approachTrace.StartsWith("clear direction=", StringComparison.Ordinal))
                    {
                        if (blockers.Count < 8)
                            blockers.Add($"r={radius:F0},a={directionIndex * 22.5f:F1},approach={approachTrace}");
                        continue;
                    }
                }

                placementTrace = $"clear radius={radius:F0} direction={directionIndex * 22.5f:F1} " +
                                 $"ground={groundHit.point} cameraOrigin={cameraOrigin} target={target} " +
                                 $"groundObject={groundHit.collider?.name ?? "<unknown>"} " +
                                 $"longApproach={approachTrace} massVacCrowd={crowdTrace}";
                return true;
            }
        }

        placementTrace = blockers.Count == 0
            ? $"no walkable candidate near player={playerPosition}"
            : $"no clear candidate near player={playerPosition}; blocked=[{string.Join(" | ", blockers)}]";
        return false;
    }

    private static bool IsMassVacPile()
        => Environment.GetEnvironmentVariable("SR2MP_TEST_MASS_VAC_PILE") == "1";

    /// <summary>
    /// Lane mode: one row across the approach. Pile mode: a dense grid whose
    /// first row is centred on laneCenter and whose rows extend away from the guest.
    /// </summary>
    private static Vector3 GetMassVacLanePosition(Vector3 laneCenter, int index, int targetCount)
    {
        var approach = GetCaptureApproachDirection(0, laneCenter, 0f, out _);
        approach.y = 0f;
        approach.Normalize();
        var right = Vector3.Cross(Vector3.up, approach).normalized;
        if (!IsMassVacPile())
            return laneCenter + right * ((index - (targetCount - 1) * .5f) * .85f);

        const float spacing = .7f;
        var columns = Math.Min(targetCount, MassVacPileColumns);
        var row = index / columns;
        var column = index % columns;
        return laneCenter + right * ((column - (columns - 1) * .5f) * spacing) - approach * (row * spacing);
    }

    private static bool TryFindObstructedMassVacLanePosition(Vector3 laneCenter, int targetCount,
        out int obstructedIndex)
    {
        // laneCenter is ground + 1 m. Every cell must also stand on the same floor:
        // a pile spilling over a ledge is unreachable for any vacuum, not a sync defect.
        var floorY = laneCenter.y - 1f;
        for (obstructedIndex = 0; obstructedIndex < targetCount; obstructedIndex++)
        {
            var position = GetMassVacLanePosition(laneCenter, obstructedIndex, targetCount);
            if (Physics.CheckSphere(position, .32f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) ||
                !Physics.Raycast(position, Vector3.down, out var floorHit, 2f, Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore) ||
                Mathf.Abs(floorHit.point.y - floorY) > .3f || floorHit.normal.y < .72f)
                return true;
        }
        obstructedIndex = -1;
        return false;
    }

    private static bool IsMassVacCorridorClear(Vector3 laneCenter, int targetCount,
        out string crowdTrace)
    {
        var approach = GetCaptureApproachDirection(0, laneCenter, 0f, out _);
        approach.y = 0f;
        approach.Normalize();
        var laneAxis = Vector3.Cross(Vector3.up, approach).normalized;
        var firstTarget = IsMassVacPile() ? laneCenter : GetMassVacLanePosition(laneCenter, 0, targetCount);
        var plannedRoot = firstTarget + approach * GetCaptureStartDistance();
        var nozzleStart = plannedRoot + Vector3.up * 1.6f - approach * .2f;
        const float corridorRadius = 2.25f;

        foreach (var (actorId, model) in ActorManager.Actors)
        {
            if (model == null)
                continue;
            var actor = model.GetGameObject();
            var vacuumable = actor ? actor.GetComponent<Vacuumable>() : null;
            if (!actor || !vacuumable)
                continue;

            var position = actor.transform.position;
            // The native cone reaches far beyond the narrow corridors; a pile is
            // held on for many seconds, so keep its whole neighbourhood free of strays.
            if (IsMassVacPile() && Vector3.Distance(position, laneCenter) <= MassVacPileClearRadius)
            {
                crowdTrace = $"actor={actorId:X} pileDistance={Vector3.Distance(position, laneCenter):F2}m radius={MassVacPileClearRadius:F0}m";
                return false;
            }
            for (var index = 0; index < targetCount; index++)
            {
                var targetPosition = GetMassVacLanePosition(laneCenter, index, targetCount);
                var aimPoint = targetPosition + Vector3.up * .4f;
                var segment = aimPoint - nozzleStart;
                var lengthSquared = segment.sqrMagnitude;
                var t = lengthSquared > .001f
                    ? Mathf.Clamp01(Vector3.Dot(position - nozzleStart, segment) / lengthSquared)
                    : 0f;
                var corridorDistance = Vector3.Distance(position, nozzleStart + segment * t);
                if (corridorDistance <= corridorRadius)
                {
                    crowdTrace = $"actor={actorId:X} corridorDistance={corridorDistance:F2}m targetIndex={index} radius={corridorRadius:F2}m";
                    return false;
                }
            }
        }

        crowdTrace = $"clear targetCount={targetCount} approach={approach} nozzleStart={nozzleStart} radius={corridorRadius:F2}m";
        return true;
    }

    private static Vector3 GetCaptureApproachDirection(long targetActorId, Vector3 targetPosition,
        float requestedNozzleDistance, out string approachTrace)
    {
        if (requestedNozzleDistance < 4f)
        {
            approachTrace = "default world-right approach";
            return Vector3.right;
        }

        var bestDirection = Vector3.right;
        var bestBlockers = int.MaxValue;
        var bestBlockerTrace = "none";
        for (var directionIndex = 0; directionIndex < 32; directionIndex++)
        {
            var angle = Mathf.PI * 2f * directionIndex / 32f;
            var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            var blockerCount = 0;
            var blockerTrace = "none";
            var projectedOrigin = targetPosition + direction * requestedNozzleDistance + Vector3.up * 1.6f;
            var targetAimPoint = targetPosition + Vector3.up * .4f;
            var ray = targetAimPoint - projectedOrigin;
            if (ray.magnitude > .5f && Physics.Raycast(projectedOrigin, ray.normalized,
                    out var sceneryHit, ray.magnitude - .5f, GetNativeVacuumRayMask(),
                    QueryTriggerInteraction.Ignore))
            {
                blockerCount++;
                blockerTrace = $"scenery={sceneryHit.collider?.name ?? "<unknown>"}";
            }

            foreach (var (actorId, model) in ActorManager.Actors)
            {
                if (actorId == targetActorId || model == null)
                    continue;

                var otherObject = model.GetGameObject();
                var otherVacuumable = otherObject ? otherObject.GetComponent<Vacuumable>() : null;
                if (!otherObject || !otherVacuumable || !otherVacuumable.CanCapture())
                    continue;

                var offset = otherObject.transform.position - targetPosition;
                offset.y = 0f;
                var along = Vector3.Dot(offset, direction);
                if (along < 1.25f || along > requestedNozzleDistance - 0.75f)
                    continue;

                var lateral = (offset - direction * along).magnitude;
                if (lateral > 1.5f)
                    continue;

                blockerCount++;
                blockerTrace = $"actor={actorId:X}@{along:F2}m/lateral={lateral:F2}m";
            }

            if (blockerCount == 0)
            {
                approachTrace = $"clear direction={directionIndex * 11.25f:F2}deg " +
                                $"distance={requestedNozzleDistance:F2}m; actors=0";
                return direction;
            }

            if (blockerCount < bestBlockers)
            {
                bestBlockers = blockerCount;
                bestDirection = direction;
                bestBlockerTrace = blockerTrace;
            }
        }

        approachTrace = $"no clear direction at {requestedNozzleDistance:F2}m; " +
                        $"best has {bestBlockers} capturable actor(s), last={bestBlockerTrace}";
        return bestDirection;
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

    internal static void PumpHostCaptureTarget()
    {
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_BOT_SPAWN_CAPTURE_TARGET") != "1" ||
            !Main.Server.IsRunning || Main.Server.GetClientCount < 1)
            return;

        if (Environment.GetEnvironmentVariable("SR2MP_TEST_MASS_VAC") == "1")
        {
            PumpHostMassVacTargets();
            return;
        }

        var repeatCount = GetCaptureRepeatCount();
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_CAPTURE_FULL_INVENTORY") == "1" &&
            repeatCount != 1)
        {
            if (!hostCaptureTargetSpawnAttempted)
            {
                hostCaptureTargetSpawnAttempted = true;
                SrLogger.LogError(
                    $"[SR2MP_TEST_BOT] capture-full-inventory FAIL: host target setup requires " +
                    $"SR2MP_TEST_CAPTURE_REPEAT_COUNT=1 (effective count={repeatCount})");
            }
            return;
        }

        if (hostCaptureTargetsSpawned >= repeatCount)
            return;

        if (hostCaptureTargetSpawnAttempted)
        {
            if (hostCaptureTargetActorId <= 0 || IsCaptureActorPresent(hostCaptureTargetActorId))
                return;

            SrLogger.LogMessage(
                $"[SR2MP_TEST_BOT] prior capture target actor={hostCaptureTargetActorId:X} " +
                $"is absent on host; preparing target {hostCaptureTargetsSpawned + 1}/{repeatCount}");
            hostCaptureTargetSpawnAttempted = false;
            hostCaptureTargetActorId = 0;
            hostCaptureTargetSpawnAt = Time.unscaledTime;
        }

        if (hostCaptureTargetSpawnAt < 0f)
        {
            hostCaptureTargetSpawnAt = Time.unscaledTime + 8f;
            SrLogger.LogMessage("[SR2MP_TEST_BOT] waiting 8s after guest connection before spawning an isolated capture target");
            return;
        }

        if (Time.unscaledTime < hostCaptureTargetSpawnAt)
            return;

        hostCaptureTargetSpawnAttempted = true;
        try
        {
            var player = SceneContext.Instance ? SceneContext.Instance.player : null;
            var sceneGroup = SystemContext.Instance?.SceneLoader?.CurrentSceneGroup;
            if (!player || sceneGroup == null || ActorManager.ActorTypes.Count == 0)
            {
                SrLogger.LogWarning("[SR2MP_TEST_BOT] capture target spawn FAIL: host gameplay context is unavailable");
                return;
            }

            var typeEntry = ActorManager.ActorTypes.FirstOrDefault(entry => entry.Value &&
                string.Equals(entry.Value.name, "Chick", StringComparison.OrdinalIgnoreCase));
            if (!typeEntry.Value)
            {
                SrLogger.LogWarning("[SR2MP_TEST_BOT] capture target spawn FAIL: Chick type was not found");
                return;
            }

            var sceneId = NetworkSceneManager.GetPersistentID(sceneGroup);
            var highestActorId = NetworkActorManager.GetHighestActorIdInRange(0, GlobalVariables.ActorIdOffset);
            var actorIdBase = hostCaptureTargetsSpawned == 0
                ? highestActorId
                : Math.Max(highestActorId, hostCaptureLastActorId);
            var nextActorId = actorIdBase + 10;
            if (sceneId < 0 || nextActorId <= 0 || nextActorId >= GlobalVariables.ActorIdOffset)
            {
                SrLogger.LogWarning(
                    $"[SR2MP_TEST_BOT] capture target spawn FAIL: invalid scene/id scene={sceneId} actor={nextActorId}");
                return;
            }

            var actorId = new ActorId(nextActorId);
            if (!TryFindClearCaptureTargetPosition(player, out var position, out var placementTrace))
            {
                SrLogger.LogWarning($"[SR2MP_TEST_BOT] capture target spawn FAIL: {placementTrace}");
                return;
            }

            SrLogger.LogMessage($"[SR2MP_TEST_BOT] capture target placement {placementTrace}");
            if (!ActorManager.TrySpawnNetworkActor(actorId, position, Quaternion.identity,
                    typeEntry.Key, sceneId, out var model) || model == null)
            {
                SrLogger.LogWarning(
                    $"[SR2MP_TEST_BOT] capture target spawn FAIL: game actor creation failed type={typeEntry.Value.name} " +
                    $"actor={actorId.Value:X} position={position}");
                return;
            }

            var actorObject = model.GetGameObject();
            var networkActor = actorObject ? actorObject.GetComponent<NetworkActor>() : null;
            if (!actorObject || !networkActor)
            {
                SrLogger.LogWarning(
                    $"[SR2MP_TEST_BOT] capture target spawn FAIL: spawned actor has no network component actor={actorId.Value:X}");
                return;
            }

            networkActor.CurrentOwnerId = LocalID;
            networkActor.LocallyOwned = true;
            networkActor.CachedLocallyOwned = true;
            networkActor.previousPosition = position;
            networkActor.nextPosition = position;
            ActorManager.OwnershipByActorId[actorId.Value] = LocalID;
            hostCaptureTargetActorId = actorId.Value;
            hostCaptureLastActorId = actorId.Value;
            hostCaptureTargetsSpawned++;

            Main.SendToAllOrServer(new ActorSpawnPacket
            {
                ActorId = actorId,
                Position = position,
                Rotation = Quaternion.identity,
                ActorType = typeEntry.Key,
                SceneGroup = (byte)sceneId,
                SpawnType = (byte)ActorSpawnType.Actor,
                MaterialIndex = (byte)SprinkleMaterialType.none,
                OwnerId = LocalID
            });

            Main.SendToAllOrServer(new TestCaptureTargetPacket { ActorId = actorId.Value });

            SrLogger.LogMessage(
                $"[SR2MP_TEST_BOT] capture target spawn PASS target={hostCaptureTargetsSpawned}/{repeatCount} " +
                $"actor={actorId.Value:X} type={typeEntry.Value.name} " +
                $"owner={LocalID} scene={sceneId} position={position}; target announced to connected clients");
        }
        catch (Exception exception)
        {
            SrLogger.LogError($"[SR2MP_TEST_BOT] capture target spawn threw: {exception}");
        }
    }

    private static void PumpHostMassVacTargets()
    {
        var repeatCount = GetCaptureRepeatCount();
        if (repeatCount < 2 || repeatCount > (IsMassVacPile() ? MaxMassVacPileTargets : 5))
            return;
        if (massVacFixtureLogged)
        {
            if (!massVacObserverLogged && massVacTargetActorIds.Count == repeatCount &&
                massVacTargetActorIds.All(actorId => !IsCaptureActorPresent(actorId)))
            {
                massVacObserverLogged = true;
                SrLogger.LogMessage($"[SR2MP_TEST_BOT] mass-vac observer PASS actors={string.Join(",", massVacTargetActorIds.Select(id => id.ToString("X")))}");
            }
            return;
        }
        if (hostCaptureTargetSpawnAt < 0f)
        {
            hostCaptureTargetSpawnAt = Time.unscaledTime + 8f;
            SrLogger.LogMessage("[SR2MP_TEST_BOT] mass-vac waiting 8s after guest connection before staging concurrent targets");
            return;
        }
        if (Time.unscaledTime < hostCaptureTargetSpawnAt)
            return;

        try
        {
            var player = SceneContext.Instance ? SceneContext.Instance.player : null;
            var sceneGroup = SystemContext.Instance?.SceneLoader?.CurrentSceneGroup;
            var typeEntry = ActorManager.ActorTypes.FirstOrDefault(entry => entry.Value &&
                string.Equals(entry.Value.name, "Chick", StringComparison.OrdinalIgnoreCase));
            var center = Vector3.zero;
            var placementTrace = "not searched";
            if (!player || sceneGroup == null || !typeEntry.Value ||
                !TryFindClearCaptureTargetPosition(player, out center, out placementTrace,
                    massVacTargetCount: repeatCount))
            {
                SrLogger.LogError("[SR2MP_TEST_BOT] mass-vac fixture FAIL: clear Chick staging lane is unavailable " +
                                  $"placement={placementTrace}");
                massVacFixtureLogged = true;
                return;
            }

            var sceneId = NetworkSceneManager.GetPersistentID(sceneGroup);
            var highestActorId = NetworkActorManager.GetHighestActorIdInRange(0, GlobalVariables.ActorIdOffset);
            if (sceneId < 0)
                throw new InvalidOperationException($"invalid scene id {sceneId}");

            var actorIds = new List<string>(repeatCount);
            for (var index = 0; index < repeatCount; index++)
            {
                // Same lane geometry the crowd planner validated and the guest approaches.
                var position = GetMassVacLanePosition(center, index, repeatCount);
                if (Physics.CheckSphere(position, .32f, Physics.DefaultRaycastLayers,
                        QueryTriggerInteraction.Ignore))
                    throw new InvalidOperationException($"lane position is obstructed index={index} position={position}");
                var actorValue = Math.Max(highestActorId, hostCaptureLastActorId) + 10;
                if (actorValue <= 0 || actorValue >= GlobalVariables.ActorIdOffset)
                    throw new InvalidOperationException($"invalid actor id {actorValue}");
                var actorId = new ActorId(actorValue);
                if (!ActorManager.TrySpawnNetworkActor(actorId, position, Quaternion.identity,
                        typeEntry.Key, sceneId, out var model) || model == null)
                    throw new InvalidOperationException($"game actor creation failed actor={actorValue:X}");
                var actorObject = model.GetGameObject();
                var networkActor = actorObject ? actorObject.GetComponent<NetworkActor>() : null;
                if (!actorObject || !networkActor)
                    throw new InvalidOperationException($"spawned actor has no network component actor={actorValue:X}");
                networkActor.CurrentOwnerId = LocalID;
                networkActor.LocallyOwned = true;
                networkActor.CachedLocallyOwned = true;
                networkActor.previousPosition = position;
                networkActor.nextPosition = position;
                ActorManager.OwnershipByActorId[actorValue] = LocalID;
                Main.SendToAllOrServer(new ActorSpawnPacket
                {
                    ActorId = actorId, Position = position, Rotation = Quaternion.identity,
                    ActorType = typeEntry.Key, SceneGroup = (byte)sceneId,
                    SpawnType = (byte)ActorSpawnType.Actor,
                    MaterialIndex = (byte)SprinkleMaterialType.none, OwnerId = LocalID
                });
                Main.SendToAllOrServer(new TestCaptureTargetPacket { ActorId = actorValue });
                massVacTargetActorIds.Add(actorValue);
                hostCaptureLastActorId = actorValue;
                actorIds.Add(actorValue.ToString("X"));
            }

            hostCaptureTargetsSpawned = repeatCount;
            hostCaptureTargetActorId = massVacTargetActorIds[0];
            massVacFixtureLogged = true;
            SrLogger.LogMessage(
                $"[SR2MP_TEST_BOT] mass-vac fixture PASS count={repeatCount} actors={string.Join(",", actorIds)} " +
                $"spacing=0.85m lane={placementTrace}; targets spawned concurrently and announced");
        }
        catch (Exception exception)
        {
            massVacFixtureLogged = true;
            SrLogger.LogError($"[SR2MP_TEST_BOT] mass-vac fixture FAIL {exception.GetType().Name}: {exception.Message}");
        }
    }

    internal static void Pump(GameObject localPlayer)
    {
        if (!localPlayer)
            return;

        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TARR_OBSERVE") == "1")
        {
            PumpTarrObserveScenario(localPlayer);
            return;
        }

        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TARR_MULTIPLY") == "1")
        {
            PumpTarrMultiplyScenario(localPlayer);
            return;
        }

        if (Environment.GetEnvironmentVariable("SR2MP_TEST_ZONE_SOAK") == "1")
        {
            PumpZoneSoakScenario(localPlayer);
            return;
        }

        if (Environment.GetEnvironmentVariable("SR2MP_TEST_THROW_VAC") == "1")
        {
            PumpThrowVacScenario(localPlayer);
            return;
        }

        if (Environment.GetEnvironmentVariable("SR2MP_TEST_THROW") == "1")
        {
            PumpThrowScenario(localPlayer);
            return;
        }

        if (Main.Server.IsRunning && Environment.GetEnvironmentVariable("SR2MP_TEST_BOT_CAPTURE_HOST_CONTROL") == "1")
        {
            TryHostCaptureControl(localPlayer);
            return;
        }

        if (!WaitForStableJoinedWorld())
            return;

        if (!ResetCaptureAmmoIfRequested())
            return;

        if (Environment.GetEnvironmentVariable("SR2MP_TEST_MASS_VAC") == "1")
        {
            PumpMassVacScenario(localPlayer);
            return;
        }

        if (Environment.GetEnvironmentVariable("SR2MP_TEST_BOT_OBSERVE_HOST_CONTROL") == "1")
        {
            ObserveHostCaptureControl();
            return;
        }

        TryFollowRemotePlayer(localPlayer);
        TryCaptureAndConsume(localPlayer);
    }

    private static void PumpMassVacScenario(GameObject localPlayer)
    {
        if (massVacFinished || massVacFailed)
            return;
        var expectedCount = GetCaptureRepeatCount();
        if (expectedCount < 2 || expectedCount > (IsMassVacPile() ? MaxMassVacPileTargets : 5))
        {
            MassVacFail($"invalid target count={expectedCount}");
            return;
        }
        if (massVacDeadline == 0f)
            massVacDeadline = Time.unscaledTime + 60f;
        if (!massVacDeadline.Equals(0f) && Time.unscaledTime >= massVacDeadline)
        {
            MassVacFail(massVacInputStarted
                ? "timed out before every target was consumed and observed on both peers"
                : "timed out waiting for all fixture target announcements");
            return;
        }
        if (massVacOrderedTargetIds.Count < expectedCount)
            return;

        if (!massVacInputStarted)
        {
            var targets = massVacOrderedTargetIds.Take(expectedCount).ToArray();
            if (targets.Any(id => !ActorManager.Actors.TryGetValue(id, out var model) || model == null))
                return;
            var first = ActorManager.Actors[targets[0]].GetGameObject();
            if (!first)
                return;
            var chickType = first.GetComponent<Identifiable>()?.identType;
            massVacVacuumItem = localPlayer.GetComponent<PlayerItemController>()?._vacuumItem;
            massVacInitialAmmo = CountLocalAmmo(chickType);
            if (!chickType || !massVacVacuumItem || massVacInitialAmmo < 0)
            {
                MassVacFail("native vacuum or Chick inventory is unavailable");
                return;
            }

            var requestedDistance = GetCaptureStartDistance();
            var direction = GetCaptureApproachDirection(targets[0], first.transform.position,
                0f, out var approachTrace);
            var anchor = first.transform.position;
            if (IsMassVacPile())
            {
                anchor = GetMassVacTargetCentroid(targets.Take(Math.Min(targets.Length, MassVacPileColumns)));
                var freeCapacity = CountLocalAmmoCapacity(chickType);
                if (freeCapacity < expectedCount)
                {
                    MassVacFail($"guest Chick ammo capacity too small free={freeCapacity} needed={expectedCount}");
                    return;
                }
            }
            SetPlayerPosition(localPlayer, anchor + direction * requestedDistance);
            localPlayer.transform.rotation = Quaternion.LookRotation(-direction);
            AimCameraAt(localPlayer, (IsMassVacPile() ? GetMassVacTargetCentroid(targets) : anchor) + Vector3.up * .5f);
            var origin = massVacVacuumItem.VacOrigin;
            var measuredDistance = origin ? Vector3.Distance(origin.transform.position, first.transform.position) : float.NaN;
            if (!float.IsFinite(measuredDistance) || measuredDistance > massVacVacuumItem.MaxVacDist + .05f ||
                measuredDistance <= massVacVacuumItem.CaptureDist + .25f)
            {
                MassVacFail($"invalid native range fixture measured={measuredDistance:F3} max={massVacVacuumItem.MaxVacDist:F3} capture={massVacVacuumItem.CaptureDist:F3}");
                return;
            }
            massVacProbeId = Guid.NewGuid();
            massVacBaselineNonTargetActors.Clear();
            foreach (var actorId in ActorManager.Actors.Keys)
                if (!targets.Contains(actorId))
                    massVacBaselineNonTargetActors.Add(actorId);
            massVacDeadline = Time.unscaledTime + 120f;
            massVacNextAimAt = Time.unscaledTime + .5f;
            massVacAimIndex = 0;
            massVacInputStarted = true;
            massVacInputStartedAt = Time.unscaledTime;
            try
            {
                var inputData = default(Il2CppMonomiPark.SlimeRancher.Input.InputEventData);
                inputData.Source = Il2CppMonomiPark.SlimeRancher.Input.InputEventSource.KEYBOARD;
                inputData.Value = 1f;
                massVacVacuumItem._vac.StartInput(inputData);
                massVacVacuumItem._vac.Perform(inputData);
                SrLogger.LogMessage(
                    $"[SR2MP_TEST_BOT] mass-vac input started count={expectedCount} actors={string.Join(",", targets.Select(id => id.ToString("X")))} " +
                    $"ammoBefore={massVacInitialAmmo} nozzleDistance={measuredDistance:F3} approach={approachTrace}; one native input held while sweeping targets");
            }
            catch (Exception exception)
            {
                MassVacFail($"native vacuum input threw {exception.GetType().Name}: {exception.Message}");
            }
            return;
        }

        var ids = massVacOrderedTargetIds.Take(expectedCount).ToArray();
        if (massVacInputStarted && Time.unscaledTime >= massVacNextDistractorCheckAt)
        {
            massVacNextDistractorCheckAt = Time.unscaledTime + .1f;
            foreach (var actorId in ActorManager.Actors.Keys)
                if (!ids.Contains(actorId))
                    massVacBaselineNonTargetActors.Add(actorId);
            foreach (var actorId in massVacBaselineNonTargetActors)
            {
                if (NetworkActorCaptureManager.TestWasConsumeRemovalConfirmed(actorId) ||
                    NetworkActorCaptureManager.TestHasPendingClientCapture(actorId) ||
                    NetworkActorCaptureManager.TestHasMatchingApprovedPullJoint(actorId))
                {
                    MassVacFail($"non-designated actor entered capture/consume actor={actorId:X}");
                    return;
                }
            }
        }
        if (!massVacStatusesStarted)
        {
            var pending = 0;
            var pulling = 0;
            var pullingIds = new List<string>();
            foreach (var actorId in ids)
            {
                if (NetworkActorCaptureManager.TestHasPendingClientCapture(actorId))
                    pending++;
                if (NetworkActorCaptureManager.TestHasMatchingApprovedPullJoint(actorId))
                {
                    pulling++;
                    pullingIds.Add(actorId.ToString("X"));
                }
            }
            massVacPeakPending = Math.Max(massVacPeakPending, pending);
            massVacPeakPulling = Math.Max(massVacPeakPulling, pulling);
            if (!massVacOverlapLogged && pending >= 2 && pulling >= 2 &&
                !ids.Any(NetworkActorCaptureManager.TestWasConsumeRemovalConfirmed))
            {
                massVacOverlapLogged = true;
                SrLogger.LogMessage(
                    $"[SR2MP_TEST_BOT] mass-vac overlap PASS pending={pending} pulling={pulling} " +
                    $"beforeFirstConsume=true actors={string.Join(",", pullingIds)}");
            }

            if (IsMassVacPile() && Time.unscaledTime >= massVacNextAimAt)
            {
                // A real player holds the nozzle on the pile; re-aim only as it shrinks.
                massVacNextAimAt = Time.unscaledTime + .45f;
                var remaining = ids.Where(id => ActorManager.Actors.TryGetValue(id, out var m) && m != null).ToArray();
                if (remaining.Length > 0)
                    AimCameraAt(localPlayer, GetMassVacTargetCentroid(remaining) + Vector3.up * .5f);
            }
            else if (Time.unscaledTime >= massVacNextAimAt)
            {
                massVacNextAimAt = Time.unscaledTime + .45f;
                for (var checkedTargets = 0; checkedTargets < ids.Length; checkedTargets++)
                {
                    massVacAimIndex = (massVacAimIndex + 1) % ids.Length;
                    if (!ActorManager.Actors.TryGetValue(ids[massVacAimIndex], out var model) || model == null)
                        continue;
                    var actor = model.GetGameObject();
                    if (!actor)
                        continue;
                    AimCameraAt(localPlayer, actor.transform.position + Vector3.up * .5f);
                    SrLogger.LogMessage($"[SR2MP_TEST_BOT] mass-vac sweep aimed actor={ids[massVacAimIndex]:X} input=held");
                    break;
                }
            }

            if (ids.Any(id => !NetworkActorCaptureManager.TestWasConsumeRemovalConfirmed(id) ||
                              ActorManager.Actors.ContainsKey(id) ||
                              GameState.TryGetIdentifiableModel(new ActorId(id), out _)))
                return;

            try { massVacVacuumItem?._vac?.Cancel(); } catch { }
            SrLogger.LogMessage(
                $"[SR2MP_TEST_BOT] mass-vac metrics count={expectedCount} pile={IsMassVacPile()} " +
                $"peakPending={massVacPeakPending} peakPulling={massVacPeakPulling} " +
                $"elapsed={Time.unscaledTime - massVacInputStartedAt:F2}s");
            massVacStatusesStarted = true;
            massVacNextStatusAt = Time.unscaledTime;
        }

        if (massVacHostStatus.Count < ids.Length && Time.unscaledTime >= massVacNextStatusAt)
        {
            massVacNextStatusAt = Time.unscaledTime + .25f;
            foreach (var actorId in ids)
            {
                if (massVacHostStatus.ContainsKey(actorId))
                    continue;
                if (massVacStatusSentAt.TryGetValue(actorId, out var sentAt) &&
                    Time.unscaledTime - sentAt < 3f)
                    continue;
                var retries = massVacStatusRetries.GetValueOrDefault(actorId);
                if (retries >= 3)
                {
                    MassVacFail($"host status timed out actor={actorId:X}");
                    return;
                }
                var queryId = ++massVacStatusQuerySequence;
                massVacStatusQueries[actorId] = queryId;
                massVacStatusRetries[actorId] = retries + 1;
                massVacStatusSentAt[actorId] = Time.unscaledTime;
                Main.Client.SendPacket(new TestCaptureStatusRequestPacket
                {
                    ProbeId = massVacProbeId,
                    QueryId = queryId,
                    ActorId = actorId
                });
            }
        }
        if (massVacHostStatus.Count != ids.Length)
            return;

        var chick = ActorManager.ActorTypes.Values.FirstOrDefault(type => type &&
            string.Equals(type.name, "Chick", StringComparison.OrdinalIgnoreCase));
        var ammoAfter = CountLocalAmmo(chick);
        if (ammoAfter != massVacInitialAmmo + expectedCount ||
            massVacHostStatus.Values.Any(status => status.actorPresent || status.modelPresent) ||
            massVacHostStatus.Count(status => !status.Value.actorPresent && !status.Value.modelPresent) != expectedCount)
        {
            MassVacFail($"aggregate verification failed ammoBefore={massVacInitialAmmo} ammoAfter={ammoAfter} expectedDelta={expectedCount} hostResults={massVacHostStatus.Count}");
            return;
        }
        foreach (var actorId in ids)
        {
            if (!NetworkActorCaptureManager.TestWasConsumeCommitAcknowledged(actorId))
            {
                MassVacFail($"actor commit acknowledgement was not observed actor={actorId:X}");
                return;
            }
            SrLogger.LogMessage($"[SR2MP_TEST_BOT] mass-vac actor-result PASS actor={actorId:X} nativeRemoval=true guestActorPresent=false guestModelPresent=false hostActorPresent=false hostModelPresent=false");
        }
        massVacFinished = true;
        SrLogger.LogMessage(
            $"[SR2MP_TEST_BOT] mass-vac suite PASS count={expectedCount} " +
            $"ammoBefore={massVacInitialAmmo} ammoAfter={ammoAfter} actors={string.Join(",", ids.Select(id => id.ToString("X")))} " +
            "nativeRemovalConfirmed=all hostAbsent=all");
    }

    private static void MassVacFail(string reason)
    {
        massVacFailed = true;
        try { massVacVacuumItem?._vac?.Cancel(); } catch { }
        SrLogger.LogError($"[SR2MP_TEST_BOT] mass-vac suite FAIL reason={reason}");
    }

    private static bool ResetCaptureAmmoIfRequested()
    {
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_CAPTURE_RESET_AMMO") != "1" || captureAmmoResetDone)
            return true;

        try
        {
            var chickType = ActorManager.ActorTypes.Values.FirstOrDefault(candidate => candidate &&
                string.Equals(candidate.name, "Chick", StringComparison.OrdinalIgnoreCase));
            var ammo = SceneContext.Instance?.PlayerState?.Ammo;
            if (!chickType || ammo?.Slots == null || ammo._ammoModel?.Slots == null)
            {
                SrLogger.LogError("[SR2MP_TEST_BOT] capture-ammo-reset FAIL: guest Chick ammo models are unavailable");
                return false;
            }

            var before = CountLocalAmmo(chickType);
            foreach (var slot in ammo.Slots)
                if (slot != null && slot.Id == chickType)
                    slot.Clear();

            foreach (var slot in ammo._ammoModel.Slots)
                if (slot != null && slot.Id == chickType)
                    slot.Clear();

            var after = CountLocalAmmo(chickType);
            if (before < 0 || after != 0)
            {
                SrLogger.LogError(
                    $"[SR2MP_TEST_BOT] capture-ammo-reset FAIL Chick before={before} after={after}");
                return false;
            }

            captureAmmoResetDone = true;
            SrLogger.LogMessage(
                $"[SR2MP_TEST_BOT] capture-ammo-reset PASS Chick before={before} after={after} " +
                "scope=guest-disposable-test-save");
            return true;
        }
        catch (Exception exception)
        {
            SrLogger.LogError(
                $"[SR2MP_TEST_BOT] capture-ammo-reset FAIL {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private static void TryHostCaptureControl(GameObject localPlayer)
    {
        if (hostControlPhase == Finished)
            return;

        if (hostControlPhase == Idle)
        {
            if (hostCaptureTargetActorId <= 0 ||
                !ActorManager.Actors.TryGetValue(hostCaptureTargetActorId, out var model) || model == null)
                return;

            captureActorId = hostCaptureTargetActorId;
            captureActorObject = model.GetGameObject();
            captureVacuumable = captureActorObject ? captureActorObject.GetComponent<Vacuumable>() : null;
            captureIdentifiableType = captureActorObject
                ? captureActorObject.GetComponent<Identifiable>()?.identType : null;
            captureVacuumItem = localPlayer.GetComponent<PlayerItemController>()?._vacuumItem;
            hostControlAmmoBefore = CountLocalAmmo(captureIdentifiableType);
            if (!captureActorObject || !captureVacuumable || !captureVacuumItem || !captureIdentifiableType ||
                hostControlAmmoBefore < 0 || !captureVacuumable.CanCapture())
            {
                hostControlPhase = Finished;
                SrLogger.LogError(
                    $"[SR2MP_TEST_BOT] host-control FAIL invalid native fixture actor={captureActorId:X} " +
                    $"actor={(bool)captureActorObject} vacuumable={(bool)captureVacuumable} " +
                    $"vacuum={(bool)captureVacuumItem} ammoBefore={hostControlAmmoBefore}");
                return;
            }

            var actorPosition = captureActorObject.transform.position;
            var requestedNozzleDistance = GetRequestedNozzleDistance();
            var rootDistance = GetCaptureStartDistance();
            var outward = GetCaptureApproachDirection(captureActorId, actorPosition,
                requestedNozzleDistance, out var approachTrace);
            var rootPosition = actorPosition + outward * rootDistance;
            SetPlayerPosition(localPlayer, rootPosition);
            localPlayer.transform.rotation = Quaternion.LookRotation(-outward);
            AimCameraAt(localPlayer, GetCaptureAimPoint());

            if (requestedNozzleDistance > 0f && captureVacuumItem.VacOrigin)
                PositionPlayerAtNozzleDistance(localPlayer, actorPosition, captureVacuumItem,
                    requestedNozzleDistance, GetCaptureAimPoint());

            var origin = captureVacuumItem.VacOrigin;
            hostControlInitialNozzleDistance = origin
                ? Vector3.Distance(origin.transform.position, captureActorObject.transform.position)
                : float.NaN;
            var nativeMaxVacDist = captureVacuumItem.MaxVacDist;
            SrLogger.LogMessage(
                $"[SR2MP_TEST_BOT] host-control approach {approachTrace} target={captureActorId:X}");
            if (!float.IsFinite(hostControlInitialNozzleDistance) || !float.IsFinite(nativeMaxVacDist) ||
                nativeMaxVacDist <= 0f || hostControlInitialNozzleDistance > nativeMaxVacDist + .05f ||
                (requestedNozzleDistance > 0f &&
                 Math.Abs(hostControlInitialNozzleDistance - requestedNozzleDistance) > .35f))
            {
                hostControlPhase = Finished;
                SrLogger.LogError(
                    $"[SR2MP_TEST_BOT] host-control FAIL invalid/out-of-range fixture " +
                    $"requestedNozzle={requestedNozzleDistance:F3} measuredNozzle={hostControlInitialNozzleDistance:F3} " +
                    $"rootDistance={Vector3.Distance(localPlayer.transform.position, captureActorObject.transform.position):F3} " +
                    $"nativeMaxVacDist={nativeMaxVacDist:F3}");
                return;
            }

            captureProbeId = Guid.NewGuid();
            hostControlMinimumNozzleDistance = hostControlInitialNozzleDistance;
            hostControlProgressChecked = false;
            hostControlProgressPassed = false;
            hostControlActionAt = Time.unscaledTime + .25f;
            hostControlDeadline = Time.unscaledTime + 120f;
            hostControlPhase = WaitingToCapture;
            CaptureCaptureTargetScreenshotOnce();
            SrLogger.LogMessage(
                $"[SR2MP_TEST_BOT] host-control fixture actor={captureActorId:X} owner=" +
                $"{captureVacuumable.GetComponent<NetworkActor>()?.CurrentOwnerId ?? "<missing>"} " +
                $"rootDistance={Vector3.Distance(localPlayer.transform.position, actorPosition):F3} " +
                $"requestedNozzle={requestedNozzleDistance:F3} measuredNozzle={hostControlInitialNozzleDistance:F3} " +
                $"nativeMaxVacDist={nativeMaxVacDist:F3} captureDist={captureVacuumItem.CaptureDist:F3} " +
                $"ammoBefore={hostControlAmmoBefore} {DescribeCaptureMechanics()}");
            return;
        }

        // Native consume destroys the Unity actor and invalidates these IL2CPP
        // wrappers before the next bot tick. Let WaitingForDestroy verify the
        // authoritative actor/model removal and ammo result below first.
        if (hostControlPhase != WaitingForDestroy &&
            (!captureActorObject || !captureVacuumable || !captureVacuumItem))
        {
            hostControlPhase = Finished;
            SrLogger.LogError($"[SR2MP_TEST_BOT] host-control FAIL fixture references expired actor={captureActorId:X}");
            return;
        }

        if (hostControlPhase == WaitingToCapture && Time.unscaledTime >= hostControlActionAt)
        {
            var input = captureVacuumItem._vac;
            if (input == null || !captureVacuumable.CanCapture())
            {
                hostControlPhase = Finished;
                SrLogger.LogError($"[SR2MP_TEST_BOT] host-control FAIL native vacuum input unavailable actor={captureActorId:X}");
                return;
            }
            try
            {
                var inputData = default(Il2CppMonomiPark.SlimeRancher.Input.InputEventData);
                inputData.Source = Il2CppMonomiPark.SlimeRancher.Input.InputEventSource.KEYBOARD;
                inputData.Value = 1f;
                input.StartInput(inputData);
                input.Perform(inputData);
                hostControlPhase = WaitingForDestroy;
                hostControlStartedAt = Time.unscaledTime;
                hostControlActionAt = Time.unscaledTime + 2f;
                SrLogger.LogMessage(
                    $"[SR2MP_TEST_BOT] host-control input actor={captureActorId:X} " +
                    $"mode={captureVacuumItem._vacMode} pressed={captureVacuumItem._vacPressed} " +
                    "source=InputEvent.StartInput -> InputEvent.Perform -> native VacuumItem.Update");
            }
            catch (Exception exception)
            {
                TryStopVanillaVacuum();
                hostControlPhase = Finished;
                SrLogger.LogError($"[SR2MP_TEST_BOT] host-control FAIL native vacuum input threw: {exception}");
            }
            return;
        }

        if (hostControlPhase != WaitingForDestroy)
            return;

        var targetPresent = ActorManager.Actors.ContainsKey(captureActorId);
        var targetModelPresent = GameState.TryGetIdentifiableModel(new ActorId(captureActorId), out _);
        if (targetPresent && captureActorObject)
        {
            var origin = captureVacuumItem.VacOrigin;
            var distance = origin
                ? Vector3.Distance(origin.transform.position, captureActorObject.transform.position)
                : float.NaN;
            if (float.IsFinite(distance))
                hostControlMinimumNozzleDistance = Math.Min(hostControlMinimumNozzleDistance, distance);
            if (!hostControlProgressChecked && Time.unscaledTime >= hostControlActionAt)
            {
                hostControlProgressChecked = true;
                hostControlProgressPassed = hostControlMinimumNozzleDistance <= hostControlInitialNozzleDistance - .5f;
                SrLogger.LogMessage(
                    $"[SR2MP_TEST_BOT] host-control pull-progress actor={captureActorId:X} " +
                    $"startNozzle={hostControlInitialNozzleDistance:F3} minNozzle2s={hostControlMinimumNozzleDistance:F3} " +
                    $"closed={hostControlInitialNozzleDistance - hostControlMinimumNozzleDistance:F3} " +
                    $"within2s={hostControlProgressPassed} currentNozzle={distance:F3} " +
                    $"joint={(captureVacuumable._captiveToJoint ? captureVacuumable._captiveToJoint.Pointer.ToInt64() : 0)}");
            }
            if (Time.unscaledTime >= hostControlNextTraceAt)
            {
                hostControlNextTraceAt = Time.unscaledTime + .25f;
                SrLogger.LogMessage(
                    $"[SR2MP_TEST_BOT] host-control trajectory actor={captureActorId:X} " +
                    $"nozzleDistance={distance:F3} captive={captureVacuumable.IsCaptive()} " +
                    $"pullJoint={(captureVacuumable._captiveToJoint ? captureVacuumable._captiveToJoint.Pointer.ToInt64() : 0)}");
            }
        }

        if (!targetPresent && !targetModelPresent)
        {
            var ammoAfter = CountLocalAmmo(captureIdentifiableType);
            TryStopVanillaVacuum();
            var closed = hostControlInitialNozzleDistance - hostControlMinimumNozzleDistance;
            if (!hostControlProgressChecked)
                hostControlProgressPassed = closed >= .5f &&
                                            Time.unscaledTime - hostControlStartedAt <= 2f;
            var passed = closed >= .5f && ammoAfter == hostControlAmmoBefore + 1;
            hostControlPhase = Finished;
            SrLogger.LogMessage(
                $"[SR2MP_TEST_BOT] host-control {(passed ? "PASS" : "FAIL")} actor={captureActorId:X} " +
                $"actorPresent=false modelPresent=false ammoType={captureIdentifiableType?.name} " +
                $"ammoBefore={hostControlAmmoBefore} ammoAfter={ammoAfter} " +
                $"startNozzle={hostControlInitialNozzleDistance:F3} minNozzle={hostControlMinimumNozzleDistance:F3} " +
                $"closed={closed:F3} progressWithin2s={hostControlProgressPassed} nativeRemoval=true");
            return;
        }

        if (Time.unscaledTime >= hostControlDeadline)
        {
            TryStopVanillaVacuum();
            hostControlPhase = Finished;
            SrLogger.LogError(
                $"[SR2MP_TEST_BOT] host-control FAIL timeout actor={captureActorId:X} " +
                $"actorPresent={targetPresent} modelPresent={targetModelPresent} " +
                $"startNozzle={hostControlInitialNozzleDistance:F3} minNozzle={hostControlMinimumNozzleDistance:F3}");
        }
    }

    private static void ObserveHostCaptureControl()
    {
        var actorId = announcedCaptureTargetActorId;
        if (hostObserverPassed || actorId <= 0)
            return;
        if (ActorManager.Actors.ContainsKey(actorId) ||
            GameState.TryGetIdentifiableModel(new ActorId(actorId), out _))
            return;

        hostObserverPassed = true;
        SrLogger.LogMessage(
            $"[SR2MP_TEST_BOT] host-control observer PASS actor={actorId:X} " +
            "guestActorPresent=false guestModelPresent=false joinedObserver=true");
    }

    private static float GetRequestedNozzleDistance()
    {
        var text = Environment.GetEnvironmentVariable("SR2MP_TEST_CAPTURE_NOZZLE_DISTANCE");
        return float.TryParse(text, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var requested) && float.IsFinite(requested)
            ? requested : 0f;
    }

    // ---- Tarr observe scenario (SR2MP_TEST_TARR_OBSERVE=1): read-only evidence of the Tarr tentacle on owner vs observer. ----
    private const float TarrObserveSettleSeconds = 20f;
    private const float TarrObserveDuration = 40f;
    private const float TarrObserveSampleInterval = .2f;
    private const float TarrObserveHierarchyInterval = 2f;
    private const float TarrObserveTimeout = 120f;
    private const int TarrObserveMaxHierarchyLines = 20;
    private const int TarrObserveMaxNodes = 40;
    private const int TarrObserveMaxHierarchyChars = 1500;

    private static bool tarrFailed;
    private static bool tarrDone;
    private static float tarrPeerSince = -1f;
    private static float tarrBeganAt = -1f;
    private static bool tarrFixtureAttempted;
    private static long tarrActorId = -1;
    private static long tarrTargetActorId = -1;
    private static float tarrSampleStart = -1f;
    private static float tarrNextSampleAt;
    private static float tarrNextHierarchyAt;
    private static int tarrSamples;
    private static int tarrTentacleSeen;
    private static int tarrHierarchyLines;
    private static bool tarrGoneLogged;
    private static readonly HashSet<long> tarrBaselineIds = new();

    private static string TarrSeat() => Main.Server.IsRunning ? "host" : "guest";

    private static void TarrFail(string reason)
    {
        if (tarrFailed)
            return;
        tarrFailed = true;
        SrLogger.LogError($"[SR2MP_TEST_BOT] tarr observe FAIL seat={TarrSeat()} reason={reason}");
    }

    private static bool IsTarrTypeName(string? name)
    {
        if (name == null || name.IndexOf("tarr", StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        foreach (var excluded in new[] { "gordo", "plort", "spawn", "tentacle" })
            if (name.IndexOf(excluded, StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
        return true;
    }

    private static KeyValuePair<int, IdentifiableType> FindTarrObserveType(Func<string, bool> exact, Func<string, bool> loose)
    {
        KeyValuePair<int, IdentifiableType> best = default;
        foreach (var entry in ActorManager.ActorTypes)
        {
            if (!entry.Value || entry.Value.name == null)
                continue;
            if (exact(entry.Value.name))
                return entry;
            if (!best.Value && loose(entry.Value.name))
                best = entry;
        }
        return best;
    }

    private static string TarrRelativePath(Transform node, Transform? root)
    {
        var parts = new List<string>();
        var current = node;
        var guard = 0;
        while (current && current != root && guard++ < 32)
        {
            parts.Add(current.name);
            current = current.parent;
        }
        if (current && current == root)
        {
            parts.Reverse();
            return parts.Count == 0 ? "." : string.Join("/", parts);
        }
        return "detached";
    }

    private static bool TryStageTarrFixture(GameObject player)
    {
        try
        {
            var sceneGroup = SystemContext.Instance?.SceneLoader?.CurrentSceneGroup;
            var tarrEntry = FindTarrObserveType(
                n => string.Equals(n, "Tarr", StringComparison.OrdinalIgnoreCase), IsTarrTypeName);
            var targetEntry = FindTarrObserveType(
                n => string.Equals(n, "Pink", StringComparison.OrdinalIgnoreCase),
                n => n.IndexOf("Pink", StringComparison.OrdinalIgnoreCase) >= 0 &&
                     n.IndexOf("Plort", StringComparison.OrdinalIgnoreCase) < 0 &&
                     n.IndexOf("Gordo", StringComparison.OrdinalIgnoreCase) < 0);
            if (!player || sceneGroup == null)
                throw new InvalidOperationException("player or scene group unavailable");
            if (!tarrEntry.Value)
                throw new InvalidOperationException("no Tarr type in ActorManager.ActorTypes");
            if (!targetEntry.Value)
                throw new InvalidOperationException("no Pink-like target type in ActorManager.ActorTypes");
            if (!TryFindClearCaptureTargetPosition(player, out var tarrPosition, out var placementTrace))
                throw new InvalidOperationException($"no clear ground for Tarr placement={placementTrace}");

            // Target ~2.5 m from the Tarr, on ground, along the horizontal direction toward the player (fall back to right).
            var toPlayer = player.transform.position - tarrPosition;
            toPlayer.y = 0f;
            var direction = toPlayer.sqrMagnitude > .01f ? toPlayer.normalized : Vector3.right;
            Vector3 targetPosition = Vector3.zero;
            var found = false;
            for (var turn = 0; turn < 8 && !found; turn++)
            {
                var candidate = tarrPosition + Quaternion.Euler(0f, turn * 45f, 0f) * direction * 2.5f;
                if (Physics.Raycast(candidate + Vector3.up * 5f, Vector3.down, out var hit, 12f,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) &&
                    hit.normal.y >= .72f && Mathf.Abs(hit.point.y - tarrPosition.y) < 2f)
                {
                    targetPosition = hit.point + Vector3.up * .55f;
                    found = !Physics.CheckSphere(targetPosition, .32f, Physics.DefaultRaycastLayers,
                        QueryTriggerInteraction.Ignore);
                }
            }
            if (!found)
                throw new InvalidOperationException("no clear ground for target near Tarr");

            var sceneId = NetworkSceneManager.GetPersistentID(sceneGroup);
            if (sceneId < 0)
                throw new InvalidOperationException($"invalid scene id {sceneId}");
            var highest = NetworkActorManager.GetHighestActorIdInRange(0, GlobalVariables.ActorIdOffset);
            var ids = new long[2];
            var spawns = new[] { (tarrEntry, tarrPosition), (targetEntry, targetPosition) };
            for (var index = 0; index < 2; index++)
            {
                var value = Math.Max(highest, hostCaptureLastActorId) + 10;
                if (value <= 0 || value >= GlobalVariables.ActorIdOffset)
                    throw new InvalidOperationException($"invalid actor id {value}");
                var actorId = new ActorId(value);
                var (entry, position) = spawns[index];
                if (!ActorManager.TrySpawnNetworkActor(actorId, position, Quaternion.identity,
                        entry.Key, sceneId, out var model) || model == null)
                    throw new InvalidOperationException($"game actor creation failed actor={value:X} type={entry.Value.name}");
                var actorObject = model.GetGameObject();
                var networkActor = actorObject ? actorObject.GetComponent<NetworkActor>() : null;
                if (!actorObject || !networkActor)
                    throw new InvalidOperationException($"spawned actor has no network component actor={value:X}");
                networkActor.CurrentOwnerId = LocalID;
                networkActor.LocallyOwned = true;
                networkActor.CachedLocallyOwned = true;
                networkActor.previousPosition = position;
                networkActor.nextPosition = position;
                ActorManager.OwnershipByActorId[value] = LocalID;
                Main.SendToAllOrServer(new ActorSpawnPacket
                {
                    ActorId = actorId, Position = position, Rotation = Quaternion.identity,
                    ActorType = entry.Key, SceneGroup = (byte)sceneId,
                    SpawnType = (byte)ActorSpawnType.Actor,
                    MaterialIndex = (byte)SprinkleMaterialType.none, OwnerId = LocalID
                });
                ids[index] = value;
                hostCaptureLastActorId = value;
            }
            tarrActorId = ids[0];
            tarrTargetActorId = ids[1];
            SrLogger.LogMessage(
                $"[SR2MP_TEST_BOT] tarr fixture PASS tarr={ids[0]:X} target={ids[1]:X} " +
                $"tarrType={tarrEntry.Value.name} targetType={targetEntry.Value.name}");
            return true;
        }
        catch (Exception exception)
        {
            SrLogger.LogError($"[SR2MP_TEST_BOT] tarr fixture FAIL reason={exception.GetType().Name}: {exception.Message}");
            TarrFail("fixture failed");
            return false;
        }
    }

    /// <summary>Guest-side discovery of the host-spawned Tarr: first new actor with a Tarr-like type.</summary>
    private static void ScanForObservedTarr(bool baselineOnly = false)
    {
        foreach (var actorId in ActorManager.Actors.Keys.ToArray())
        {
            if (tarrBaselineIds.Contains(actorId) ||
                !ActorManager.Actors.TryGetValue(actorId, out var model) || model == null)
                continue;
            var go = model.GetGameObject();
            if (!go)
                continue;
            tarrBaselineIds.Add(actorId);
            if (!baselineOnly && IsTarrTypeName(go.GetComponent<Identifiable>()?.identType?.name))
            {
                tarrActorId = actorId;
                SrLogger.LogMessage($"[SR2MP_TEST_BOT] tarr observer saw tarr={actorId:X}");
                return;
            }
        }
    }

    private static string TarrObjectToken(GameObject? value)
    {
        if (!value)
            return "none";
        return $"{value!.name}#{value.GetInstanceID()}";
    }

    private static string TarrCompactSize(Vector3 value)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return $"{value.x.ToString("F2", inv)}x{value.y.ToString("F2", inv)}x{value.z.ToString("F2", inv)}";
    }

    private static void LogTarrHierarchy(string seat, GameObject tarrObject, GameObject? tentacle)
    {
        if (tarrHierarchyLines >= TarrObserveMaxHierarchyLines)
            return;
        tarrHierarchyLines++;
        var nodes = new List<string>();
        var renderers = new List<string>();
        var roots = new List<Transform>();
        var baseTransform = tentacle ? tentacle!.transform : tarrObject.transform;
        if (tentacle)
            roots.Add(tentacle!.transform);
        else
            foreach (var child in tarrObject.GetComponentsInChildren<Transform>(true))
                if (child != tarrObject.transform && child.name.IndexOf("entacle", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    roots.Count < TarrObserveMaxNodes)
                    roots.Add(child);

        var seen = new HashSet<int>();
        foreach (var root in roots)
        {
            foreach (var node in root.GetComponentsInChildren<Transform>(true))
            {
                if (nodes.Count >= TarrObserveMaxNodes || !seen.Add(node.GetInstanceID()))
                    continue;
                var components = new List<string>();
                foreach (var component in node.gameObject.GetComponents<Component>())
                    components.Add(component ? component.GetType().Name : "null");
                nodes.Add($"{TarrRelativePath(node, baseTransform)}:{string.Join("+", components)}:{(node.gameObject.activeInHierarchy ? "1" : "0")}");
                var renderer = node.GetComponent<Renderer>();
                if (renderer && renderers.Count < TarrObserveMaxNodes)
                    renderers.Add($"{TarrRelativePath(node, baseTransform)}:{renderer.GetType().Name}:" +
                                  $"{(renderer.enabled ? "1" : "0")}:{TarrCompactSize(renderer.bounds.size)}");
            }
        }
        var line = $"[SR2MP_TEST_BOT] tarr tentacle-hierarchy seat={seat} base={(tentacle ? "activeTentacle" : "tarrChildren")} " +
                   $"nodes=[{string.Join(",", nodes)}] renderers=[{string.Join(",", renderers)}]";
        if (line.Length > TarrObserveMaxHierarchyChars)
            line = line.Substring(0, TarrObserveMaxHierarchyChars) + "...";
        SrLogger.LogMessage(line);
    }

    private static void SampleTarr(string seat, GameObject tarrObject)
    {
        // An ordinary Tarr grapples through the TentacleGrapple slime sub-behaviour;
        // TarrGrowthTentacleGrapple belongs to the Labyrinth growth variant.
        TentacleGrapple? plain = null;
        TarrGrowthTentacleGrapple? growth = null;
        try { plain = tarrObject.GetComponentInChildren<TentacleGrapple>(true); } catch { }
        if (!plain)
            try { growth = tarrObject.GetComponentInChildren<TarrGrowthTentacleGrapple>(true); } catch { }
        var grapple = (UnityEngine.Object?)(plain ? plain : growth);
        var network = tarrObject.GetComponent<NetworkActor>();
        var targetText = "none";
        var grapplingText = "n/a";
        GameObject? tentacle = null;
        if (grapple)
        {
            try
            {
                var target = plain ? plain!._target : growth!._target;
                if (target)
                {
                    var targetActor = target.GetComponentInParent<NetworkActor>();
                    targetText = targetActor ? targetActor.ActorId.Value.ToString("X")
                        : target.GetComponentInParent<SRCharacterController>() ? "player" : "unmapped";
                }
            }
            catch (Exception exception) { targetText = $"error:{exception.GetType().Name}"; }
            try { tentacle = plain ? plain!._activeTentacle : growth!._activeTentacle; } catch { }
            if (plain)
                try { grapplingText = plain!._grappling ? "true" : "false"; } catch { grapplingText = "error"; }
        }
        var tentacleActive = tentacle && tentacle!.activeInHierarchy;
        var parentText = tentacle ? TarrRelativePath(tentacle!.transform, tarrObject.transform) : "none";
        if (tentacle)
            tarrTentacleSeen++;
        var position = tarrObject.transform.position;
        SrLogger.LogMessage(
            $"[SR2MP_TEST_BOT] tarr sample seat={seat} t={FmtThrowSeconds(Time.unscaledTime - tarrSampleStart)} " +
            $"tarr={tarrActorId:X} owner={(network ? network.CurrentOwnerId : "none")} pos={FmtThrowVector(position)} " +
            $"grapple={(plain ? "plain" : growth ? "growth" : "missing")} grappling={grapplingText} " +
            $"target={targetText} activeTentacle={TarrObjectToken(tentacle)} " +
            $"tentacleActive={(tentacleActive ? "true" : "false")} tentacleParent={parentText}");
        if (Time.unscaledTime >= tarrNextHierarchyAt)
        {
            tarrNextHierarchyAt = Time.unscaledTime + TarrObserveHierarchyInterval;
            try
            {
                LogTarrHierarchy(seat, tarrObject, tentacle);
                var hooks = UnityEngine.Object.FindObjectsOfType<TentacleHook>();
                SrLogger.LogMessage($"[SR2MP_TEST_BOT] tarr scene-hooks seat={seat} count={hooks.Length} " +
                    $"hooks=[{string.Join(",", hooks.Take(4).Select(h => $"{h.name}#{h.GetInstanceID()}:{(h.gameObject.activeInHierarchy ? 1 : 0)}"))}]");
            }
            catch (Exception exception)
            {
                SrLogger.LogWarning($"[SR2MP_TEST_BOT] tarr tentacle-hierarchy seat={seat} error={exception.GetType().Name}");
            }
        }
    }

    private static void PumpTarrObserveScenario(GameObject localPlayer)
    {
        if (tarrFailed || tarrDone)
            return;
        var seat = TarrSeat();
        var host = seat == "host";
        var peerPresent = TryGetOtherPlayerPosition(out _);
        if (host ? Main.Server.GetClientCount < 1 : !WaitForStableJoinedWorld())
            peerPresent = false;
        if (!peerPresent)
        {
            tarrPeerSince = -1f;
            return;
        }
        if (tarrPeerSince < 0f)
        {
            tarrPeerSince = Time.unscaledTime;
            SrLogger.LogMessage($"[SR2MP_TEST_BOT] tarr scenario peer present seat={seat}");
        }
        if (host && Time.unscaledTime < tarrPeerSince + TarrObserveSettleSeconds)
            return;

        if (tarrBeganAt < 0f)
        {
            tarrBeganAt = Time.unscaledTime;
            if (!host)
            {
                ScanForObservedTarr(true); // baseline: only actors appearing later are candidates
                SrLogger.LogMessage($"[SR2MP_TEST_BOT] tarr scenario began seat={seat} knownActors={tarrBaselineIds.Count}");
            }
        }
        if (Time.unscaledTime - tarrBeganAt > TarrObserveTimeout)
        {
            TarrFail("scenario timed out");
            return;
        }

        if (host && !tarrFixtureAttempted)
        {
            tarrFixtureAttempted = true;
            if (!TryStageTarrFixture(localPlayer))
                return;
        }
        if (!host && tarrActorId < 0)
            ScanForObservedTarr();
        if (tarrActorId < 0)
            return;

        var tarrObject = ActorManager.Actors.TryGetValue(tarrActorId, out var model) && model != null
            ? model.GetGameObject()
            : null;
        if (!tarrObject)
        {
            if (tarrSampleStart >= 0f && !tarrGoneLogged)
            {
                tarrGoneLogged = true;
                SrLogger.LogWarning($"[SR2MP_TEST_BOT] tarr sample seat={seat} tarr={tarrActorId:X} actor object is gone");
            }
            return;
        }
        if (tarrSampleStart < 0f)
        {
            tarrSampleStart = Time.unscaledTime;
            tarrNextSampleAt = tarrSampleStart;
            tarrNextHierarchyAt = tarrSampleStart;
        }
        if (Time.unscaledTime - tarrSampleStart > TarrObserveDuration)
        {
            tarrDone = true;
            SrLogger.LogMessage(
                $"[SR2MP_TEST_BOT] tarr observe DONE seat={seat} samples={tarrSamples} tentacleSeenSamples={tarrTentacleSeen}");
            return;
        }
        if (Time.unscaledTime < tarrNextSampleAt)
            return;
        tarrNextSampleAt += TarrObserveSampleInterval;
        if (tarrNextSampleAt < Time.unscaledTime)
            tarrNextSampleAt = Time.unscaledTime + TarrObserveSampleInterval;
        tarrSamples++;
        try { SampleTarr(seat, tarrObject); }
        catch (Exception exception)
        {
            SrLogger.LogWarning($"[SR2MP_TEST_BOT] tarr sample seat={seat} error={exception.GetType().Name}: {exception.Message}");
        }
    }

    // ---- Tarr multiply scenario (SR2MP_TEST_TARR_MULTIPLY=1): census of every scene Tarr on both seats while Tarrs multiply. ----
    private const float TarrMultiplySettleSeconds = 8f;
    private const float TarrMultiplyDuration = 90f;
    private const float TarrMultiplyCensusInterval = 2f;
    private const float TarrMultiplyStableSeconds = 10f;
    private const int TarrMultiplyMaxEntries = 40;
    private const int TarrMultiplyPinkCount = 6;

    private static bool tmDone;
    private static bool tmFixtureAttempted;
    private static float tmPeerSince = -1f;
    private static float tmBeganAt = -1f;
    private static float tmNextCensusAt;
    private static int tmLastCount = -1;
    private static float tmCountSince;
    private static int tmFinalCount;

    private static bool TarrMultiplySpawnsHere()
    {
        var spawnSeat = Environment.GetEnvironmentVariable("SR2MP_TEST_TARR_SPAWN_SEAT") == "host" ? "host" : "guest";
        return spawnSeat == TarrSeat();
    }

    private static bool TryStageTarrMultiplyFixture(GameObject player)
    {
        try
        {
            var sceneGroup = SystemContext.Instance?.SceneLoader?.CurrentSceneGroup;
            var tarrEntry = FindTarrObserveType(
                n => string.Equals(n, "Tarr", StringComparison.OrdinalIgnoreCase), IsTarrTypeName);
            var pinkEntry = FindTarrObserveType(
                n => string.Equals(n, "Pink", StringComparison.OrdinalIgnoreCase),
                n => n.IndexOf("Pink", StringComparison.OrdinalIgnoreCase) >= 0 &&
                     n.IndexOf("Plort", StringComparison.OrdinalIgnoreCase) < 0 &&
                     n.IndexOf("Gordo", StringComparison.OrdinalIgnoreCase) < 0);
            if (!player || sceneGroup == null)
                throw new InvalidOperationException("player or scene group unavailable");
            if (!tarrEntry.Value)
                throw new InvalidOperationException("no Tarr type in ActorManager.ActorTypes");
            if (!pinkEntry.Value)
                throw new InvalidOperationException("no Pink-like type in ActorManager.ActorTypes");
            if (!TryFindClearCaptureTargetPosition(player, out var tarrPosition, out var placementTrace))
                throw new InvalidOperationException($"no clear ground for Tarr placement={placementTrace}");

            // Pinks in a ring ~1.5 m around the Tarr, each on same-floor ground with clear space.
            var spawns = new List<(KeyValuePair<int, IdentifiableType> entry, Vector3 position)> { (tarrEntry, tarrPosition) };
            for (var index = 0; index < TarrMultiplyPinkCount; index++)
            {
                var found = false;
                for (var attempt = 0; attempt < 6 && !found; attempt++)
                {
                    var angle = (index * 60f + attempt * 20f) * Mathf.Deg2Rad;
                    var candidate = tarrPosition + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * (1.5f + attempt * .25f);
                    if (!Physics.Raycast(candidate + Vector3.up * 5f, Vector3.down, out var hit, 12f,
                            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) ||
                        hit.normal.y < .72f || Mathf.Abs(hit.point.y - tarrPosition.y) > 2f)
                        continue;
                    var pinkPosition = hit.point + Vector3.up * .55f;
                    if (Physics.CheckSphere(pinkPosition, .32f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                        continue;
                    spawns.Add((pinkEntry, pinkPosition));
                    found = true;
                }
                if (!found)
                    throw new InvalidOperationException($"no clear ground for pink {index} near Tarr");
            }

            var sceneId = NetworkSceneManager.GetPersistentID(sceneGroup);
            if (sceneId < 0)
                throw new InvalidOperationException($"invalid scene id {sceneId}");
            var highest = NetworkActorManager.GetHighestActorIdInRange(0, GlobalVariables.ActorIdOffset);
            var ids = new List<long>();
            foreach (var (entry, position) in spawns)
            {
                var value = Math.Max(highest, hostCaptureLastActorId) + 10;
                if (value <= 0 || value >= GlobalVariables.ActorIdOffset)
                    throw new InvalidOperationException($"invalid actor id {value}");
                var actorId = new ActorId(value);
                if (!ActorManager.TrySpawnNetworkActor(actorId, position, Quaternion.identity,
                        entry.Key, sceneId, out var model) || model == null)
                    throw new InvalidOperationException($"game actor creation failed actor={value:X} type={entry.Value.name}");
                var actorObject = model.GetGameObject();
                var networkActor = actorObject ? actorObject.GetComponent<NetworkActor>() : null;
                if (!actorObject || !networkActor)
                    throw new InvalidOperationException($"spawned actor has no network component actor={value:X}");
                networkActor.CurrentOwnerId = LocalID;
                networkActor.LocallyOwned = true;
                networkActor.CachedLocallyOwned = true;
                networkActor.previousPosition = position;
                networkActor.nextPosition = position;
                ActorManager.OwnershipByActorId[value] = LocalID;
                Main.SendToAllOrServer(new ActorSpawnPacket
                {
                    ActorId = actorId, Position = position, Rotation = Quaternion.identity,
                    ActorType = entry.Key, SceneGroup = (byte)sceneId,
                    SpawnType = (byte)ActorSpawnType.Actor,
                    MaterialIndex = (byte)SprinkleMaterialType.none, OwnerId = LocalID
                });
                ids.Add(value);
                hostCaptureLastActorId = value;
            }
            SrLogger.LogMessage(
                $"[SR2MP_TEST_BOT] tarr multiply fixture PASS seat={TarrSeat()} tarr={ids[0]:X} " +
                $"pinks={string.Join(",", ids.Skip(1).Select(id => id.ToString("X")))}");
            return true;
        }
        catch (Exception exception)
        {
            SrLogger.LogError($"[SR2MP_TEST_BOT] tarr multiply fixture FAIL reason={exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Logs one census line of every Tarr GameObject in the scene, including ones missing from
    /// ActorManager or without a NetworkActor (scans IdentifiableActor components, not ActorManager.Actors).
    /// Returns the Tarr count.
    /// </summary>
    private static int LogTarrCensus(string seat, float elapsed)
    {
        var entries = new List<(long id, string text)>();
        var count = 0;
        foreach (var actor in UnityEngine.Object.FindObjectsOfType<IdentifiableActor>())
        {
            if (!actor || !IsTarrTypeName(actor.GetComponent<Identifiable>()?.identType?.name))
                continue;
            count++;
            var id = actor._model != null ? actor._model.actorId.Value : 0L;
            var network = actor.GetComponent<NetworkActor>();
            var registered = ActorManager.Actors.TryGetValue(id, out var registeredModel) &&
                             registeredModel != null && registeredModel.GetGameObject() == actor.gameObject;
            var position = actor.transform.position;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            entries.Add((id, $"{id:X}:net={(network ? "true" : "false")}:" +
                             $"owner={(network ? network.CurrentOwnerId.ToString() : "none")}:" +
                             $"locallyOwned={(network && network.LocallyOwned ? "true" : "false")}:" +
                             $"registered={(registered ? "true" : "false")}:" +
                             $"pos=({position.x.ToString("F1", inv)},{position.y.ToString("F1", inv)},{position.z.ToString("F1", inv)})"));
        }
        entries.Sort((a, b) => a.id.CompareTo(b.id));
        SrLogger.LogMessage(
            $"[SR2MP_TEST_BOT] tarr census seat={seat} t={FmtThrowSeconds(elapsed)} count={count} " +
            $"entries=[{string.Join(";", entries.Take(TarrMultiplyMaxEntries).Select(e => e.text))}]");
        return count;
    }

    private static void PumpTarrMultiplyScenario(GameObject localPlayer)
    {
        if (tmDone)
            return;
        var seat = TarrSeat();
        var host = seat == "host";
        var peerPresent = TryGetOtherPlayerPosition(out _);
        if (host ? Main.Server.GetClientCount < 1 : !WaitForStableJoinedWorld())
            peerPresent = false;
        if (!peerPresent)
        {
            tmPeerSince = -1f;
            return;
        }
        if (tmPeerSince < 0f)
        {
            tmPeerSince = Time.unscaledTime;
            SrLogger.LogMessage($"[SR2MP_TEST_BOT] tarr multiply peer present seat={seat}");
        }
        if (Time.unscaledTime < tmPeerSince + TarrMultiplySettleSeconds)
            return;

        var spawner = TarrMultiplySpawnsHere();
        if (tmBeganAt < 0f)
        {
            tmBeganAt = Time.unscaledTime;
            tmNextCensusAt = tmBeganAt;
            SrLogger.LogMessage($"[SR2MP_TEST_BOT] tarr multiply began seat={seat} spawner={spawner}");
        }
        if (spawner && !tmFixtureAttempted)
        {
            tmFixtureAttempted = true;
            if (!TryStageTarrMultiplyFixture(localPlayer))
            {
                tmDone = true;
                return;
            }
        }
        if (Time.unscaledTime < tmNextCensusAt)
            return;
        tmNextCensusAt += TarrMultiplyCensusInterval;
        if (tmNextCensusAt < Time.unscaledTime)
            tmNextCensusAt = Time.unscaledTime + TarrMultiplyCensusInterval;

        var elapsed = Time.unscaledTime - tmBeganAt;
        int count;
        try { count = LogTarrCensus(seat, elapsed); }
        catch (Exception exception)
        {
            SrLogger.LogWarning($"[SR2MP_TEST_BOT] tarr census seat={seat} error={exception.GetType().Name}: {exception.Message}");
            return;
        }
        tmFinalCount = count;
        if (count != tmLastCount)
        {
            tmLastCount = count;
            tmCountSince = Time.unscaledTime;
        }
        // Each seat stops when its own count has been >=3 and stable for 10 s, or at the 90 s cap.
        if (elapsed >= TarrMultiplyDuration || (count >= 3 && Time.unscaledTime - tmCountSince >= TarrMultiplyStableSeconds))
        {
            tmDone = true;
            SrLogger.LogMessage($"[SR2MP_TEST_BOT] tarr multiply DONE seat={seat} finalCount={tmFinalCount}");
        }
    }

    // ---- Throw scenario (SR2MP_TEST_THROW=1): compares a thrown item's motion on the owner and observer. ----
    private const int ThrowCount = 3;
    private const int ThrowMaxSamples = 60;
    private const float ThrowSampleInterval = .1f;
    private const float ThrowSampleDuration = 5f;
    private const float ThrowSpacingSeconds = 6f;

    private sealed class ThrowTrack
    {
        public long ActorId;
        public int Index;
        public float StartedAt;
        public float NextSampleAt;
        public int Samples;
        public bool Done;
        public bool GoneLogged;
    }

    private static bool throwFailed;
    private static bool throwDone;
    private static bool throwObserverDone;
    private static float throwPeerSince = -1f;
    private static float throwBeganAt = -1f;
    private static bool throwSetupDone;
    private static string throwCarrotName = "<none>";
    private static IdentifiableType? throwCarrotType;
    private static int throwIndex;
    private static float throwNextActionAt;
    private static float throwFiredAt = -1f;
    private static bool throwCancelled;
    private static float throwDeadline;
    private static VacuumItem? throwVacuumItem;
    private static ThrowTrack? throwOwnerTrack;
    private static readonly HashSet<long> throwKnownActorIds = new();
    private static readonly List<long> throwOwnerActors = new();
    private static readonly List<ThrowTrack> throwObserverTracks = new();

    private static string FmtThrowVector(Vector3 value)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return $"({value.x.ToString("F3", inv)}, {value.y.ToString("F3", inv)}, {value.z.ToString("F3", inv)})";
    }

    private static string FmtThrowSeconds(float value)
        => value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    private static void ThrowFail(string reason, bool observer)
    {
        if (throwFailed)
            return;
        throwFailed = true;
        try { throwVacuumItem?._shoot?.Cancel(); } catch { }
        SrLogger.LogError(observer
            ? $"[SR2MP_TEST_BOT] throw observer FAIL reason={reason}"
            : $"[SR2MP_TEST_BOT] throw suite FAIL reason={reason}");
    }

    private static IdentifiableType? FindThrowCarrotType()
    {
        IdentifiableType? best = null;
        foreach (var candidate in ActorManager.ActorTypes.Values)
        {
            if (!candidate || candidate.name == null ||
                candidate.name.IndexOf("Carrot", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (string.Equals(candidate.name, "Carrot", StringComparison.OrdinalIgnoreCase))
                return candidate;
            best ??= candidate;
        }
        return best;
    }

    private static bool TryGetOtherPlayerPosition(out Vector3 position)
    {
        var remote = PlayerObjects.FirstOrDefault(entry =>
            !string.Equals(entry.Key, LocalID, StringComparison.Ordinal) && entry.Value);
        position = remote.Value ? remote.Value.transform.position : Vector3.zero;
        return remote.Value;
    }

    private static bool IsThrowCarrot(GameObject actorObject)
    {
        var type = actorObject.GetComponent<Identifiable>()?.identType;
        return type && type!.name != null &&
               type.name.IndexOf("Carrot", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Evaluates each new actor key once; returns carrot actors with their local ownership.</summary>
    private static List<(long id, GameObject go, bool locallyOwned)> ScanNewThrowCarrots()
    {
        var found = new List<(long, GameObject, bool)>();
        foreach (var actorId in ActorManager.Actors.Keys.ToArray())
        {
            if (throwKnownActorIds.Contains(actorId))
                continue;
            if (!ActorManager.Actors.TryGetValue(actorId, out var model) || model == null)
                continue;
            var go = model.GetGameObject();
            if (!go)
                continue; // retry next frame until the game object exists
            throwKnownActorIds.Add(actorId);
            if (!IsThrowCarrot(go))
                continue;
            var network = go.GetComponent<NetworkActor>();
            found.Add((actorId, go, network && network.LocallyOwned));
        }
        return found;
    }

    private static void LogThrowSample(string seat, ThrowTrack track, GameObject actorObject, Vector3 other)
    {
        var body = actorObject.GetComponent<Rigidbody>();
        var velocity = body ? body.velocity : Vector3.zero;
        var captureActive = NetworkActorCaptureManager.TestHasPendingClientCapture(track.ActorId) ||
                            NetworkActorCaptureManager.TestHasMatchingApprovedPullJoint(track.ActorId);
        SrLogger.LogMessage(
            $"[SR2MP_TEST_BOT] throw sample seat={seat} throw={track.Index} actor={track.ActorId:X} " +
            $"t={FmtThrowSeconds(Time.unscaledTime - track.StartedAt)} pos={FmtThrowVector(actorObject.transform.position)} " +
            $"vel={FmtThrowVector(velocity)} other={FmtThrowVector(other)} captureActive={(captureActive ? "true" : "false")}");
    }

    /// <summary>Returns true when the sample window for this track is complete.</summary>
    private static bool PumpThrowTrack(string seat, ThrowTrack track, GameObject localPlayer)
    {
        if (track.Done)
            return true;
        if (Time.unscaledTime - track.StartedAt > ThrowSampleDuration || track.Samples >= ThrowMaxSamples)
        {
            track.Done = true;
            return true;
        }
        if (Time.unscaledTime < track.NextSampleAt)
            return false;
        track.NextSampleAt += ThrowSampleInterval;
        if (track.NextSampleAt < Time.unscaledTime)
            track.NextSampleAt = Time.unscaledTime + ThrowSampleInterval;

        var actorObject = ActorManager.Actors.TryGetValue(track.ActorId, out var model) && model != null
            ? model.GetGameObject()
            : null;
        if (!actorObject)
        {
            if (!track.GoneLogged)
            {
                track.GoneLogged = true;
                SrLogger.LogWarning($"[SR2MP_TEST_BOT] throw sample seat={seat} throw={track.Index} " +
                                    $"actor={track.ActorId:X} actor object is gone");
            }
            return false;
        }

        Vector3 other;
        if (seat == "owner")
        {
            if (!TryGetOtherPlayerPosition(out other))
                other = Vector3.zero;
        }
        else
        {
            other = localPlayer.transform.position;
        }
        track.Samples++;
        LogThrowSample(seat, track, actorObject, other);
        return false;
    }

    private static bool ThrowSeatIsActing()
    {
        var seat = Environment.GetEnvironmentVariable("SR2MP_TEST_THROW_SEAT");
        return string.Equals(seat, Main.Server.IsRunning ? "host" : "guest", StringComparison.OrdinalIgnoreCase);
    }

    private static void PumpThrowScenario(GameObject localPlayer)
    {
        if (throwFailed || (throwDone && throwObserverDone))
            return;
        var acting = ThrowSeatIsActing();
        if (acting ? throwDone : throwObserverDone)
            return;

        // Gate: host needs a connected guest with its remote player object; guest needs a joined world.
        var peerPresent = TryGetOtherPlayerPosition(out var otherPosition);
        if (Main.Server.IsRunning ? Main.Server.GetClientCount < 1 : !WaitForStableJoinedWorld())
            peerPresent = false;
        if (!peerPresent)
        {
            throwPeerSince = -1f;
            return;
        }
        if (throwPeerSince < 0f)
        {
            throwPeerSince = Time.unscaledTime;
            SrLogger.LogMessage($"[SR2MP_TEST_BOT] throw scenario peer present acting={acting} " +
                                $"seat={Environment.GetEnvironmentVariable("SR2MP_TEST_THROW_SEAT")}");
        }
        // The observer must baseline before the first throw: watch at once, throw late.
        if (Time.unscaledTime < throwPeerSince + (acting ? 30f : 0f))
            return;

        if (throwBeganAt < 0f)
        {
            throwBeganAt = Time.unscaledTime;
            throwDeadline = Time.unscaledTime + (acting ? 150f : 90f);
            // Baseline every existing actor so only actors that appear after this point are considered.
            ScanNewThrowCarrots();
            SrLogger.LogMessage($"[SR2MP_TEST_BOT] throw scenario began acting={acting} knownActors={throwKnownActorIds.Count}");
        }
        if (Time.unscaledTime > throwDeadline)
        {
            ThrowFail(acting ? "scenario timed out" : "timed out waiting for 3 observed carrots", !acting);
            return;
        }

        if (!acting)
            PumpThrowObserver(localPlayer);
        else
            PumpThrowActing(localPlayer, otherPosition);
    }

    private static void PumpThrowObserver(GameObject localPlayer)
    {
        foreach (var (id, _, locallyOwned) in ScanNewThrowCarrots())
        {
            if (locallyOwned || throwObserverTracks.Count >= ThrowCount)
                continue;
            var track = new ThrowTrack
            {
                ActorId = id,
                Index = throwObserverTracks.Count + 1,
                StartedAt = Time.unscaledTime,
                NextSampleAt = Time.unscaledTime,
            };
            throwObserverTracks.Add(track);
            SrLogger.LogMessage($"[SR2MP_TEST_BOT] throw observer saw spawn throw={track.Index} actor={id:X}");
        }

        var finished = 0;
        foreach (var track in throwObserverTracks)
            if (PumpThrowTrack("observer", track, localPlayer))
                finished++;
        if (finished >= ThrowCount && !throwObserverDone)
        {
            throwObserverDone = true;
            SrLogger.LogMessage(
                $"[SR2MP_TEST_BOT] throw observer DONE actors={string.Join(",", throwObserverTracks.Select(t => t.ActorId.ToString("X")))}");
        }
    }

    private static bool TrySetupThrowAmmo(out string failure)
    {
        failure = string.Empty;
        throwCarrotType = FindThrowCarrotType();
        if (!throwCarrotType)
        {
            failure = "no IdentifiableType with a name containing Carrot in ActorManager.ActorTypes";
            return false;
        }
        throwCarrotName = throwCarrotType!.name;
        SrLogger.LogMessage($"[SR2MP_TEST_BOT] throw carrot type chosen name={throwCarrotName}");

        var ammo = SceneContext.Instance?.PlayerState?.Ammo;
        if (ammo?.Slots == null)
        {
            failure = "player ammo is unavailable";
            return false;
        }

        // Prefer an empty unlocked slot, otherwise a slot already holding carrots.
        var slotIndex = -1;
        for (var index = 0; index < ammo.Slots.Count && slotIndex < 0; index++)
        {
            var slot = ammo.Slots[index];
            if (slot != null && slot.IsUnlocked && (!slot.Id || slot.Count == 0) && slot.MaxCount >= ThrowCount)
                slotIndex = index;
        }
        for (var index = 0; index < ammo.Slots.Count && slotIndex < 0; index++)
        {
            var slot = ammo.Slots[index];
            if (slot != null && slot.IsUnlocked && slot.Id == throwCarrotType && slot.MaxCount >= ThrowCount)
                slotIndex = index;
        }
        if (slotIndex < 0)
        {
            failure = $"no usable unlocked ammo slot [{DescribeAmmoSlots(ammo)}]";
            return false;
        }

        var wasHandlingPacket = HandlingPacket;
        try
        {
            HandlingPacket = true;
            var slot = ammo.Slots[slotIndex];
            var metadata = new AmmoSlot.AmmoMetadata(throwCarrotType, slot.Emotions, slot.Radiant);
            var needed = ThrowCount - (slot.Id == throwCarrotType ? slot.Count : 0);
            if (needed > 0)
            {
                if (!ammo.CouldAddToSlot(metadata, slotIndex, false))
                {
                    failure = $"CouldAddToSlot rejected carrot slot={slotIndex}";
                    return false;
                }
                var added = ammo.MaybeAddToSpecificSlot(metadata, slotIndex, needed, false);
                SrLogger.LogMessage($"[SR2MP_TEST_BOT] throw ammo seeded slot={slotIndex} added={added} " +
                                    $"count={slot.Count}/{slot.MaxCount} method=MaybeAddToSpecificSlot handlingPacket=true");
            }
            if (slot.Count < ThrowCount || slot.Id != throwCarrotType)
            {
                failure = $"slot {slotIndex} holds {slot.Count} of the carrot after seeding [{DescribeAmmoSlots(ammo)}]";
                return false;
            }
            ammo.SetSelectedSlot(slotIndex);
        }
        catch (Exception exception)
        {
            failure = $"ammo seeding threw {exception.GetType().Name}: {exception.Message}";
            return false;
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        if (ammo.GetSelectedId() != throwCarrotType)
        {
            failure = $"selected slot is not the carrot after SetSelectedSlot({slotIndex}) [{DescribeAmmoSlots(ammo)}]";
            return false;
        }
        SrLogger.LogMessage($"[SR2MP_TEST_BOT] throw slot selected index={slotIndex} item={throwCarrotName} [{DescribeAmmoSlots(ammo)}]");
        return true;
    }

    private static void AimThrow(GameObject localPlayer, Vector3 other, bool log)
    {
        var from = localPlayer.transform.position;
        var toOther = other - from;
        toOther.y = 0f;
        if (toOther.sqrMagnitude < .01f)
            toOther = localPlayer.transform.forward;
        toOther.Normalize();
        var horizontal = new Vector3(-toOther.z, 0f, toOther.x);
        const float pitch = 15f * Mathf.Deg2Rad;
        var direction = horizontal * Mathf.Cos(pitch) + Vector3.up * Mathf.Sin(pitch);
        localPlayer.transform.rotation = Quaternion.LookRotation(horizontal, Vector3.up);
        var controller = localPlayer.GetComponent<SRCharacterController>();
        var cameraPosition = controller && controller._cameraController != null
            ? controller._cameraController.transform.position
            : from + Vector3.up * 1.5f;
        AimCameraAt(localPlayer, cameraPosition + direction * 10f);
        if (log)
            SrLogger.LogMessage($"[SR2MP_TEST_BOT] throw aim throw={throwIndex + 1} thrower={FmtThrowVector(from)} " +
                                $"other={FmtThrowVector(other)} direction={FmtThrowVector(direction)}");
    }

    private static void PumpThrowActing(GameObject localPlayer, Vector3 other)
    {
        if (!throwSetupDone)
        {
            if (!TrySetupThrowAmmo(out var failure))
            {
                ThrowFail(failure, false);
                return;
            }
            throwVacuumItem = localPlayer.GetComponent<PlayerItemController>()?._vacuumItem;
            if (!throwVacuumItem || throwVacuumItem!._shoot == null)
            {
                ThrowFail("native vacuum item or shoot input is unavailable", false);
                return;
            }
            throwSetupDone = true;
            throwNextActionAt = Time.unscaledTime + 1f;
            return;
        }

        // Keep camera aim latched while waiting for a throw to start and while holding it.
        if (throwFiredAt >= 0f || (throwOwnerTrack == null && Time.unscaledTime >= throwNextActionAt - .5f))
            AimThrow(localPlayer, other, false);

        if (throwOwnerTrack != null)
        {
            if (!throwCancelled && Time.unscaledTime >= throwFiredAt + .15f)
            {
                throwCancelled = true;
                try { throwVacuumItem!._shoot.Cancel(); } catch (Exception exception)
                {
                    ThrowFail($"shoot cancel threw {exception.GetType().Name}", false);
                    return;
                }
            }
            if (PumpThrowTrack("owner", throwOwnerTrack, localPlayer))
            {
                throwOwnerTrack = null;
                throwFiredAt = -1f;
                if (throwIndex >= ThrowCount)
                {
                    throwDone = true;
                    SrLogger.LogMessage(
                        $"[SR2MP_TEST_BOT] throw suite DONE throws={ThrowCount} " +
                        $"actors={string.Join(",", throwOwnerActors.Select(id => id.ToString("X")))} carrotType={throwCarrotName}");
                }
            }
            return;
        }

        if (throwFiredAt >= 0f)
        {
            // Fired; identify the new locally-owned carrot actor.
            foreach (var (id, _, locallyOwned) in ScanNewThrowCarrots())
            {
                if (!locallyOwned || throwOwnerTrack != null)
                    continue;
                throwOwnerActors.Add(id);
                throwOwnerTrack = new ThrowTrack
                {
                    ActorId = id,
                    Index = throwIndex,
                    StartedAt = Time.unscaledTime,
                    NextSampleAt = Time.unscaledTime,
                };
                throwNextActionAt = Time.unscaledTime + ThrowSpacingSeconds;
                SrLogger.LogMessage($"[SR2MP_TEST_BOT] throw spawned throw={throwIndex} actor={id:X}");
            }
            if (throwOwnerTrack == null && Time.unscaledTime > throwFiredAt + 3f)
                ThrowFail($"no locally owned carrot actor appeared after throw {throwIndex}", false);
            return;
        }

        if (throwIndex >= ThrowCount || Time.unscaledTime < throwNextActionAt)
            return;

        // Fire through the native shoot input only.
        AimThrow(localPlayer, other, true);
        throwIndex++;
        throwCancelled = false;
        try
        {
            var inputData = default(Il2CppMonomiPark.SlimeRancher.Input.InputEventData);
            inputData.Source = Il2CppMonomiPark.SlimeRancher.Input.InputEventSource.KEYBOARD;
            inputData.Value = 1f;
            throwVacuumItem!._shoot.StartInput(inputData);
            throwVacuumItem._shoot.Perform(inputData);
            throwFiredAt = Time.unscaledTime;
            SrLogger.LogMessage($"[SR2MP_TEST_BOT] throw input fired throw={throwIndex}");
        }
        catch (Exception exception)
        {
            ThrowFail($"native shoot input threw {exception.GetType().Name}: {exception.Message}", false);
        }
    }

    // ---- Zone-soak scenario (SR2MP_TEST_ZONE_SOAK=1): guest teleports between regions; both seats log reliability health. ----
    private const float SoakSettleSeconds = 20f;
    private const float SoakToggleSeconds = 15f;
    private const float SoakActiveSeconds = 150f;
    private const float SoakIdleSeconds = 20f;
    private const float SoakSampleInterval = 5f;
    private const float SoakMinDistance = 300f;
    private static float soakPeerSince = -1f;
    private static float soakBeganAt = -1f;
    private static float soakNextSampleAt;
    private static bool soakDone;
    private static bool soakFailed;
    private static bool soakAtFar;
    private static int soakTeleports;
    private static Vector3 soakHome;
    private static Vector3 soakFar;

    private static void SoakFail(string reason)
    {
        if (soakFailed)
            return;
        soakFailed = true;
        SrLogger.LogWarning($"[SR2MP_TEST_BOT] soak FAIL seat={(Main.Server.IsRunning ? "host" : "guest")} reason={reason}");
    }

    private static string SoakPendingCount()
    {
        try
        {
            // Both peers keep their ReliabilityManager private; read the pending count by reflection (test-only, read-only).
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            object? manager;
            if (Main.Server.IsRunning)
            {
                var network = Main.Server.GetType().GetField("NetworkManager", flags)?.GetValue(Main.Server);
                manager = network?.GetType().GetField("reliabilityManager", flags)?.GetValue(network);
            }
            else
                manager = Main.Client.GetType().GetField("reliabilityManager", flags)?.GetValue(Main.Client);
            var method = manager?.GetType().GetMethod("GetPendingPacketCount");
            return method?.Invoke(manager, null)?.ToString() ?? "n/a";
        }
        catch
        {
            return "n/a";
        }
    }

    private static bool TrySnapToGround(Vector3 around, float startUp, float range, float maxDy, out Vector3 ground)
    {
        ground = default;
        var origin = around + Vector3.up * startUp;
        if (!Physics.Raycast(origin, Vector3.down, out var hit, range, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore))
            return false;
        if (hit.normal.y < .7f || Math.Abs(hit.point.y - around.y) > maxDy || !float.IsFinite(hit.point.y))
            return false;
        ground = hit.point + Vector3.up * 1.5f;
        return true;
    }

    /// <summary>Pick the farthest actor model position 200..1200 m away (same scene group as the home neighbourhood, loaded or not).</summary>
    private static bool TryFindSoakFarPoint(Vector3 home, out Vector3 point, out string trace)
    {
        point = default;
        trace = "none";
        var models = new List<(float distance, Vector3 position, string group)>();
        foreach (var model in ActorManager.Actors.Values.ToArray())
        {
            if (model == null)
                continue;
            Vector3 position;
            string group;
            try { position = model.lastPosition; group = NetworkSceneManager.GetPersistentID(model.sceneGroup).ToString(); }
            catch { continue; }
            var flat = position - home;
            flat.y = 0f;
            if (float.IsFinite(flat.magnitude) && float.IsFinite(position.y))
                models.Add((flat.magnitude, position, group));
        }
        var homeGroup = models.OrderBy(entry => entry.distance).Take(30).GroupBy(entry => entry.group)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        var far = models.Where(entry => entry.distance >= 200f && entry.distance <= 1200f && entry.group == homeGroup)
            .OrderByDescending(entry => entry.distance).FirstOrDefault();
        if (far.distance >= 200f)
        {
            point = far.position;
            trace = $"actor-model group={far.group} distance={far.distance:F0} candidates={models.Count}";
            return true;
        }
        point = home + Vector3.forward * 320f;
        trace = $"radial-fallback distance=320 (no actor model 200..1200 m in group {homeGroup}; models={models.Count})";
        return true;
    }

    private static float soakGroundWaitUntil = -1f;

    private static int SoakAwakeActors()
    {
        var awake = 0;
        foreach (var model in ActorManager.Actors.Values.ToArray())
        {
            var go = model?.GetGameObject();
            if (go && go!.activeInHierarchy)
                awake++;
        }
        return awake;
    }

    private static void PumpZoneSoakScenario(GameObject localPlayer)
    {
        if (soakDone || soakFailed)
            return;
        var host = Main.Server.IsRunning;
        var seat = host ? "host" : "guest";
        var peerPresent = host ? Main.Server.GetClientCount >= 1 && TryGetOtherPlayerPosition(out _) : WaitForStableJoinedWorld();
        if (!peerPresent)
        {
            soakPeerSince = -1f;
            return;
        }
        if (soakPeerSince < 0f)
        {
            soakPeerSince = Time.unscaledTime;
            SrLogger.LogMessage($"[SR2MP_TEST_BOT] soak peer present seat={seat}");
        }
        if (Time.unscaledTime < soakPeerSince + SoakSettleSeconds)
            return;

        if (soakBeganAt < 0f)
        {
            soakBeganAt = Time.unscaledTime;
            soakNextSampleAt = Time.unscaledTime;
            soakHome = localPlayer.transform.position;
            if (!host)
            {
                TryFindSoakFarPoint(soakHome, out soakFar, out var trace);
                SrLogger.LogMessage($"[SR2MP_TEST_BOT] soak far point chosen home={FmtThrowVector(soakHome)} " +
                                    $"far={FmtThrowVector(soakFar)} distance={Vector3.Distance(soakHome, soakFar):F0} via={trace}");
            }
            SrLogger.LogMessage($"[SR2MP_TEST_BOT] soak began seat={seat}");
        }

        var elapsed = Time.unscaledTime - soakBeganAt;
        if (Time.unscaledTime >= soakNextSampleAt)
        {
            soakNextSampleAt += SoakSampleInterval;
            SrLogger.LogMessage($"[SR2MP_TEST_BOT] soak sample seat={seat} t={FmtThrowSeconds(elapsed)} " +
                                $"pending={SoakPendingCount()} actors={ActorManager.Actors.Count} awake={SoakAwakeActors()} " +
                                $"pos={FmtThrowVector(localPlayer.transform.position)} atFar={soakAtFar}");
        }

        if (!host)
        {
            if (soakGroundWaitUntil >= 0f)
            {
                var ok = TrySnapToGround(localPlayer.transform.position, 30f, 200f, 80f, out var ground);
                if (ok || Time.unscaledTime >= soakGroundWaitUntil)
                {
                    SetPlayerPosition(localPlayer, ok ? ground : soakFar + Vector3.up);
                    SrLogger.LogMessage($"[SR2MP_TEST_BOT] soak far ground {(ok ? "found" : "not found; kept model y+1")} " +
                                        $"pos={FmtThrowVector(localPlayer.transform.position)} " +
                                        $"distance={Vector3.Distance(soakHome, localPlayer.transform.position):F0}");
                    soakGroundWaitUntil = -1f;
                }
            }
            var wantFar = elapsed < SoakActiveSeconds && ((int)(elapsed / SoakToggleSeconds) % 2) == 1;
            if (wantFar != soakAtFar)
            {
                soakAtFar = wantFar;
                soakTeleports++;
                SetPlayerPosition(localPlayer, wantFar ? soakFar + Vector3.up * 2f : soakHome + Vector3.up * .5f);
                soakGroundWaitUntil = wantFar ? Time.unscaledTime + 3f : -1f;
                SrLogger.LogMessage($"[SR2MP_TEST_BOT] soak teleport seat=guest n={soakTeleports} " +
                                    $"to={(wantFar ? "far" : "home")} t={FmtThrowSeconds(elapsed)}");
            }
        }

        if (elapsed >= SoakActiveSeconds + SoakIdleSeconds)
        {
            soakDone = true;
            SrLogger.LogMessage($"[SR2MP_TEST_BOT] soak DONE seat={seat} teleports={soakTeleports} " +
                                $"pending={SoakPendingCount()}");
        }
    }

    // ---- Throw-vac scenario (SR2MP_TEST_THROW_VAC=1): host throws a carrot toward the guest, guest vacuums it. ----
    private const int TvCount = 3;
    private const float TvLandDelay = 1.5f;
    private const float TvVacTimeout = 10f;
    private static bool tvFailed;
    private static bool tvDone;
    private static float tvPeerSince = -1f;
    private static float tvBeganAt = -1f;
    private static float tvDeadline;
    private static bool tvSetup;
    private static float tvNextActionAt;
    private static int tvIndex;
    private static float tvFiredAt = -1f;
    private static bool tvCancelled;
    private static long tvActor;
    private static float tvActorSince;
    private static int tvResolved;
    private static float tvFinalAt = -1f;
    private static readonly List<long> tvActors = new();
    private static VacuumItem? tvVacuumItem;
    // Guest side.
    private static bool tvStaged;
    private static IdentifiableType? tvCarrotType;
    private static readonly Queue<long> tvQueue = new();
    private static int tvGuestN;
    private static long tvGuestActor;
    private static int tvPhase; // 0 idle, 1 landing, 2 waiting, 3 vacuuming
    private static float tvSpawnedAt;
    private static float tvLastSampleAt;
    private static Vector3 tvLastPos;
    private static float tvSettledSince = -1f;
    private static float tvLandedAt;
    private static float tvVacStartedAt;
    private static int tvAmmoBefore;
    private static bool tvRepositioned;
    private static readonly List<long> tvGuestActors = new();

    private static void TvFail(string reason)
    {
        if (tvFailed)
            return;
        tvFailed = true;
        try { tvVacuumItem?._shoot?.Cancel(); } catch { }
        try { tvVacuumItem?._vac?.Cancel(); } catch { }
        SrLogger.LogWarning($"[SR2MP_TEST_BOT] throw-vac FAIL seat={(Main.Server.IsRunning ? "host" : "guest")} reason={reason}");
    }

    private static void PumpThrowVacScenario(GameObject localPlayer)
    {
        if (tvFailed || tvDone)
            return;
        var host = Main.Server.IsRunning;
        var peerPresent = TryGetOtherPlayerPosition(out var otherPosition);
        if (host ? Main.Server.GetClientCount < 1 : !WaitForStableJoinedWorld())
            peerPresent = false;
        if (!peerPresent)
        {
            tvPeerSince = -1f;
            return;
        }
        if (tvPeerSince < 0f)
        {
            tvPeerSince = Time.unscaledTime;
            SrLogger.LogMessage($"[SR2MP_TEST_BOT] throw-vac peer present seat={(host ? "host" : "guest")}");
        }
        // The guest must baseline before the first throw: guest watches at once, host throws late.
        if (Time.unscaledTime < tvPeerSince + (host ? 30f : 0f))
            return;
        if (tvBeganAt < 0f)
        {
            tvBeganAt = Time.unscaledTime;
            tvDeadline = Time.unscaledTime + (host ? 200f : 230f);
            ScanNewThrowCarrots();
            SrLogger.LogMessage($"[SR2MP_TEST_BOT] throw-vac began seat={(host ? "host" : "guest")}");
        }
        if (Time.unscaledTime > tvDeadline)
        {
            TvFail("scenario timed out");
            return;
        }
        if (host)
            PumpThrowVacHost(localPlayer, otherPosition);
        else
            PumpThrowVacGuest(localPlayer);
    }

    private static void AimThrowAtPeer(GameObject localPlayer, Vector3 other)
    {
        var from = localPlayer.transform.position;
        var flat = other - from;
        flat.y = 0f;
        if (flat.sqrMagnitude < .01f)
            flat = localPlayer.transform.forward;
        flat.Normalize();
        const float pitch = 15f * Mathf.Deg2Rad;
        var direction = flat * Mathf.Cos(pitch) + Vector3.up * Mathf.Sin(pitch);
        localPlayer.transform.rotation = Quaternion.LookRotation(flat, Vector3.up);
        var controller = localPlayer.GetComponent<SRCharacterController>();
        var cameraPosition = controller && controller._cameraController != null
            ? controller._cameraController.transform.position
            : from + Vector3.up * 1.5f;
        AimCameraAt(localPlayer, cameraPosition + direction * 10f);
    }

    private static readonly HashSet<long> tvPreKeys = new();
    private static int tvRetries;
    private static int tvSeedFailures;

    /// <summary>Re-select the carrot slot and make sure it has ammo; re-seed via the setup path when empty.</summary>
    private static bool TvEnsureCarrotSelected()
    {
        var ammo = SceneContext.Instance?.PlayerState?.Ammo;
        if (ammo?.Slots == null || !throwCarrotType)
            return false;
        for (var index = 0; index < ammo.Slots.Count; index++)
        {
            var slot = ammo.Slots[index];
            if (slot == null || !slot.IsUnlocked || slot.Id != throwCarrotType || slot.Count <= 0)
                continue;
            try { ammo.SetSelectedSlot(index); } catch { return false; }
            if (ammo.GetSelectedId() == throwCarrotType)
            {
                SrLogger.LogMessage($"[SR2MP_TEST_BOT] throw-vac carrot slot ok index={index} count={slot.Count} [{DescribeAmmoSlots(ammo)}]");
                return true;
            }
        }
        SrLogger.LogWarning($"[SR2MP_TEST_BOT] throw-vac no carrot ammo selectable; re-seeding [{DescribeAmmoSlots(ammo)}]");
        if (++tvSeedFailures > 3 || !TrySetupThrowAmmo(out var failure))
        {
            if (tvSeedFailures > 3)
                TvFail($"carrot ammo could not be re-seeded [{DescribeAmmoSlots(ammo)}]");
            return false;
        }
        return false; // fire on the next tick after the re-seed has been verified above
    }
    private static readonly Queue<long> tvAnnounced = new();
    private static bool tvGuestSeen;

    /// <summary>Newly registered carrot (any owner) closest to the nozzle, absent from the pre-throw snapshot.</summary>
    private static long TvFindThrownCarrot(GameObject localPlayer)
    {
        var origin = tvVacuumItem && tvVacuumItem!.VacOrigin
            ? tvVacuumItem.VacOrigin.transform.position
            : localPlayer.transform.position + Vector3.up;
        long best = 0;
        var bestDistance = 15f;
        foreach (var id in ActorManager.Actors.Keys.ToArray())
        {
            if (tvPreKeys.Contains(id) || !ActorManager.Actors.TryGetValue(id, out var model) || model == null)
                continue;
            var name = model.ident ? model.ident.name : null;
            if (name == null || name.IndexOf("Carrot", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            var go = model.GetGameObject();
            var distance = Vector3.Distance(origin, go ? go.transform.position : model.lastPosition);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = id;
            }
        }
        return best;
    }

    internal static void TvReceiveAnnouncedTarget(long actorId)
    {
        if (tvAnnounced.Contains(actorId) || tvGuestActors.Contains(actorId))
            return;
        tvAnnounced.Enqueue(actorId);
        SrLogger.LogMessage($"[SR2MP_TEST_BOT] throw-vac guest received announced actor={actorId:X}");
    }

    private static void PumpThrowVacHost(GameObject localPlayer, Vector3 other)
    {
        if (!tvSetup)
        {
            if (!TrySetupThrowAmmo(out var failure))
            {
                TvFail(failure);
                return;
            }
            tvVacuumItem = localPlayer.GetComponent<PlayerItemController>()?._vacuumItem;
            if (!tvVacuumItem || tvVacuumItem!._shoot == null)
            {
                TvFail("native vacuum item or shoot input is unavailable");
                return;
            }
            tvSetup = true;
            tvNextActionAt = Time.unscaledTime + 1f;
            return;
        }

        if (tvResolved >= TvCount)
        {
            if (tvFinalAt < 0f)
                tvFinalAt = Time.unscaledTime + 3f;
            if (Time.unscaledTime < tvFinalAt)
                return;
            foreach (var id in tvActors)
                SrLogger.LogMessage($"[SR2MP_TEST_BOT] throw-vac final seat=host actor={id:X} present={ActorManager.Actors.ContainsKey(id)}");
            tvDone = true;
            SrLogger.LogMessage($"[SR2MP_TEST_BOT] throw-vac DONE seat=host throws={tvActors.Count}");
            return;
        }

        if (tvActor != 0)
        {
            if (!ActorManager.Actors.ContainsKey(tvActor) || Time.unscaledTime - tvActorSince > 25f)
            {
                SrLogger.LogMessage($"[SR2MP_TEST_BOT] throw-vac host-observed n={tvIndex} actor={tvActor:X} " +
                                    $"present={ActorManager.Actors.ContainsKey(tvActor)} " +
                                    $"seconds={FmtThrowSeconds(Time.unscaledTime - tvActorSince)}");
                tvActor = 0;
                tvFiredAt = -1f;
                tvResolved++;
                tvRetries = 0;
                tvNextActionAt = Time.unscaledTime + 2.5f;
            }
            return;
        }

        if (tvFiredAt >= 0f)
        {
            AimThrowAtPeer(localPlayer, other);
            if (!tvCancelled && Time.unscaledTime >= tvFiredAt + .15f)
            {
                tvCancelled = true;
                try { tvVacuumItem!._shoot.Cancel(); }
                catch (Exception exception) { TvFail($"shoot cancel threw {exception.GetType().Name}"); return; }
            }
            var found = TvFindThrownCarrot(localPlayer);
            if (found != 0)
            {
                tvActor = found;
                tvActorSince = Time.unscaledTime;
                tvActors.Add(found);
                Main.SendToAllOrServer(new TestCaptureTargetPacket { ActorId = found });
                var owner = ActorManager.OwnershipByActorId.TryGetValue(found, out var ownerId) ? ownerId : "<none>";
                SrLogger.LogMessage($"[SR2MP_TEST_BOT] throw-vac thrown n={tvIndex} actor={found:X} owner={owner} announced=true");
            }
            if (tvActor == 0 && Time.unscaledTime > tvFiredAt + 3f)
            {
                var ammoState = DescribeAmmoSlots(SceneContext.Instance?.PlayerState?.Ammo);
                if (tvRetries >= 2)
                {
                    TvFail($"no new carrot actor registered after throw {tvIndex} within 15 m of the nozzle " +
                           $"retries={tvRetries} newActors={ActorManager.Actors.Keys.Count(k => !tvPreKeys.Contains(k))} ammo=[{ammoState}]");
                    return;
                }
                tvRetries++;
                SrLogger.LogWarning($"[SR2MP_TEST_BOT] throw-vac retry n={tvIndex} attempt={tvRetries} " +
                                    $"reason=no new carrot actor within 15 m newActors={ActorManager.Actors.Keys.Count(k => !tvPreKeys.Contains(k))} ammo=[{ammoState}]");
                try { tvVacuumItem!._shoot.Cancel(); } catch { }
                tvIndex--;
                tvFiredAt = -1f;
                tvNextActionAt = Time.unscaledTime + 2f;
            }
            return;
        }

        if (Time.unscaledTime < tvNextActionAt || tvIndex >= TvCount)
            return;
        if (!TvEnsureCarrotSelected())
        {
            tvNextActionAt = Time.unscaledTime + 1f;
            return;
        }
        AimThrowAtPeer(localPlayer, other);
        tvIndex++;
        tvCancelled = false;
        try
        {
            var inputData = default(Il2CppMonomiPark.SlimeRancher.Input.InputEventData);
            inputData.Source = Il2CppMonomiPark.SlimeRancher.Input.InputEventSource.KEYBOARD;
            inputData.Value = 1f;
            tvVacuumItem!._shoot.StartInput(inputData);
            tvPreKeys.Clear();
            foreach (var key in ActorManager.Actors.Keys)
                tvPreKeys.Add(key);
            tvVacuumItem._shoot.Perform(inputData);
            tvFiredAt = Time.unscaledTime;
            SrLogger.LogMessage($"[SR2MP_TEST_BOT] throw-vac input fired n={tvIndex} thrower={FmtThrowVector(localPlayer.transform.position)} guest={FmtThrowVector(other)}");
        }
        catch (Exception exception)
        {
            TvFail($"native shoot input threw {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void TvGuestResult(bool consumed, string note)
    {
        var seconds = Time.unscaledTime - tvVacStartedAt;
        SrLogger.LogMessage($"[SR2MP_TEST_BOT] throw-vac result n={tvGuestN} actor={tvGuestActor:X} " +
                            $"consumed={(consumed ? "true" : "false")} seconds={FmtThrowSeconds(seconds)} " +
                            $"ammoBefore={tvAmmoBefore} ammoAfter={CountLocalAmmo(tvCarrotType)} repositioned={tvRepositioned} {note}");
        tvGuestActors.Add(tvGuestActor);
        tvGuestActor = 0;
        tvPhase = 0;
    }

    private static void PumpThrowVacGuest(GameObject localPlayer)
    {
        if (!tvStaged)
        {
            tvCarrotType = FindThrowCarrotType();
            tvVacuumItem = localPlayer.GetComponent<PlayerItemController>()?._vacuumItem;
            if (!tvCarrotType || !tvVacuumItem || tvVacuumItem!._vac == null)
            {
                TvFail("carrot type or native vacuum input unavailable");
                return;
            }
            var free = CountLocalAmmoCapacity(tvCarrotType);
            if (free < TvCount)
            {
                TvFail($"guest carrot ammo capacity too small free={free}");
                return;
            }
            // Stage the guest a few metres from the host (same helper idea as follow-remote) so a throw toward it can land near.
            if (TryGetOtherPlayerPosition(out var hostPosition))
            {
                var staged = hostPosition + Vector3.right * 8f + Vector3.up * 1f;
                SetPlayerPosition(localPlayer, staged);
                SrLogger.LogMessage($"[SR2MP_TEST_BOT] throw-vac guest staged at {FmtThrowVector(staged)} host={FmtThrowVector(hostPosition)}");
            }
            tvStaged = true;
            return;
        }

        if (tvGuestActors.Count >= TvCount && tvPhase == 0)
        {
            if (tvFinalAt < 0f)
                tvFinalAt = Time.unscaledTime + 3f;
            if (Time.unscaledTime < tvFinalAt)
                return;
            foreach (var id in tvGuestActors)
                SrLogger.LogMessage($"[SR2MP_TEST_BOT] throw-vac final seat=guest actor={id:X} present={ActorManager.Actors.ContainsKey(id)}");
            tvDone = true;
            SrLogger.LogMessage($"[SR2MP_TEST_BOT] throw-vac DONE seat=guest results={tvGuestActors.Count}");
            return;
        }

        if (tvPhase == 0)
        {
            if (tvAnnounced.Count == 0)
                return;
            tvGuestActor = tvAnnounced.Dequeue();
            tvGuestSeen = false;
            tvGuestN++;
            tvPhase = 1;
            tvSpawnedAt = Time.unscaledTime;
            tvLastSampleAt = 0f;
            tvSettledSince = -1f;
            tvRepositioned = false;
            tvVacStartedAt = Time.unscaledTime;
            tvAmmoBefore = CountLocalAmmo(tvCarrotType);
            return;
        }

        var model = ActorManager.Actors.TryGetValue(tvGuestActor, out var found) ? found : null;
        var actorObject = model?.GetGameObject();
        if (tvPhase == 3)
        {
            if (!ActorManager.Actors.ContainsKey(tvGuestActor))
            {
                try { tvVacuumItem!._vac.Cancel(); } catch { }
                TvGuestResult(true, "note=actor-removed");
                return;
            }
            if (actorObject)
                AimCameraAt(localPlayer, actorObject!.transform.position + Vector3.up * .3f);
            if (Time.unscaledTime - tvVacStartedAt > TvVacTimeout)
            {
                try { tvVacuumItem!._vac.Cancel(); } catch { }
                TvGuestResult(false, "note=timeout");
            }
            return;
        }

        if (!actorObject)
        {
            // The announcement can beat the actor spawn packet: wait for the announced actor only.
            if (ActorManager.Actors.ContainsKey(tvGuestActor))
                tvGuestSeen = true;
            if (Time.unscaledTime - tvSpawnedAt > 10f)
                TvGuestResult(false, tvGuestSeen ? "note=no-game-object" : "note=announced-actor-never-appeared");
            else if (tvGuestSeen && !ActorManager.Actors.ContainsKey(tvGuestActor))
                TvGuestResult(false, "note=vanished-before-vac");
            return;
        }
        tvGuestSeen = true;

        var position = actorObject!.transform.position;
        if (tvPhase == 1)
        {
            if (Time.unscaledTime - tvLastSampleAt >= .25f)
            {
                var moved = Vector3.Distance(position, tvLastPos);
                tvLastPos = position;
                tvLastSampleAt = Time.unscaledTime;
                if (moved < .05f)
                {
                    if (tvSettledSince < 0f)
                        tvSettledSince = Time.unscaledTime;
                }
                else
                    tvSettledSince = -1f;
            }
            if ((tvSettledSince >= 0f && Time.unscaledTime - tvSettledSince >= .5f) || Time.unscaledTime - tvSpawnedAt > 6f)
            {
                tvPhase = 2;
                tvLandedAt = Time.unscaledTime;
                SrLogger.LogMessage($"[SR2MP_TEST_BOT] throw-vac landed n={tvGuestN} actor={tvGuestActor:X} pos={FmtThrowVector(position)} " +
                                    $"guestDistance={Vector3.Distance(localPlayer.transform.position, position):F2}");
            }
            return;
        }

        // Phase 2: wait for the landing delay, keep the camera latched on the carrot, then start native vacuum.
        AimCameraAt(localPlayer, position + Vector3.up * .3f);
        if (Time.unscaledTime < tvLandedAt + TvLandDelay)
            return;
        var flatDistance = new Vector2(position.x - localPlayer.transform.position.x, position.z - localPlayer.transform.position.z).magnitude;
        var origin = tvVacuumItem!.VacOrigin;
        var nozzle = origin ? Vector3.Distance(origin.transform.position, position) : float.NaN;
        if (flatDistance < 3f || flatDistance > 5f || !(nozzle > tvVacuumItem.CaptureDist + .3f && nozzle < tvVacuumItem.MaxVacDist - .3f))
        {
            tvRepositioned = true;
            PositionPlayerAtNozzleDistance(localPlayer, position, tvVacuumItem,
                Math.Min(3.5f, tvVacuumItem.MaxVacDist - .5f), position + Vector3.up * .3f);
        }
        AimCameraAt(localPlayer, position + Vector3.up * .3f);
        try
        {
            var inputData = default(Il2CppMonomiPark.SlimeRancher.Input.InputEventData);
            inputData.Source = Il2CppMonomiPark.SlimeRancher.Input.InputEventSource.KEYBOARD;
            inputData.Value = 1f;
            tvVacuumItem._vac.StartInput(inputData);
            tvVacuumItem._vac.Perform(inputData);
            tvVacStartedAt = Time.unscaledTime;
            tvPhase = 3;
            SrLogger.LogMessage($"[SR2MP_TEST_BOT] throw-vac vac started n={tvGuestN} actor={tvGuestActor:X} " +
                                $"flatDistance={flatDistance:F2} nozzle={nozzle:F2} repositioned={tvRepositioned}");
        }
        catch (Exception exception)
        {
            TvFail($"native vacuum input threw {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static bool WaitForStableJoinedWorld()
    {
        if (!Main.Client.IsJoined)
        {
            gameplayReadyDelayStarted = false;
            return false;
        }

        if (!gameplayReadyDelayStarted)
        {
            gameplayReadyDelayStarted = true;
            gameplayReadyAt = Time.unscaledTime + 12f;
            SrLogger.LogMessage("[SR2MP_TEST_BOT] waiting 12s after join for the loaded save/player spawn to settle");
        }

        return Time.unscaledTime >= gameplayReadyAt;
    }

    private static void TryFollowRemotePlayer(GameObject localPlayer)
    {
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_BOT_FOLLOW_REMOTE") != "1")
            return;

        if (!Main.Client.IsJoined)
            return;

        if (followAttempted)
        {
            if (capturePhase == Idle)
            {
                var followedRemote = PlayerObjects.FirstOrDefault(entry =>
                    !string.Equals(entry.Key, LocalID, StringComparison.Ordinal) && entry.Value);
                if (followedRemote.Value)
                    AimCameraAt(localPlayer, followedRemote.Value.transform.position + Vector3.up);
            }
            else if (capturePhase is (WaitingToCapture or WaitingForOwnership or WaitingForInventoryReject) && captureActorObject)
            {
                var target = GetCaptureAimPoint();
                AimCameraAt(localPlayer, target);
                AimVacuumOriginAt(localPlayer, target);
                CaptureCaptureTargetScreenshotOnce();

                if (Time.unscaledTime >= nextCaptureCameraTraceAt)
                {
                    nextCaptureCameraTraceAt = Time.unscaledTime + 1f;
                    var camera = Camera.main;
                    if (camera)
                    {
                        var screenPoint = camera.WorldToScreenPoint(target);
                        var viewportPoint = camera.WorldToViewportPoint(target);
                        var blocked = Physics.Linecast(camera.transform.position, target, out var hit,
                            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                        var hitIsTarget = blocked && captureActorObject && hit.transform &&
                            (hit.transform == captureActorObject.transform ||
                             hit.transform.IsChildOf(captureActorObject.transform));
                        var blocker = !blocked ? "<clear>" : hitIsTarget ? "<target>" :
                            hit.collider ? hit.collider.name : "<unknown>";
                        SrLogger.LogMessage(
                            $"[SR2MP_TEST_BOT] capture camera position={camera.transform.position} " +
                            $"forward={camera.transform.forward} target={target} " +
                            $"screen={screenPoint} viewport={viewportPoint} " +
                            $"lineOfSightBlocker={blocker} hitDistance={(blocked ? hit.distance : -1f):F2}");
                    }
                }
            }

            if (followAttempted && !followVerified && Time.unscaledTime >= followVerifyAt)
                VerifyFollow(localPlayer);
            return;
        }

        var remote = PlayerObjects.FirstOrDefault(entry =>
            !string.Equals(entry.Key, LocalID, StringComparison.Ordinal) && entry.Value);
        if (!remote.Value)
            return;

        var remotePosition = remote.Value.transform.position;
        // Keep the diagnostic camera far enough back to frame the remote body
        // separately from its world-space username panel.
        var localPosition = remotePosition + Vector3.right * 5f;
        SetPlayerPosition(localPlayer, localPosition);
        var lookDirection = remotePosition - localPosition;
        lookDirection.y = 0f;
        if (lookDirection.sqrMagnitude > 0.01f)
            localPlayer.transform.rotation = Quaternion.LookRotation(lookDirection);
        AimCameraAt(localPlayer, remotePosition + Vector3.up);

        followAttempted = true;
        followVerifyAt = Time.unscaledTime + 5f;
        var visibility = DescribeRemoteVisibility(remote.Value);
        SrLogger.LogMessage(
            $"[SR2MP_TEST_BOT] follow-remote positioned local={LocalID} remote={remote.Key} " +
            $"localPosition={localPosition} remotePosition={remotePosition} " +
            $"remoteActive={remote.Value.activeInHierarchy} {visibility}");
    }

    private static void VerifyFollow(GameObject localPlayer)
    {
        followVerified = true;
        var remote = PlayerObjects.FirstOrDefault(entry =>
            !string.Equals(entry.Key, LocalID, StringComparison.Ordinal) && entry.Value);
        if (!remote.Value)
        {
            SrLogger.LogWarning("[SR2MP_TEST_BOT] follow-remote FAIL: remote player object disappeared");
            return;
        }

        var distance = Vector3.Distance(localPlayer.transform.position, remote.Value.transform.position);
        AimCameraAt(localPlayer, remote.Value.transform.position + Vector3.up);
        SrLogger.LogMessage(
            $"[SR2MP_TEST_BOT] follow-remote {(distance <= 6f ? "PASS" : "FAIL")} " +
            $"distance={distance:F2} local={localPlayer.transform.position} remote={remote.Value.transform.position} " +
            DescribeRemoteVisibility(remote.Value));
        if (distance <= 6f)
            CaptureFollowScreenshotOnce();
    }

    private static void AimCameraAt(GameObject localPlayer, Vector3 target)
    {
        var controller = localPlayer.GetComponent<SRCharacterController>();
        if (!controller || controller._cameraController == null)
            return;

        var cameraTransform = controller._cameraController.transform;
        var direction = target - cameraTransform.position;
        if (direction.sqrMagnitude > 0.01f)
            // Update controller-owned yaw/pitch; LateUpdate overwrites raw transform writes.
            controller._cameraController.SetRotation(Quaternion.LookRotation(direction, Vector3.up));
    }

    private static void AimVacuumOriginAt(GameObject localPlayer, Vector3 target)
    {
        // Nozzle rotation is an independent diagnostic, never part of normal bot aim.
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_ROTATE_VACUUM_ORIGIN") != "1")
            return;

        var itemController = localPlayer.GetComponent<PlayerItemController>();
        var vacuumItem = itemController ? itemController._vacuumItem : null;
        var origin = vacuumItem ? vacuumItem.VacOrigin : null;
        if (!origin)
            return;

        var direction = target - origin.transform.position;
        if (direction.sqrMagnitude > 0.01f)
            origin.transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
    }

    private static Vector3 GetCaptureAimPoint()
    {
        if (!captureActorObject)
            return Vector3.zero;

        var colliders = captureActorObject.GetComponentsInChildren<Collider>(true);
        var collider = colliders
            .Where(candidate => candidate && candidate.enabled && candidate.gameObject.activeInHierarchy && !candidate.isTrigger)
            .OrderByDescending(candidate => candidate.bounds.extents.sqrMagnitude)
            .FirstOrDefault();
        if (!collider)
            collider = colliders
                .Where(candidate => candidate && candidate.enabled && candidate.gameObject.activeInHierarchy)
                .OrderByDescending(candidate => candidate.bounds.extents.sqrMagnitude)
                .FirstOrDefault();

        return collider ? collider.bounds.center : captureActorObject.transform.position + Vector3.up * 0.5f;
    }

    private static string DescribeRemoteVisibility(GameObject remotePlayer)
    {
        var renderers = remotePlayer.GetComponentsInChildren<Renderer>(true);
        var activeRenderers = renderers.Where(renderer => renderer && renderer.enabled &&
            renderer.gameObject.activeInHierarchy).ToArray();
        var visibleRendererCount = activeRenderers.Count(renderer => renderer.isVisible);
        var camera = Camera.main;
        var planes = camera ? GeometryUtility.CalculateFrustumPlanes(camera) : null;
        var inFrustum = camera
            ? activeRenderers.Where(renderer => GeometryUtility.TestPlanesAABB(planes!, renderer.bounds)).ToArray()
            : Array.Empty<Renderer>();
        var diagnosticRenderers = activeRenderers
            .Where(renderer => renderer.isVisible || (camera && GeometryUtility.TestPlanesAABB(planes!, renderer.bounds)))
            .Take(12)
            .Select(renderer =>
            {
                var bounds = renderer.bounds;
                var screen = camera ? camera.WorldToScreenPoint(bounds.center).ToString() : "<no-camera>";
                var viewport = camera ? camera.WorldToViewportPoint(bounds.center).ToString() : "<no-camera>";
                var layerInCameraMask = camera && (camera.cullingMask & (1 << renderer.gameObject.layer)) != 0;
                return $"{GetHierarchyPath(renderer.transform)} visible={renderer.isVisible} " +
                       $"layer={renderer.gameObject.layer} cameraCullingMaskIncludesLayer={layerInCameraMask} " +
                       $"boundsCenter={bounds.center} boundsExtents={bounds.extents} " +
                       $"screenCenter={screen} viewportCenter={viewport}";
            });

        var cameraPosition = camera ? camera.transform.position.ToString() : "<none>";
        var cameraForward = camera ? camera.transform.forward.ToString() : "<none>";
        return $"rendererCount={renderers.Length} activeRendererCount={activeRenderers.Length} " +
               $"visibleRendererCount={visibleRendererCount} frustumRendererCount={inFrustum.Length} " +
               $"cameraPosition={cameraPosition} cameraForward={cameraForward} " +
               $"visibleRendererDetails=[{string.Join(" | ", diagnosticRenderers)}]";
    }

    private static string GetHierarchyPath(Transform transform)
    {
        var parts = new List<string>();
        for (var current = transform; current; current = current.parent)
            parts.Add(current.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static void CaptureFollowScreenshotOnce()
    {
        if (followScreenshotAttempted)
            return;

        followScreenshotAttempted = true;
        CaptureDiagnosticScreenshot("follow");
    }

    private static void CaptureCaptureTargetScreenshotOnce()
    {
        if (captureTargetScreenshotAttempted)
            return;

        captureTargetScreenshotAttempted = true;
        CaptureDiagnosticScreenshot("capture-target");
    }

    private static void CaptureDiagnosticScreenshot(string suffix)
    {
        var configuredPath = Environment.GetEnvironmentVariable("SR2MP_TEST_CAPTURE_PATH");
        if (string.IsNullOrWhiteSpace(configuredPath))
            return;

        try
        {
            var fullPath = Path.GetFullPath(configuredPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var stem = Path.GetFileNameWithoutExtension(fullPath);
            var extension = Path.GetExtension(fullPath);
            var candidate = Path.Combine(directory ?? string.Empty, $"{stem}-{suffix}{extension}");
            for (var collisionIndex = 1; File.Exists(candidate); collisionIndex++)
                candidate = Path.Combine(directory ?? string.Empty,
                    $"{stem}-{suffix}-{collisionIndex:D2}{extension}");

            ScreenCapture.CaptureScreenshot(candidate);
            SrLogger.LogMessage($"[SR2MP_TEST_BOT] diagnostic screenshot requested path={candidate}");
        }
        catch (Exception exception)
        {
            SrLogger.LogWarning($"[SR2MP_TEST_BOT] diagnostic screenshot failed: {exception}");
        }
    }

    private static void TryCaptureAndConsume(GameObject localPlayer)
    {
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_BOT_CAPTURE_REMOTE_ACTOR") != "1" ||
            !Main.Client.IsJoined || capturePhase == Finished)
            return;

        if (!ConfigureCaptureScenario())
            return;

        var repeatCount = GetCaptureRepeatCount();

        if (capturePhase == SettlingAfterCapture)
        {
            if (Time.unscaledTime < captureActionAt)
                return;

            var vacuumState = DescribeNativeVacuumReleaseState();
            SrLogger.LogMessage(
                $"[SR2MP_TEST_BOT] native vacuum release settle complete after " +
                $"{CaptureReleaseSettleSeconds:F1}s {vacuumState}");
            ResetForNextCaptureIteration();
        }

        if (Environment.GetEnvironmentVariable("SR2MP_TEST_BOT_FOLLOW_REMOTE") == "1" && !followVerified)
            return;

        if (capturePhase == Idle)
        {
            if (!captureTargetWaitStarted)
            {
                captureTargetWaitStarted = true;
                captureTargetWaitDeadline = Time.unscaledTime + 45f;
                SrLogger.LogMessage(
                    "[SR2MP_TEST_BOT] waiting up to 45s for the host's designated Chick capture target");
            }

            if (announcedCaptureTargetActorId == 0)
            {
                if (Time.unscaledTime >= captureTargetWaitDeadline)
                {
                    capturePhase = Finished;
                    SrLogger.LogWarning(
                        "[SR2MP_TEST_BOT] capture FAIL: host did not announce a designated Chick target");
                }
                return;
            }

            if (!TryFindRemoteCapturableActor(announcedCaptureTargetActorId, out captureActorId,
                    out captureActorObject, out captureVacuumable, out captureIdentifiableType,
                    out captureAmmoCountBefore, out var targetReplicated, out var targetFailure))
            {
                if (!targetReplicated && Time.unscaledTime < captureTargetWaitDeadline)
                    return;

                capturePhase = Finished;
                SrLogger.LogWarning(
                    $"[SR2MP_TEST_BOT] capture FAIL: designated Chick target={announcedCaptureTargetActorId:X} " +
                    $"replicated={targetReplicated} reason={targetFailure}");
                return;
            }

            var actorPosition = captureActorObject!.transform.position;
            // The default fixture isolates capture near the nozzle. Extended
            // distance runs must prove the target starts outside capture range.
            var requestedNozzleDistance = GetRequestedNozzleDistance();
            var outward = GetCaptureApproachDirection(captureActorId, actorPosition,
                requestedNozzleDistance, out var approachTrace);
            var playerPosition = actorPosition + outward * GetCaptureStartDistance();
            var massVac = Environment.GetEnvironmentVariable("SR2MP_TEST_MASS_VAC") == "1";
            if (!massVac || captureIterationsPassed == 0)
                SetPlayerPosition(localPlayer, playerPosition);
            var lookDirection = actorPosition - playerPosition;
            lookDirection.y = 0f;
            if (lookDirection.sqrMagnitude > 0.01f)
                localPlayer.transform.rotation = Quaternion.LookRotation(lookDirection);
            AimCameraAt(localPlayer, GetCaptureAimPoint());
            captureVacuumItem = localPlayer.GetComponent<PlayerItemController>()?._vacuumItem;
            if (requestedNozzleDistance > 0f && captureVacuumItem && captureVacuumItem.VacOrigin)
            {
                PositionPlayerAtNozzleDistance(localPlayer, actorPosition, captureVacuumItem,
                    requestedNozzleDistance, GetCaptureAimPoint());
                playerPosition = localPlayer.transform.position;
            }

            captureProbeId = Guid.NewGuid();
            captureStatusQueryId = 0;
            captureStatusQueryPending = false;
            captureHostStatusReceived = false;
            captureHostActorPresent = true;
            captureHostModelPresent = true;
            captureTimeoutAt = 0f;
            capturePhase = WaitingToCapture;
            captureActionAt = Time.unscaledTime + 0.1f;
            SrLogger.LogMessage(
                $"[SR2MP_TEST_BOT] capture approach {approachTrace} target={captureActorId:X}");
            SrLogger.LogMessage(
                $"[SR2MP_TEST_BOT] capture target actor={captureActorId:X} " +
                $"owner={captureVacuumable!.GetComponent<NetworkActor>()?.CurrentOwnerId} " +
                $"actorPosition={actorPosition} playerPosition={playerPosition} " +
                $"canCapture={captureVacuumable.CanCapture()} " +
                $"ammoType={captureIdentifiableType!.name} ammoBefore={captureAmmoCountBefore} " +
                $"scene={NetworkSceneManager.GetPersistentID(ActorManager.Actors[captureActorId].sceneGroup)}");
            return;
        }

        if (capturePhase == WaitingToCapture && Time.unscaledTime >= captureActionAt)
        {
            if (!captureActorObject || !captureVacuumable || !captureVacuumable.CanCapture())
            {
                NetworkActorCaptureManager.CancelClientCapture(captureActorId,
                    "capture target expired before native capture started");
                capturePhase = Finished;
                SrLogger.LogWarning("[SR2MP_TEST_BOT] capture FAIL: target expired or cannot be captured after positioning");
                return;
            }

            var playerItemController = localPlayer.GetComponent<PlayerItemController>();
            captureVacuumItem = playerItemController ? playerItemController._vacuumItem : null;
            if (!captureVacuumItem)
            {
                capturePhase = Finished;
                SrLogger.LogWarning("[SR2MP_TEST_BOT] capture FAIL: local VacuumItem is unavailable");
                return;
            }

            var origin = captureVacuumItem.VacOrigin;
            var nozzleDistance = origin
                ? Vector3.Distance(origin.transform.position, GetCaptureAimPoint())
                : float.NaN;
            var nativeMaxVacDist = captureVacuumItem.MaxVacDist;
            if (!float.IsFinite(nozzleDistance) || !float.IsFinite(nativeMaxVacDist) || nativeMaxVacDist <= 0f ||
                nozzleDistance > nativeMaxVacDist + .05f ||
                (GetRequestedNozzleDistance() > 0f &&
                 Math.Abs(nozzleDistance - GetRequestedNozzleDistance()) > .35f))
            {
                NetworkActorCaptureManager.CancelClientCapture(captureActorId,
                    "nozzle-distance fixture was invalid or outside native MaxVacDist");
                capturePhase = Finished;
                SrLogger.LogError(
                    $"[SR2MP_TEST_BOT] capture FAIL nozzle fixture actor={captureActorId:X} " +
                    $"requestedNozzle={GetRequestedNozzleDistance():F3} measuredNozzle={nozzleDistance:F3} " +
                    $"nativeMaxVacDist={nativeMaxVacDist:F3} cameraAim={GetCaptureAimPoint()} " +
                    DescribeCaptureMechanics());
                return;
            }
            SrLogger.LogMessage(
                $"[SR2MP_TEST_BOT] capture-nozzle preflight PASS actor={captureActorId:X} " +
                $"time={Time.unscaledTime:F3} " +
                $"rootDistance={Vector3.Distance(localPlayer.transform.position, captureActorObject.transform.position):F3} " +
                $"requestedNozzle={GetRequestedNozzleDistance():F3} measuredNozzle={nozzleDistance:F3} " +
                $"nativeMaxVacDist={nativeMaxVacDist:F3} captureDist={captureVacuumItem.CaptureDist:F3} " +
                $"cameraAim={GetCaptureAimPoint()} {DescribeCaptureMechanics()}");

            var vacuumInput = captureVacuumItem._vac;
            if (vacuumInput == null)
            {
                NetworkActorCaptureManager.CancelClientCapture(captureActorId, "vanilla vacuum input event is unavailable");
                capturePhase = Finished;
                SrLogger.LogError("[SR2MP_TEST_BOT] capture FAIL: VacuumItem._vac input event is unavailable");
                return;
            }

            try
            {
                // Follow the same started -> performed lifecycle as the game's
                // InputEventBinding and leave the action active until cleanup.
                // Perform() alone behaves like a tap here, so the native vacuum
                // leaves VAC mode before the target reaches CaptureDist.
                var inputData = default(Il2CppMonomiPark.SlimeRancher.Input.InputEventData);
                inputData.Source = Il2CppMonomiPark.SlimeRancher.Input.InputEventSource.KEYBOARD;
                inputData.Value = 1f;
                vacuumInput.StartInput(inputData);
                vacuumInput.Perform(inputData);
            }
            catch (Exception exception)
            {
                NetworkActorCaptureManager.CancelClientCapture(captureActorId,
                    "vanilla vacuum input event threw before native capture");
                TryStopVanillaVacuum();
                capturePhase = Finished;
                SrLogger.LogError(
                    $"[SR2MP_TEST_BOT] capture FAIL: VacuumItem._vac.Perform() threw: {exception}");
                return;
            }

            if (fullInventoryRejectionMode)
            {
                fullInventoryInputPerformed = true;
                fullInventoryInputStartedAt = Time.unscaledTime;
                fullInventoryTargetObserved = false;
                fullInventoryEarlyReleaseReason = string.Empty;
                capturePhase = WaitingForInventoryReject;
                captureActionAt = fullInventoryInputStartedAt + FullInventoryVacuumAttemptSeconds;
                captureTimeoutAt = 0f;
                SrLogger.LogMessage(
                    $"[SR2MP_TEST_BOT] capture-full-inventory input performed actor={captureActorId:X} " +
                    $"mode={captureVacuumItem._vacMode} pressed={captureVacuumItem._vacPressed} " +
                    $"ammoBefore={captureAmmoCountBefore} fixture=[{fullInventoryFixtureTrace}] " +
                    "source=InputEvent.StartInput -> InputEvent.Perform -> native vacuum update; " +
                    "no ForceJoint/UpdateHeld/held assignment or approved-stage request");
                return;
            }

            capturePhase = WaitingForOwnership;
            captureActionAt = Time.unscaledTime + 20f;
            captureStageFallbackAt = Time.unscaledTime + 4f;
            captureStageFallbackAttempted = false;
            if (GetRequestedNozzleDistance() > 0f || GetCaptureStartDistance() > 2f)
            {
                var closeNativeIntakeBaseline = GetRequestedNozzleDistance() > 0f &&
                                                GetRequestedNozzleDistance() <= captureVacuumItem.CaptureDist - .25f;
                var actualDistance = captureVacuumItem.VacOrigin
                    ? Vector3.Distance(captureVacuumItem.VacOrigin.transform.position, GetCaptureAimPoint())
                    : -1f;
                if (!closeNativeIntakeBaseline && actualDistance <= captureVacuumItem.CaptureDist + 0.25f)
                {
                    SrLogger.LogError($"[SR2MP_TEST_BOT] capture FAIL: extended-distance fixture is already inside capture range actor={captureActorId:X}");
                    TryStopVanillaVacuum();
                    capturePhase = Finished;
                    return;
                }
                if (closeNativeIntakeBaseline)
                    SrLogger.LogMessage($"[SR2MP_TEST_BOT] capture-intake baseline eligible actor={captureActorId:X} " +
                        $"distance={actualDistance:F2} captureDist={captureVacuumItem.CaptureDist:F2} " +
                        $"requestedNozzle={GetRequestedNozzleDistance():F2}");
                else
                    SrLogger.LogMessage($"[SR2MP_TEST_BOT] capture-distance preflight PASS actor={captureActorId:X} " +
                        $"distance={actualDistance:F2} captureDist={captureVacuumItem.CaptureDist:F2} " +
                        $"requestedRoot={GetCaptureStartDistance():F2} requestedNozzle={GetRequestedNozzleDistance():F2}");
            }
            SrLogger.LogMessage(
                $"[SR2MP_TEST_BOT] capture input performed actor={captureActorId:X} " +
                $"mode={captureVacuumItem._vacMode} pressed={captureVacuumItem._vacPressed} " +
                $"owner={captureVacuumable.GetComponent<NetworkActor>()?.CurrentOwnerId} " +
                "source=InputEvent.StartInput -> InputEvent.Perform -> native VacTriggered/Update; " +
                "no ForceJoint/UpdateHeld/ConsumeVacuumable calls " +
                DescribeCaptureMechanics());

            return;
        }

        if (capturePhase == WaitingForInventoryReject)
        {
            var activeActor = LocalActorVacuumPredictionManager.ActiveActor;
            if ((activeActor && activeActor.ActorId.Value == captureActorId) || IsFullInventoryTargetUnderViewRay())
                fullInventoryTargetObserved = true;

            var guestActorPresent = ActorManager.Actors.ContainsKey(captureActorId);
            var guestModelPresent = GameState.TryGetIdentifiableModel(new ActorId(captureActorId), out _);
            var pendingReservation = NetworkActorCaptureManager.TestHasPendingClientCapture(captureActorId);
            var captive = captureVacuumable && captureVacuumable.IsCaptive();
            var heldMatches = captureVacuumItem && captureActorObject &&
                              captureVacuumItem._held == captureActorObject;
            var lockJoint = captureVacuumItem ? captureVacuumItem.LockJoint : null;
            var jointConnectedToTarget = lockJoint && captureVacuumable && captureVacuumable._body &&
                                         lockJoint.connectedBody == captureVacuumable._body;
            var unsafeToKeepVacuuming = !guestActorPresent || !guestModelPresent || pendingReservation ||
                                        captive || heldMatches || jointConnectedToTarget;
            if (unsafeToKeepVacuuming && string.IsNullOrEmpty(fullInventoryEarlyReleaseReason))
            {
                fullInventoryEarlyReleaseReason =
                    $"unexpected native state before minimum attempt elapsed: actorPresent={guestActorPresent} " +
                    $"modelPresent={guestModelPresent} pendingReservation={pendingReservation} captive={captive} " +
                    $"heldTarget={heldMatches} jointConnectedToTarget={jointConnectedToTarget}";
                TryStopVanillaVacuum();
                capturePhase = SettlingInventoryReject;
                captureActionAt = Math.Max(fullInventoryInputStartedAt + FullInventoryVacuumAttemptSeconds,
                    Time.unscaledTime + CaptureReleaseSettleSeconds);
                captureTimeoutAt = captureActionAt + FullInventoryStatusTimeoutSeconds;
                SrLogger.LogWarning(
                    $"[SR2MP_TEST_BOT] capture-full-inventory early release requested actor={captureActorId:X} " +
                    $"reason={fullInventoryEarlyReleaseReason} {DescribeNativeVacuumReleaseState()}");
                return;
            }

            if (Time.unscaledTime < captureActionAt)
                return;

            TryStopVanillaVacuum();
            capturePhase = SettlingInventoryReject;
            captureActionAt = Time.unscaledTime + CaptureReleaseSettleSeconds;
            captureTimeoutAt = captureActionAt + FullInventoryStatusTimeoutSeconds;
            SrLogger.LogMessage(
                $"[SR2MP_TEST_BOT] capture-full-inventory native input held for " +
                $"{Time.unscaledTime - fullInventoryInputStartedAt:F2}s; releasing and settling " +
                $"{CaptureReleaseSettleSeconds:F1}s before checking both peers " +
                DescribeNativeVacuumReleaseState());
            return;
        }

        if (capturePhase == SettlingInventoryReject)
        {
            var activeActor = LocalActorVacuumPredictionManager.ActiveActor;
            if ((activeActor && activeActor.ActorId.Value == captureActorId) || IsFullInventoryTargetUnderViewRay())
                fullInventoryTargetObserved = true;

            if (Time.unscaledTime < captureActionAt)
                return;

            var localState = DescribeFullInventoryRejectionState(out _, out _, out _);
            capturePhase = WaitingForInventoryRejectStatus;
            captureStatusQueryId = 0;
            captureStatusQueryPending = false;
            captureHostStatusReceived = false;
            captureHostActorPresent = false;
            captureHostModelPresent = false;
            captureTimeoutAt = Time.unscaledTime + FullInventoryStatusTimeoutSeconds;
            captureActionAt = Time.unscaledTime;
            SrLogger.LogMessage(
                $"[SR2MP_TEST_BOT] capture-full-inventory settled after native release " +
                $"fixture=[{fullInventoryFixtureTrace}] local=[{localState}]");
            SendFullInventoryStatusQuery();
            return;
        }

        if (capturePhase == WaitingForInventoryRejectStatus)
        {
            if (captureHostStatusReceived)
            {
                var details = DescribeFullInventoryRejectionState(
                    out var guestActorPresent, out var guestModelPresent, out var localAssertionsPass);
                var bothPeersPresent = guestActorPresent && guestModelPresent &&
                                       captureHostActorPresent && captureHostModelPresent;
                if (localAssertionsPass && bothPeersPresent)
                {
                    capturePhase = Finished;
                    SrLogger.LogMessage(
                        $"[SR2MP_TEST_BOT] capture-full-inventory PASS actor={captureActorId:X} " +
                        $"{details} hostActorPresent={captureHostActorPresent} " +
                        $"hostModelPresent={captureHostModelPresent} query={captureStatusQueryId}");
                    SrLogger.LogMessage(
                        "[SR2MP_TEST_BOT] capture-suite PASS count=1 scenario=full-inventory-rejection");
                    TryStopVanillaVacuum();
                    return;
                }

                capturePhase = Finished;
                SrLogger.LogError(
                    $"[SR2MP_TEST_BOT] capture-full-inventory FAIL actor={captureActorId:X} " +
                    $"assertionsPass={localAssertionsPass} {details} " +
                    $"hostActorPresent={captureHostActorPresent} hostModelPresent={captureHostModelPresent} " +
                    $"query={captureStatusQueryId}");
                TryStopVanillaVacuum();
                return;
            }

            if (captureStatusQueryPending && Time.unscaledTime >= captureActionAt)
            {
                captureStatusQueryPending = false;
                SrLogger.LogWarning(
                    $"[SR2MP_TEST_BOT] capture-full-inventory host status timed out " +
                    $"actor={captureActorId:X} query={captureStatusQueryId}; retrying");
            }

            if (!captureStatusQueryPending && Time.unscaledTime >= captureActionAt)
                SendFullInventoryStatusQuery();

            if (Time.unscaledTime >= captureTimeoutAt)
            {
                capturePhase = Finished;
                var localState = DescribeFullInventoryRejectionState(out _, out _, out var ignored);
                SrLogger.LogError(
                    $"[SR2MP_TEST_BOT] capture-full-inventory FAIL actor={captureActorId:X} " +
                    $"reason=host status did not arrive hostStatusReceived={captureHostStatusReceived} " +
                    $"hostActorPresent={captureHostActorPresent} hostModelPresent={captureHostModelPresent} " +
                    $"local=[{localState}] query={captureStatusQueryId} detail={ignored}");
                TryStopVanillaVacuum();
            }
        }

        if (capturePhase == WaitingForOwnership)
        {
            if (!captureVacuumItem)
            {
                NetworkActorCaptureManager.CancelClientCapture(captureActorId, "vacuum item expired during capture test");
                TryStopVanillaVacuum();
                capturePhase = Finished;
                SrLogger.LogWarning("[SR2MP_TEST_BOT] capture FAIL: local VacuumItem expired during capture test");
                return;
            }

            var guestActorPresent = ActorManager.Actors.ContainsKey(captureActorId);
            var guestModelPresent = GameState.TryGetIdentifiableModel(new ActorId(captureActorId), out _);
            if (!guestActorPresent && !guestModelPresent)
            {
                if (!NetworkActorCaptureManager.TestWasConsumeRemovalConfirmed(captureActorId))
                {
                    if (!captureAbsenceWithoutNativeRemovalLogged)
                    {
                        captureAbsenceWithoutNativeRemovalLogged = true;
                        SrLogger.LogWarning(
                            $"[SR2MP_TEST_BOT] actor/model absent locally for actor={captureActorId:X}, " +
                            "but capture manager has no native-consume removal confirmation; " +
                            "waiting for snapshot state to settle instead of treating absence as consume");
                    }

                    if (Time.unscaledTime >= captureActionAt)
                    {
                        NetworkActorCaptureManager.CancelClientCapture(captureActorId,
                            "actor/model disappeared without native-consume removal confirmation");
                        TryStopVanillaVacuum();
                        capturePhase = Finished;
                        SrLogger.LogWarning(
                            $"[SR2MP_TEST_BOT] capture ownership/held FAIL actor={captureActorId:X} " +
                            "actor/model absent without native-consume removal confirmation");
                    }
                    return;
                }

                BeginWaitingForDestroy();
                SrLogger.LogMessage(
                    $"[SR2MP_TEST_BOT] capture target removal confirmed by capture manager actor={captureActorId:X}");
                return;
            }

            var currentOwner = captureVacuumable
                ? captureVacuumable.GetComponent<NetworkActor>()?.CurrentOwnerId
                : ActorManager.OwnershipByActorId.GetValueOrDefault(captureActorId);
            var heldMatches = captureActorObject && captureVacuumItem._held == captureActorObject;
            var fallbackTarget = captureVacuumable;
            var fallbackVacuumItem = captureVacuumItem;
            var fallbackTargetObject = captureActorObject;

            if (!captureStageFallbackAttempted &&
                Environment.GetEnvironmentVariable("SR2MP_TEST_BOT_REQUEST_APPROVED_STAGE") == "1" &&
                Time.unscaledTime >= captureStageFallbackAt &&
                !string.Equals(currentOwner, LocalID, StringComparison.Ordinal) &&
                fallbackTarget is not null && fallbackVacuumItem is not null &&
                fallbackTargetObject is not null && !fallbackTarget.IsCaptive() &&
                !NetworkActorCaptureManager.TestHasPendingClientCapture(captureActorId))
            {
                captureStageFallbackAttempted = true;
                var passThrough = NetworkActorCaptureManager.BeforeConsume(
                    fallbackVacuumItem, fallbackTargetObject, testStageRequest: true);
                SrLogger.LogMessage(
                    $"[SR2MP_TEST_BOT] test-only approved-stage request actor={captureActorId:X} " +
                    $"intercepted={(!passThrough)} after native input had no capture callback");
            }

            if (Time.unscaledTime >= nextCaptureMechanicsTraceAt)
            {
                nextCaptureMechanicsTraceAt = Time.unscaledTime + 2f;
                SrLogger.LogMessage($"[SR2MP_TEST_BOT] capture mechanics actor={captureActorId:X} " +
                                    $"owner={currentOwner ?? "<missing>"} held={heldMatches} " +
                                    DescribeCaptureMechanics());
            }

            if (TestGameplayBotPolicy.IsNativeHeldStateValid(heldMatches) &&
                ActorCaptureTransferPolicy.CanAdvanceCaptureBotToConsume(
                    string.Equals(currentOwner, LocalID, StringComparison.Ordinal),
                    captureActorObject,
                    heldMatches))
            {
                BeginWaitingForDestroy();
                SrLogger.LogMessage(
                    $"[SR2MP_TEST_BOT] capture ownership/native held PASS actor={captureActorId:X} " +
                    $"owner={currentOwner} captive={(captureVacuumable ? captureVacuumable.IsCaptive() : false)} " +
                    $"held={heldMatches} mode={captureVacuumItem._vacMode}");
                return;
            }

            if (Time.unscaledTime >= captureActionAt)
            {
                NetworkActorCaptureManager.CancelClientCapture(captureActorId, "test harness timed out waiting for native capture");
                TryStopVanillaVacuum();
                capturePhase = Finished;
                SrLogger.LogWarning(
                    $"[SR2MP_TEST_BOT] capture ownership/held FAIL actor={captureActorId:X} " +
                    $"owner={currentOwner ?? "<missing>"} captive={(captureVacuumable ? captureVacuumable.IsCaptive() : false)} " +
                    $"held={heldMatches} actorPresent={guestActorPresent} modelPresent={guestModelPresent} " +
                    $"distance={(captureActorObject ? Vector3.Distance(localPlayer.transform.position, captureActorObject.transform.position) : -1f):F2}");
            }

            return;
        }

        if (capturePhase == WaitingForDestroy)
        {
            var guestActorPresent = ActorManager.Actors.ContainsKey(captureActorId);
            var guestModelPresent = GameState.TryGetIdentifiableModel(new ActorId(captureActorId), out _);
            var nativeConsumeRemovalConfirmed =
                NetworkActorCaptureManager.TestWasConsumeRemovalConfirmed(captureActorId);
            var captureAmmoCountAfter = CountLocalAmmo(captureIdentifiableType);
            var guestAmmoIncrementConfirmed = ActorCaptureTransferPolicy.HasExpectedAmmoIncrement(
                captureAmmoCountBefore, captureAmmoCountAfter);

            if (captureStatusQueryPending && Time.unscaledTime >= captureActionAt)
            {
                captureStatusQueryPending = false;
                captureHostStatusReceived = false;
                SrLogger.LogWarning(
                    $"[SR2MP_TEST_BOT] capture status response timed out actor={captureActorId:X} " +
                    $"query={captureStatusQueryId}; retrying");
            }

            if (!guestActorPresent && !guestModelPresent && nativeConsumeRemovalConfirmed &&
                !captureStatusQueryPending &&
                Time.unscaledTime >= captureActionAt)
            {
                captureStatusQueryId++;
                captureStatusQueryPending = true;
                captureHostStatusReceived = false;
                captureActionAt = Time.unscaledTime + 3f;
                Main.Client.SendPacket(new TestCaptureStatusRequestPacket
                {
                    ProbeId = captureProbeId,
                    QueryId = captureStatusQueryId,
                    ActorId = captureActorId
                });
                SrLogger.LogMessage(
                    $"[SR2MP_TEST_BOT] capture status query actor={captureActorId:X} query={captureStatusQueryId}");
            }

            if (captureHostStatusReceived && nativeConsumeRemovalConfirmed &&
                TestGameplayBotPolicy.CanReportCapturePass(guestActorPresent, guestModelPresent,
                    captureHostActorPresent, captureHostModelPresent, guestAmmoIncrementConfirmed))
            {
                SrLogger.LogMessage(
                    $"[SR2MP_TEST_BOT] capture/consume PASS actor={captureActorId:X} " +
                    "guestActorPresent=false guestModelPresent=false " +
                    "hostActorPresent=false hostModelPresent=false " +
                    $"ammoType={captureIdentifiableType?.name} ammoBefore={captureAmmoCountBefore} " +
                    $"ammoAfter={captureAmmoCountAfter} query={captureStatusQueryId}");
                if (Environment.GetEnvironmentVariable("SR2MP_TEST_MASS_VAC") != "1")
                    TryStopVanillaVacuum();
                captureIterationsPassed++;
                SrLogger.LogMessage(
                    $"[SR2MP_TEST_BOT] capture iteration PASS iteration={captureIterationsPassed}/{repeatCount} " +
                    $"actor={captureActorId:X} ammoType={captureIdentifiableType?.name} " +
                    $"ammoBefore={captureAmmoCountBefore} ammoAfter={captureAmmoCountAfter} " +
                    $"ammoDelta={captureAmmoCountAfter - captureAmmoCountBefore}");

                if (captureIterationsPassed >= repeatCount)
                {
                    capturePhase = Finished;
                    TryStopVanillaVacuum();
                    SrLogger.LogMessage(
                        $"[SR2MP_TEST_BOT] {(Environment.GetEnvironmentVariable("SR2MP_TEST_MASS_VAC") == "1" ? "mass-vac suite" : "capture-suite")} PASS count={captureIterationsPassed}");
                    return;
                }

                capturePhase = SettlingAfterCapture;
                captureActionAt = Time.unscaledTime + CaptureReleaseSettleSeconds;
                if (Environment.GetEnvironmentVariable("SR2MP_TEST_MASS_VAC") == "1")
                    massVacNextAimAt = Time.unscaledTime;
                SrLogger.LogMessage(
                    $"[SR2MP_TEST_BOT] capture iteration {captureIterationsPassed} passed; " +
                    (Environment.GetEnvironmentVariable("SR2MP_TEST_MASS_VAC") == "1"
                        ? "keeping native vacuum active while sweeping to next target "
                        : $"allowing {CaptureReleaseSettleSeconds:F1}s for native vacuum release before next target ") +
                    DescribeNativeVacuumReleaseState());
                return;
            }

            if (captureHostStatusReceived)
            {
                captureHostStatusReceived = false;
                captureActionAt = Time.unscaledTime + 1f;
            }

            if (Time.unscaledTime >= captureTimeoutAt)
            {
                NetworkActorCaptureManager.CancelClientCapture(captureActorId,
                    "test harness timed out before both actor copies disappeared");
                SrLogger.LogError(
                    $"[SR2MP_TEST_BOT] capture/consume FAIL actor={captureActorId:X} " +
                    $"guestActorPresent={guestActorPresent} guestModelPresent={guestModelPresent} " +
                    $"hostStatusReceived={captureHostStatusReceived} " +
                    $"hostActorPresent={captureHostActorPresent} hostModelPresent={captureHostModelPresent} " +
                    $"nativeConsumeRemovalConfirmed={nativeConsumeRemovalConfirmed} " +
                    $"ammoType={captureIdentifiableType?.name} ammoBefore={captureAmmoCountBefore} " +
                    $"ammoAfter={captureAmmoCountAfter} ammoIncrementConfirmed={guestAmmoIncrementConfirmed}");
                TryStopVanillaVacuum();
                capturePhase = Finished;
            }
        }
    }

    private static void BeginWaitingForDestroy()
    {
        capturePhase = WaitingForDestroy;
        captureActionAt = Time.unscaledTime;
        captureTimeoutAt = Time.unscaledTime + 45f;
    }

    private static void ResetForNextCaptureIteration()
    {
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_MASS_VAC") == "1" && massVacReceivedTargetIds.Count > 0)
        {
            announcedCaptureTargetActorId = massVacReceivedTargetIds[0];
            massVacReceivedTargetIds.RemoveAt(0);
        }
        else
        {
            announcedCaptureTargetActorId = queuedCaptureTargetActorId;
        }
        queuedCaptureTargetActorId = 0;
        captureTargetWaitStarted = false;
        captureTargetWaitDeadline = 0f;
        captureProbeId = Guid.Empty;
        captureStatusQueryId = 0;
        captureStatusQueryPending = false;
        captureHostStatusReceived = false;
        captureHostActorPresent = false;
        captureHostModelPresent = false;
        captureTimeoutAt = 0f;
        captureActorId = 0;
        captureActorObject = null;
        captureVacuumable = null;
        captureVacuumItem = null;
        captureIdentifiableType = null;
        captureAmmoCountBefore = 0;
        captureStageFallbackAttempted = false;
        captureAbsenceWithoutNativeRemovalLogged = false;
        capturePhase = Idle;
        SrLogger.LogMessage(
            $"[SR2MP_TEST_BOT] capture iteration {captureIterationsPassed} complete; " +
            (announcedCaptureTargetActorId != 0
                ? $"queued next target actor={announcedCaptureTargetActorId:X}"
                : "waiting for host to announce the next target"));
    }

    private static string DescribeNativeVacuumReleaseState()
    {
        if (!captureVacuumItem)
            return "vacuumItem=<none>";

        var held = captureVacuumItem._held;
        var lockJoint = captureVacuumItem.LockJoint;
        return $"vacuumMode={captureVacuumItem._vacMode} pressed={captureVacuumItem._vacPressed} " +
               $"held={(held ? "present" : "none")} lockJoint={(lockJoint ? "present" : "none")}";
    }

    private static bool TrySetVanillaVacMode(VacuumItem vacuumItem, VacuumItem.VacMode mode)
    {
        var setVacMode = AccessTools.Method(typeof(VacuumItem), "SetVacMode",
            new[] { typeof(VacuumItem.VacMode) });
        if (setVacMode == null)
        {
            SrLogger.LogError(
                "[SR2MP_TEST_BOT] capture FAIL: native VacuumItem.SetVacMode(VacMode) was not found");
            return false;
        }

        try
        {
            setVacMode.Invoke(vacuumItem, new object[] { mode });
            return vacuumItem._vacMode == mode;
        }
        catch (Exception exception)
        {
            SrLogger.LogError($"[SR2MP_TEST_BOT] native SetVacMode({mode}) threw: {exception}");
            return false;
        }
    }

    private static string DescribeCaptureMechanics()
    {
        if (!captureVacuumItem || !captureVacuumable || !captureActorObject)
            return "vacuum mechanics unavailable";

        var originObject = captureVacuumItem.VacOrigin;
        var origin = originObject ? originObject.transform : null;
        var targetPosition = GetCaptureAimPoint();
        var targetLayer = captureActorObject.layer;
        var vacuumRayMask = DescribeVacuumRayMask("VAC_RAY_MASK", targetLayer);
        var targetingRayMask = DescribeVacuumRayMask("TARGETING_RAY_MASK", targetLayer);
        var directionToTarget = origin ? targetPosition - origin.position : Vector3.zero;
        var targetDistance = directionToTarget.magnitude;
        var aimAngle = origin && targetDistance > 0.01f
            ? Vector3.Angle(origin.up.normalized, directionToTarget)
            : -1f;
        var axisMiss = origin && targetDistance > 0.01f
            ? Vector3.Cross(origin.up.normalized, directionToTarget).magnitude / targetDistance
            : -1f;
        var hit = default(RaycastHit);
        var rayHit = origin && Physics.Raycast(origin.position, origin.up.normalized, out hit,
            Math.Max(targetDistance + 1f, 5f), Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        var rayHitTarget = rayHit && hit.transform &&
            (hit.transform == captureActorObject.transform || hit.transform.IsChildOf(captureActorObject.transform));
        var rayHitName = !rayHit ? "<clear>" : rayHitTarget ? "<target>" :
            hit.collider ? hit.collider.name : "<unknown>";
        var triggerHit = default(RaycastHit);
        var rayHitWithTriggers = origin && Physics.Raycast(origin.position, origin.up.normalized, out triggerHit,
            Math.Max(targetDistance + 1f, 5f), Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);
        var rayHitWithTriggersTarget = rayHitWithTriggers && triggerHit.transform &&
            (triggerHit.transform == captureActorObject.transform ||
             triggerHit.transform.IsChildOf(captureActorObject.transform));
        var rayHitWithTriggersName = !rayHitWithTriggers ? "<clear>" : rayHitWithTriggersTarget ? "<target>" :
            triggerHit.collider ? triggerHit.collider.name : "<unknown>";

        var renderers = captureActorObject.GetComponentsInChildren<Renderer>(true);
        var activeRenderers = renderers.Where(renderer => renderer && renderer.enabled &&
            renderer.gameObject.activeInHierarchy).ToArray();
        var visibleRenderers = activeRenderers.Count(renderer => renderer.isVisible);
        var colliders = captureActorObject.GetComponentsInChildren<Collider>(true);
        var activeColliders = colliders.Where(collider => collider && collider.enabled &&
            collider.gameObject.activeInHierarchy).ToArray();
        var colliderDetails = string.Join(" | ", activeColliders.Select(collider =>
            $"{GetHierarchyPath(collider.transform)} trigger={collider.isTrigger} " +
            $"layer={collider.gameObject.layer}:{LayerMask.LayerToName(collider.gameObject.layer)} " +
            $"center={collider.bounds.center} extents={collider.bounds.extents}"));
        var body = captureVacuumable._body;
        var networkActor = captureActorObject.GetComponent<NetworkActor>();
        var vacuumProperties = typeof(VacuumItem).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(property => property.Name.Contains("joint", StringComparison.OrdinalIgnoreCase) ||
                               property.Name.Contains("held", StringComparison.OrdinalIgnoreCase) ||
                               property.Name.Contains("vac", StringComparison.OrdinalIgnoreCase) ||
                               property.Name.Contains("input", StringComparison.OrdinalIgnoreCase))
            .Take(24)
            .Select(property =>
            {
                try
                {
                    return $"{property.Name}:{property.PropertyType.Name}={property.GetValue(captureVacuumItem) ?? "null"}";
                }
                catch (Exception exception)
                {
                    return $"{property.Name}:{property.PropertyType.Name}=<{exception.GetType().Name}>";
                }
            });
        return $"origin={(origin ? origin.position.ToString() : "<none>")} " +
               $"originNativeUp={(origin ? origin.up.normalized.ToString() : "<none>")} " +
               $"targetPoint={targetPosition} targetDistance={targetDistance:F2} " +
               $"aimAngle={aimAngle:F1} axisMiss={axisMiss:F2} rayHit={rayHitName} " +
               $"rayHitIncludingTriggers={rayHitWithTriggersName} " +
               $"{vacuumRayMask} {targetingRayMask} " +
               $"vacuumableActive={captureVacuumable.gameObject.activeInHierarchy} " +
               $"canCapture={captureVacuumable.CanCapture()} captive={captureVacuumable.IsCaptive()} " +
               $"captureDist={captureVacuumItem.CaptureDist:F2} " +
               $"vacuumMode={captureVacuumItem._vacMode} vacuumPressed={captureVacuumItem._vacPressed} " +
               $"bodyKinematic={(body ? body.isKinematic : false)} " +
               $"networkOwner={networkActor?.CurrentOwnerId ?? "<none>"} " +
               $"renderers={renderers.Length}/{activeRenderers.Length}/{visibleRenderers} " +
               $"colliders={colliders.Length}/{activeColliders.Length} colliderDetails=[{colliderDetails}] " +
               $"vacuumProperties=[{string.Join(",", vacuumProperties)}] " +
               $"actorPath={GetHierarchyPath(captureActorObject.transform)}";
    }

    private static string DescribeVacuumRayMask(string fieldName, int targetLayer)
    {
        try
        {
            var property = AccessTools.Property(typeof(VacuumItem), fieldName);
            object? value = property?.GetValue(null);
            if (property == null)
            {
                var field = AccessTools.Field(typeof(VacuumItem), fieldName);
                if (field == null)
                    return $"{fieldName}=<missing>";
                value = field.GetValue(null);
            }

            var mask = Convert.ToInt32(value);
            var layerName = LayerMask.LayerToName(targetLayer);
            var includesTarget = targetLayer >= 0 && targetLayer < 32 &&
                                 (mask & (1 << targetLayer)) != 0;
            return $"{fieldName}=0x{mask:X8} targetLayer={targetLayer}:{layerName} includesTarget={includesTarget}";
        }
        catch (Exception exception)
        {
            return $"{fieldName}=<{exception.GetType().Name}>";
        }
    }

    private static void TryStopVanillaVacuum()
    {
        if (!captureVacuumItem)
            return;

        try
        {
            var vacuumInput = captureVacuumItem._vac;
            if (vacuumInput != null)
                vacuumInput.Cancel();
        }
        catch (Exception exception)
        {
            SrLogger.LogWarning(
                $"[SR2MP_TEST_BOT] native VacuumItem._vac.Cancel() threw: {exception.GetType().Name}: {exception.Message}");
        }

        if (captureVacuumItem._vacMode != VacuumItem.VacMode.NONE &&
            !TrySetVanillaVacMode(captureVacuumItem, VacuumItem.VacMode.NONE))
        {
            // Test-only emergency cleanup if the native mode transition itself
            // fails; this does not participate in capture or consume.
            captureVacuumItem._vacMode = VacuumItem.VacMode.NONE;
        }
    }

    private static bool IsFullInventoryTargetUnderViewRay()
    {
        if (!captureActorObject || !captureVacuumItem || !captureVacuumItem._vacPressed ||
            captureVacuumItem._vacMode != VacuumItem.VacMode.VAC)
            return false;
        var camera = Camera.main;
        if (!camera)
            return false;
        var ray = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        if (!Physics.Raycast(ray, out var hit, captureVacuumItem.MaxVacDist,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) || !hit.collider)
            return false;
        var hitTransform = hit.collider.transform;
        var matches = hitTransform == captureActorObject.transform ||
                      hitTransform.IsChildOf(captureActorObject.transform);
        if (matches && !fullInventoryTargetObserved)
            SrLogger.LogMessage($"[SR2MP_TEST_BOT] capture-full-inventory target under view ray actor={captureActorId:X} distance={hit.distance:F2}");
        return matches;
    }

    private static Vector3 GetMassVacTargetCentroid(IEnumerable<long> actorIds)
    {
        var sum = Vector3.zero;
        var count = 0;
        foreach (var actorId in actorIds)
        {
            if (!ActorManager.Actors.TryGetValue(actorId, out var model) || model == null)
                continue;
            var actor = model.GetGameObject();
            if (!actor)
                continue;
            sum += actor.transform.position;
            count++;
        }
        return count > 0 ? sum / count : Vector3.zero;
    }

    /// <summary>Free room for this type: its own slots plus empty unlocked slots.</summary>
    private static int CountLocalAmmoCapacity(IdentifiableType? identifiableType)
    {
        if (!identifiableType)
            return -1;
        try
        {
            var ammo = SceneContext.Instance?.PlayerState?.Ammo;
            if (ammo?.Slots == null)
                return -1;
            var free = 0;
            foreach (var slot in ammo.Slots)
            {
                if (slot == null || !slot.IsUnlocked || slot.MaxCount <= 0)
                    continue;
                if (slot.Id == identifiableType)
                    free += Math.Max(0, slot.MaxCount - slot.Count);
                else if (!slot.Id || slot.Count == 0)
                    free += slot.MaxCount;
            }
            return free;
        }
        catch (Exception exception)
        {
            SrLogger.LogWarning($"[SR2MP_TEST_BOT] guest ammo capacity failed: {exception.GetType().Name}");
            return -1;
        }
    }

    private static int CountLocalAmmo(IdentifiableType? identifiableType)
    {
        if (!identifiableType)
            return -1;

        try
        {
            var ammo = SceneContext.Instance?.PlayerState?.Ammo;
            if (ammo?.Slots == null)
                return -1;

            var count = 0;
            foreach (var slot in ammo.Slots)
                if (slot != null && slot.Id == identifiableType)
                    count = checked(count + slot.Count);
            return count;
        }
        catch (Exception exception)
        {
            SrLogger.LogWarning($"[SR2MP_TEST_BOT] guest ammo count failed: {exception.GetType().Name}");
            return -1;
        }
    }

    private static string DescribeFullInventoryRejectionState(out bool guestActorPresent,
        out bool guestModelPresent, out bool assertionsPass)
    {
        guestActorPresent = false;
        guestModelPresent = false;
        assertionsPass = false;
        var ammoAfter = -1;
        var chickCanBeAdded = true;
        var unlockedCount = 0;
        var fullUnlockedCount = 0;
        var inventoryKnown = false;
        var inventorySlots = "<unavailable>";
        var pendingReservation = false;
        var nativeConsumeRemovalConfirmed = false;
        var captive = false;
        var heldTarget = false;
        var jointConnectedToTarget = false;
        var nativeStateAvailable = false;

        try
        {
            guestActorPresent = ActorManager.Actors.ContainsKey(captureActorId);
            guestModelPresent = GameState.TryGetIdentifiableModel(new ActorId(captureActorId), out _);
            ammoAfter = CountLocalAmmo(captureIdentifiableType);
            var ammo = SceneContext.Instance?.PlayerState?.Ammo;
            inventoryKnown = captureIdentifiableType && TrySummarizeFullInventory(ammo,
                captureIdentifiableType, out inventorySlots, out unlockedCount,
                out fullUnlockedCount, out chickCanBeAdded, out _);
            pendingReservation = NetworkActorCaptureManager.TestHasPendingClientCapture(captureActorId);
            nativeConsumeRemovalConfirmed =
                NetworkActorCaptureManager.TestWasConsumeRemovalConfirmed(captureActorId);
            nativeStateAvailable = captureVacuumItem && captureVacuumable && captureActorObject;
            if (nativeStateAvailable)
            {
                captive = captureVacuumable!.IsCaptive();
                heldTarget = captureVacuumItem!._held == captureActorObject;
                var lockJoint = captureVacuumItem!.LockJoint;
                jointConnectedToTarget = lockJoint && captureVacuumable!._body &&
                                         lockJoint.connectedBody == captureVacuumable._body;
            }

            var ammoUnchanged = captureAmmoCountBefore >= 0 && ammoAfter == captureAmmoCountBefore;
            var ammoDelta = captureAmmoCountBefore >= 0 && ammoAfter >= 0
                ? (ammoAfter - captureAmmoCountBefore).ToString()
                : "<unknown>";
            var allUnlockedSlotsFull = inventoryKnown && unlockedCount > 0 &&
                                       fullUnlockedCount == unlockedCount;
            var nativeReleaseSafe = nativeStateAvailable && !pendingReservation && !captive &&
                                    !heldTarget && !jointConnectedToTarget;
            assertionsPass = fullInventoryFixturePrepared && fullInventoryInputPerformed &&
                             fullInventoryTargetObserved && string.IsNullOrEmpty(fullInventoryEarlyReleaseReason) &&
                             guestActorPresent && guestModelPresent && !nativeConsumeRemovalConfirmed && ammoUnchanged &&
                             allUnlockedSlotsFull && !chickCanBeAdded && nativeReleaseSafe;
            return
                $"fixturePrepared={fullInventoryFixturePrepared} inputPerformed={fullInventoryInputPerformed} " +
                $"targetObserved={fullInventoryTargetObserved} guestActorPresent={guestActorPresent} " +
                $"guestModelPresent={guestModelPresent} ammoBefore={captureAmmoCountBefore} " +
                $"ammoAfter={ammoAfter} ammoDelta={ammoDelta} ammoUnchanged={ammoUnchanged} " +
                $"couldAddChick={chickCanBeAdded} chickCapacityBlocked={!chickCanBeAdded} " +
                $"unlockedFull={fullUnlockedCount}/{unlockedCount} inventoryKnown={inventoryKnown} " +
                $"slots=[{inventorySlots}] pendingReservation={pendingReservation} " +
                $"nativeConsumeRemovalConfirmed={nativeConsumeRemovalConfirmed} captive={captive} " +
                $"heldTarget={heldTarget} jointConnectedToTarget={jointConnectedToTarget} " +
                $"nativeStateAvailable={nativeStateAvailable} nativeReleaseSafe={nativeReleaseSafe} " +
                $"earlyReleaseReason={(string.IsNullOrEmpty(fullInventoryEarlyReleaseReason) ? "<none>" : fullInventoryEarlyReleaseReason)}";
        }
        catch (Exception exception)
        {
            assertionsPass = false;
            return
                $"stateInspectionException={exception.GetType().Name}:{exception.Message} " +
                $"fixturePrepared={fullInventoryFixturePrepared} inputPerformed={fullInventoryInputPerformed} " +
                $"targetObserved={fullInventoryTargetObserved} guestActorPresent={guestActorPresent} " +
                $"guestModelPresent={guestModelPresent} ammoBefore={captureAmmoCountBefore} ammoAfter={ammoAfter} " +
                $"pendingReservation={pendingReservation} " +
                $"nativeConsumeRemovalConfirmed={nativeConsumeRemovalConfirmed} captive={captive} heldTarget={heldTarget} " +
                $"jointConnectedToTarget={jointConnectedToTarget} " +
                $"earlyReleaseReason={fullInventoryEarlyReleaseReason}";
        }
    }

    private static void SendFullInventoryStatusQuery()
    {
        captureStatusQueryId++;
        captureStatusQueryPending = true;
        captureHostStatusReceived = false;
        captureActionAt = Time.unscaledTime + 3f;
        Main.Client.SendPacket(new TestCaptureStatusRequestPacket
        {
            ProbeId = captureProbeId,
            QueryId = captureStatusQueryId,
            ActorId = captureActorId
        });
        SrLogger.LogMessage(
            $"[SR2MP_TEST_BOT] capture-full-inventory host status query actor={captureActorId:X} " +
            $"query={captureStatusQueryId}");
    }

    internal static void ReceiveCaptureStatus(TestCaptureStatusResultPacket packet)
    {
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_MASS_VAC") == "1")
        {
            if (packet.ProbeId != massVacProbeId ||
                !massVacStatusQueries.TryGetValue(packet.ActorId, out var expectedQuery) ||
                packet.QueryId != expectedQuery)
                return;
            massVacHostStatus[packet.ActorId] = (packet.HostActorPresent, packet.HostModelPresent);
            SrLogger.LogMessage(
                $"[SR2MP_TEST_BOT] mass-vac host status actor={packet.ActorId:X} query={packet.QueryId} " +
                $"actorPresent={packet.HostActorPresent} modelPresent={packet.HostModelPresent}");
            return;
        }
        if (capturePhase is not (WaitingForDestroy or WaitingForInventoryRejectStatus) ||
            packet.ProbeId != captureProbeId ||
            packet.QueryId != captureStatusQueryId || packet.ActorId != captureActorId)
            return;

        captureHostStatusReceived = true;
        captureStatusQueryPending = false;
        captureHostActorPresent = packet.HostActorPresent;
        captureHostModelPresent = packet.HostModelPresent;
        if (packet.HostActorPresent || packet.HostModelPresent)
            captureActionAt = Time.unscaledTime + 1f;
        SrLogger.LogMessage(
            $"[SR2MP_TEST_BOT] {(capturePhase == WaitingForInventoryRejectStatus ? "capture-full-inventory host status" : "host capture status")} " +
            $"actor={captureActorId:X} query={packet.QueryId} " +
            $"actorPresent={packet.HostActorPresent} modelPresent={packet.HostModelPresent}");
    }

    internal static void ReceiveCaptureTarget(long actorId)
    {
        if (actorId <= 0 || actorId >= GlobalVariables.ActorIdOffset)
        {
            SrLogger.LogWarning($"[SR2MP_TEST_BOT] ignored invalid designated capture target actor={actorId:X}");
            return;
        }

        if (Environment.GetEnvironmentVariable("SR2MP_TEST_THROW_VAC") == "1")
        {
            TvReceiveAnnouncedTarget(actorId);
            return;
        }

        if (capturePhase == Finished)
            return;

        if (Environment.GetEnvironmentVariable("SR2MP_TEST_MASS_VAC") == "1")
        {
            if (actorId == announcedCaptureTargetActorId || actorId == captureActorId ||
                massVacOrderedTargetIds.Contains(actorId))
                return;
            massVacOrderedTargetIds.Add(actorId);
            if (announcedCaptureTargetActorId == 0 && capturePhase == Idle)
                announcedCaptureTargetActorId = actorId;
            else
                massVacReceivedTargetIds.Add(actorId);
            SrLogger.LogMessage($"[SR2MP_TEST_BOT] mass-vac target staged actor={actorId:X} remaining={massVacReceivedTargetIds.Count}");
            return;
        }

        if (actorId == announcedCaptureTargetActorId || actorId == captureActorId ||
            actorId == queuedCaptureTargetActorId)
        {
            SrLogger.LogMessage($"[SR2MP_TEST_BOT] ignored duplicate designated capture target actor={actorId:X}");
            return;
        }

        if (capturePhase == Idle && announcedCaptureTargetActorId == 0)
        {
            announcedCaptureTargetActorId = actorId;
            SrLogger.LogMessage($"[SR2MP_TEST_BOT] received designated capture target actor={actorId:X}");
            return;
        }

        if (capturePhase != Idle && queuedCaptureTargetActorId == 0)
        {
            queuedCaptureTargetActorId = actorId;
            SrLogger.LogMessage(
                $"[SR2MP_TEST_BOT] queued next designated capture target actor={actorId:X} " +
                $"while current actor={captureActorId:X} is being verified");
            return;
        }

        if (announcedCaptureTargetActorId != 0 && announcedCaptureTargetActorId != actorId)
        {
            SrLogger.LogError(
                $"[SR2MP_TEST_BOT] conflicting capture target announcements current={announcedCaptureTargetActorId:X} " +
                $"queued={queuedCaptureTargetActorId:X} next={actorId:X}");
            capturePhase = Finished;
            return;
        }

        announcedCaptureTargetActorId = actorId;
        SrLogger.LogMessage($"[SR2MP_TEST_BOT] received designated capture target actor={actorId:X}");
    }

    private static void SetPlayerPosition(GameObject player, Vector3 position)
    {
        // SR2 drives its player transform from KinematicCharacterMotor. A raw
        // Transform write is overwritten on the next character-controller tick,
        // so the test bot must teleport through the controller's authoritative
        // Position property for its networked movement/capture scenario.
        var controller = player.GetComponent<SRCharacterController>();
        if (controller)
        {
            controller.Position = position;
            controller.ResetVelocity(yOnly: false);
            controller.ForceUnground();
            return;
        }

        player.transform.position = position;
    }

    private static void PositionPlayerAtNozzleDistance(GameObject player, Vector3 actorPosition,
        VacuumItem vacuumItem, float requestedDistance, Vector3 aimPoint)
    {
        if (!player || !vacuumItem || !vacuumItem.VacOrigin)
            return;

        var outward = player.transform.position - actorPosition;
        outward.y = 0f;
        if (outward.sqrMagnitude < .001f)
            outward = Vector3.right;
        outward.Normalize();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var measured = Vector3.Distance(vacuumItem.VacOrigin.transform.position, aimPoint);
            var error = requestedDistance - measured;
            if (!float.IsFinite(measured) || Math.Abs(error) <= .025f)
                break;

            var rootPosition = player.transform.position + outward * error;
            SetPlayerPosition(player, rootPosition);
            var lookDirection = actorPosition - rootPosition;
            lookDirection.y = 0f;
            if (lookDirection.sqrMagnitude > .01f)
                player.transform.rotation = Quaternion.LookRotation(lookDirection);
            AimCameraAt(player, aimPoint);
        }
    }

    private static bool TryFindRemoteCapturableActor(long targetActorId, out long actorId,
        out GameObject? actorObject, out Vacuumable? vacuumable, out IdentifiableType? identifiableType,
        out int ammoCountBefore, out bool targetReplicated, out string failureReason)
    {
        if (targetActorId <= 0 || !ActorManager.Actors.TryGetValue(targetActorId, out var model) || model == null)
        {
            actorId = 0;
            actorObject = null;
            vacuumable = null;
            identifiableType = null;
            ammoCountBefore = -1;
            targetReplicated = false;
            failureReason = "announced actor model has not replicated yet";
            return false;
        }

        targetReplicated = true;
        actorId = targetActorId;
        actorObject = model.GetGameObject();
        vacuumable = actorObject ? actorObject.GetComponent<Vacuumable>() : null;
        var networkActor = actorObject ? actorObject.GetComponent<NetworkActor>() : null;
        var identifiable = actorObject ? actorObject.GetComponent<Identifiable>() : null;
        identifiableType = identifiable ? identifiable.identType : null;
        ammoCountBefore = -1;

        if (!actorObject || !actorObject.activeInHierarchy)
        {
            failureReason = "announced actor has no active game object";
            return false;
        }

        if (!TestGameplayBotPolicy.IsDesignatedCaptureCandidate(targetActorId, actorId,
                identifiableType ? identifiableType.name : null))
        {
            failureReason = $"actor type is not Chick ({identifiableType?.name ?? "<none>"})";
            return false;
        }

        var owner = networkActor?.CurrentOwnerId;
        if (!networkActor || string.IsNullOrWhiteSpace(owner) ||
            string.Equals(owner, LocalID, StringComparison.Ordinal))
        {
            failureReason = $"designated actor is not owned by another player ({owner ?? "<missing>"})";
            return false;
        }

        if (!vacuumable || !vacuumable.CanCapture())
        {
            failureReason = "designated Chick is not capturable";
            return false;
        }

        var ammo = SceneContext.Instance?.PlayerState?.Ammo;
        if (ammo == null)
        {
            failureReason = "guest ammo state is unavailable";
            return false;
        }

        if (fullInventoryRejectionMode)
        {
            if (!fullInventoryFixturePrepared)
            {
                if (!TryPrepareFullInventoryFixture(ammo, identifiableType!, out var fixtureFailure))
                {
                    failureReason = $"full-inventory fixture failed: {fixtureFailure}";
                    return false;
                }
            }

            if (!TrySummarizeFullInventory(ammo, identifiableType!, out var slotTrace,
                    out var unlockedCount, out var fullUnlockedCount, out var chickCanBeAdded,
                    out var inventoryFailure))
            {
                failureReason = $"full-inventory verification failed: {inventoryFailure}";
                return false;
            }

            if (fullUnlockedCount != unlockedCount || chickCanBeAdded)
            {
                failureReason =
                    $"full-inventory fixture no longer satisfies its postcondition " +
                    $"(unlockedFull={fullUnlockedCount}/{unlockedCount}, chickCapacity={chickCanBeAdded}, " +
                    $"slots=[{slotTrace}])";
                return false;
            }
        }
        else if (!ammo.CouldAddToAnySlot(new AmmoSlot.AmmoMetadata(identifiableType!)))
        {
            failureReason = "guest ammo has no capacity for the designated Chick";
            return false;
        }

        ammoCountBefore = CountLocalAmmo(identifiableType);
        if (ammoCountBefore < 0)
        {
            failureReason = "guest Chick ammo count is unavailable";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }
}
#endif

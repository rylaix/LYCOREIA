using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Player;
using Il2CppMonomiPark.SlimeRancher.Player.PlayerItems;
using SR2MP.Components.Actor;
using SR2MP.Packets.Actor;

namespace SR2MP.Shared.Managers;

/// <summary>
/// Keeps guest vacuum captures behind a host prepare/commit exchange. The native
/// capture runs only for a matching live approval; ownership changes only after
/// the client reports that native operation complete.
/// </summary>
internal static class NetworkActorCaptureManager
{
    private const int MaxPendingClientCaptures = 256;
    private const int MaxCompletedClientCaptures = 512;
    private const int MaxRememberedServerResults = 4096;
    private const float PendingClientPrepareTimeout = 8f;
    private const float PendingClientCommitTimeout = 30f;
    private const float CompletedClientCaptureTimeout = 60f;
    // Keep the prepared actor locked through the client's full commit retry window.
    // A lost first commit must not turn every later retry into a guaranteed rejection.
    private const float ServerCaptureReservationTimeout = 35f;
    private const float CommittedServerResultTimeout = 45f;
    private const float ClientRetryInterval = 1.5f;
    private const float NativeVacuumCandidateLifetime = 2.5f;
    private const int MaxPrepareRetries = 3;
    private const int MaxCommitRetries = 16;
    private const float ClientCapturePruneInterval = 1f;
    private const int MaxCaptureDenialRetries = 3;
    private const int MaxCaptureDenialStates = 256;
    private const float CaptureDenialStateLifetime = 30f;

    private static readonly Dictionary<long, PendingCapture> pendingClientCaptures = new();
    private static readonly Dictionary<Guid, CompletedCapture> completedClientCaptures = new();
    private static readonly Queue<Guid> completedClientCaptureOrder = new();
    private static readonly Dictionary<CaptureRequestKey, ActorTransferPacket> serverResults = new();
    private static readonly Dictionary<CaptureRequestKey, float> serverResultExpiresAt = new();
    private static readonly Dictionary<long, CaptureRequestKey> serverCaptureByActor = new();
    private static readonly Dictionary<long, ActorTransferPacket> deferredServerTransfers = new();
    private static readonly Queue<CaptureRequestKey> serverResultOrder = new();
    private static readonly HashSet<long> replayingCaptureActors = new();
    private static readonly Dictionary<long, float> failedCaptureRetryAfter = new();
    private static readonly Dictionary<long, CaptureDenialRetryState> captureDenialRetries = new();
    private static readonly Dictionary<int, VacuumActivationState> vacuumActivations = new();
    private static long nextVacuumActivationId;
    private static float nextClientCapturePruneAt;
#if SR2MP_TEST
    private static readonly Dictionary<long, float> testConsumeRemovalConfirmedUntil = new();
    private static readonly Dictionary<long, float> testConsumeCommitAcknowledgedUntil = new();
    private static bool testResyncAfterCommitRequested;
#endif

    static NetworkActorCaptureManager()
    {
        Main.Client.OnConnected += _ => ClearClientCaptures();
        Main.Client.OnDisconnected += ClearClientCaptures;
        Main.Server.OnServerStarted += ClearServerResults;
        Main.Server.ClientManager.OnClientRemoved += client => RemoveServerResults(client.PlayerId);
    }

    /// <summary>
    /// Fallback guard for VacuumItem.ConsumeVacuumable. The normal preflight runs
    /// earlier, in Vacuumable.Capture(Joint), before suction changes actor state.
    /// </summary>
    internal static bool BeforeConsume(VacuumItem vacuumItem, GameObject targetObject,
        bool testStageRequest = false)
    {
        nativeConsumeStartedActorId = 0;
        if (HandlingPacket || !vacuumItem || !targetObject ||
            (!Main.Server.IsRunning && !Main.Client.IsConnected))
            return true;

        var vacuumable = targetObject.GetComponent<Vacuumable>();
        if (!vacuumable)
            vacuumable = targetObject.GetComponentInParent<Vacuumable>();
        if (!vacuumable)
            return true;

        var networkActor = vacuumable.GetComponent<NetworkActor>();
        var actorId = vacuumable._identifiable ? vacuumable._identifiable.GetActorId() : default;
        if (actorId.Value == 0)
            return ActorCaptureTransferPolicy.CanPassCaptureWithoutActorId(
                Main.Server.IsRunning, networkActor != null,
                networkActor != null ? networkActor.CurrentOwnerId : null, LocalID);

        var ownerId = ResolveCaptureOwner(actorId.Value, networkActor);
        if (ownerId == null)
            return true;

#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_VAC_STATE") == "1")
        {
            var held = vacuumItem._held == targetObject || vacuumItem._held == vacuumable.gameObject;
            var body = vacuumable._body;
            var lockJoint = vacuumItem.LockJoint;
            var jointConnected = lockJoint && body && lockJoint.connectedBody == body;
            SrLogger.LogMessage(
                $"[SR2MP_TEST] {(testStageRequest ? "test-stage-request" : "native-vacuum-consume")} " +
                $"actor={actorId.Value:X} owner={ownerId} " +
                $"held={held} captive={vacuumable.IsCaptive()} canCapture={vacuumable.CanCapture()} " +
                $"body={(body != null)} lockJointConnected={jointConnected} " +
                $"mode={vacuumItem._vacMode} pressed={vacuumItem._vacPressed}");
        }
#endif

        if (replayingCaptureActors.Contains(actorId.Value))
            return true;

        if (!Main.Server.IsRunning &&
            pendingClientCaptures.TryGetValue(actorId.Value, out var approvedPending) &&
            approvedPending.Kind == PendingCaptureKind.Consume &&
            approvedPending.Phase == PendingCapturePhase.AwaitingCommit &&
            approvedPending.WaitingForNativeConsume)
        {
            if (approvedPending.VacuumItem != vacuumItem ||
                (approvedPending.TargetObject != targetObject &&
                 approvedPending.TargetObject != vacuumable.gameObject))
                return false;

#if SR2MP_TEST
            if (Environment.GetEnvironmentVariable("SR2MP_TEST_ASSIGN_APPROVED_ACTOR_HELD_ON_CONSUME") == "1")
            {
                var heldObject = vacuumItem._held;
                if (!heldObject)
                {
                    approvedPending.TestHeldBeforeConsume = heldObject;
                    approvedPending.TestHeldInjectionActive = true;
                    vacuumItem._held = targetObject;
                    SrLogger.LogMessage(
                        $"[SR2MP_TEST] approved-capture-held-on-consume actor={actorId.Value:X} " +
                        $"set={(vacuumItem._held == targetObject || vacuumItem._held == vacuumable.gameObject)}");
                }
                else if (heldObject != targetObject && heldObject != vacuumable.gameObject)
                {
                    SrLogger.LogWarning(
                        $"[SR2MP_TEST] approved-capture-held-on-consume skipped actor={actorId.Value:X}; " +
                        "vacuum already holds another object.");
                    return false;
                }
            }
#endif

            // Native polls Consume every frame while the actor is in the cone. Arm
            // only for the exact live approved joint so a lost pull cannot keep
            // re-arming the permit and deferring its cancellation indefinitely.
            if (!HasMatchingApprovedPullJoint(actorId.Value))
                return false;

            approvedPending.ConsumeDestroyPermitArmed = true;
            approvedPending.ConsumeDestroyPermitUntil = Time.unscaledTime + 2f;
            approvedPending.NativeConsumeInProgress = true;
            replayingCaptureActors.Add(actorId.Value);
            nativeConsumeStartedActorId = actorId.Value;
            return true;
        }

        if (Main.Server.IsRunning && HasServerCaptureReservation(actorId.Value))
        {
            SrLogger.LogDebug($"Host consume deferred while actor {actorId.Value} has a guest capture reservation.");
            return false;
        }

        if (string.Equals(ownerId, LocalID, StringComparison.Ordinal))
            return true;

        // A host is the authority. Its local native consume action can transfer
        // ownership immediately before the game mutates the host's ammo.
        if (Main.Server.IsRunning)
        {
            NetworkActorManager.RequestOwnershipTransfer(new ActorTransferPacket
            {
                ActorId = actorId,
                OwnerId = LocalID,
                PreviousOwnerId = ownerId
            });
            return true;
        }

        return QueueClientCapture(vacuumItem, targetObject, vacuumable, null, PendingCaptureKind.Consume,
            actorId, ownerId, nativeVacuumCandidate: !testStageRequest);
    }

    private static long nativeConsumeStartedActorId;

    /// <summary>
    /// Returns and clears the actor whose approved native consume BeforeConsume just
    /// started, so the patch can finalize exactly that transaction.
    /// </summary>
    internal static long TakeNativeConsumeStartedActor()
    {
        var actorId = nativeConsumeStartedActorId;
        nativeConsumeStartedActorId = 0;
        return actorId;
    }

    /// <summary>
    /// Commits an approved consume only after the native method actually removes
    /// its actor/model. A prepared joint alone does not prove the vacuum consumed it.
    /// </summary>
    internal static void AfterConsume(VacuumItem vacuumItem, long actorId, bool nativeThrew)
    {
        if (actorId == 0)
            return;

        // The replay marker lets native paths bypass host approval; never leave it set.
        replayingCaptureActors.Remove(actorId);
        if (!vacuumItem || !pendingClientCaptures.TryGetValue(actorId, out var pending) ||
            pending.VacuumItem != vacuumItem || !pending.NativeConsumeInProgress)
            return;

        pending.NativeConsumeInProgress = false;
        if (nativeThrew)
            Logger.LogDiagnostic("capture", "native-consume-threw",
                $"actorId={actorId:X}; requestId={pending.RequestId}");
        var consumeCompleted = ObserveLocalConsumeCompletion(pending);
#if SR2MP_TEST
        RestoreTestInjectedHeld(pending);
#endif
        if (!consumeCompleted)
        {
#if SR2MP_TEST
            if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") == "1")
                SrLogger.LogWarning(
                    $"[SR2MP_TEST] approved native consume returned without removing actor {pending.ActorId:X}; " +
                    "ownership commit remains withheld while native vacuum continues.");
#endif
            return;
        }

        CompleteApprovedClientCapture(pending.ActorId, pending);
    }

#if SR2MP_TEST
    private static void RestoreTestInjectedHeld(PendingCapture pending)
    {
        if (!pending.TestHeldInjectionActive)
            return;

        var heldObject = pending.VacuumItem ? pending.VacuumItem._held : null;
        if (pending.VacuumItem &&
            (!heldObject || heldObject == pending.TargetObject ||
             (pending.Target && heldObject == pending.Target.gameObject)))
        {
            pending.VacuumItem._held = pending.TestHeldBeforeConsume;
            if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") == "1")
                SrLogger.LogMessage(
                    $"[SR2MP_TEST] approved-capture-held-on-consume restored actor={pending.ActorId:X}");
        }

        pending.TestHeldInjectionActive = false;
        pending.TestHeldBeforeConsume = null;
    }
#endif

    /// <summary>
    /// Starts the same host-approved consume transaction when the local vacuum
    /// ray has a remote actor inside native capture range. Some remote actors do
    /// not enter the native VacRegion trigger, so waiting for Capture/Consume
    /// leaves the vanilla vacuum pulling an actor without ever asking for approval.
    /// </summary>
    internal static void RequestPredictedClientCapture(VacuumItem vacuumItem, Vacuumable vacuumable,
        float targetDistance)
    {
        if (HandlingPacket || Main.Server.IsRunning || !Main.Client.IsConnected ||
            !vacuumItem || !vacuumable)
            return;

        var networkActor = vacuumable.GetComponent<NetworkActor>();
        if (!networkActor || networkActor.LocallyOwned || networkActor.IsDestroyed)
            return;

        var actorId = vacuumable._identifiable ? vacuumable._identifiable.GetActorId() : default;
        if (actorId.Value == 0)
            return;

        var ownerId = ResolveCaptureOwner(actorId.Value, networkActor);
        if (string.IsNullOrWhiteSpace(ownerId) ||
            string.Equals(ownerId, LocalID, StringComparison.Ordinal))
            return;

        if (!ActorCaptureTransferPolicy.ShouldUseConsumePreflight(IsStoredInAmmo(actorId.Value)))
            return;

        var hadPendingRequest = pendingClientCaptures.ContainsKey(actorId.Value);
        QueueClientCapture(vacuumItem, vacuumable.gameObject, vacuumable, null,
            PendingCaptureKind.Consume, actorId, ownerId);
#if SR2MP_TEST
        if (!hadPendingRequest && Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") == "1")
            SrLogger.LogMessage(
                $"[SR2MP_TEST] actor-capture predicted-preflight actor={actorId.Value:X} " +
                $"owner={ownerId} distance={targetDistance:F2} captureDist={vacuumItem.CaptureDist:F2}");
#endif
    }

    /// <summary>
    /// Harmony prefix for Vacuumable.Capture(Joint). Deferring here prevents the
    /// guest from starting to mutate a host-owned actor before approval arrives.
    /// </summary>
    internal static bool BeforeCapture(Vacuumable vacuumable, Joint joint)
    {
        if (HandlingPacket || !vacuumable || (!Main.Server.IsRunning && !Main.Client.IsConnected))
            return true;

        var actorId = vacuumable._identifiable ? vacuumable._identifiable.GetActorId() : default;
        var networkActor = vacuumable.GetComponent<NetworkActor>();
        if (actorId.Value == 0)
            return ActorCaptureTransferPolicy.CanPassCaptureWithoutActorId(
                Main.Server.IsRunning, networkActor != null,
                networkActor != null ? networkActor.CurrentOwnerId : null, LocalID);

        var ownerId = ResolveCaptureOwner(actorId.Value, networkActor);
        if (ownerId == null)
            return true;

#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") == "1")
            SrLogger.LogMessage(
                $"[SR2MP_TEST] actor-capture native-hook actor={actorId.Value:X} owner={ownerId} " +
                $"networkComponent={(networkActor != null)} locallyOwned={networkActor && networkActor.LocallyOwned}");
#endif

        if (actorId.Value != 0 && joint && vacuumable._captiveToJoint == joint &&
            vacuumable.IsCaptive() && vacuumable._body && joint.connectedBody == vacuumable._body &&
            pendingClientCaptures.TryGetValue(actorId.Value, out var approvedCapture) &&
            approvedCapture.Phase == PendingCapturePhase.AwaitingCommit &&
            approvedCapture.Kind == PendingCaptureKind.Consume && approvedCapture.Target == vacuumable)
        {
            // Native SetCaptive destroys the previous joint before assigning the
            // next one, including when both references are identical.
            return false;
        }

        if (actorId.Value != 0 && replayingCaptureActors.Contains(actorId.Value))
            return true;

        if (Main.Server.IsRunning && actorId.Value != 0 && HasServerCaptureReservation(actorId.Value))
        {
            SrLogger.LogDebug($"Host capture deferred while actor {actorId.Value} has a guest capture reservation.");
            return false;
        }

        if (string.Equals(ownerId, LocalID, StringComparison.Ordinal))
            return true;

        if (Main.Server.IsRunning)
        {
            if (actorId.Value != 0)
                NetworkActorManager.RequestOwnershipTransfer(new ActorTransferPacket
                {
                    ActorId = actorId,
                    OwnerId = LocalID,
                    PreviousOwnerId = ownerId
                });
            return true;
        }

        if (actorId.Value == 0)
            return false;

        var player = SceneContext.Instance?.player;
        var itemController = player ? player.GetComponent<PlayerItemController>() : null;
        var vacuumItem = itemController ? itemController._vacuumItem : null;
        if (!vacuumItem || !joint)
        {
            SrLogger.LogWarning(
                $"Blocked vacuum capture for non-owned actor {actorId.Value}: the local vacuum joint is unavailable.");
            return false;
        }

        return QueueClientCapture(vacuumItem, vacuumable.gameObject, vacuumable, joint,
            PendingCaptureKind.Capture, actorId, ownerId, nativeVacuumCandidate: true);
    }

    /// <summary>
    /// Stops ForceJoint before vanilla adds an unapproved remote actor's joint to
    /// VacuumItem._joints. Approved replay is the only guest path that may create
    /// the native pull joint for a remotely owned actor.
    /// </summary>
    internal static bool BeforeForceJoint(VacuumItem vacuumItem, Vacuumable vacuumable)
    {
        if (HandlingPacket || !vacuumItem || !vacuumable ||
            (!Main.Server.IsRunning && !Main.Client.IsConnected))
            return true;

        var actorId = vacuumable._identifiable ? vacuumable._identifiable.GetActorId() : default;
        if (actorId.Value == 0)
            return true;
        if (replayingCaptureActors.Contains(actorId.Value))
            return true;

        var networkActor = vacuumable.GetComponent<NetworkActor>();
        var ownerId = ResolveCaptureOwner(actorId.Value, networkActor);
        if (ownerId == null || string.Equals(ownerId, LocalID, StringComparison.Ordinal))
            return true;
        if (!ActorCaptureTransferPolicy.ShouldUseConsumePreflight(IsStoredInAmmo(actorId.Value)))
            return true; // Hold-only targets retain the existing Capture(Joint) path.

        if (Main.Server.IsRunning)
        {
            if (HasServerCaptureReservation(actorId.Value))
                return false;
            NetworkActorManager.RequestOwnershipTransfer(new ActorTransferPacket
            {
                ActorId = actorId,
                OwnerId = LocalID,
                PreviousOwnerId = ownerId
            });
            return true;
        }

        QueueClientCapture(vacuumItem, vacuumable.gameObject, vacuumable, null,
            PendingCaptureKind.Consume, actorId, ownerId, nativeVacuumCandidate: true);
        return false;
    }

    private static bool CanAcceptCapturedAmmo(long actorId)
    {
        try
        {
            var ammo = SceneContext.Instance?.PlayerState?.Ammo;
            if (ammo == null || !ActorManager.Actors.TryGetValue(actorId, out var model) || model.ident == null)
                return false;
            if (ammo.Slots == null)
                return false;
            if (IsHoldOnlySize(model))
                return true; // held, never stored: no slot capacity applies
            var knownDefinition = false;
            var storedInAmmo = false;
            foreach (var slot in ammo.Slots)
            {
                if (slot?.Definition == null)
                    continue;
                knownDefinition = true;
                storedInAmmo |= slot.Definition.IsAllowed(model.ident);
            }
            // Hold-only and direct-pickup actors must keep their native rules;
            // the capacity gate applies to types that the player's slots store.
            return knownDefinition && (!storedInAmmo ||
                ammo.CouldAddToAnySlot(new AmmoSlot.AmmoMetadata(model.ident)));
        }
        catch (Exception exception)
        {
            SrLogger.LogWarning($"Capture capacity unavailable for actor {actorId}: {exception.GetType().Name}");
            return false;
        }
    }

    private static bool IsStoredInAmmo(long actorId)
    {
        try
        {
            if (!ActorManager.Actors.TryGetValue(actorId, out var model) || model.ident == null)
                return false;
            if (IsHoldOnlySize(model))
                return false;
            var slots = SceneContext.Instance?.PlayerState?.Ammo?.Slots;
            if (slots == null)
                return false;
            foreach (var slot in slots)
                if (slot?.Definition != null && slot.Definition.IsAllowed(model.ident))
                    return true;
            return false;
        }
        catch (Exception exception)
        {
            SrLogger.LogWarning(
                $"Could not classify vacuum actor {actorId} for consume preflight: {exception.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// LARGE/GIANT vacuumables (breakable containers such as ContainerFields01) are only ever held and
    /// thrown, never stored, even when a slot definition would accept their type. Treating them as
    /// consumable sent a Consume request whose pull was cancelled as soon as native switched to holding.
    /// </summary>
    private static bool IsHoldOnlySize(Il2CppMonomiPark.SlimeRancher.DataModel.IdentifiableModel model)
    {
        var gameObject = model.GetGameObject();
        var vacuumable = gameObject ? gameObject.GetComponent<Vacuumable>() : null;
        return vacuumable && vacuumable!.Size != Il2CppMonomiPark.SlimeRancher.VacuumableSize.NORMAL;
    }

    private static string? ResolveCaptureOwner(long actorId, NetworkActor? networkActor)
    {
        var hasCachedOwner = ActorManager.OwnershipByActorId.TryGetValue(actorId, out var cachedOwnerId);
        return ActorCaptureTransferPolicy.ResolveActorOwner(
            hasNetworkActor: networkActor != null,
            networkOwnerId: networkActor != null ? networkActor.CurrentOwnerId : null,
            hasCachedOwner: hasCachedOwner,
            cachedOwnerId: cachedOwnerId);
    }

    /// <summary>
    /// Releases a guest-side prepare/commit transaction when the native game
    /// action or a test probe fails before it can finish. Prepared server-side
    /// ownership reservations are explicitly aborted; an already-mutated local
    /// consume requests an authoritative resync as well.
    /// </summary>
    internal static bool IsWaitingForApprovedConsume(long actorId)
        => pendingClientCaptures.TryGetValue(actorId, out var pending) &&
           pending.Phase == PendingCapturePhase.AwaitingCommit && pending.WaitingForNativeConsume;

    internal static bool HasMatchingApprovedPullJoint(long actorId)
        => pendingClientCaptures.TryGetValue(actorId, out var pending) &&
           pending.Phase == PendingCapturePhase.AwaitingCommit && pending.WaitingForNativeConsume &&
           pending.Kind == PendingCaptureKind.Consume && pending.ApprovedPullJoint &&
           IsMatchingNativePullStage(pending, pending.ApprovedPullJoint, out _);

    internal static bool IsFreshNativeVacuumCandidate(long actorId)
        => pendingClientCaptures.TryGetValue(actorId, out var pending) &&
           pending.NativeVacuumCandidate &&
           ActorCaptureTransferPolicy.IsFreshNativeVacuumCandidate(
               Time.unscaledTime, pending.NativeVacuumCandidateAt, NativeVacuumCandidateLifetime,
               pending.VacuumActivationId, GetVacuumActivationId(pending.VacuumItem));

    internal static void CancelClientCapturesForVacuum(VacuumItem vacuumItem, string reason)
    {
        if (!vacuumItem)
            return;
        foreach (var (actorId, pending) in pendingClientCaptures.ToArray())
        {
            if (pending.VacuumItem != vacuumItem)
                continue;
            CancelClientCapture(actorId, reason);
        }
    }

    internal static void CancelUnapprovedClientCapturesExcept(VacuumItem vacuumItem,
        long currentTargetActorId, string reason)
    {
        if (!vacuumItem)
            return;
        foreach (var (actorId, pending) in pendingClientCaptures.ToArray())
            if (pending.VacuumItem == vacuumItem && pending.Phase == PendingCapturePhase.AwaitingApproval &&
                actorId != currentTargetActorId && !IsFreshNativeVacuumCandidate(actorId))
                CancelClientCapture(actorId, reason);
    }

    internal static bool ShouldReplicateApprovedConsumePull(long actorId)
        => actorId != 0 && pendingClientCaptures.TryGetValue(actorId, out var pending) &&
           pending.Phase == PendingCapturePhase.AwaitingCommit && pending.WaitingForNativeConsume &&
           pending.Kind == PendingCaptureKind.Consume && !pending.CommitSent &&
           !IsClientCaptureExpired(Time.unscaledTime, pending);

    /// <summary>
    /// A guest's native vacuum joint moves a remotely owned actor locally while
    /// the host waits for consume completion. Permit those transform samples only
    /// for the exact, live host-approved consume reservation.
    /// </summary>
    internal static bool IsApprovedConsumePullUpdate(string playerId, long actorId, out string expectedOwnerId)
    {
        expectedOwnerId = string.Empty;
        if (string.IsNullOrWhiteSpace(playerId) || actorId == 0 ||
            !serverCaptureByActor.TryGetValue(actorId, out var key) || key.PlayerId != playerId ||
            !serverResults.TryGetValue(key, out var approval) || !approval.CaptureIntent ||
            !approval.CaptureApproved || approval.CaptureCommit || !approval.CaptureConsumesActor ||
            approval.ActorId.Value != actorId || approval.CaptureRequestId != key.RequestId ||
            !serverResultExpiresAt.TryGetValue(key, out var expiresAt) || expiresAt <= Time.unscaledTime)
            return false;

        expectedOwnerId = approval.PreviousOwnerId;
        return !string.IsNullOrWhiteSpace(expectedOwnerId);
    }

    private static void RemovePendingClientCapture(long actorId)
    {
        replayingCaptureActors.Remove(actorId);
        if (pendingClientCaptures.TryGetValue(actorId, out var pending))
        {
            ReleaseApprovedPullJoint(pending);
            pending.ApprovedPullJoint = null;
        }
        LocalActorVacuumPredictionManager.EndApprovedConsume(actorId);
        pendingClientCaptures.Remove(actorId);
    }

    private static void ReleaseApprovedPullJoint(PendingCapture pending)
    {
        if (pending.CommitSent || pending.LocalDestroyObserved || !pending.ApprovedPullJoint ||
            !pending.Target || !pending.VacuumItem || !pending.Target._body)
            return;

        if (!IsMatchingNativePullStage(pending, pending.ApprovedPullJoint, out _))
            return;

        try
        {
            pending.Target.Release();
        }
        catch (Exception exception)
        {
            SrLogger.LogWarning(
                $"Could not release approved vacuum pull joint actor={pending.ActorId:X}: " +
                $"{exception.GetType().Name}");
        }
    }

    /// <summary>
    /// Compact diagnostic context for capture events: readable type, requester, and the
    /// distance from the requesting nozzle (local vacuum) or avatar (remote requester) to the actor.
    /// </summary>
    internal static string DiagActorContext(long actorId, string? requesterId = null, VacuumItem? vacuumItem = null)
    {
        try
        {
            var type = "<unknown>";
            var distance = float.NaN;
            if (ActorManager.Actors.TryGetValue(actorId, out var model) && model != null)
            {
                type = model.ident ? model.ident.name : type;
                var obj = model.GetGameObject();
                if (obj)
                {
                    var from = Vector3.zero;
                    var haveFrom = false;
                    if (string.IsNullOrEmpty(requesterId) || string.Equals(requesterId, LocalID, StringComparison.Ordinal))
                    {
                        if (!vacuumItem)
                        {
                            var player = SceneContext.Instance ? SceneContext.Instance.player : null;
                            var controller = player ? player.GetComponent<PlayerItemController>() : null;
                            vacuumItem = controller ? controller._vacuumItem : null;
                        }

                        var origin = vacuumItem ? vacuumItem.VacOrigin : null;
                        if (origin)
                        {
                            from = origin.transform.position;
                            haveFrom = true;
                        }
                    }
                    else if (PlayerManager.GetPlayer(requesterId!) is { } remote)
                    {
                        from = remote.Position;
                        haveFrom = true;
                    }

                    if (haveFrom)
                        distance = Vector3.Distance(from, obj.transform.position);
                }
            }

            return $"type={type}; requester={requesterId ?? "<none>"}; nozzleDistance={distance:F2}";
        }
        catch (Exception exception)
        {
            return $"type=<error>; diagError={exception.GetType().Name}";
        }
    }

    internal static void CancelClientCapture(long actorId, string reason)
    {
        if (actorId == 0 || !pendingClientCaptures.TryGetValue(actorId, out var pending))
            return;

        if (pending.Kind == PendingCaptureKind.Consume &&
            pending.Phase == PendingCapturePhase.AwaitingCommit &&
            ObserveLocalConsumeCompletion(pending))
        {
            CompleteApprovedClientCapture(actorId, pending);
            return;
        }
        if (!ActorCaptureTransferPolicy.CanCancelPendingCapture(
                pending.LocalDestroyObserved, pending.CommitSent, pending.NativeConsumeInProgress,
                pending.ConsumeDestroyPermitArmed, Time.unscaledTime, pending.ConsumeDestroyPermitUntil,
                pending.ConsumeDestroyAttemptAccepted, pending.ConsumeDestroyAttemptUntil))
        {
            Logger.LogDiagnostic("capture", "cancel-deferred-irreversible",
                $"actorId={actorId:X}; requestId={pending.RequestId}; reason={reason}; " +
                $"destroyPermitArmed={pending.ConsumeDestroyPermitArmed}; " +
                $"destroyAttemptAccepted={pending.ConsumeDestroyAttemptAccepted}; " +
                $"nativeConsumeInProgress={pending.NativeConsumeInProgress}; " +
                DiagActorContext(actorId, LocalID, pending.VacuumItem),
                rateKey: "capture-cancel-deferred", minimumIntervalMilliseconds: 1000);
            return;
        }

        var resync = ActorCaptureTransferPolicy.ShouldResyncAfterCancel(
            pending.Phase == PendingCapturePhase.AwaitingCommit, pending.TargetObject, pending.Target,
            GameState.TryGetIdentifiableModel(new ActorId(actorId), out _));

        RemovePendingClientCapture(actorId);
        replayingCaptureActors.Remove(actorId);
        completedClientCaptures.Remove(pending.RequestId);

#if SR2MP_DEEPLOG || SR2MP_TEST
        Logger.LogDiagnostic("capture", "cancelled",
            $"actorId={actorId:X}; requestId={pending.RequestId}; reason={reason}; phase={pending.Phase}; " +
            $"resync={resync}; " + DiagActorContext(actorId, LocalID, pending.VacuumItem),
            $"capture-cancelled:{actorId:X}", 1000);
#endif
        if (pending.RequestId != Guid.Empty)
            SendCaptureAbort(actorId, pending.RequestId);

        if (resync)
            RequestAuthoritativeResync(actorId, reason);
        else if (pending.Phase == PendingCapturePhase.AwaitingCommit)
            Logger.LogDiagnostic("capture", "cancel-without-resync",
                $"actorId={actorId:X}; requestId={pending.RequestId}; reason={reason}; actorIntact=True");

        SrLogger.LogWarning(
            $"Cancelled guest actor capture actor={actorId} request={pending.RequestId} " +
            $"phase={pending.Phase}: {reason}");
    }

    /// <summary>
    /// Called from VacuumItem.Update so timed out requests release their Unity
    /// references even when the player never starts another capture.
    /// </summary>
    internal static void Pump(VacuumItem vacuumItem)
    {
        var now = Time.unscaledTime;
        ObserveVacuumActivation(vacuumItem);
        PruneCaptureDenialRetries(now);
        var currentSceneGroupId = GetCurrentSceneGroupId();
        foreach (var (actorId, state) in captureDenialRetries.ToArray())
            if (state.SceneGroupId != currentSceneGroupId)
                captureDenialRetries.Remove(actorId);
        PruneExpiredServerCaptures(now);
        PruneExpiredCompletedClientCaptures(now);

        if (now >= nextClientCapturePruneAt)
        {
            nextClientCapturePruneAt = now + ClientCapturePruneInterval;
            PumpClientCaptures(now);
        }
    }

    private static void ObserveVacuumActivation(VacuumItem vacuumItem)
    {
        if (!vacuumItem || !LocalActorVacuumPredictionManager.IsLocalVacuumItem(vacuumItem))
            return;

        var instanceId = vacuumItem.GetInstanceID();
        var active = vacuumItem._vacPressed && vacuumItem._vacMode == VacuumItem.VacMode.VAC;
        if (!vacuumActivations.TryGetValue(instanceId, out var state))
        {
            state = new VacuumActivationState();
            vacuumActivations[instanceId] = state;
        }

        if (state.Active && !active)
        {
            ClearDenialRetriesForActivation(instanceId, state.ActivationId);
            state.Active = false;
        }
        else if (!state.Active && active)
        {
            state.ActivationId = ++nextVacuumActivationId;
            state.Active = true;
        }
    }

    private static long GetVacuumActivationId(VacuumItem vacuumItem)
    {
        if (!vacuumItem || !LocalActorVacuumPredictionManager.IsLocalVacuumItem(vacuumItem))
            return 0;
        return vacuumActivations.TryGetValue(vacuumItem.GetInstanceID(), out var state) && state.Active
            ? state.ActivationId
            : 0;
    }

    private static int GetCurrentSceneGroupId()
    {
        try
        {
            var sceneGroup = SystemContext.Instance?.SceneLoader?.CurrentSceneGroup;
            return sceneGroup == null ? -1 : NetworkSceneManager.GetPersistentID(sceneGroup);
        }
        catch
        {
            return -1;
        }
    }

    private static bool CanRetryAfterCaptureDenial(long actorId, string ownerId,
        VacuumItem vacuumItem, float now)
    {
        var activationId = GetVacuumActivationId(vacuumItem);
        if (activationId == 0 || !captureDenialRetries.TryGetValue(actorId, out var state))
            return true;

        var sameContext = ActorCaptureTransferPolicy.MatchesCaptureDenialContext(
            state.ExpectedOwnerId, ownerId, state.SceneGroupId, GetCurrentSceneGroupId(),
            state.VacuumActivationId, activationId);
        if (now >= state.ExpiresAt || !sameContext)
        {
            captureDenialRetries.Remove(actorId);
            return true;
        }

        return !ActorCaptureTransferPolicy.ShouldSuppressCaptureRetry(
            now, state.RetryAfter, sameContext: true);
    }

    private static int RecordCaptureDenial(PendingCapture pending)
    {
        if (pending.VacuumActivationId == 0)
            return 0;

        var now = Time.unscaledTime;
        if (!captureDenialRetries.TryGetValue(pending.ActorId, out var state) ||
            !ActorCaptureTransferPolicy.MatchesCaptureDenialContext(
                state.ExpectedOwnerId, pending.ExpectedOwnerId, state.SceneGroupId,
                pending.SceneGroupId, state.VacuumActivationId, pending.VacuumActivationId) ||
            now >= state.ExpiresAt)
        {
            state = new CaptureDenialRetryState(pending.ExpectedOwnerId, pending.SceneGroupId,
                pending.VacuumInstanceId, pending.VacuumActivationId);
            captureDenialRetries[pending.ActorId] = state;
        }

        state.DenialCount = Math.Min(MaxCaptureDenialRetries, state.DenialCount + 1);
        var delay = ActorCaptureTransferPolicy.GetCaptureDenialRetryDelayMilliseconds(state.DenialCount);
        state.RetryAfter = now + delay / 1000f;
        state.ExpiresAt = now + CaptureDenialStateLifetime;

        if (captureDenialRetries.Count > MaxCaptureDenialStates)
        {
            var oldest = captureDenialRetries.MinBy(pair => pair.Value.ExpiresAt).Key;
            captureDenialRetries.Remove(oldest);
        }

        Logger.LogDiagnostic("capture", "denial-retry-scheduled",
            $"actorId={pending.ActorId:X}; requestId={pending.RequestId}; owner={pending.ExpectedOwnerId}; " +
            $"activation={pending.VacuumActivationId}; denialCount={state.DenialCount}; retryDelayMs={delay}; " +
            DiagActorContext(pending.ActorId, LocalID, pending.VacuumItem));
        return delay;
    }

    private static void ClearDenialRetriesForActivation(int vacuumInstanceId, long activationId)
    {
        foreach (var (actorId, state) in captureDenialRetries.ToArray())
            if (state.VacuumInstanceId == vacuumInstanceId && state.VacuumActivationId == activationId)
                captureDenialRetries.Remove(actorId);
    }

    private static void PruneCaptureDenialRetries(float now)
    {
        foreach (var (actorId, state) in captureDenialRetries.ToArray())
            if (now >= state.ExpiresAt)
                captureDenialRetries.Remove(actorId);
    }

    private static bool QueueClientCapture(VacuumItem vacuumItem, GameObject targetObject,
        Vacuumable vacuumable, Joint? joint, PendingCaptureKind kind, ActorId actorId, string ownerId,
        bool nativeVacuumCandidate = false)
    {
        PruneExpiredClientCaptures(Time.unscaledTime);
        if (failedCaptureRetryAfter.TryGetValue(actorId.Value, out var retryAfter))
        {
            if (Time.unscaledTime < retryAfter)
                return false;
            failedCaptureRetryAfter.Remove(actorId.Value);
        }
        if (pendingClientCaptures.TryGetValue(actorId.Value, out var pending))
        {
            if (!IsClientCaptureExpired(Time.unscaledTime, pending))
            {
                if (pending.Phase != PendingCapturePhase.AwaitingApproval)
                    return false;

                // Keep the latest live object references while the same actor's
                // approval is in flight. The operation kind was already sent on
                // the wire and cannot be relabelled under the same request ID.
                pending.VacuumItem = vacuumItem;
                pending.TargetObject = targetObject;
                pending.Target = vacuumable;
                pending.NativeVacuumCandidate |= nativeVacuumCandidate;
                if (nativeVacuumCandidate)
                    pending.NativeVacuumCandidateAt = Time.unscaledTime;
                if (ActorCaptureTransferPolicy.CanMergePendingCaptureJoint(
                        existingIsCapture: pending.Kind == PendingCaptureKind.Capture,
                        incomingIsCapture: kind == PendingCaptureKind.Capture))
                {
                    if (joint)
                        pending.Joint = joint;
                }
                else if (pending.Kind != kind && !pending.MergeMismatchLogged)
                {
                    pending.MergeMismatchLogged = true;
                    Logger.LogDiagnostic("capture", "capture-intent-merge-ignored",
                        $"actorId={actorId.Value:X}; requestId={pending.RequestId}; " +
                        $"existingKind={pending.Kind}; incomingKind={kind}");
                }
#if SR2MP_TEST
                if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") == "1")
                    SrLogger.LogMessage(
                        $"[SR2MP_TEST] actor-capture pending-merge actor={actorId.Value:X} " +
                        $"incoming={kind} effective={pending.Kind} joint={(pending.Joint != null)}");
#endif
                return false;
            }

            RemovePendingClientCapture(actorId.Value);
        }

        if (!CanRetryAfterCaptureDenial(actorId.Value, ownerId, vacuumItem, Time.unscaledTime))
            return false;

        if (!CanAcceptCapturedAmmo(actorId.Value))
            return false;

        if (pendingClientCaptures.Count >= MaxPendingClientCaptures)
        {
            SrLogger.LogWarning("Actor capture deferred: too many ownership approvals are pending.");
            return false;
        }

        var requestId = Guid.NewGuid();
        pendingClientCaptures[actorId.Value] = new PendingCapture(
            actorId.Value, ownerId, vacuumItem, targetObject, vacuumable, joint,
            kind, requestId, Time.unscaledTime, Time.unscaledTime + ClientRetryInterval,
            vacuumItem.GetInstanceID(), GetVacuumActivationId(vacuumItem), GetCurrentSceneGroupId(),
            nativeVacuumCandidate, nativeVacuumCandidate ? Time.unscaledTime : 0f);
        try
        {
            Main.Client.SendPacket(CreateClientCapturePacket(actorId.Value,
                pendingClientCaptures[actorId.Value], commit: false));
            Logger.LogDiagnostic("capture", "approval-requested",
                $"actorId={actorId.Value:X}; requestId={requestId}; kind={kind}; previousOwner={ownerId}; " +
                $"claimant={LocalID}; jointPresent={(joint != null)}; targetPosition={targetObject.transform.position.ToString("F2")}; " +
                DiagActorContext(actorId.Value, LocalID, vacuumItem));
            var sameVacuumPending = pendingClientCaptures.Values.Count(other =>
                other.VacuumItem == vacuumItem && !other.CommitSent);
            if (sameVacuumPending >= 2)
                Logger.LogDiagnostic("capture", "multi-vacuum-pending",
                    $"actorId={actorId.Value:X}; requestId={requestId}; pendingForVacuum={sameVacuumPending}; " +
                    $"vacuumInstance={vacuumItem.GetInstanceID()}; activation={GetVacuumActivationId(vacuumItem)}");
#if SR2MP_TEST
            if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") == "1")
                SrLogger.LogMessage(
                    $"[SR2MP_TEST] actor-capture request actor={actorId.Value:X} " +
                    $"previous={ownerId} claimant={LocalID} request={requestId}");
#endif
        }
        catch (Exception exception)
        {
            RemovePendingClientCapture(actorId.Value);
            SrLogger.LogError($"Could not request actor capture approval for {actorId.Value}: {exception}");
        }

        // Fail closed: do not grant ammo until an explicit host ACK is received.
        return false;
    }

    /// <summary>
    /// Resolves a prepare result without changing ownership. A matching live
    /// capture runs once and then reports its completion to the host.
    /// </summary>
    internal static void ResolveClientRequest(ActorTransferPacket packet)
    {
        if (!packet.CaptureIntent)
            return;

        if (!pendingClientCaptures.TryGetValue(packet.ActorId.Value, out var pending))
        {
            if (packet.CaptureCommit && string.Equals(packet.OwnerId, LocalID, StringComparison.Ordinal))
                ResolveCompletedClientCapture(packet);
            else if (packet.CaptureCommit)
                completedClientCaptures.Remove(packet.CaptureRequestId);
            return;
        }

        if (!ActorCaptureTransferPolicy.MatchesPendingRequest(pending.RequestId, packet.CaptureRequestId))
            return;

        if (!ActorCaptureTransferPolicy.MatchesCaptureOperation(
                packet.CaptureConsumesActor, pending.Kind == PendingCaptureKind.Consume))
        {
            if (!pending.OperationMismatchLogged)
            {
                pending.OperationMismatchLogged = true;
                Logger.LogDiagnostic("capture", "operation-mismatch",
                    $"actorId={packet.ActorId.Value:X}; requestId={packet.CaptureRequestId}; " +
                    $"pendingKind={pending.Kind}; packetConsumesActor={packet.CaptureConsumesActor}; " +
                    $"commit={packet.CaptureCommit}");
            }
            if (!ActorCaptureTransferPolicy.ShouldAbortCaptureOperationMismatch(
                    packet.CaptureCommit, pending.Phase == PendingCapturePhase.AwaitingApproval))
            {
                // Preserve an in-progress or irreversible transaction; a stale
                // prepare result must not discard its commit retry or tombstone.
                return;
            }

            RemovePendingClientCapture(packet.ActorId.Value);
            SendCaptureAbort(packet.ActorId.Value, packet.CaptureRequestId);
            RequestAuthoritativeResync(packet.ActorId.Value, "capture operation kind mismatch");
            return;
        }

        if (packet.CaptureCommit)
        {
            if (pending.Phase != PendingCapturePhase.AwaitingCommit)
                return;

            var isCommittedOwner = string.Equals(packet.OwnerId, LocalID, StringComparison.Ordinal);
#if SR2MP_TEST
            if (packet.CaptureApproved && isCommittedOwner && packet.CaptureConsumesActor)
            {
                testConsumeCommitAcknowledgedUntil[packet.ActorId.Value] = Time.unscaledTime + 150f;
                SrLogger.LogMessage($"[SR2MP_TEST] actor-consume commit-acknowledged actor={packet.ActorId.Value:X}");
            }
#endif
            var actorModelStillExists = GameState.TryGetIdentifiableModel(packet.ActorId, out _);
            var localDestroyObserved = pending.LocalDestroyObserved || !pending.TargetObject || !pending.Target ||
                                       !actorModelStillExists;
            var sendDestroy = ActorCaptureTransferPolicy.CanSendCaptureDestroyAfterCommit(
                acceptedCommit: isCommittedOwner,
                matchingPendingRequest: true,
                wasConsumeOperation: pending.Kind == PendingCaptureKind.Consume,
                localDestroyObserved: localDestroyObserved);
            RemovePendingClientCapture(packet.ActorId.Value);
            if (!isCommittedOwner)
            {
                completedClientCaptures.Remove(packet.CaptureRequestId);
                SrLogger.LogWarning(
                    $"Host rejected completed capture for actor {packet.ActorId.Value}; local state will resync.");
                RequestAuthoritativeResync(packet.ActorId.Value, "host rejected capture commit");
            }
            else if (!packet.CaptureApproved)
                SrLogger.LogWarning(
                    $"Capture commit {packet.CaptureRequestId} was not accepted as a transaction, " +
                    $"but the host reports actor {packet.ActorId.Value} is already owned by this client; " +
                    "using the host's owner state as the authoritative result.");
            else if (pending.Kind == PendingCaptureKind.Consume && !localDestroyObserved)
                SrLogger.LogWarning(
                    $"Consume returned for actor {packet.ActorId.Value} without an observed Destroyer call, " +
                    "destroyed object, or removed model; no ActorDestroy is sent, so native consume semantics " +
                    "need runtime validation.");

            if (sendDestroy && !packet.CaptureConsumesActor)
                SendCommittedCaptureDestroy(packet.ActorId);
            completedClientCaptures.Remove(packet.CaptureRequestId);
            return;
        }

        if (pending.Phase != PendingCapturePhase.AwaitingApproval)
            return;

        if (IsClientCaptureExpired(Time.unscaledTime, pending))
        {
            RemovePendingClientCapture(packet.ActorId.Value);
            if (packet.CaptureApproved)
                SendCaptureAbort(packet.ActorId.Value, packet.CaptureRequestId);
            return;
        }

        if (!packet.CaptureApproved)
        {
            var retryDelay = RecordCaptureDenial(pending);
            RemovePendingClientCapture(packet.ActorId.Value);
            Logger.LogDiagnostic("capture", "approval-rejected",
                $"actorId={packet.ActorId.Value:X}; requestId={packet.CaptureRequestId}; " +
                $"owner={packet.OwnerId}; retryDelayMs={retryDelay}; " +
                DiagActorContext(packet.ActorId.Value, LocalID, pending.VacuumItem));
#if SR2MP_TEST
            if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") == "1")
                SrLogger.LogWarning(
                    $"[SR2MP_TEST] actor-capture rejected actor={packet.ActorId.Value:X} " +
                    $"owner={packet.OwnerId} request={packet.CaptureRequestId}");
#endif
            return;
        }

        captureDenialRetries.Remove(packet.ActorId.Value);

        var component = pending.Target ? pending.Target.GetComponent<NetworkActor>() : null;
        var requestMatches =
            string.Equals(packet.OwnerId, LocalID, StringComparison.Ordinal) &&
            string.Equals(packet.PreviousOwnerId, pending.ExpectedOwnerId, StringComparison.Ordinal) &&
            component && string.Equals(component.CurrentOwnerId, pending.ExpectedOwnerId, StringComparison.Ordinal);
        var targetAvailable = pending.VacuumItem && pending.TargetObject && pending.Target &&
                             (pending.Kind != PendingCaptureKind.Capture || pending.Joint);
        if (!ActorCaptureTransferPolicy.CanExecutePreparedCapture(
                isApproved: true,
                matchingPendingRequest: requestMatches,
                awaitingApproval: pending.Phase == PendingCapturePhase.AwaitingApproval,
                pendingNotExpired: true,
                expectedOwnerMatches: component &&
                                     string.Equals(component.CurrentOwnerId, pending.ExpectedOwnerId,
                                         StringComparison.Ordinal),
                nativeTargetAvailable: targetAvailable))
        {
            RemovePendingClientCapture(packet.ActorId.Value);
            SendCaptureAbort(packet.ActorId.Value, packet.CaptureRequestId);
            SrLogger.LogWarning($"Ignored stale or unusable capture approval for actor {packet.ActorId.Value}.");
            return;
        }

        pending.Phase = PendingCapturePhase.AwaitingCommit;
        pending.PhaseStartedAt = Time.unscaledTime;
        Logger.LogDiagnostic("capture", "approval-received",
            $"actorId={packet.ActorId.Value:X}; requestId={packet.CaptureRequestId}; kind={pending.Kind}; " +
            $"previousOwner={pending.ExpectedOwnerId}; targetAvailable={targetAvailable}; requestMatches={requestMatches}; " +
            DiagActorContext(packet.ActorId.Value, LocalID, pending.VacuumItem));
        var replayOutcome = ReplayApprovedAction(packet, pending);
        if (replayOutcome != ReplayOutcome.Completed)
        {
            if (failedCaptureRetryAfter.Count >= MaxPendingClientCaptures)
                failedCaptureRetryAfter.Remove(failedCaptureRetryAfter.MinBy(pair => pair.Value).Key);
            failedCaptureRetryAfter[packet.ActorId.Value] = Time.unscaledTime + 2f;
            RemovePendingClientCapture(packet.ActorId.Value);
            SendCaptureAbort(packet.ActorId.Value, packet.CaptureRequestId);
            if (replayOutcome == ReplayOutcome.Unknown)
                RequestAuthoritativeResync(packet.ActorId.Value,
                    "native vacuum operation threw after invocation began");
            return;
        }

        if (pending.Kind == PendingCaptureKind.Consume && !ObserveLocalConsumeCompletion(pending))
        {
            Logger.LogDiagnostic("capture", "awaiting-native-consume",
                $"actorId={packet.ActorId.Value:X}; requestId={packet.CaptureRequestId}; " +
                $"actorPresent={pending.TargetObject}; captive={(pending.Target ? pending.Target.IsCaptive() : false)}");
#if SR2MP_TEST
            if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") == "1")
                SrLogger.LogMessage(
                    $"[SR2MP_TEST] actor-capture stage waiting for native consume actor={packet.ActorId.Value:X}; " +
                    "ownership commit is withheld until the actor/model is removed");
#endif
            pending.WaitingForNativeConsume = true;
            pending.RetryCount = 0;
            pending.NextRetryAt = Time.unscaledTime + ClientRetryInterval;
            return;
        }

        CompleteApprovedClientCapture(packet.ActorId.Value, pending);
    }

    private static ReplayOutcome ReplayApprovedAction(ActorTransferPacket packet, PendingCapture pending)
    {
        if (!pending.VacuumItem || !pending.TargetObject || !pending.Target)
        {
            SrLogger.LogWarning(
                $"Actor {packet.ActorId.Value} ownership was approved, but its vacuum target expired before replay; " +
                "the host will release its short capture reservation.");
            return ReplayOutcome.NotStarted;
        }

        if (!CanAcceptCapturedAmmo(packet.ActorId.Value))
            return ReplayOutcome.NotStarted;
        replayingCaptureActors.Add(packet.ActorId.Value);
        var nativeInvocationStarted = false;
        try
        {
            if (pending.Kind == PendingCaptureKind.Capture)
            {
                if (!pending.Joint)
                {
                    SrLogger.LogWarning(
                        $"Actor {packet.ActorId.Value} ownership was approved, but its vacuum joint expired.");
                    return ReplayOutcome.NotStarted;
                }

                // This guarded replay lets the original native operation finish
                // before the client reports a commit to the host.
                nativeInvocationStarted = true;
                pending.Target.Capture(pending.Joint!);
                var targetBody = pending.Target._body;
                var jointConnectedToTarget = targetBody && pending.Joint!.connectedBody == targetBody;
                var isCaptive = pending.Target.IsCaptive();
                if (!ActorCaptureTransferPolicy.HasConfirmedNativeCapture(
                        isCaptive, jointConnectedToTarget))
                {
                    SrLogger.LogWarning(
                        $"Native capture returned without attaching actor {packet.ActorId.Value} " +
                        $"to the approved vacuum joint (captive={isCaptive}, " +
                        $"jointConnected={jointConnectedToTarget}); " +
                        "the client will abort the reservation and resync instead of committing ownership.");
                    return ReplayOutcome.Unknown;
                }

#if SR2MP_TEST
                if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") == "1")
                    SrLogger.LogMessage(
                        $"[SR2MP_TEST] actor-capture native-postcondition actor={packet.ActorId.Value:X} " +
                        $"captive={isCaptive} jointConnected={jointConnectedToTarget}");
#endif
                return ReplayOutcome.Completed;
            }

            // ConsumeVacuumable is too late to start an approved capture: the
            // native method returns unless the vacuum already owns a captive,
            // jointed target. Prepare that native stage after host approval and
            // let the next normal VacuumItem.Update finish consumption.
            return PrepareApprovedConsumeCapture(packet.ActorId.Value, pending,
                ref nativeInvocationStarted);
        }
        catch (Exception exception)
        {
            SrLogger.LogError($"Approved vacuum operation failed for actor {packet.ActorId.Value}: {exception}");
            return nativeInvocationStarted ? ReplayOutcome.Unknown : ReplayOutcome.NotStarted;
        }
        finally
        {
            replayingCaptureActors.Remove(packet.ActorId.Value);
        }
    }

    private static ReplayOutcome PrepareApprovedConsumeCapture(long actorId, PendingCapture pending,
        ref bool nativeInvocationStarted)
    {
        if (!pending.VacuumItem || !pending.Target)
            return ReplayOutcome.NotStarted;
        if (!CanAcceptCapturedAmmo(actorId))
            return ReplayOutcome.NotStarted;
        var heldObject = pending.VacuumItem._held;
        if (heldObject && heldObject != pending.TargetObject && heldObject != pending.Target.gameObject)
        {
            SrLogger.LogWarning($"Approved capture for actor {actorId} deferred: vacuum holds another object.");
            return ReplayOutcome.NotStarted;
        }

#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_PIN_REMOTE_CAPTURE_ACTOR") == "1")
        {
            var testActor = pending.Target.GetComponent<NetworkActor>();
            if (testActor)
                LocalActorVacuumPredictionManager.PrepareTestActorForApprovedStage(
                    pending.VacuumItem, testActor);
        }
#endif

        var targetBody = pending.Target._body;
        if (!targetBody)
        {
            SrLogger.LogWarning(
                $"Host approved vacuum capture for actor {actorId}, but its Rigidbody is unavailable.");
            return ReplayOutcome.Unknown;
        }

        var actor = pending.Target.GetComponent<NetworkActor>();
        if (!actor || !LocalActorVacuumPredictionManager.BeginApprovedConsume(pending.VacuumItem, actor))
            return ReplayOutcome.NotStarted;

        var originalLockJoint = pending.VacuumItem.LockJoint;
        var pullJoint = pending.Target._captiveToJoint;
        if (!IsMatchingNativePullStage(pending, pullJoint, out var stageFailure))
        {
            if (pending.Target.IsCaptive() && pullJoint)
            {
                SrLogger.LogWarning(
                    $"Approved vacuum stage for actor {actorId} is already captive to unrelated joint " +
                    $"{pullJoint.Pointer.ToInt64():X}; refusing to replace it.");
                return ReplayOutcome.Unknown;
            }

            var forceJoint = AccessTools.Method(typeof(VacuumItem), "ForceJoint", new[] { typeof(Vacuumable) });
            if (forceJoint == null)
            {
                SrLogger.LogWarning(
                    $"Host approved vacuum capture for actor {actorId}, but VacuumItem.ForceJoint(Vacuumable) " +
                    "is unavailable; the reservation will be aborted and state resynced.");
                return ReplayOutcome.Unknown;
            }

            nativeInvocationStarted = true;
            forceJoint.Invoke(pending.VacuumItem, new object[] { pending.Target });
            pullJoint = pending.Target._captiveToJoint;
        }

        if (!IsMatchingNativePullStage(pending, pullJoint, out stageFailure))
        {
            SrLogger.LogWarning(
                $"Approved vacuum pull stage failed for actor {actorId}: {stageFailure}; " +
                "the reservation will be aborted and state resynced.");
            return ReplayOutcome.Unknown;
        }

        if (originalLockJoint && (!pending.VacuumItem.LockJoint ||
            originalLockJoint.Pointer != pending.VacuumItem.LockJoint.Pointer))
        {
            SrLogger.LogWarning(
                $"Approved vacuum pull stage changed the original LockJoint for actor {actorId}; " +
                "the reservation will be aborted and state resynced.");
            return ReplayOutcome.Unknown;
        }
        pending.ApprovedPullJoint = pullJoint;
        var isCaptive = pending.Target.IsCaptive();
        var jointConnected = pullJoint && pullJoint.connectedBody == targetBody;

        var held = pending.VacuumItem._held == pending.TargetObject ||
                   pending.VacuumItem._held == pending.Target.gameObject;

#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") == "1")
        {
            var origin = pending.VacuumItem.VacOrigin;
            var distance = origin
                ? Vector3.Distance(origin.transform.position, targetBody.position)
                : float.NaN;
            SrLogger.LogMessage(
                $"[SR2MP_TEST] actor-capture stage-prepared actor={actorId:X} " +
                $"captive={isCaptive} pullJoint={pullJoint.Pointer.ToInt64():X} " +
                $"pullJointOwned=True listMember=True parentIsVacOrigin=True " +
                $"jointConnected={jointConnected} lockJoint={JointPointer(originalLockJoint)} held={held} " +
                $"distance={distance:F2} captureDist={pending.VacuumItem.CaptureDist:F2}; " +
                "native VacuumItem.Update will continue into consume");
            SrLogger.LogMessage(
                $"[SR2MP_TEST] actor-capture pull-joint-active actor={actorId:X} " +
                $"joint={pullJoint.Pointer.ToInt64():X} exactTarget=True listMember=True");
        }
#endif

        return ReplayOutcome.Completed;
    }

    private static bool IsMatchingNativePullStage(PendingCapture pending, Joint? joint, out string failure)
    {
        failure = "missing joint";
        if (!joint)
            return false;
        if (!pending.Target || !pending.VacuumItem || !pending.Target._body)
        {
            failure = "target, vacuum, or body unavailable";
            return false;
        }
        if (!pending.Target.IsCaptive() || !pending.Target._captiveToJoint ||
            pending.Target._captiveToJoint.Pointer != joint.Pointer)
        {
            failure = "joint is not the actor's current captive joint";
            return false;
        }
        if (!joint.connectedBody || joint.connectedBody.Pointer != pending.Target._body.Pointer)
        {
            failure = "joint is not connected to the exact target body";
            return false;
        }
        var origin = pending.VacuumItem.VacOrigin;
        if (!origin || !joint.transform.parent || joint.transform.parent.Pointer != origin.transform.Pointer)
        {
            failure = "joint is not parented to this vacuum's VacOrigin";
            return false;
        }
        var joints = pending.VacuumItem._joints;
        if (joints == null)
        {
            failure = "vacuum pulling-joint list is unavailable";
            return false;
        }
        for (var i = 0; i < joints.Count; i++)
        {
            var listed = joints[i];
            if (listed && listed.Pointer == joint.Pointer)
                // Read the actual native pulling-joint collection. Il2CppInterop can
                // expose a SpringJoint instance through the base Joint wrapper, so
                // CLR `is SpringJoint` checks reject valid native joints here.
                return true;
        }
        failure = "joint is not a member of this vacuum's native pulling-joint list";
        return false;
    }

    private static long JointPointer(Joint? joint) => joint ? joint.Pointer.ToInt64() : 0;

    /// <summary>
    /// Allows a Destroyer call for the exact live host-approved Consume transaction
    /// to remove the local object while withholding ActorDestroy until host commit.
    /// </summary>
    internal static bool TryMarkReplayConsumeDestroy(long actorId, GameObject actorObject, string source)
    {
        if (!string.Equals(source, "Vacuumable.consume", StringComparison.Ordinal) ||
            !pendingClientCaptures.TryGetValue(actorId, out var pending) ||
            !pending.ConsumeDestroyPermitArmed || Time.unscaledTime > pending.ConsumeDestroyPermitUntil ||
            !ActorCaptureTransferPolicy.CanPermitApprovedConsumeDestroy(
                !Main.Server.IsRunning && Main.Client.IsConnected,
                pending.TargetObject == actorObject || (pending.Target && pending.Target.gameObject == actorObject),
                pending.Phase == PendingCapturePhase.AwaitingCommit,
                pending.Kind == PendingCaptureKind.Consume,
                IsClientCaptureExpired(Time.unscaledTime, pending), pending.CommitSent,
                replayingCaptureActors.Contains(actorId), pending.WaitingForNativeConsume))
            return false;

#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_DENY_APPROVED_CONSUME_DESTROY") == "1")
        {
            SrLogger.LogMessage($"[SR2MP_TEST] mutation denied approved native destroy actor={actorId:X}");
            return false;
        }
        SrLogger.LogMessage($"[SR2MP_TEST] approved native destroy permitted actor={actorId:X} " +
                            $"async={!pending.NativeConsumeInProgress} source={source}");
#endif
        pending.ConsumeDestroyPermitArmed = false;
        pending.ConsumeDestroyAttemptAccepted = true;
        pending.ConsumeDestroyAttemptUntil = Time.unscaledTime + NativeVacuumCandidateLifetime;
        // A permitted attempt is not proof that native destruction finished.
        return true;
    }

    private static void ObserveAndRememberCompletedCapture(long actorId, PendingCapture pending)
    {
        ObserveLocalConsumeCompletion(pending);

        var completed = new CompletedCapture(actorId, pending.Kind, pending.LocalDestroyObserved,
            Time.unscaledTime + CompletedClientCaptureTimeout);
        completedClientCaptures[pending.RequestId] = completed;
        completedClientCaptureOrder.Enqueue(pending.RequestId);
        while (completedClientCaptureOrder.Count > MaxCompletedClientCaptures)
            completedClientCaptures.Remove(completedClientCaptureOrder.Dequeue());
    }

    private static bool ObserveLocalConsumeCompletion(PendingCapture pending)
    {
        var modelStillExists = GameState.TryGetIdentifiableModel(new ActorId(pending.ActorId), out _);
        pending.LocalDestroyObserved |= ActorCaptureTransferPolicy.HasConfirmedConsumeRemoval(
            pending.TargetObject, pending.Target, modelStillExists);
#if SR2MP_TEST
        if (pending.LocalDestroyObserved)
            testConsumeRemovalConfirmedUntil[pending.ActorId] = Time.unscaledTime + 150f;
#endif
        return pending.LocalDestroyObserved;
    }

    private const float HeldCommitDelaySeconds = 0.3f;

    // True once the vacuum has continuously held the target for a moment, so a transient hold on the way to a
    // normal consume never downgrades the transaction.
    private static bool IsHeldByPendingVacuum(PendingCapture pending, float now)
    {
        var held = pending.VacuumItem && pending.Target && !pending.LocalDestroyObserved
            ? pending.VacuumItem._held : null;
        if (!held || (held != pending.TargetObject && held != pending.Target.gameObject))
        {
            pending.HeldSince = -1f;
            return false;
        }
        if (pending.HeldSince < 0f)
            pending.HeldSince = now;
        return now - pending.HeldSince >= HeldCommitDelaySeconds;
    }

    private static void CompleteApprovedClientCapture(long actorId, PendingCapture pending)
    {
        if (pending.CommitSent)
            return;

#if SR2MP_TEST
        if (pending.Kind == PendingCaptureKind.Consume)
            SrLogger.LogMessage($"[SR2MP_TEST] actor-consume removal-confirmed-before-commit actor={actorId:X}");
#endif
        pending.WaitingForNativeConsume = false;
        pending.ConsumeDestroyPermitArmed = false;
        pending.ConsumeDestroyAttemptAccepted = false;
        pending.ApprovedPullJoint = null;
        ObserveAndRememberCompletedCapture(actorId, pending);
        pending.RetryCount = 0;
        pending.NextRetryAt = Time.unscaledTime + ClientRetryInterval;
        pending.CommitSent = true;
        try
        {
            Main.Client.SendPacket(CreateClientCapturePacket(actorId, pending, commit: true));
            Logger.LogDiagnostic("capture", "commit-sent",
                $"actorId={actorId:X}; requestId={pending.RequestId}; kind={pending.Kind}; " +
                $"consumed={pending.LocalDestroyObserved}; claimant={LocalID}; " +
                DiagActorContext(actorId, LocalID, pending.VacuumItem));
#if SR2MP_TEST
            if (Environment.GetEnvironmentVariable("SR2MP_TEST_DUPLICATE_CAPTURE_COMMIT") == "1")
            {
                for (var duplicate = 0; duplicate < 3; duplicate++)
                    Main.Client.SendPacket(CreateClientCapturePacket(actorId, pending, commit: true));
                SrLogger.LogMessage($"[SR2MP_TEST] sent duplicate consume commits actor={actorId:X} duplicates=3");
            }
            if (!testResyncAfterCommitRequested && pending.Kind == PendingCaptureKind.Consume &&
                Environment.GetEnvironmentVariable("SR2MP_TEST_RESYNC_ON_CAPTURE_COMMIT") == "1")
            {
                testResyncAfterCommitRequested = true;
                SrLogger.LogMessage($"[SR2MP_TEST] requesting resync immediately after consume commit actor={actorId:X}");
                Main.Server.ReSyncManager.RequestResync();
            }
#endif
        }
        catch (Exception exception)
        {
            pending.CommitSent = false;
            SrLogger.LogError($"Could not commit completed actor capture {actorId}: {exception}");
        }
    }

    private static void ResolveCompletedClientCapture(ActorTransferPacket packet)
    {
        if (!completedClientCaptures.TryGetValue(packet.CaptureRequestId, out var completed) ||
            completed.ActorId != packet.ActorId.Value ||
            !string.Equals(packet.OwnerId, LocalID, StringComparison.Ordinal))
            return;

        completedClientCaptures.Remove(packet.CaptureRequestId);
        if (!packet.CaptureConsumesActor && ActorCaptureTransferPolicy.CanSendCaptureDestroyAfterCommit(
                acceptedCommit: true,
                matchingPendingRequest: true,
                wasConsumeOperation: completed.Kind == PendingCaptureKind.Consume,
                localDestroyObserved: completed.LocalDestroyObserved))
            SendCommittedCaptureDestroy(packet.ActorId);
    }

    private static void SendCommittedCaptureDestroy(ActorId actorId)
    {
        ActorManager.Actors.Remove(actorId.Value);
        ActorManager.OwnershipByActorId.Remove(actorId.Value);
        ActorManager.EpochByActorId.Remove(actorId.Value);
        Main.SendToAllOrServer(new ActorDestroyPacket
        {
            ActorId = actorId,
            OwnerId = LocalID,
            HasOwnerId = true
        });
    }

    internal static bool TryGetServerResult(string playerId, Guid requestId, out ActorTransferPacket result)
    {
        PruneExpiredServerCaptures(Time.unscaledTime);
        if (serverResults.TryGetValue(new CaptureRequestKey(playerId, requestId), out var cached))
        {
            result = Clone(cached);
            return true;
        }

        result = null!;
        return false;
    }

    internal static bool TryReserveServerCapture(string playerId, ActorTransferPacket request)
    {
        PruneExpiredServerCaptures(Time.unscaledTime);
        if (string.IsNullOrWhiteSpace(playerId) || request.ActorId.Value == 0 ||
            request.CaptureRequestId == Guid.Empty)
            return false;

        var key = new CaptureRequestKey(playerId, request.CaptureRequestId);
        if (serverCaptureByActor.TryGetValue(request.ActorId.Value, out var currentKey))
        {
            if (currentKey == key)
                return true;

            if (serverResultExpiresAt.TryGetValue(currentKey, out var expiresAt) &&
                expiresAt > Time.unscaledTime)
                return false;

            serverCaptureByActor.Remove(request.ActorId.Value);
        }

        serverCaptureByActor[request.ActorId.Value] = key;
        return true;
    }

    internal static bool IsServerCapturePrepared(string playerId, ActorTransferPacket request)
    {
        PruneExpiredServerCaptures(Time.unscaledTime);
        var key = new CaptureRequestKey(playerId, request.CaptureRequestId);
        return serverCaptureByActor.TryGetValue(request.ActorId.Value, out var activeKey) &&
               activeKey == key &&
               serverResults.TryGetValue(key, out var prepared) &&
               prepared.CaptureIntent && prepared.CaptureApproved && !prepared.CaptureCommit &&
               ActorCaptureTransferPolicy.IsCompatibleCommitKind(prepared.CaptureConsumesActor,
                   request.CaptureConsumesActor) &&
               ActorCaptureTransferPolicy.IsSameCaptureRequest(
                   prepared.CaptureRequestId, request.CaptureRequestId,
                   prepared.ActorId.Value, request.ActorId.Value,
                   prepared.PreviousOwnerId, request.PreviousOwnerId);
    }

    internal static bool HasServerCaptureReservation(long actorId)
    {
        PruneExpiredServerCaptures(Time.unscaledTime);
        return serverCaptureByActor.ContainsKey(actorId);
    }

    internal static string GetDiagnosticCaptureCorrelation(long actorId)
    {
        if (pendingClientCaptures.TryGetValue(actorId, out var pending))
            return pending.RequestId.ToString("N");
        if (serverCaptureByActor.TryGetValue(actorId, out var key))
            return key.RequestId.ToString("N");
        return "<none>";
    }

    internal static bool AbortServerCapture(string playerId, long actorId, Guid requestId)
    {
        if (string.IsNullOrWhiteSpace(playerId) || actorId == 0 || requestId == Guid.Empty)
            return false;

        var key = new CaptureRequestKey(playerId, requestId);
        if (!serverCaptureByActor.TryGetValue(actorId, out var reservedKey) || reservedKey != key ||
            !serverResults.TryGetValue(key, out var prepared))
            return false;

        var isPreparedApproval = prepared.CaptureIntent && prepared.CaptureApproved && !prepared.CaptureCommit;
        if (!ActorCaptureTransferPolicy.CanAbortPreparedCapture(
                authenticatedPlayer: true,
                authenticatedPlayerId: playerId,
                reservationPlayerId: reservedKey.PlayerId,
                requestedActorId: actorId,
                reservedActorId: prepared.ActorId.Value,
                requestId: requestId,
                reservedRequestId: prepared.CaptureRequestId,
                isPreparedApproval: isPreparedApproval,
                alreadyCommitted: prepared.CaptureCommit))
            return false;

        RemoveServerResult(key);
        return true;
    }

#if SR2MP_TEST
    internal static bool TestWasConsumeRemovalConfirmed(long actorId)
        => testConsumeRemovalConfirmedUntil.TryGetValue(actorId, out var until) && Time.unscaledTime <= until;

    internal static bool TestHasPendingClientCapture(long actorId)
        => pendingClientCaptures.ContainsKey(actorId);

    internal static bool TestHasMatchingApprovedPullJoint(long actorId)
        => HasMatchingApprovedPullJoint(actorId);

    internal static bool TestWasConsumeCommitAcknowledged(long actorId)
        => testConsumeCommitAcknowledgedUntil.TryGetValue(actorId, out var until) &&
           Time.unscaledTime <= until;

    internal static bool VerifyServerReservationAbort()
    {
        var actorId = long.MaxValue - 0x51;
        var requestId = Guid.NewGuid();
        var playerId = $"SR2MP_TEST_{Guid.NewGuid():N}";
        var approval = new ActorTransferPacket
        {
            ActorId = new ActorId(actorId),
            OwnerId = playerId,
            PreviousOwnerId = "SR2MP_TEST_HOST",
            HasPreviousOwnerId = true,
            CaptureIntent = true,
            CaptureApproved = true,
            CaptureRequestId = requestId,
            CaptureCommit = false
        };

        if (!TryReserveServerCapture(playerId, approval))
            return false;

        RememberServerResult(playerId, approval);
        var wasReserved = HasServerCaptureReservation(actorId);
        var aborted = AbortServerCapture(playerId, actorId, requestId);
        var remainsReserved = HasServerCaptureReservation(actorId);
        var remainsCached = TryGetServerResult(playerId, requestId, out _);
        return wasReserved && aborted && !remainsReserved && !remainsCached;
    }
#endif

    internal static void DeferServerOwnershipTransfer(ActorTransferPacket packet)
    {
        if (packet.ActorId.Value == 0 || string.IsNullOrWhiteSpace(packet.OwnerId))
            return;

        deferredServerTransfers[packet.ActorId.Value] = Clone(packet);
    }

    internal static void RememberServerResult(string playerId, ActorTransferPacket result)
    {
        if (string.IsNullOrWhiteSpace(playerId) || result.CaptureRequestId == Guid.Empty)
            return;

        var key = new CaptureRequestKey(playerId, result.CaptureRequestId);
        if (serverResults.ContainsKey(key))
        {
            serverResults[key] = Clone(result);
        }
        else
        {
            serverResults[key] = Clone(result);
            serverResultOrder.Enqueue(key);
        }

        serverResultExpiresAt[key] = Time.unscaledTime +
                                     (result.CaptureCommit
                                         ? CommittedServerResultTimeout
                                         : ServerCaptureReservationTimeout);

        if (result.CaptureIntent && result.CaptureCommit && result.CaptureApproved &&
            serverCaptureByActor.TryGetValue(result.ActorId.Value, out var reservedKey) && reservedKey == key)
        {
            serverCaptureByActor.Remove(result.ActorId.Value);
            deferredServerTransfers.Remove(result.ActorId.Value);
        }
        else if (result.CaptureIntent && !result.CaptureApproved &&
                 serverCaptureByActor.TryGetValue(result.ActorId.Value, out var rejectedKey) && rejectedKey == key)
        {
            serverCaptureByActor.Remove(result.ActorId.Value);
            FlushDeferredServerTransfer(result.ActorId.Value);
        }

        while (serverResultOrder.Count > MaxRememberedServerResults)
        {
            var oldest = serverResultOrder.Dequeue();
            RemoveServerResult(oldest);
        }
    }

    private static void PumpClientCaptures(float now)
    {
        foreach (var (actorId, pending) in pendingClientCaptures.ToArray())
        {
            if (IsClientCaptureExpired(now, pending) && TryCommitObservedRemovalAtExpiry(actorId, pending, now))
                continue;
            if (IsClientCaptureExpired(now, pending))
            {
                RemovePendingClientCapture(actorId);
                SrLogger.LogWarning(
                    $"Actor capture request {pending.RequestId} for {actorId} expired in phase {pending.Phase}.");
                Logger.LogDiagnostic("capture", "client-transaction-expired",
                    $"actorId={actorId:X}; requestId={pending.RequestId}; kind={pending.Kind}; phase={pending.Phase}; " +
                    $"retries={pending.RetryCount}; commitSent={pending.CommitSent}; " +
                    $"localDestroyObserved={pending.LocalDestroyObserved}; " +
                    $"targetObjectPresent={pending.TargetObject}; targetComponentPresent={pending.Target}; " +
                    $"worldModelPresent={GameState.TryGetIdentifiableModel(new ActorId(actorId), out _)}; " +
                    DiagActorContext(actorId, LocalID, pending.VacuumItem));
                if (ActorCaptureTransferPolicy.ShouldSendCaptureAbortOnExpiry(pending.RequestId))
                    SendCaptureAbort(actorId, pending.RequestId);
                if (pending.Phase == PendingCapturePhase.AwaitingCommit)
                {
                    RequestAuthoritativeResync(actorId, "commit acknowledgement timed out");
                }
                continue;
            }

            var timeout = GetClientCaptureTimeout(pending);
            var maxRetries = GetClientCaptureMaxRetries(pending);
            if (pending.WaitingForNativeConsume)
            {
                if (ObserveLocalConsumeCompletion(pending))
                    CompleteApprovedClientCapture(actorId, pending);
                else if (IsHeldByPendingVacuum(pending, now))
                {
                    // Native will never consume a held object (Tarr, or any item while its slot is full): commit a
                    // non-consuming capture so the host hands ownership to this client. Otherwise the host kept
                    // simulating it and overwrote the throw, so the object just dropped off the nozzle.
                    pending.Kind = PendingCaptureKind.Capture;
                    Logger.LogDiagnostic("capture", "held-ownership-commit",
                        $"actorId={actorId:X}; requestId={pending.RequestId}; " +
                        DiagActorContext(actorId, LocalID, pending.VacuumItem));
                    CompleteApprovedClientCapture(actorId, pending);
                }
                else if (!HasMatchingApprovedPullJoint(actorId) && pending.HeldSince < 0f)
                    // While native is holding the target (HeldSince set), the pull joint is gone by design;
                    // wait for the held commit instead of cancelling.
                    CancelClientCapture(actorId, "approved native vacuum pulling joint is no longer valid");
                continue;
            }

            if (!ActorCaptureTransferPolicy.CanRetryPendingCapture(
                    now, pending.PhaseStartedAt, timeout,
                    pending.NextRetryAt, pending.RetryCount, maxRetries))
                continue;

            pending.RetryCount++;
            pending.NextRetryAt = now + ClientRetryInterval;
            try
            {
                Main.Client.SendPacket(CreateClientCapturePacket(actorId, pending,
                    commit: pending.Phase == PendingCapturePhase.AwaitingCommit));
                if (pending.Phase == PendingCapturePhase.AwaitingCommit)
                    Logger.LogDiagnostic("capture", "commit-retried",
                        $"actorId={actorId:X}; requestId={pending.RequestId}; retry={pending.RetryCount}; " +
                        $"localDestroyObserved={pending.LocalDestroyObserved}; " +
                        $"targetObjectPresent={pending.TargetObject}; " +
                        $"worldModelPresent={GameState.TryGetIdentifiableModel(new ActorId(actorId), out _)}");
            }
            catch (Exception exception)
            {
                SrLogger.LogWarning($"Retry {pending.RetryCount} failed for actor capture {actorId}: {exception.Message}");
            }
        }
    }

    private static ActorTransferPacket CreateClientCapturePacket(long actorId, PendingCapture pending, bool commit)
        => new()
        {
            ActorId = new ActorId(actorId),
            OwnerId = LocalID,
            PreviousOwnerId = pending.ExpectedOwnerId,
            HasPreviousOwnerId = true,
            CaptureIntent = true,
            CaptureApproved = false,
            CaptureRequestId = pending.RequestId,
            CaptureCommit = commit,
            CaptureConsumesActor = pending.Kind == PendingCaptureKind.Consume
        };

    private static void PruneExpiredClientCaptures(float now)
    {
        foreach (var (actorId, pending) in pendingClientCaptures.ToArray())
            if (IsClientCaptureExpired(now, pending) && !TryCommitObservedRemovalAtExpiry(actorId, pending, now))
            {
                RemovePendingClientCapture(actorId);
                Logger.LogDiagnostic("capture", "client-transaction-expired",
                    $"actorId={actorId:X}; requestId={pending.RequestId}; kind={pending.Kind}; phase={pending.Phase}; " +
                    $"retries={pending.RetryCount}; commitSent={pending.CommitSent}; " +
                    $"localDestroyObserved={pending.LocalDestroyObserved}; " +
                    $"targetObjectPresent={pending.TargetObject}; targetComponentPresent={pending.Target}; " +
                    $"worldModelPresent={GameState.TryGetIdentifiableModel(new ActorId(actorId), out _)}; " +
                    DiagActorContext(actorId, LocalID, pending.VacuumItem));
                if (ActorCaptureTransferPolicy.ShouldSendCaptureAbortOnExpiry(pending.RequestId))
                    SendCaptureAbort(actorId, pending.RequestId);
                if (pending.Phase == PendingCapturePhase.AwaitingCommit)
                {
                    RequestAuthoritativeResync(actorId, "commit acknowledgement timed out");
                }
            }
    }

    /// <summary>
    /// A native destroy that lands just before the commit deadline is irreversible;
    /// commit it with a fresh ACK window instead of aborting and resyncing.
    /// </summary>
    private static bool TryCommitObservedRemovalAtExpiry(long actorId, PendingCapture pending, float now)
    {
        if (pending.Kind != PendingCaptureKind.Consume || pending.CommitSent ||
            pending.Phase != PendingCapturePhase.AwaitingCommit || !ObserveLocalConsumeCompletion(pending))
            return false;

        pending.PhaseStartedAt = now;
        CompleteApprovedClientCapture(actorId, pending);
        Logger.LogDiagnostic("capture", "commit-at-expiry",
            $"actorId={actorId:X}; requestId={pending.RequestId}; " +
            DiagActorContext(actorId, LocalID, pending.VacuumItem));
        return true;
    }

    private static bool IsClientCaptureExpired(float now, PendingCapture pending)
        => ActorCaptureTransferPolicy.IsPendingExpired(
            now, pending.PhaseStartedAt, GetClientCaptureTimeout(pending));

    private static float GetClientCaptureTimeout(PendingCapture pending)
        => pending.Phase == PendingCapturePhase.AwaitingCommit
            ? PendingClientCommitTimeout
            : PendingClientPrepareTimeout;

    private static int GetClientCaptureMaxRetries(PendingCapture pending)
        => pending.Phase == PendingCapturePhase.AwaitingCommit
            ? MaxCommitRetries
            : MaxPrepareRetries;

    private static void SendCaptureAbort(long actorId, Guid requestId)
    {
        if (!Main.Client.IsConnected || actorId == 0 || requestId == Guid.Empty)
            return;

        try
        {
            Main.Client.SendPacket(new CaptureAbortPacket
            {
                ActorId = new ActorId(actorId),
                CaptureRequestId = requestId
            });
        }
        catch (Exception exception)
        {
            SrLogger.LogWarning(
                $"Could not release host capture reservation for actor {actorId}: {exception.Message}");
        }
    }

    private static void RequestAuthoritativeResync(long actorId, string reason)
    {
        if (!Main.Client.IsConnected)
            return;

        try
        {
            Main.Server.ReSyncManager.RequestResync();
            SrLogger.LogWarning($"Requested host resync after actor {actorId} capture uncertainty: {reason}.");
        }
        catch (Exception exception)
        {
            SrLogger.LogError(
                $"Could not request host resync after actor {actorId} capture uncertainty ({reason}): {exception}");
        }
    }

    private static void PruneExpiredServerCaptures(float now)
    {
        foreach (var (key, expiresAt) in serverResultExpiresAt.ToArray())
            if (now >= expiresAt)
                RemoveServerResult(key);
    }

    private static void PruneExpiredCompletedClientCaptures(float now)
    {
        foreach (var (requestId, completed) in completedClientCaptures.ToArray())
            if (now >= completed.ExpiresAt)
                completedClientCaptures.Remove(requestId);
    }

    private static void RemoveServerResult(CaptureRequestKey key)
    {
        serverResults.Remove(key);
        serverResultExpiresAt.Remove(key);
        foreach (var (actorId, reservedKey) in serverCaptureByActor.ToArray())
            if (reservedKey == key)
            {
                serverCaptureByActor.Remove(actorId);
                FlushDeferredServerTransfer(actorId);
            }
    }

    private static void FlushDeferredServerTransfer(long actorId)
    {
        if (!deferredServerTransfers.Remove(actorId, out var packet) || !Main.Server.IsRunning)
            return;

        NetworkActorManager.ApplyOwnership(packet);
        Main.Server.SendToAll(packet);
    }

    internal static void CancelClientCapturesForResync()
    {
        var now = Time.unscaledTime;
        foreach (var (actorId, pending) in pendingClientCaptures.ToArray())
        {
            if (pending.Kind == PendingCaptureKind.Consume && !pending.CommitSent &&
                pending.Phase == PendingCapturePhase.AwaitingCommit && ObserveLocalConsumeCompletion(pending))
                CompleteApprovedClientCapture(actorId, pending);

            pending.ConsumeDestroyPermitArmed = false;
            LocalActorVacuumPredictionManager.EndApprovedConsume(actorId);
            replayingCaptureActors.Remove(actorId);
            if (pending.CommitSent || pending.LocalDestroyObserved)
            {
                // Native consume is irreversible. Keep its commit/ACK record across
                // snapshots; aborting it here could restore an already-consumed actor.
                pending.WaitingForNativeConsume = false;
                continue;
            }
            if (ActorCaptureTransferPolicy.ShouldPreserveCaptureAcrossResync(
                    false, false, pending.NativeConsumeInProgress, now,
                    pending.ConsumeDestroyAttemptAccepted, pending.ConsumeDestroyAttemptUntil))
            {
                // An accepted native destroy may still finish. Keep waiting so the
                // pump commits on removal or cancels once the bounded attempt expires.
                Logger.LogDiagnostic("capture", "resync-preserved-destroy-attempt",
                    $"actorId={actorId:X}; requestId={pending.RequestId}; " +
                    $"nativeConsumeInProgress={pending.NativeConsumeInProgress}");
                continue;
            }
            RemovePendingClientCapture(actorId);
            SendCaptureAbort(actorId, pending.RequestId);
        }
        // Completed tombstones must survive a snapshot racing the commit ACK.
    }

    private static void ClearClientCaptures()
    {
        foreach (var pending in pendingClientCaptures.Values)
        {
            ReleaseApprovedPullJoint(pending);
            pending.ApprovedPullJoint = null;
        }
        LocalActorVacuumPredictionManager.Clear();
        pendingClientCaptures.Clear();
        completedClientCaptures.Clear();
        completedClientCaptureOrder.Clear();
        replayingCaptureActors.Clear();
        failedCaptureRetryAfter.Clear();
        captureDenialRetries.Clear();
        vacuumActivations.Clear();
#if SR2MP_TEST
        testConsumeRemovalConfirmedUntil.Clear();
        testConsumeCommitAcknowledgedUntil.Clear();
        testResyncAfterCommitRequested = false;
#endif
    }

    private static void ClearServerResults()
    {
        serverResults.Clear();
        serverResultExpiresAt.Clear();
        serverCaptureByActor.Clear();
        deferredServerTransfers.Clear();
        serverResultOrder.Clear();
    }

    private static void RemoveServerResults(string playerId)
    {
        foreach (var key in serverResults.Keys.Where(key => key.PlayerId == playerId).ToArray())
            RemoveServerResult(key);
    }

    private static ActorTransferPacket Clone(ActorTransferPacket packet) => new()
    {
        ActorId = packet.ActorId,
        Epoch = packet.Epoch,
        HasEpoch = packet.HasEpoch,
        OwnerId = packet.OwnerId,
        PreviousOwnerId = packet.PreviousOwnerId,
        HasPreviousOwnerId = packet.HasPreviousOwnerId,
        CaptureIntent = packet.CaptureIntent,
        CaptureApproved = packet.CaptureApproved,
        CaptureRequestId = packet.CaptureRequestId,
        CaptureCommit = packet.CaptureCommit,
        CaptureConsumesActor = packet.CaptureConsumesActor,
        WorldRevision = packet.WorldRevision,
        HasWorldRevision = packet.HasWorldRevision
    };

    private readonly record struct CaptureRequestKey(string PlayerId, Guid RequestId);

    private readonly record struct CompletedCapture(
        long ActorId,
        PendingCaptureKind Kind,
        bool LocalDestroyObserved,
        float ExpiresAt);

    private enum PendingCaptureKind : byte
    {
        Capture,
        Consume
    }

    private enum PendingCapturePhase : byte
    {
        AwaitingApproval,
        AwaitingCommit
    }

    private enum ReplayOutcome : byte
    {
        NotStarted,
        Completed,
        Unknown
    }

    private sealed class VacuumActivationState
    {
        internal bool Active { get; set; }
        internal long ActivationId { get; set; }
    }

    private sealed class CaptureDenialRetryState
    {
        internal CaptureDenialRetryState(string expectedOwnerId, int sceneGroupId,
            int vacuumInstanceId, long vacuumActivationId)
        {
            ExpectedOwnerId = expectedOwnerId;
            SceneGroupId = sceneGroupId;
            VacuumInstanceId = vacuumInstanceId;
            VacuumActivationId = vacuumActivationId;
        }

        internal string ExpectedOwnerId { get; }
        internal int SceneGroupId { get; }
        internal int VacuumInstanceId { get; }
        internal long VacuumActivationId { get; }
        internal int DenialCount { get; set; }
        internal float RetryAfter { get; set; }
        internal float ExpiresAt { get; set; }
    }

    private sealed class PendingCapture
    {
        internal PendingCapture(long actorId, string expectedOwnerId, VacuumItem vacuumItem, GameObject targetObject,
            Vacuumable target, Joint? joint, PendingCaptureKind kind, Guid requestId, float requestedAt,
            float nextRetryAt, int vacuumInstanceId, long vacuumActivationId, int sceneGroupId,
            bool nativeVacuumCandidate, float nativeVacuumCandidateAt)
        {
            ActorId = actorId;
            ExpectedOwnerId = expectedOwnerId;
            VacuumItem = vacuumItem;
            TargetObject = targetObject;
            Target = target;
            Joint = joint;
            Kind = kind;
            RequestId = requestId;
            PhaseStartedAt = requestedAt;
            NextRetryAt = nextRetryAt;
            VacuumInstanceId = vacuumInstanceId;
            VacuumActivationId = vacuumActivationId;
            SceneGroupId = sceneGroupId;
            NativeVacuumCandidate = nativeVacuumCandidate;
            NativeVacuumCandidateAt = nativeVacuumCandidateAt;
        }

        internal long ActorId { get; }
        internal string ExpectedOwnerId { get; }
        internal VacuumItem VacuumItem { get; set; }
        internal GameObject TargetObject { get; set; }
        internal Vacuumable Target { get; set; }
        internal Joint? Joint { get; set; }
        internal Joint? ApprovedPullJoint { get; set; }
        internal PendingCaptureKind Kind { get; set; }
        internal bool NativeVacuumCandidate { get; set; }
        internal float NativeVacuumCandidateAt { get; set; }
        internal bool MergeMismatchLogged { get; set; }
        internal bool OperationMismatchLogged { get; set; }
        internal Guid RequestId { get; }
        internal int VacuumInstanceId { get; }
        internal long VacuumActivationId { get; }
        internal int SceneGroupId { get; }
        internal float PhaseStartedAt { get; set; }
        internal float HeldSince { get; set; } = -1f;
        internal float NextRetryAt { get; set; }
        internal int RetryCount { get; set; }
        internal PendingCapturePhase Phase { get; set; }
        internal bool LocalDestroyObserved { get; set; }
        internal bool WaitingForNativeConsume { get; set; }
        internal bool NativeConsumeInProgress { get; set; }
        internal bool CommitSent { get; set; }
        internal bool ConsumeDestroyPermitArmed { get; set; }
        internal float ConsumeDestroyPermitUntil { get; set; }
        internal bool ConsumeDestroyAttemptAccepted { get; set; }
        internal float ConsumeDestroyAttemptUntil { get; set; }
#if SR2MP_TEST
        internal GameObject? TestHeldBeforeConsume { get; set; }
        internal bool TestHeldInjectionActive { get; set; }
#endif
    }
}

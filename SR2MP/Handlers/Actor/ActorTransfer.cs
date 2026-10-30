using System.Net;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Player.PlayerItems;
using SR2MP.Components.Actor;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Actor;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;
using SR2MP.Shared.Utils;

namespace SR2MP.Handlers.Actor;

[PacketHandler((byte)PacketType.ActorTransfer)]
internal sealed class ActorTransferHandler : BasePacketHandler<ActorTransferPacket>
{
    protected override bool Handle(ActorTransferPacket packet, IPEndPoint? clientEndpoint)
    {
        if (packet.OwnershipInterest)
        {
            // Interest is a claim for the arbiter, never a transfer. It is authenticated against the endpoint
            // and a losing interest gets no reply (no repair spam). Clients never act on one.
            if (Main.Server.IsRunning && clientEndpoint != null && !packet.CaptureIntent &&
                Main.Server.ClientManager.TryGetClient(clientEndpoint, out var interestSender) &&
                interestSender != null && interestSender.PlayerId == packet.OwnerId)
            {
                OwnershipArbiter.RegisterInterest(interestSender.PlayerId, packet.ActorId.Value,
                    packet.InterestDistance, UnityEngine.Time.unscaledTime);
                OwnershipArbiter.EvaluateNow(packet.ActorId.Value);
            }
            return false;
        }

        if (Main.Server.IsRunning)
        {
            if (packet.CaptureIntent && !packet.CaptureCommit)
            {
                HandleCapturePrepare(packet, clientEndpoint);
                return false;
            }

            if (!ValidateServerTransfer(packet, clientEndpoint))
                return false;
        }
        else if (packet.CaptureIntent && !packet.CaptureCommit)
        {
            // A prepare approval is a grant to run the native action, never an
            // ownership transfer. Only a later committed result changes owner.
#if SR2MP_TEST
            if (SR2MP.Shared.Utils.TestBootstrap.TryHandleCaptureAbortProbe(packet))
                return false;

            if (packet.CaptureRequestId != Guid.Empty && !packet.CaptureApproved &&
                string.IsNullOrEmpty(packet.OwnerId) &&
                Environment.GetEnvironmentVariable("SR2MP_TEST_PROBE_EMPTY_CAPTURE") == "1")
                SrLogger.LogMessage(
                    $"[SR2MP_TEST] empty-owner-capture-probe rejected actor={packet.ActorId.Value:X} " +
                    $"request={packet.CaptureRequestId}");
#endif
            NetworkActorCaptureManager.ResolveClientRequest(packet);
            return false;
        }

#if SR2MP_DEEPLOG || SR2MP_TEST
        ActorDiag.Log("ownership-accepted", packet.ActorId.Value,
            $"previous={packet.PreviousOwnerId}; claimant={packet.OwnerId}; endpoint={clientEndpoint?.ToString() ?? "server"}",
            ownerHint: packet.OwnerId, perActorMilliseconds: 250);
#endif
#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") == "1")
            SrLogger.LogMessage(
                $"[SR2MP_TEST] actor-ownership accepted actor={packet.ActorId.Value:X} " +
                $"previous={packet.PreviousOwnerId} claimant={packet.OwnerId} " +
                $"endpoint={clientEndpoint?.ToString() ?? "server"}");
#endif

        if (!Main.Server.IsRunning &&
            !ActorCaptureTransferPolicy.ShouldApplyOwnerState(
                packet.CaptureIntent, packet.CaptureApproved, packet.CaptureCommit))
        {
            // Prepare rejections and negative commit results are ACKs, not the
            // claimant's ownership update.
            NetworkActorCaptureManager.ResolveClientRequest(packet);
            if (packet.CaptureIntent && !packet.CaptureApproved &&
                (packet.CaptureCommit || HasLocalActorState(packet.ActorId.Value)))
            {
                // The host's rejected commit carries its current owner. Apply only
                // the authoritative owner correction; never apply the claimant from
                // the failed transaction. A prepare rejection can also repair stale
                // state, but do not create an ownership entry for an unknown actor.
                NetworkActorManager.ApplyOwnership(new ActorTransferPacket
                {
                    ActorId = packet.ActorId,
                    OwnerId = packet.OwnerId,
                    PreviousOwnerId = packet.PreviousOwnerId,
                    HasPreviousOwnerId = true,
                    Epoch = packet.Epoch,
                    HasEpoch = packet.HasEpoch
                });
                if (packet.CaptureCommit)
                    ActorDestroyHandler.ResolveDeferredDestroy(packet.ActorId.Value, packet.OwnerId);
            }
            return false;
        }

        if (Main.Server.IsRunning && clientEndpoint != null && packet.HasEpoch &&
            packet.Epoch < ActorManager.GetEpoch(packet.ActorId.Value))
            SrLogger.LogDebug(
                $"Actor {packet.ActorId.Value} ownership request carries epoch {packet.Epoch} " +
                $"older than host epoch {ActorManager.GetEpoch(packet.ActorId.Value)}; host validation decides.");

        var applied = NetworkActorManager.ApplyOwnership(packet);

        if (!applied)
        {
            // Client dropped a stale/conflicting ownership packet. A capture request still needs its ACK
            // resolved, but the stale owner must not reach destroy-ordering.
            if (packet.CaptureIntent)
                NetworkActorCaptureManager.ResolveClientRequest(packet);
            return false;
        }

        if (Main.Server.IsRunning && clientEndpoint != null)
        {
            if (packet.CaptureIntent && packet.CaptureCommit)
                return CompleteAcceptedCaptureCommit(packet, clientEndpoint);

            Main.Server.SendToClient(packet, clientEndpoint);
        }
        else
        {
            if (packet.CaptureIntent)
                NetworkActorCaptureManager.ResolveClientRequest(packet);

            ActorDestroyHandler.ResolveDeferredDestroy(packet.ActorId.Value, packet.OwnerId);
        }

        return true;
    }

    private static bool CompleteAcceptedCaptureCommit(ActorTransferPacket packet, IPEndPoint clientEndpoint)
    {
        if (!Main.Server.ClientManager.TryGetClient(clientEndpoint, out var sender) || sender == null ||
            packet.OwnerId != sender.PlayerId)
            return false;

        var committedOwnerId = sender.PlayerId;
        var previousOwnerId = packet.PreviousOwnerId;
        if (packet.CaptureConsumesActor &&
            !ActorDestroyHandler.ApplyCommittedCaptureDestroy(packet.ActorId, committedOwnerId))
        {
            var actorEntryPresent = ActorManager.Actors.ContainsKey(packet.ActorId.Value);
            var cachedOwnerPresent = ActorManager.OwnershipByActorId.ContainsKey(packet.ActorId.Value);
            var modelPresent = GameState.TryGetIdentifiableModel(packet.ActorId, out _);
            // An accepted consume ACK is valid only after the host has removed
            // the exact live actor/model. Restore ownership and reject otherwise.
            NetworkActorManager.ApplyOwnership(new ActorTransferPacket
            {
                ActorId = packet.ActorId,
                OwnerId = previousOwnerId,
                PreviousOwnerId = committedOwnerId,
                HasPreviousOwnerId = true
            });
            SendCaptureResult(packet, clientEndpoint, previousOwnerId, approved: false, commit: true);
            SrLogger.LogError(
                $"Rejected consume commit for actor {packet.ActorId.Value}: host removal was not confirmed.");
            Logger.LogDiagnostic("capture", "host-commit-rejected",
                $"actorId={packet.ActorId.Value:X}; requestId={packet.CaptureRequestId}; claimant={committedOwnerId}; " +
                $"reason=host-removal-not-confirmed; actorEntryPresent={actorEntryPresent}; " +
                $"cachedOwnerPresent={cachedOwnerPresent}; modelPresent={modelPresent}; " +
                $"ownership-restored=true");
            return false;
        }

        // Send one canonical commit result to every peer. The server stamps this
        // packet before it is cached, so a retry carries the original transfer
        // revision even though the destroy tombstone is newer.
        Main.Server.SendToAll(packet);
        NetworkActorCaptureManager.RememberServerResult(committedOwnerId, packet);

        if (packet.CaptureConsumesActor)
        {
            Main.Server.SendToAll(new ActorDestroyPacket
            {
                ActorId = packet.ActorId,
                OwnerId = committedOwnerId,
                HasOwnerId = true
            });
        }

        Logger.LogDiagnostic("capture", "host-commit-accepted",
            $"actorId={packet.ActorId.Value:X}; requestId={packet.CaptureRequestId}; claimant={committedOwnerId}; " +
            $"previousOwner={previousOwnerId}; consumed={packet.CaptureConsumesActor}; hostActorPresent={HasLocalActorState(packet.ActorId.Value)}; " +
            NetworkActorCaptureManager.DiagActorContext(packet.ActorId.Value, committedOwnerId));

        // This commit was already sent to all clients; suppress the base handler's
        // second broadcast. Ordinary capture commits retain the actor.
        return false;
    }

    private static bool HasLocalActorState(long actorId)
        => ActorManager.Actors.ContainsKey(actorId) ||
           ActorManager.OwnershipByActorId.ContainsKey(actorId) ||
           GameState.TryGetIdentifiableModel(new Il2CppMonomiPark.SlimeRancher.DataModel.ActorId(actorId), out _);

    private static bool ValidateServerTransfer(ActorTransferPacket packet, IPEndPoint? clientEndpoint)
    {
        if (clientEndpoint == null ||
            !Main.Server.ClientManager.TryGetClient(clientEndpoint, out var sender) || sender == null ||
            packet.OwnerId != sender.PlayerId)
        {
            if (packet.CaptureIntent && packet.CaptureCommit)
                Logger.LogDiagnostic("capture", "host-commit-rejected",
                    $"actorId={packet.ActorId.Value:X}; requestId={packet.CaptureRequestId}; " +
                    $"reason=sender-authentication-failed; endpointPresent={clientEndpoint != null}; " +
                    $"claimedOwner={packet.OwnerId}");
            return false;
        }

        if (packet.CaptureIntent)
            return packet.CaptureCommit && ValidateCaptureCommit(packet, clientEndpoint, sender.PlayerId);

        if (packet.CaptureApproved || packet.CaptureCommit || packet.CaptureRequestId != Guid.Empty ||
            packet.CaptureConsumesActor)
            return false;

        string currentOwnerId;
        bool currentOwnerIsHibernating;
        if (ActorManager.Actors.TryGetValue(packet.ActorId.Value, out var model) &&
            model.TryGetNetworkComponent(out var networkActor))
        {
            currentOwnerId = networkActor.CurrentOwnerId;
            currentOwnerIsHibernating = networkActor.RegionMember?._hibernating == true;
        }
        else if (ActorManager.OwnershipByActorId.TryGetValue(packet.ActorId.Value, out var cachedOwnerId))
        {
            // Zone unloading removes the live component but keeps the world-model
            // ownership cache. A cached-only actor has no server-side simulation,
            // so an active peer may claim it using this compare-and-swap owner.
            currentOwnerId = cachedOwnerId;
            currentOwnerIsHibernating = true;
        }
        else
        {
            return false;
        }

        if (NetworkActorCaptureManager.HasServerCaptureReservation(packet.ActorId.Value) &&
            packet.OwnerId != currentOwnerId)
            return false;

        bool expectedOwnerMatches;

        if (packet.HasPreviousOwnerId)
        {
            // An empty previous owner means only an actually unowned actor. It is
            // never a wildcard for taking an actor from its current owner.
            var previousOwnerMatches = packet.PreviousOwnerId == currentOwnerId;
            var ownershipCanChange = string.IsNullOrEmpty(currentOwnerId) ||
                                     packet.OwnerId == currentOwnerId ||
                                     !PlayerManager.CheckPlayerExists(currentOwnerId) ||
                                     currentOwnerIsHibernating;
            expectedOwnerMatches = previousOwnerMatches && ownershipCanChange;
        }
        else
        {
            // Legacy peers cannot provide an expected-owner value. Keep their
            // idempotent ownership updates working, and allow takeover only after
            // the previous owner has disconnected or the actor is unowned.
            expectedOwnerMatches = packet.OwnerId == currentOwnerId ||
                                   string.IsNullOrEmpty(currentOwnerId) ||
                                   !PlayerManager.CheckPlayerExists(currentOwnerId) ||
                                   currentOwnerIsHibernating;
        }

        if (expectedOwnerMatches)
            return true;

        // Repair a sender that raced a newer ownership transfer. The response is
        // sent only to that endpoint and is not relayed as a new transfer request.
        if (!string.IsNullOrEmpty(currentOwnerId))
        {
            Main.Server.SendToClient(new ActorTransferPacket
            {
                ActorId = packet.ActorId,
                OwnerId = currentOwnerId,
                PreviousOwnerId = packet.OwnerId,
                Epoch = ActorManager.GetEpoch(packet.ActorId.Value),
                HasEpoch = true
            }, clientEndpoint);
        }

        SrLogger.LogDebug(
            $"Rejected stale actor ownership transfer for {packet.ActorId.Value}: " +
            $"sender={sender.PlayerId}, expected={packet.PreviousOwnerId}, current={currentOwnerId}");
        return false;
    }

    private static void HandleCapturePrepare(ActorTransferPacket packet, IPEndPoint? clientEndpoint)
    {
        if (clientEndpoint == null ||
            !Main.Server.ClientManager.TryGetClient(clientEndpoint, out var sender) || sender == null)
            return;

        if (packet.OwnerId != sender.PlayerId)
        {
            LogCaptureRejection(packet, sender.PlayerId, CaptureRejectionReason.Identity);
            SendCaptureResult(packet, clientEndpoint, string.Empty, approved: false, commit: false);
            return;
        }

        if (packet.CaptureApproved || packet.CaptureCommit || packet.CaptureRequestId == Guid.Empty ||
            !packet.HasPreviousOwnerId)
        {
            var reason = !packet.HasPreviousOwnerId
                ? CaptureRejectionReason.MissingOwner
                : CaptureRejectionReason.OperationMismatch;
            LogCaptureRejection(packet, sender.PlayerId, reason);
            SendCaptureResult(packet, clientEndpoint, string.Empty, approved: false, commit: false);
            return;
        }

        var senderPlayerId = sender.PlayerId;
        if (NetworkActorCaptureManager.TryGetServerResult(senderPlayerId, packet.CaptureRequestId,
                out var cachedResult))
        {
            if (!ActorCaptureTransferPolicy.IsSameCaptureRequest(
                    cachedResult.CaptureRequestId, packet.CaptureRequestId,
                    cachedResult.ActorId.Value, packet.ActorId.Value,
                    cachedResult.PreviousOwnerId, packet.PreviousOwnerId) ||
                cachedResult.CaptureConsumesActor != packet.CaptureConsumesActor)
            {
                LogCaptureRejection(packet, senderPlayerId, CaptureRejectionReason.OperationMismatch,
                    "cached request identity or operation differs");
                SendCaptureResult(packet, clientEndpoint, string.Empty, approved: false,
                    commit: false, rememberResult: false);
                return;
            }

            if (cachedResult.CaptureCommit)
            {
                LogCaptureRejection(packet, senderPlayerId, CaptureRejectionReason.Expired,
                    "prepare arrived after commit");
                // A prepare retry after the transaction was already committed is
                // stale. The final result is resent only to commit-phase retries.
                SendCaptureResult(packet, clientEndpoint, GetCurrentOwner(packet.ActorId.Value),
                    approved: false, commit: false, rememberResult: false);
                return;
            }

            if (cachedResult.CaptureApproved &&
                (!NetworkActorCaptureManager.IsServerCapturePrepared(senderPlayerId, packet) ||
                 GetCurrentOwner(packet.ActorId.Value) != cachedResult.PreviousOwnerId ||
                 !IsLiveCapturableActor(packet.ActorId.Value)))
            {
                var cachedRejection = GetCurrentOwner(packet.ActorId.Value) != cachedResult.PreviousOwnerId
                    ? CaptureRejectionReason.OwnerChanged
                    : IsLiveCapturableActor(packet.ActorId.Value)
                        ? CaptureRejectionReason.Expired
                        : CaptureRejectionReason.MissingActor;
                LogCaptureRejection(packet, senderPlayerId, cachedRejection,
                    "cached approval is no longer live");
                SendCaptureResult(packet, clientEndpoint, GetCurrentOwner(packet.ActorId.Value),
                    approved: false, commit: false);
                return;
            }

            Main.Server.SendToClient(cachedResult, clientEndpoint);
            return;
        }

        string currentOwnerId;
        if (!ActorManager.Actors.TryGetValue(packet.ActorId.Value, out var model) || model == null ||
            !model.TryGetNetworkComponent(out var networkActor))
        {
            currentOwnerId = ActorManager.OwnershipByActorId.GetValueOrDefault(packet.ActorId.Value, string.Empty);
            LogCaptureRejection(packet, senderPlayerId, CaptureRejectionReason.MissingActor,
                $"currentOwner={currentOwnerId}");
            SendCaptureResult(packet, clientEndpoint, currentOwnerId, approved: false, commit: false);
            return;
        }

        currentOwnerId = networkActor.CurrentOwnerId ?? string.Empty;
        var remotePlayer = PlayerManager.GetPlayer(senderPlayerId);
        var actorObject = model.GetGameObject();
        var targetStateReadable = TryReadVacuumableCaptureState(
            actorObject, out var targetHasVacuumable, out var targetCanCapture, out var targetIsCaptive);
        var playerSceneGroup = remotePlayer?.SceneGroup ?? -1;
        var actorSceneGroup = NetworkSceneManager.GetPersistentID(model.sceneGroup);
        var distance = remotePlayer != null && actorObject
            ? Vector3.Distance(remotePlayer.Position, actorObject.transform.position)
            : float.NaN;
        var maximumVacuumAcquisitionDistance = GetMaximumVacuumAcquisitionDistance();

        var actorExists = actorObject && actorObject.activeInHierarchy && remotePlayer != null && networkActor != null;
        var rejectionReason = ActorCaptureTransferPolicy.GetApprovalRejectionReason(
            authenticatedEndpoint: true,
            authenticatedPlayerId: senderPlayerId,
            claimedOwnerId: packet.OwnerId,
            hasExpectedOwner: packet.HasPreviousOwnerId,
            expectedOwnerId: packet.PreviousOwnerId,
            currentOwnerId: currentOwnerId,
            requestId: packet.CaptureRequestId,
            actorExists: actorExists,
            nativeStateReadable: targetStateReadable,
            targetHasVacuumable: targetHasVacuumable,
            targetCanCapture: targetCanCapture,
            targetIsCaptive: targetIsCaptive,
            playerSceneGroup: playerSceneGroup,
            actorSceneGroup: actorSceneGroup,
            playerToActorDistance: distance,
            maximumVacuumAcquisitionDistance: maximumVacuumAcquisitionDistance);

        if (rejectionReason == CaptureRejectionReason.MissingActor && actorObject && !actorObject.activeInHierarchy &&
            networkActor != null && currentOwnerId == Main.Server.PlayerId && !string.IsNullOrEmpty(senderPlayerId) &&
            float.IsFinite(distance) && distance <= maximumVacuumAcquisitionDistance &&
            playerSceneGroup == actorSceneGroup)
        {
            // The host owns the actor but its copy is asleep (region hibernating near nobody on the host), so it
            // neither simulates nor updates it while the requester sees it at their nozzle. Release it to the
            // requester; their next capture runs as the owner. Without this the client retried forever.
            NetworkActorManager.MarkReleasedByOwner(packet.ActorId.Value, currentOwnerId);
            // The requester is vacuuming it, so it is the nearest interested player by definition.
            OwnershipArbiter.RegisterInterest(senderPlayerId, packet.ActorId.Value, 0f, UnityEngine.Time.unscaledTime);
            OwnershipArbiter.EvaluateNow(packet.ActorId.Value);
            Logger.LogDiagnostic("capture", "inactive-host-copy-released",
                $"actorId={packet.ActorId.Value:X}; requester={senderPlayerId}; hostDistance={distance:F1}");
        }

        if (rejectionReason != CaptureRejectionReason.None)
        {
            LogCaptureRejection(packet, senderPlayerId, rejectionReason,
                $"currentOwner={currentOwnerId}; expectedOwner={packet.PreviousOwnerId}; " +
                $"scenes={playerSceneGroup}/{actorSceneGroup}; distance={distance:F2}; " +
                $"nativeBound={maximumVacuumAcquisitionDistance:F2}; " +
                $"hasVacuumable={targetHasVacuumable}; canCapture={targetCanCapture}; captive={targetIsCaptive}");
            SendCaptureResult(packet, clientEndpoint, currentOwnerId, approved: false, commit: false);
#if SR2MP_TEST
            if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") == "1")
                SrLogger.LogWarning(
                    $"[SR2MP_TEST] actor-capture rejected actor={packet.ActorId.Value:X} " +
                    $"requester={senderPlayerId} expected={packet.PreviousOwnerId} current={currentOwnerId} " +
                    $"scene={playerSceneGroup}/{actorSceneGroup} distance={distance} " +
                    $"range={maximumVacuumAcquisitionDistance} request={packet.CaptureRequestId}");
#endif
            return;
        }

        var approval = new ActorTransferPacket
        {
            ActorId = packet.ActorId,
            OwnerId = packet.OwnerId,
            PreviousOwnerId = currentOwnerId,
            HasPreviousOwnerId = true,
            CaptureIntent = true,
            CaptureApproved = true,
            CaptureRequestId = packet.CaptureRequestId,
            CaptureCommit = false,
            CaptureConsumesActor = packet.CaptureConsumesActor
        };
        if (!NetworkActorCaptureManager.TryReserveServerCapture(senderPlayerId, approval))
        {
            LogCaptureRejection(packet, senderPlayerId, CaptureRejectionReason.ReservationBusy);
            SendCaptureResult(packet, clientEndpoint, currentOwnerId, approved: false, commit: false);
            return;
        }

        NetworkActorCaptureManager.RememberServerResult(senderPlayerId, approval);
        Main.Server.SendToClient(approval, clientEndpoint);
        Logger.LogDiagnostic("capture", "host-approval-sent",
            $"actorId={packet.ActorId.Value:X}; requestId={packet.CaptureRequestId}; requester={senderPlayerId}; " +
            $"previousOwner={currentOwnerId}; distance={distance:F2}; nativeBound={maximumVacuumAcquisitionDistance:F2}; " +
            $"consumes={packet.CaptureConsumesActor}; " +
            NetworkActorCaptureManager.DiagActorContext(packet.ActorId.Value, senderPlayerId));
#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") == "1")
            SrLogger.LogMessage(
                $"[SR2MP_TEST] actor-capture approved actor={packet.ActorId.Value:X} " +
                $"requester={senderPlayerId} previous={currentOwnerId} distance={distance} " +
                $"range={maximumVacuumAcquisitionDistance} request={packet.CaptureRequestId}");
#endif
    }

    private static bool ValidateCaptureCommit(ActorTransferPacket packet, IPEndPoint clientEndpoint,
        string senderPlayerId)
    {
        if (packet.CaptureApproved || packet.CaptureRequestId == Guid.Empty || !packet.HasPreviousOwnerId)
        {
            Logger.LogDiagnostic("capture", "host-commit-rejected",
                $"actorId={packet.ActorId.Value:X}; requestId={packet.CaptureRequestId}; claimant={senderPlayerId}; " +
                $"reason=malformed-commit; approved={packet.CaptureApproved}; " +
                $"hasRequest={(packet.CaptureRequestId != Guid.Empty)}; hasPreviousOwner={packet.HasPreviousOwnerId}");
            SendCaptureResult(packet, clientEndpoint, GetCurrentOwner(packet.ActorId.Value),
                approved: false, commit: true);
            return false;
        }

        if (NetworkActorCaptureManager.TryGetServerResult(senderPlayerId, packet.CaptureRequestId,
                out var cachedResult))
        {
            if (!ActorCaptureTransferPolicy.IsSameCaptureRequest(
                    cachedResult.CaptureRequestId, packet.CaptureRequestId,
                    cachedResult.ActorId.Value, packet.ActorId.Value,
                    cachedResult.PreviousOwnerId, packet.PreviousOwnerId) ||
                !ActorCaptureTransferPolicy.IsCompatibleCommitKind(cachedResult.CaptureConsumesActor,
                    packet.CaptureConsumesActor))
            {
                Logger.LogDiagnostic("capture", "host-commit-rejected",
                    $"actorId={packet.ActorId.Value:X}; requestId={packet.CaptureRequestId}; claimant={senderPlayerId}; " +
                    "reason=cached-request-mismatch");
                SendCaptureResult(packet, clientEndpoint, GetCurrentOwner(packet.ActorId.Value),
                    approved: false, commit: true, rememberResult: false);
                return false;
            }

            if (cachedResult.CaptureCommit)
            {
                var currentOwner = GetCurrentOwner(packet.ActorId.Value);
                var consumedActorIsAbsent = !ActorManager.Actors.ContainsKey(packet.ActorId.Value) &&
                                            !ActorManager.OwnershipByActorId.ContainsKey(packet.ActorId.Value) &&
                                            !GameState.TryGetIdentifiableModel(packet.ActorId, out _);
                if (cachedResult.CaptureConsumesActor && cachedResult.CaptureApproved &&
                    string.Equals(cachedResult.OwnerId, senderPlayerId, StringComparison.Ordinal) &&
                    consumedActorIsAbsent)
                    Main.Server.SendCachedCaptureResult(cachedResult, clientEndpoint);
                else if (!cachedResult.CaptureConsumesActor &&
                         ActorCaptureTransferPolicy.CanReplayCachedApproval(
                        cachedApproval: cachedResult.CaptureApproved,
                        cachedOwnerId: cachedResult.OwnerId,
                        currentOwnerId: currentOwner,
                        authenticatedPlayerId: senderPlayerId))
                    Main.Server.SendCachedCaptureResult(cachedResult, clientEndpoint);
                else
                {
                    Logger.LogDiagnostic("capture", "host-commit-rejected",
                        $"actorId={packet.ActorId.Value:X}; requestId={packet.CaptureRequestId}; claimant={senderPlayerId}; " +
                        $"reason=cached-result-no-longer-valid; owner={currentOwner}; " +
                        $"consumes={cachedResult.CaptureConsumesActor}; actorAbsent={consumedActorIsAbsent}");
                    SendCaptureResult(packet, clientEndpoint, currentOwner,
                        approved: false, commit: true, rememberResult: false);
                }
                return false;
            }
        }

        var currentOwnerId = GetCurrentOwner(packet.ActorId.Value);
        var reservationMatches = NetworkActorCaptureManager.IsServerCapturePrepared(senderPlayerId, packet);
        var reservationNotExpired = reservationMatches;
        var targetStillEligible = IsLiveCapturableActor(packet.ActorId.Value);
        var accepted = ActorCaptureTransferPolicy.CanCommitPreparedCapture(
            authenticatedEndpoint: true,
            authenticatedPlayerId: senderPlayerId,
            claimedOwnerId: packet.OwnerId,
            hasExpectedOwner: packet.HasPreviousOwnerId,
            expectedOwnerId: packet.PreviousOwnerId,
            currentOwnerId: currentOwnerId,
            requestId: packet.CaptureRequestId,
            reservationMatches: reservationMatches,
            reservationNotExpired: reservationNotExpired,
            targetStillEligible: targetStillEligible);

        if (!accepted)
        {
            Logger.LogDiagnostic("capture", "host-commit-rejected",
                $"actorId={packet.ActorId.Value:X}; requestId={packet.CaptureRequestId}; claimant={senderPlayerId}; " +
                $"reason=prepare-validation-failed; owner={currentOwnerId}; expectedOwner={packet.PreviousOwnerId}; " +
                $"reservationMatches={reservationMatches}; actorLive={targetStillEligible}; " +
                $"consumes={packet.CaptureConsumesActor}");
            SendCaptureResult(packet, clientEndpoint, currentOwnerId, approved: false, commit: true);
            return false;
        }

        packet.CaptureApproved = true;
        packet.CaptureCommit = true;
        return true;
    }

    private static string GetCurrentOwner(long actorId)
    {
        if (ActorManager.Actors.TryGetValue(actorId, out var model) &&
            model.TryGetNetworkComponent(out var networkActor))
            return networkActor.CurrentOwnerId ?? string.Empty;

        return ActorManager.OwnershipByActorId.GetValueOrDefault(actorId, string.Empty);
    }

    private static bool IsLiveCapturableActor(long actorId)
    {
        try
        {
            if (!ActorManager.Actors.TryGetValue(actorId, out var model) || model == null ||
                !model.TryGetNetworkComponent(out _))
                return false;

            var actorObject = model.GetGameObject();
            if (!actorObject || !actorObject.activeInHierarchy ||
                !TryReadVacuumableCaptureState(actorObject,
                    out var hasVacuumable, out var canCapture, out var isCaptive))
                return false;

            return hasVacuumable && canCapture && !isCaptive;
        }
        catch (Exception exception)
        {
            SrLogger.LogWarning(
                $"Could not revalidate vacuum capture target {actorId}: {exception.Message}");
            return false;
        }
    }

    private static bool TryReadVacuumableCaptureState(
        GameObject? actorObject,
        out bool hasVacuumable,
        out bool canCapture,
        out bool isCaptive)
    {
        hasVacuumable = false;
        canCapture = false;
        isCaptive = true;
        if (!actorObject)
            return true;

        try
        {
            var vacuumable = actorObject!.GetComponent<Vacuumable>();
            if (!vacuumable)
                return true;

            hasVacuumable = true;
            canCapture = vacuumable.CanCapture();
            isCaptive = vacuumable.IsCaptive();
            return true;
        }
        catch (Exception exception)
        {
            SrLogger.LogWarning(
                $"Could not read Vacuumable capture state: {exception.Message}");
            return false;
        }
    }

    private static float GetMaximumVacuumAcquisitionDistance()
    {
        try
        {
            var player = SceneContext.Instance?.player;
            if (!player)
                return float.NaN;

            var itemController = player.GetComponent<PlayerItemController>();
            var vacuumItem = itemController ? itemController._vacuumItem : null;
            if (!vacuumItem)
                return float.NaN;

            return ActorCaptureTransferPolicy.GetMaximumVacuumAcquisitionDistance(vacuumItem.MaxVacDist);
        }
        catch (Exception exception)
        {
            SrLogger.LogWarning($"Could not read the host vacuum acquisition range: {exception.Message}");
            return float.NaN;
        }
    }

    private static void LogCaptureRejection(ActorTransferPacket packet, string requester,
        CaptureRejectionReason reason, string? details = null)
    {
        Logger.LogDiagnostic("capture", "host-approval-rejected",
            $"actorId={packet.ActorId.Value:X}; requestId={packet.CaptureRequestId}; " +
            $"requester={requester}; reason={reason}; {details ?? string.Empty}; " +
            NetworkActorCaptureManager.DiagActorContext(packet.ActorId.Value, requester));
    }

    private static void SendCaptureResult(ActorTransferPacket request, IPEndPoint clientEndpoint,
        string currentOwnerId, bool approved, bool commit, bool rememberResult = true)
    {
        var response = new ActorTransferPacket
        {
            ActorId = request.ActorId,
            OwnerId = currentOwnerId,
            PreviousOwnerId = request.PreviousOwnerId,
            HasPreviousOwnerId = true,
            CaptureIntent = true,
            CaptureApproved = approved,
            CaptureRequestId = request.CaptureRequestId,
            CaptureCommit = commit,
            CaptureConsumesActor = request.CaptureConsumesActor,
            // Current epoch, no bump: a result reports the owner, it does not change it.
            Epoch = ActorManager.GetEpoch(request.ActorId.Value),
            HasEpoch = true
        };

        if (rememberResult)
            NetworkActorCaptureManager.RememberServerResult(Main.Server.ClientManager.TryGetClient(
                clientEndpoint, out var sender) && sender != null ? sender.PlayerId : string.Empty, response);
        Main.Server.SendToClient(response, clientEndpoint);
#if SR2MP_TEST
        if (string.IsNullOrEmpty(currentOwnerId) &&
            Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_EMPTY_CAPTURE") == "1")
            SrLogger.LogMessage(
                $"[SR2MP_TEST] empty-owner-capture-result sent actor={response.ActorId.Value:X} " +
                $"approved={approved} commit={commit} request={response.CaptureRequestId}");
#endif
    }
}

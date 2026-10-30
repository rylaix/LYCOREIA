#if SR2MP_TEST
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.UI.MainMenu;
using Il2CppMonomiPark.SlimeRancher.UI.MainMenu.Model;
using Il2CppMonomiPark.SlimeRancher.Player.PlayerItems;
using Il2CppMonomiPark.SlimeRancher.Caretaker;
using Il2CppMonomiPark.SlimeRancher.Event;
using Il2CppMonomiPark.SlimeRancher.Player;
using Il2CppMonomiPark.SlimeRancher.UI.IntroSequence;
using System.Reflection;
using SR2MP.Handlers.Internal;
using SR2MP.Handlers.LandPlots;
using SR2MP.Shared.Managers;

namespace SR2MP.Shared.Utils;

/// <summary>
/// Local-only test automation. It is dormant unless a test environment variable is set.
/// This lets isolated Proton copies start/join without relying on the game's mouse capture.
/// </summary>
internal static class TestBootstrap
{
    private static bool hostAttempted;
    private static int clientAttemptCount;
    private static float nextClientAttemptAt;
    private static bool continueAttempted;
    private static bool newGameStartAttempted;
    private static bool displaySettingsRequestLogged;
    private static bool introSkipAttempted;
    private static bool resyncAllAttempted;
    private static bool requestResyncAttempted;
    private static bool playerFxDumpAttempted;
    private static bool ammoDecrementTestAttempted;
    private static bool eventDumpAttempted;
    private static bool actorModelDumpAttempted;
    private static bool ownerSnapshotSelfTestAttempted;
    private static bool gardenStateSelfTestAttempted;
    private static bool gardenOwnershipSelfTestAttempted;
    private static bool actorDestroySelfTestAttempted;
    private static bool actorCaptureSelfTestAttempted;
    private static bool emptyOwnerCaptureProbeAttempted;
    private static bool captureAbortProbePrepared;
    private static bool captureAbortProbeResponseSent;
    private static bool ammoSnapshotSelfTestAttempted;
    private static bool packetMutationAuthorizationSelfTestAttempted;
    private static bool orderedDeliverySelfTestAttempted;
    private static bool upgradeRevisionSelfTestAttempted;
    private static bool upgradePurchaseSelfTestAttempted;
    private static bool playerInventorySelfTestAttempted;
    private static bool playerFxSelfTestsAttempted;
    private static bool slimeBehaviorSnapshotSelfTestAttempted;
    private static bool pendingSnapshotDeltaSelfTestAttempted;
    private static bool subsystemPolicySelfTestsAttempted;
    private static bool fullSyncPolicySelfTestsAttempted;
    private static float hostClientConnectedAt = -1f;
    private static float clientJoinedAt = -1f;
    private static float eventDumpConnectedAt = -1f;
    private static float ammoTestConnectedAt = -1f;
    private static float nextVacuumTrace;
    private static string? lastVacuumTrace;
    private static float nextContinueAttempt;
    private static float nextDisplaySettingsCheck;
    private static float nextIntroSkipAttempt;
    private static float nextMenuTrace;
    private static float nextPeriodicCapture = 20f;
    private static int screenshotSequence;
    private static float nextStatusLog;
    private static readonly long CaptureAbortProbeActorId = 0x7ffffffe;
    private static readonly Guid CaptureAbortProbeRequestId =
        Guid.Parse("12a86c12-94b2-4e47-96a1-76d31c4a6e22");

    internal static void Pump()
    {
        try
        {
            if (TestLifecycleControl.Pump())
                return;

            TryEnforceTestDisplaySettings();
            TryOwnerSnapshotSelfTest();
            TryGardenStateSelfTest();
            TryGardenOwnershipSelfTest();
            TryActorDestroySelfTest();
            TryActorCaptureSelfTests();
            TryAmmoSnapshotSelfTest();
            TryPacketMutationAuthorizationSelfTest();
            TryOrderedDeliverySelfTest();
            TryUpgradeRevisionSelfTest();
            TryUpgradePurchaseSelfTest();
            TryPlayerInventorySelfTest();
            TryPlayerFxSelfTests();
            TrySlimeBehaviorSnapshotSelfTest();
            TryPendingSnapshotDeltaSelfTest();
            TrySubsystemPolicySelfTests();
            TryFullSyncPolicySelfTests();
            TryAutoContinue();
            TryAutoStartNewGame();
            TrySkipIntroSequence();
            TraceMainMenu();

            var screenshotPath = Environment.GetEnvironmentVariable("SR2MP_TEST_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(screenshotPath)
                && UnityEngine.Time.unscaledTime >= nextPeriodicCapture
                && screenshotSequence < 6)
            {
                nextPeriodicCapture = UnityEngine.Time.unscaledTime + 20f;
                CaptureGameFrame(screenshotPath);
            }

            var gameplayReady = SystemContext.Instance
                                && SystemContext.Instance.SceneLoader.IsCurrentSceneGroupGameplay();
            var scenePlayer = SceneContext.Instance ? SceneContext.Instance.player : null;
            var playerReady = scenePlayer != null;
            var playerPositionReady = playerReady
                                      && scenePlayer!.transform.position.sqrMagnitude > 1f;
            if (!gameplayReady || !playerPositionReady)
            {
                if (UnityEngine.Time.unscaledTime >= nextStatusLog)
                {
                    nextStatusLog = UnityEngine.Time.unscaledTime + 5f;
                    SrLogger.LogMessage(
                        $"[SR2MP_TEST] waiting scene={(bool)SceneContext.Instance} player={playerReady} " +
                        $"position={playerPositionReady} " +
                        $"gameplay={gameplayReady} actorModels={ActorManager.Actors.Count}");
                }
                return;
            }

            TryEmptyOwnerCaptureProbe();
            TryPrepareCaptureAbortProbe();
            TryDumpPlayerFxHierarchy();
            TryTraceVacuumState();
            TryDumpEventProducers();
            TryDumpActorModels();
            TryExerciseSiloDecrement();
            TestShopTrace.Pump();
            TestGameplayBot.PumpHostCaptureTarget();
            TestGameplayBot.Pump(scenePlayer!);

            if (!hostAttempted && ushort.TryParse(
                    Environment.GetEnvironmentVariable("SR2MP_TEST_AUTOHOST_PORT"), out var hostPort))
            {
                hostAttempted = true;
                Main.Server.Start(hostPort, true);
                SrLogger.LogMessage($"[SR2MP_TEST] Auto-host requested on UDP {hostPort}; running={Main.Server.IsRunning}");
            }

            var target = Environment.GetEnvironmentVariable("SR2MP_TEST_AUTOCONNECT");
            if (!string.IsNullOrWhiteSpace(target) &&
                !Main.Client.IsConnected && !Main.Client.IsJoined &&
                clientAttemptCount < 12 &&
                UnityEngine.Time.unscaledTime >= TestLifecycleControl.SuppressAutoConnectUntil &&
                UnityEngine.Time.unscaledTime >= nextClientAttemptAt)
            {
                var separator = target.LastIndexOf(':');
                if (separator > 0 && ushort.TryParse(target[(separator + 1)..], out var port))
                {
                    var address = target[..separator].Trim('[', ']');
                    clientAttemptCount++;
                    nextClientAttemptAt = UnityEngine.Time.unscaledTime + 10f;
                    Main.Client.Connect(address, port);
                    SrLogger.LogMessage(
                        $"[SR2MP_TEST] Auto-connect attempt {clientAttemptCount}/12 to {address}:{port}");
                }
                else
                    nextClientAttemptAt = UnityEngine.Time.unscaledTime + 30f;
            }

            TryScheduledResync();

            if (UnityEngine.Time.unscaledTime < nextStatusLog)
                return;

            nextStatusLog = UnityEngine.Time.unscaledTime + 5f;
            var playerPosition = scenePlayer!.transform.position;
            var remotePositions = string.Join(";", PlayerObjects
                .Where(entry => entry.Value)
                .Select(entry => $"{entry.Key}@{entry.Value.transform.position}"));
            var actorIdTrace = string.Empty;
            var ownershipTrace = string.Empty;
            if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") == "1")
            {
                var actorIds = ActorManager.Actors.Keys
                    .OrderBy(actorId => actorId)
                    .Select(actorId => actorId.ToString("X"));
                actorIdTrace = $" actorIds=[{string.Join(",", actorIds)}]";

                var owners = ActorManager.OwnershipByActorId
                    .OrderBy(entry => entry.Key)
                    .Select(entry => $"{entry.Key:X}:{(string.IsNullOrWhiteSpace(entry.Value) ? "<none>" : entry.Value)}");
                ownershipTrace = $" ownership=[{string.Join(",", owners)}]";
            }
            SrLogger.LogMessage(
                $"[SR2MP_TEST] state host={Main.Server.IsRunning} clients={Main.Server.GetClientCount} " +
                $"client={Main.Client.IsConnected} joined={Main.Client.IsJoined} local={LocalID} " +
                $"position={playerPosition} actors={ActorManager.Actors.Count}{actorIdTrace}{ownershipTrace} " +
                $"remotes=[{remotePositions}]");
        }
        catch (Exception ex)
        {
            SrLogger.LogError($"[SR2MP_TEST] Bootstrap error: {ex}");
        }
    }

    private static void TryOwnerSnapshotSelfTest()
    {
        if (ownerSnapshotSelfTestAttempted)
            return;

        ownerSnapshotSelfTestAttempted = true;
        var passed = NetworkDroneManager.VerifyOwnerSnapshotReconciliation();
        SrLogger.LogMessage($"[SR2MP_TEST] drone-owner-snapshot-selftest={(passed ? "PASS" : "FAIL")}");
    }

    private static void TryGardenStateSelfTest()
    {
        if (gardenStateSelfTestAttempted)
            return;

        gardenStateSelfTestAttempted = true;
        var passed = GardenStateCache.VerifyRevisionOrdering();
        var cropOrdering = GardenCropStateCache.VerifyRevisionOrdering();
        var cropMutation = GardenCropMutationPolicy.VerifyHostMutationAcceptance();
        var rejectedCropCorrection = GardenCropMutationPolicy.VerifyRejectedCorrectionRules();
        var cropCodec = SR2MP.Packets.LandPlots.GardenPlantPacket.VerifySerializationRoundTrip();
        SrLogger.LogMessage(
            $"[SR2MP_TEST] garden-state-revision-selftest={(passed ? "PASS" : "FAIL")} " +
            $"garden-crop-revision-selftest={(cropOrdering ? "PASS" : "FAIL")} " +
            $"garden-host-crop-acceptance-selftest={(cropMutation ? "PASS" : "FAIL")} " +
            $"garden-rejected-crop-correction-selftest={(rejectedCropCorrection ? "PASS" : "FAIL")} " +
            $"garden-crop-codec={(cropCodec ? "PASS" : "FAIL")}");
    }

    private static void TryGardenOwnershipSelfTest()
    {
        if (gardenOwnershipSelfTestAttempted)
            return;

        gardenOwnershipSelfTestAttempted = true;
        var passed = NetworkGardenManager.VerifyOwnershipAuthorization();
        var releaseStateRules = NetworkGardenManager.VerifyFinalStateReleaseRules();
        var releaseStateCodec = SR2MP.Packets.LandPlots.GardenOwnershipPacket.VerifyFinalStateSerializationRoundTrip();
        SrLogger.LogMessage(
            $"[SR2MP_TEST] garden-owner-authorization-selftest={(passed ? "PASS" : "FAIL")} " +
            $"garden-release-state-policy={(releaseStateRules ? "PASS" : "FAIL")} " +
            $"garden-release-state-codec={(releaseStateCodec ? "PASS" : "FAIL")}");
    }

    private static void TryActorDestroySelfTest()
    {
        if (actorDestroySelfTestAttempted)
            return;

        actorDestroySelfTestAttempted = true;
        var passed = ActorDestroyPolicy.VerifyRules();
        SrLogger.LogMessage($"[SR2MP_TEST] actor-destroy-authority-selftest={(passed ? "PASS" : "FAIL")}");
    }

    private static void TryActorCaptureSelfTests()
    {
        if (actorCaptureSelfTestAttempted)
            return;

        actorCaptureSelfTestAttempted = true;
        var capturePolicy = ActorCaptureTransferPolicy.VerifyRules();
        var destroyOrdering = ActorDestroyOrderingPolicy.VerifyRules();
        var transferCodec = SR2MP.Packets.Actor.ActorTransferPacket.VerifySerializationRoundTrip();
        var epochTests = SR2MP.Shared.Managers.OwnershipEpochPolicy.VerifyRules() &&
                         SR2MP.Shared.Managers.OwnershipArbiter.VerifyRules() &&
                         SR2MP.Packets.Actor.ActorUpdatePacket.VerifySerializationRoundTrip();
        var abortCodec = SR2MP.Packets.Actor.CaptureAbortPacket.VerifySerializationRoundTrip();
        var destroyCodec = SR2MP.Packets.Actor.ActorDestroyPacket.VerifySerializationRoundTrip();
        var reservationAbort = NetworkActorCaptureManager.VerifyServerReservationAbort();
        SrLogger.LogMessage(
            $"[SR2MP_TEST] actor-capture-policy-selftest={(capturePolicy ? "PASS" : "FAIL")} " +
            $"actor-destroy-ordering-selftest={(destroyOrdering ? "PASS" : "FAIL")} " +
            $"actor-transfer-codec={(transferCodec ? "PASS" : "FAIL")} " +
            $"ownership-epoch={(epochTests ? "PASS" : "FAIL")} " +
            $"actor-capture-abort-codec={(abortCodec ? "PASS" : "FAIL")} " +
            $"actor-capture-reservation-abort-selftest={(reservationAbort ? "PASS" : "FAIL")} " +
            $"actor-destroy-codec={(destroyCodec ? "PASS" : "FAIL")}");
    }

    private static void TryEmptyOwnerCaptureProbe()
    {
        if (emptyOwnerCaptureProbeAttempted ||
            Environment.GetEnvironmentVariable("SR2MP_TEST_PROBE_EMPTY_CAPTURE") != "1" ||
            !Main.Client.IsConnected || !Main.Client.IsJoined)
            return;

        emptyOwnerCaptureProbeAttempted = true;
        var requestId = Guid.NewGuid();
        var probe = new SR2MP.Packets.Actor.ActorTransferPacket
        {
            // This deliberately nonexistent id cannot mutate a live actor. The host
            // should return a negative preflight response with both owner fields empty.
            ActorId = new ActorId(0x7fffffff),
            OwnerId = LocalID,
            PreviousOwnerId = string.Empty,
            HasPreviousOwnerId = true,
            CaptureIntent = true,
            CaptureApproved = false,
            CaptureRequestId = requestId,
            CaptureCommit = false
        };

        Main.Client.SendPacket(probe);
        SrLogger.LogMessage(
            $"[SR2MP_TEST] empty-owner-capture-probe sent actor={probe.ActorId.Value:X} request={requestId}");
    }

    private static void TryPrepareCaptureAbortProbe()
    {
        if (captureAbortProbePrepared ||
            Environment.GetEnvironmentVariable("SR2MP_TEST_PROBE_CAPTURE_ABORT") != "1" ||
            !Main.Server.IsRunning)
            return;

        var client = Main.Server.ClientManager.GetAllClients().FirstOrDefault();
        if (client == null)
            return;

        var approval = new SR2MP.Packets.Actor.ActorTransferPacket
        {
            // This synthetic id has no actor model; the probe exercises only
            // authenticated reservation release and cannot alter a live actor.
            ActorId = new ActorId(CaptureAbortProbeActorId),
            OwnerId = client.PlayerId,
            PreviousOwnerId = LocalID,
            HasPreviousOwnerId = true,
            CaptureIntent = true,
            CaptureApproved = true,
            CaptureRequestId = CaptureAbortProbeRequestId,
            CaptureCommit = false
        };

        if (!NetworkActorCaptureManager.TryReserveServerCapture(client.PlayerId, approval))
            return;

        NetworkActorCaptureManager.RememberServerResult(client.PlayerId, approval);
        captureAbortProbePrepared = true;
        try
        {
            Main.Server.SendToClient(approval, client.EndPoint);
            SrLogger.LogMessage(
                $"[SR2MP_TEST] actor-capture abort-probe prepared actor={CaptureAbortProbeActorId:X} " +
                $"player={client.PlayerId} request={CaptureAbortProbeRequestId}");
        }
        catch (Exception exception)
        {
            NetworkActorCaptureManager.AbortServerCapture(
                client.PlayerId, CaptureAbortProbeActorId, CaptureAbortProbeRequestId);
            SrLogger.LogError($"[SR2MP_TEST] actor-capture abort-probe send failed: {exception}");
        }
    }

    internal static bool TryHandleCaptureAbortProbe(SR2MP.Packets.Actor.ActorTransferPacket packet)
    {
        if (captureAbortProbeResponseSent ||
            Environment.GetEnvironmentVariable("SR2MP_TEST_PROBE_CAPTURE_ABORT") != "1" ||
            !Main.Client.IsConnected ||
            packet.ActorId.Value != CaptureAbortProbeActorId ||
            packet.CaptureRequestId != CaptureAbortProbeRequestId ||
            !packet.CaptureIntent || !packet.CaptureApproved || packet.CaptureCommit ||
            !string.Equals(packet.OwnerId, LocalID, StringComparison.Ordinal))
            return false;

        captureAbortProbeResponseSent = true;
        try
        {
            Main.Client.SendPacket(new SR2MP.Packets.Actor.CaptureAbortPacket
            {
                ActorId = packet.ActorId,
                CaptureRequestId = packet.CaptureRequestId
            });
            SrLogger.LogMessage(
                $"[SR2MP_TEST] actor-capture abort-probe sent actor={packet.ActorId.Value:X} " +
                $"request={packet.CaptureRequestId}");
            return true;
        }
        catch (Exception exception)
        {
            SrLogger.LogError($"[SR2MP_TEST] actor-capture abort-probe send failed: {exception}");
            return true;
        }
    }

    private static void TryAmmoSnapshotSelfTest()
    {
        if (ammoSnapshotSelfTestAttempted)
            return;

        ammoSnapshotSelfTestAttempted = true;
        var passed = SR2MP.Packets.Ammo.NetworkAmmoSlot.VerifyRadiantSerializationRoundTrip();
        SrLogger.LogMessage($"[SR2MP_TEST] ammo-slot-radiant-selftest={(passed ? "PASS" : "FAIL")}");
    }

    private static void TryPacketMutationAuthorizationSelfTest()
    {
        if (packetMutationAuthorizationSelfTestAttempted)
            return;

        packetMutationAuthorizationSelfTestAttempted = true;
        var passed = PacketMutationAuthorization.VerifyRules();
        var connectionApproveCodec = SR2MP.Packets.Internal.ConnectionApprovePacket.VerifySerializationRoundTrip();
        SrLogger.LogMessage(
            $"[SR2MP_TEST] packet-mutation-auth-selftest={(passed ? "PASS" : "FAIL")} " +
            $"connection-approve-currency-codec={(connectionApproveCodec ? "PASS" : "FAIL")}");
    }

    private static void TryOrderedDeliverySelfTest()
    {
        if (orderedDeliverySelfTestAttempted)
            return;

        orderedDeliverySelfTestAttempted = true;
        var expected = SR2MP.Packets.Utils.PacketReliability.ReliableOrdered;
        var initialMap = new SR2MP.Packets.Loading.InitialMapPacket();
        var mapUnlock = new SR2MP.Packets.World.MapUnlockPacket();
        var initialSwitches = new SR2MP.Packets.Loading.InitialSwitchesPacket();
        var worldSwitch = new SR2MP.Packets.Switch.WorldSwitchPacket();
        var initialPedia = new SR2MP.Packets.Loading.InitialPediaPacket();
        var pediaUnlock = new SR2MP.Packets.Pedia.PediaUnlockPacket();
        var initialRefinery = new SR2MP.Packets.Loading.InitialRefineryPacket();
        var refineryUpdate = new SR2MP.Packets.World.RefineryUpdatePacket();
        var refineryDeltaPolicy = RefineryMutationPolicy.VerifyRules();
        var refinerySnapshotCodec = SR2MP.Packets.Loading.InitialRefineryPacket.VerifySerializationRoundTrip();
        var refineryUpdateCodec = SR2MP.Packets.World.RefineryUpdatePacket.VerifySerializationRoundTrip();
        var initialLandPlots = new SR2MP.Packets.Loading.InitialLandPlotsPacket();
        var gardenOwnership = new SR2MP.Packets.LandPlots.GardenOwnershipPacket();
        var gardenUpdate = new SR2MP.Packets.LandPlots.GardenUpdatePacket();
        var gardenPlant = new SR2MP.Packets.LandPlots.GardenPlantPacket();
        var actorSnapshot = new SR2MP.Packets.Loading.InitialActorsPacket();
        var actorSpawn = new SR2MP.Packets.Actor.ActorSpawnPacket();
        var actorDestroy = new SR2MP.Packets.Actor.ActorDestroyPacket();
        var actorTransfer = new SR2MP.Packets.Actor.ActorTransferPacket();
        var actorState = new SR2MP.Packets.Actor.ActorStatePacket();
        var currency = new SR2MP.Packets.Economy.CurrencyPacket();
        var marketPrices = new SR2MP.Packets.Economy.MarketPricePacket();
        var ammoAdd = new SR2MP.Packets.Ammo.AmmoAddPacket();
        var ammoAddToSlot = new SR2MP.Packets.Ammo.AmmoAddToSlotPacket();
        var ammoDecrement = new SR2MP.Packets.Ammo.AmmoDecrementPacket();

        var passed = initialMap.Reliability == expected && mapUnlock.Reliability == expected &&
                     initialMap.Channel == mapUnlock.Channel &&
                     initialSwitches.Reliability == expected && worldSwitch.Reliability == expected &&
                     initialSwitches.Channel == worldSwitch.Channel &&
                     initialPedia.Reliability == expected && pediaUnlock.Reliability == expected &&
                     initialPedia.Channel == pediaUnlock.Channel &&
                     initialRefinery.Reliability == expected && refineryUpdate.Reliability == expected &&
                     initialRefinery.Channel == refineryUpdate.Channel &&
                     refinerySnapshotCodec && refineryUpdateCodec && refineryDeltaPolicy &&
                     initialLandPlots.Reliability == expected && gardenOwnership.Reliability == expected &&
                     gardenUpdate.Reliability == expected && gardenPlant.Reliability == expected &&
                     initialLandPlots.Channel == gardenPlant.Channel &&
                     initialLandPlots.Channel == gardenOwnership.Channel &&
                     gardenOwnership.Channel == gardenUpdate.Channel &&
                     actorSnapshot.Reliability == expected && actorSpawn.Reliability == expected &&
                     actorDestroy.Reliability == expected && actorTransfer.Reliability == expected &&
                     actorState.Reliability == expected && actorSnapshot.Channel == actorSpawn.Channel &&
                     actorSpawn.Channel == actorDestroy.Channel && actorDestroy.Channel == actorTransfer.Channel &&
                     actorTransfer.Channel == actorState.Channel &&
                     currency.Reliability == expected && marketPrices.Reliability == expected &&
                     currency.Channel == marketPrices.Channel && ammoAdd.Reliability == expected &&
                     ammoAddToSlot.Reliability == expected && ammoDecrement.Reliability == expected &&
                     ammoAdd.Channel == ammoAddToSlot.Channel && ammoAddToSlot.Channel == ammoDecrement.Channel;

        SrLogger.LogMessage(
            $"[SR2MP_TEST] ordered-snapshot-delivery-selftest={(passed ? "PASS" : "FAIL")} " +
            $"refinery-delta-dedup-concurrency-limits={(refineryDeltaPolicy ? "PASS" : "FAIL")} " +
            $"refinery-snapshot-codec={(refinerySnapshotCodec ? "PASS" : "FAIL")} " +
            $"refinery-update-codec={(refineryUpdateCodec ? "PASS" : "FAIL")}");
    }

    private static void TryUpgradeRevisionSelfTest()
    {
        if (upgradeRevisionSelfTestAttempted)
            return;

        upgradeRevisionSelfTestAttempted = true;
        var passed = NetworkUpgradeManager.VerifyRevisionOrdering();
        var livePacketRoundTrip = SR2MP.Packets.Upgrade.PlayerUpgradePacket.VerifySerializationRoundTrip();
        var snapshotRoundTrip = SR2MP.Packets.Loading.InitialUpgradesPacket.VerifySerializationRoundTrip();
        SrLogger.LogMessage(
            $"[SR2MP_TEST] upgrade-revision-ordering-selftest={(passed ? "PASS" : "FAIL")} " +
            $"live-packet-codec={(livePacketRoundTrip ? "PASS" : "FAIL")} " +
            $"snapshot-packet-codec={(snapshotRoundTrip ? "PASS" : "FAIL")}");
    }

    private static void TryUpgradePurchaseSelfTest()
    {
        if (upgradePurchaseSelfTestAttempted)
            return;

        upgradePurchaseSelfTestAttempted = true;
        var passed = UpgradePurchaseRequestLedger.VerifyRules();
        SrLogger.LogMessage($"[SR2MP_TEST] upgrade-purchase-request-ledger-selftest={(passed ? "PASS" : "FAIL")}");
    }

    private static void TryPlayerInventorySelfTest()
    {
        if (playerInventorySelfTestAttempted)
            return;

        playerInventorySelfTestAttempted = true;
        var passed = PlayerInventorySnapshotRules.VerifySerializationAndBounds();
        SrLogger.LogMessage($"[SR2MP_TEST] player-inventory-snapshot-selftest={(passed ? "PASS" : "FAIL")}");
    }

    private static void TryPlayerFxSelfTests()
    {
        if (playerFxSelfTestsAttempted)
            return;

        playerFxSelfTestsAttempted = true;
        var suctionRouting = SR2MP.Packets.FX.PlayerFXPacket.VerifySuctionStateProtocol();
        var visualPolicy = SR2MP.Components.FX.RemoteVacuumSuctionFX.VerifyVisualComponentPolicy();
        SrLogger.LogMessage(
            $"[SR2MP_TEST] player-fx-suction-routing-selftest={(suctionRouting ? "PASS" : "FAIL")} " +
            $"remote-vacuum-visual-policy-selftest={(visualPolicy ? "PASS" : "FAIL")}");
    }

    private static void TrySlimeBehaviorSnapshotSelfTest()
    {
        if (slimeBehaviorSnapshotSelfTestAttempted)
            return;

        slimeBehaviorSnapshotSelfTestAttempted = true;
        var ordering = NetworkSlimeBehaviorManager.VerifyRevisionOrdering();
        var codec = SR2MP.Packets.Slime.InitialSlimeBehaviorSnapshotPacket.VerifySerializationRoundTrip();
        SrLogger.LogMessage(
            $"[SR2MP_TEST] slime-behavior-revision-ordering-selftest={(ordering ? "PASS" : "FAIL")} " +
            $"snapshot-codec={(codec ? "PASS" : "FAIL")}");
    }

    private static void TryPendingSnapshotDeltaSelfTest()
    {
        if (pendingSnapshotDeltaSelfTestAttempted)
            return;

        pendingSnapshotDeltaSelfTestAttempted = true;
        var passed = OrderedSnapshotDeltaQueue<int, int>.VerifyOrdering();
        SrLogger.LogMessage($"[SR2MP_TEST] pending-snapshot-delta-ordering-selftest={(passed ? "PASS" : "FAIL")}");
    }

    private static void TrySubsystemPolicySelfTests()
    {
        if (subsystemPolicySelfTestsAttempted)
            return;

        subsystemPolicySelfTestsAttempted = true;

        var droneMutation = DroneMutationPolicy.VerifyRules();
        var droneOwnerCodec = SR2MP.Packets.Drone.DroneOwnershipPacket.VerifySerializationRoundTrip();
        var droneUpdateCodec = SR2MP.Packets.Drone.DroneUpdatePacket.VerifySerializationRoundTrip();
        var droneResourceCodec = SR2MP.Packets.World.DroneResourcePacket.VerifySerializationRoundTrip();

        var currencyProtocol = NetworkCurrencyManager.VerifyProtocolRules();
        var shopRequestCodec = SR2MP.Packets.Shop.ShopPurchaseRequestPacket.VerifySerializationRoundTrip();
        var shopResultCodec = SR2MP.Packets.Shop.ShopPurchaseResultPacket.VerifySerializationRoundTrip();
        var shopLedger = ShopPurchaseRequestLedger.VerifyRules();
        var shopSnapshotCodec = SR2MP.Packets.Shop.ShopStateSnapshotPacket.VerifySerializationRoundTrip();
        var shopSnapshotOrdering = SR2MP.Packets.Shop.ShopStateSnapshotPacket.VerifyRevisionOrdering();

        var resourceNode = ResourceNodeSyncPolicy.VerifyRules();
        var actorSpawnPublication = ActorSpawnPublicationPolicy.VerifyRules();
        var gadgetPair = GadgetLinkPairPolicy.VerifyRules();
        var teleporterSnapshot = TeleporterLinkSnapshotPolicy.VerifyRules();
        var treasurePod = TreasurePodOpenPolicy.VerifyRules();
        var eventSync = EventSyncPolicy.VerifyRules();
        var mapUnlock = SR2MP.Handlers.Map.MapUnlockPolicy.VerifyRules();
        var weatherSync = SR2MP.Client.Managers.WeatherSyncPolicy.VerifyRules();

        SrLogger.LogMessage(
            $"[SR2MP_TEST] drone-mutation-policy={(droneMutation ? "PASS" : "FAIL")} " +
            $"drone-owner-codec={(droneOwnerCodec ? "PASS" : "FAIL")} " +
            $"drone-update-codec={(droneUpdateCodec ? "PASS" : "FAIL")} " +
            $"drone-resource-codec={(droneResourceCodec ? "PASS" : "FAIL")} " +
            $"currency-protocol={(currencyProtocol ? "PASS" : "FAIL")} " +
            $"shop-request-codec={(shopRequestCodec ? "PASS" : "FAIL")} " +
            $"shop-result-codec={(shopResultCodec ? "PASS" : "FAIL")} " +
            $"shop-ledger={(shopLedger ? "PASS" : "FAIL")} " +
            $"shop-snapshot-codec={(shopSnapshotCodec ? "PASS" : "FAIL")} " +
            $"shop-snapshot-ordering={(shopSnapshotOrdering ? "PASS" : "FAIL")} " +
            $"resource-node-policy={(resourceNode ? "PASS" : "FAIL")} " +
            $"actor-spawn-publication-policy={(actorSpawnPublication ? "PASS" : "FAIL")} " +
            $"gadget-pair-policy={(gadgetPair ? "PASS" : "FAIL")} " +
            $"teleporter-snapshot-policy={(teleporterSnapshot ? "PASS" : "FAIL")} " +
            $"treasure-pod-policy={(treasurePod ? "PASS" : "FAIL")} " +
            $"event-sync-policy={(eventSync ? "PASS" : "FAIL")} " +
            $"map-unlock-policy={(mapUnlock ? "PASS" : "FAIL")} " +
            $"weather-sync-policy={(weatherSync ? "PASS" : "FAIL")}");
    }

    private static void TryFullSyncPolicySelfTests()
    {
        if (fullSyncPolicySelfTestsAttempted)
            return;

        fullSyncPolicySelfTestsAttempted = true;

        var actorSnapshotBuffer = ActorSnapshotDeltaBuffer.VerifyPacketClassification();
        var actorWorldRevision = SR2MP.Packets.Actor.ActorWorldRevision.VerifyHostStampIsMonotonic();
        var actorSnapshotRevision = SR2MP.Packets.Loading.InitialActorsPacket.VerifyWorldRevisionRoundTrip();
        var heartbeatTimeout = ServerHeartbeatTimeoutPolicy.VerifyRules();
        var connectionRetry = ClientConnectionGenerationPolicy.VerifyDenialRetryRules();
        var captureSuccessGates = TestGameplayBotPolicy.VerifyCaptureSuccessGates();
        var captureTargetSelection = TestGameplayBotPolicy.VerifyCaptureTargetSelection();
        var remotePlayerPose = SR2MP.Client.Models.RemotePlayerPosePolicy.VerifyRules();
        var remotePlayerRenderLayer = SR2MP.Client.Models.RemotePlayerRenderLayerPolicy.VerifyRules();
        var captureTargetPacket = SR2MP.Packets.Actor.TestCaptureTargetPacket.VerifySerializationRoundTrip();
        var captureStatusRequest = SR2MP.Packets.Actor.TestCaptureStatusRequestPacket.VerifySerializationRoundTrip();
        var captureStatusResult = SR2MP.Packets.Actor.TestCaptureStatusResultPacket.VerifySerializationRoundTrip();
        var canonicalAmmoSnapshot = NetworkAmmoManager.VerifyCanonicalSnapshotRules();
        var ammoMutationSnapshot = SR2MP.Handlers.Ammo.AmmoMutationSnapshotPolicy.VerifyRules();
        var canonicalEventReconciliation = SR2MP.Handlers.Events.EventCanonicalReconciliationPolicy.VerifyRules();
        var zoneUnlock = EventSyncPolicy.VerifyZoneUnlockRules();
        var treasurePodRewards = NetworkTreasurePodManager.VerifyTestPolicies();
        var gordoRewardProtocol = NetworkGordoSlimeManager.VerifyTestPolicies();

        SrLogger.LogMessage(
            $"[SR2MP_TEST] actor-snapshot-buffer={(actorSnapshotBuffer ? "PASS" : "FAIL")} " +
            $"actor-world-revision={(actorWorldRevision ? "PASS" : "FAIL")} " +
            $"actor-snapshot-revision-codec={(actorSnapshotRevision ? "PASS" : "FAIL")} " +
            $"heartbeat-timeout-policy={(heartbeatTimeout ? "PASS" : "FAIL")} " +
            $"connection-denial-retry-policy={(connectionRetry ? "PASS" : "FAIL")} " +
            $"capture-success-gates={(captureSuccessGates ? "PASS" : "FAIL")} " +
            $"capture-target-selection={(captureTargetSelection ? "PASS" : "FAIL")} " +
            $"remote-player-pose-policy={(remotePlayerPose ? "PASS" : "FAIL")} " +
            $"remote-player-render-layer-policy={(remotePlayerRenderLayer ? "PASS" : "FAIL")} " +
            $"capture-target-codec={(captureTargetPacket ? "PASS" : "FAIL")} " +
            $"capture-status-request-codec={(captureStatusRequest ? "PASS" : "FAIL")} " +
            $"capture-status-result-codec={(captureStatusResult ? "PASS" : "FAIL")} " +
            $"canonical-ammo-snapshot={(canonicalAmmoSnapshot ? "PASS" : "FAIL")} " +
            $"ammo-mutation-snapshot-policy={(ammoMutationSnapshot ? "PASS" : "FAIL")} " +
            $"canonical-event-reconciliation-policy={(canonicalEventReconciliation ? "PASS" : "FAIL")} " +
            $"zone-unlock-policy={(zoneUnlock ? "PASS" : "FAIL")} " +
            $"treasure-pod-reward-sync={(treasurePodRewards ? "PASS" : "FAIL")} " +
            $"gordo-reward-protocol={(gordoRewardProtocol ? "PASS" : "FAIL")}");
    }

    private static void TryDumpEventProducers()
    {
        if (eventDumpAttempted || Environment.GetEnvironmentVariable("SR2MP_TEST_DUMP_EVENTS") != "1")
            return;

        var connected = Main.Client.IsJoined || (Main.Server.IsRunning && Main.Server.GetClientCount > 0);
        if (!connected)
            return;

        if (eventDumpConnectedAt < 0f)
            eventDumpConnectedAt = UnityEngine.Time.unscaledTime;
        if (UnityEngine.Time.unscaledTime - eventDumpConnectedAt < 10f)
            return;

        var eventDirector = SceneContext.Instance.eventDirector;
        if (eventDirector == null || eventDirector._model == null || eventDirector._model.table == null)
            return;

        eventDumpAttempted = true;
        var producerPrefixes = Resources.FindObjectsOfTypeAll<StringEventProducer>()
            .Where(producer => producer && !string.IsNullOrWhiteSpace(producer.KeyPrefix))
            .Select(producer => producer.KeyPrefix)
            .Distinct()
            .OrderBy(prefix => prefix, StringComparer.Ordinal)
            .ToArray();
        SrLogger.LogMessage($"[SR2MP_TEST] event producer prefixes=[{string.Join(",", producerPrefixes)}]");

        foreach (var producer in Resources.FindObjectsOfTypeAll<StringEventProducer>())
        {
            if (!producer || string.IsNullOrWhiteSpace(producer.KeyPrefix))
                continue;

            var keyPrefix = producer.KeyPrefix;
            if (keyPrefix.IndexOf("gift", StringComparison.OrdinalIgnoreCase) < 0 &&
                keyPrefix.IndexOf("restock", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            SrLogger.LogMessage(
                $"[SR2MP_TEST] event producer type={producer.GetType().FullName} key={keyPrefix} " +
                $"name={producer.name} instance={producer.GetInstanceID()}");
        }

        var table = eventDirector._model.table;
        var emitted = 0;
        var eventKeys = new List<string>();
        foreach (var key in table.Keys)
        {
            if (!string.IsNullOrEmpty(key))
                eventKeys.Add(key);
        }
        eventKeys.Sort(StringComparer.Ordinal);

        foreach (var eventKey in eventKeys)
        {
            if (!table.TryGetValue(eventKey, out var entries) || entries == null)
                continue;

            var dataKeys = new List<string>();
            foreach (var key in entries.Keys)
            {
                if (!string.IsNullOrEmpty(key))
                    dataKeys.Add(key);
            }
            dataKeys.Sort(StringComparer.Ordinal);

            foreach (var dataKey in dataKeys)
            {
                if (!entries.TryGetValue(dataKey, out var record) || record == null)
                    continue;

                SrLogger.LogMessage(
                    $"[SR2MP_TEST] event record key={eventKey} data={dataKey} count={record.count} " +
                    $"createdGame={record.createdGameTime} updatedGame={record.updatedGameTime} " +
                    $"createdReal={record.createdRealTime} updatedReal={record.updatedRealTime}");
                if (++emitted >= 300)
                {
                    SrLogger.LogMessage("[SR2MP_TEST] event record dump truncated at 300 entries");
                    return;
                }
            }
        }
    }

    private static void TryExerciseSiloDecrement()
    {
        if (ammoDecrementTestAttempted ||
            Environment.GetEnvironmentVariable("SR2MP_TEST_DECREMENT_SILO_AMMO") != "1" ||
            !Main.Server.IsRunning || Main.Server.GetClientCount <= 0)
            return;

        if (ammoTestConnectedAt < 0f)
            ammoTestConnectedAt = UnityEngine.Time.unscaledTime;
        if (UnityEngine.Time.unscaledTime - ammoTestConnectedAt < 15f)
            return;

        ammoDecrementTestAttempted = true;
        foreach (var siloStorage in Resources.FindObjectsOfTypeAll<SiloStorage>())
        {
            if (!siloStorage)
                continue;

            AmmoSlotManager? ammo;
            try
            {
                ammo = siloStorage.GetRelevantAmmo();
            }
            catch (Exception exception)
            {
                // Resources.FindObjectsOfTypeAll also returns linked silo components whose
                // parent storage model has not been initialized. Keep looking for a live silo.
                SrLogger.LogWarning(
                    $"[SR2MP_TEST] skipping uninitialized silo component '{siloStorage.name}' " +
                    $"({exception.GetType().Name})");
                continue;
            }

            if (ammo == null || string.IsNullOrEmpty(ammo.GetPlotID()))
                continue;

            for (var slotIndex = 0; slotIndex < ammo.Slots.Count; slotIndex++)
            {
                var slot = ammo.Slots[slotIndex];
                if (slot == null || slot.Count <= 0)
                    continue;

                var before = slot.Count;
                SrLogger.LogMessage(
                    $"[SR2MP_TEST] decrementing one item in silo id={ammo.GetPlotID()} slot={slotIndex} before={before}");
                ammo.Decrement(slotIndex, 1);
                SrLogger.LogMessage(
                    $"[SR2MP_TEST] silo decrement returned id={ammo.GetPlotID()} slot={slotIndex} after={slot.Count}");
                return;
            }
        }

        SrLogger.LogWarning("[SR2MP_TEST] no registered silo ammo slot with stock was available for decrement test");
    }

    private static void TryDumpActorModels()
    {
        if (actorModelDumpAttempted ||
            Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") != "1" ||
            (!Main.Client.IsJoined && (!Main.Server.IsRunning || Main.Server.GetClientCount <= 0)))
            return;

        actorModelDumpAttempted = true;
        foreach (var entry in ActorManager.Actors.OrderBy(entry => entry.Key))
        {
            var model = entry.Value;
            try
            {
                var ident = model.ident;
                var gameObject = model.GetGameObject();
                SrLogger.LogMessage(
                    $"[SR2MP_TEST] actor model id={entry.Key:X} model={model.GetType().Name} " +
                    $"ident={(ident ? ident.name : "<none>")} scene={NetworkSceneManager.GetPersistentID(model.sceneGroup)} " +
                    $"position={model.lastPosition} active={(gameObject ? gameObject.activeInHierarchy : false)}");
            }
            catch (Exception exception)
            {
                SrLogger.LogWarning(
                    $"[SR2MP_TEST] actor model trace failed id={entry.Key:X} " +
                    $"({exception.GetType().Name})");
            }
        }
    }

    private static void TryDumpPlayerFxHierarchy()
    {
        if (playerFxDumpAttempted ||
            Environment.GetEnvironmentVariable("SR2MP_TEST_DUMP_PLAYER_FX") != "1")
            return;

        var localPlayer = SceneContext.Instance.Player;
        var itemController = localPlayer ? localPlayer.GetComponent<PlayerItemController>() : null;
        var vacuumItem = itemController?._vacuumItem;
        var remotePlayer = PlayerObjects.Values.FirstOrDefault(player => player);
        if (!localPlayer || !itemController || !vacuumItem || !remotePlayer)
            return;

        playerFxDumpAttempted = true;
        SrLogger.LogMessage("[SR2MP_TEST] Dumping player/vacuum visual hierarchy");
        DumpHierarchy("local-player", localPlayer.transform, 160);
        DumpHierarchy("remote-player", remotePlayer!.transform, 160);
        DumpNamedMembers(vacuumItem!, "VacFX", "ShootFX", "VacOrigin", "_vacMode");
    }

    private static void TryTraceVacuumState()
    {
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_VAC_STATE") != "1" ||
            UnityEngine.Time.unscaledTime < nextVacuumTrace)
            return;

        nextVacuumTrace = UnityEngine.Time.unscaledTime + 1f;
        var localPlayer = SceneContext.Instance.Player;
        var itemController = localPlayer ? localPlayer.GetComponent<PlayerItemController>() : null;
        var vacuumItem = itemController?._vacuumItem;
        if (!vacuumItem)
            return;

        var state = string.Join(";", new[] { "_vacMode", "VacFX", "ShootFX", "VacOrigin" }
            .Select(name => $"{name}={DescribeUnityState(GetNamedMember(vacuumItem!, name))}"));
        if (state == lastVacuumTrace)
            return;

        lastVacuumTrace = state;
        SrLogger.LogMessage($"[SR2MP_TEST] local vacuum state {state}");
    }

    private static object? GetNamedMember(object instance, string name)
    {
        var type = instance.GetType();
        var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null && property.GetIndexParameters().Length == 0)
        {
            try { return property.GetValue(instance); }
            catch { return null; }
        }

        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null)
            return null;

        try { return field.GetValue(instance); }
        catch { return null; }
    }

    private static string DescribeUnityState(object? value) => value switch
    {
        ParticleSystem particle => particle
            ? $"ParticleSystem({particle.gameObject.name},active={particle.gameObject.activeInHierarchy},playing={particle.isPlaying})"
            : "ParticleSystem(null)",
        GameObject gameObject => gameObject
            ? $"GameObject({gameObject.name},active={gameObject.activeInHierarchy})"
            : "GameObject(null)",
        Transform transform => transform
            ? $"Transform({transform.name},position={transform.position})"
            : "Transform(null)",
        Component component => component
            ? $"{component.GetType().Name}({component.gameObject.name},active={component.gameObject.activeInHierarchy})"
            : "Component(null)",
        null => "null",
        _ => value.ToString() ?? value.GetType().FullName ?? "unknown"
    };

    private static void DumpNamedMembers(object instance, params string[] names)
    {
        var wanted = new HashSet<string>(names, StringComparer.Ordinal);
        var type = instance.GetType();
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!wanted.Contains(property.Name) || property.GetIndexParameters().Length != 0)
                continue;

            try
            {
                var value = property.GetValue(instance);
                SrLogger.LogMessage($"[SR2MP_TEST] VacuumItem.{property.Name} type={property.PropertyType.FullName} value={DescribeUnityValue(value)}");
                DumpUnityValue($"VacuumItem.{property.Name}", value);
            }
            catch (Exception ex)
            {
                SrLogger.LogWarning($"[SR2MP_TEST] Could not read VacuumItem.{property.Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!wanted.Contains(field.Name))
                continue;

            try
            {
                var value = field.GetValue(instance);
                SrLogger.LogMessage($"[SR2MP_TEST] VacuumItem.{field.Name} type={field.FieldType.FullName} value={DescribeUnityValue(value)}");
                DumpUnityValue($"VacuumItem.{field.Name}", value);
            }
            catch (Exception ex)
            {
                SrLogger.LogWarning($"[SR2MP_TEST] Could not read VacuumItem.{field.Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static string DescribeUnityValue(object? value) => value switch
    {
        GameObject gameObject => gameObject ? $"GameObject({gameObject.name})" : "GameObject(null)",
        Transform transform => transform ? $"Transform({transform.name})" : "Transform(null)",
        Component component => component ? $"{component.GetType().Name}({component.gameObject.name})" : "Component(null)",
        null => "null",
        _ => value.ToString() ?? value.GetType().FullName ?? "unknown"
    };

    private static void DumpUnityValue(string label, object? value)
    {
        if (value is GameObject gameObject && gameObject)
            DumpHierarchy(label, gameObject.transform, 80);
        else if (value is Transform transform && transform)
            DumpHierarchy(label, transform, 80);
        else if (value is Component component && component)
            DumpHierarchy(label, component.transform, 80);
    }

    private static void DumpHierarchy(string label, Transform root, int maxEntries)
    {
        if (!root)
            return;

        var transforms = root.GetComponentsInChildren<Transform>(true);
        var count = Math.Min(transforms.Length, maxEntries);
        for (var index = 0; index < count; index++)
        {
            var transform = transforms[index];
            if (!transform)
                continue;

            var pathParts = new List<string>();
            var cursor = transform;
            while (cursor && pathParts.Count < 16)
            {
                pathParts.Add(cursor.name);
                if (cursor == root)
                    break;
                cursor = cursor.parent;
            }
            pathParts.Reverse();

            var components = string.Join(",", transform.GetComponents<Component>()
                .Where(component => component)
                .Select(component => component!.GetType().Name));
            SrLogger.LogMessage(
                $"[SR2MP_TEST] hierarchy {label}/{string.Join("/", pathParts)} active={transform.gameObject.activeInHierarchy} " +
                $"local={transform.localPosition} world={transform.position} components=[{components}]");
        }

        if (transforms.Length > count)
            SrLogger.LogMessage($"[SR2MP_TEST] hierarchy {label}: truncated {transforms.Length - count} child transforms");
    }

    private static void TryScheduledResync()
    {
        var elapsed = UnityEngine.Time.unscaledTime;
        var hostHasClient = Main.Server.IsRunning && Main.Server.GetClientCount > 0;
        if (hostHasClient)
            hostClientConnectedAt = hostClientConnectedAt < 0f ? elapsed : hostClientConnectedAt;
        else
            hostClientConnectedAt = -1f;

        if (Main.Client.IsJoined)
            clientJoinedAt = clientJoinedAt < 0f ? elapsed : clientJoinedAt;
        else
            clientJoinedAt = -1f;

        if (!resyncAllAttempted
            && float.TryParse(Environment.GetEnvironmentVariable("SR2MP_TEST_RESYNC_ALL_AFTER"), out var resyncAllAfter)
            && hostClientConnectedAt >= 0f
            && elapsed - hostClientConnectedAt >= resyncAllAfter)
        {
            resyncAllAttempted = true;
            Main.Server.ReSyncManager.SynchronizeAll();
            SrLogger.LogMessage("[SR2MP_TEST] Scheduled host SynchronizeAll executed");
        }

        if (!requestResyncAttempted
            && float.TryParse(Environment.GetEnvironmentVariable("SR2MP_TEST_REQUEST_RESYNC_AFTER"), out var requestResyncAfter)
            && clientJoinedAt >= 0f
            && elapsed - clientJoinedAt >= requestResyncAfter)
        {
            requestResyncAttempted = true;
            Main.Server.ReSyncManager.RequestResync();
            SrLogger.LogMessage("[SR2MP_TEST] Scheduled client resync request executed");
        }
    }

    private static void CaptureGameFrame(string screenshotPath)
    {
        var directory = Path.GetDirectoryName(screenshotPath);
        var stem = Path.GetFileNameWithoutExtension(screenshotPath);
        var extension = Path.GetExtension(screenshotPath);
        var capturePath = Path.Combine(directory ?? string.Empty,
            $"{stem}-{++screenshotSequence:D3}{extension}");
        UnityEngine.ScreenCapture.CaptureScreenshot(capturePath);
        SrLogger.LogMessage($"[SR2MP_TEST] Captured game frame to {capturePath}");
    }

    private static int continueAttempts;

    private static void TryAutoContinue()
    {
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_AUTO_CONTINUE") != "1"
            || UnityEngine.Time.unscaledTime < nextContinueAttempt)
            return;

        // A Continue invoked before the menu is interactive does nothing: retry every 10 s until a
        // save is loaded (bounded), instead of giving up after the first attempt.
        if (continueAttempted)
        {
            if (Starlight.ContextShortcuts.inGame || continueAttempts >= 6)
                return;
            continueAttempted = false;
        }

        nextContinueAttempt = UnityEngine.Time.unscaledTime + 1f;

        var menus = Resources.FindObjectsOfTypeAll<MainMenuLandingRootUI>();
        if (menus.Length == 0 || menus.All(m => !m || !m.gameObject.activeInHierarchy))
        {
            if (UnityEngine.Time.unscaledTime > 60f && (int)UnityEngine.Time.unscaledTime % 10 == 0)
                SrLogger.LogMessage($"[SR2MP_TEST] auto-continue: no active landing menu (found {menus.Length})");
            return;
        }
        for (var menuIndex = 0; menuIndex < menus.Length; menuIndex++)
        {
            var menu = menus[menuIndex];
            if (!menu || !menu.gameObject.activeInHierarchy || menu._models == null)
                continue;

            if (menu._models.Count == 0)
                continue;

            // The generated IL2CPP proxy reports ButtonBehaviorModel as the managed
            // type for concrete native models. The default title-menu model is the
            // visible Continue button; tests can override its index for another menu
            // action such as opening Save Files.
            var modelIndex = 0;
            if (int.TryParse(Environment.GetEnvironmentVariable("SR2MP_TEST_AUTO_CONTINUE_INDEX"), out var requestedIndex))
                modelIndex = requestedIndex;

            if (modelIndex < 0 || modelIndex >= menu._models.Count)
                continue;

            var continueModel = menu._models[modelIndex];
            if (continueModel == null)
                continue;

            continueAttempted = true;
            continueAttempts++;
            nextContinueAttempt = UnityEngine.Time.unscaledTime + 10f;
            SrLogger.LogMessage($"[SR2MP_TEST] Invoked main menu model index {modelIndex} (attempt {continueAttempts})");
            continueModel.InvokeBehavior();
            return;
        }
    }

    private static void TryEnforceTestDisplaySettings()
    {
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_WINDOWED") != "1"
            || UnityEngine.Time.unscaledTime < nextDisplaySettingsCheck)
            return;

        nextDisplaySettingsCheck = UnityEngine.Time.unscaledTime + 2f;
        var width = int.TryParse(Environment.GetEnvironmentVariable("SR2MP_TEST_SCREEN_WIDTH"), out var requestedWidth)
            ? requestedWidth
            : 1024;
        var height = int.TryParse(Environment.GetEnvironmentVariable("SR2MP_TEST_SCREEN_HEIGHT"), out var requestedHeight)
            ? requestedHeight
            : 576;

        if (UnityEngine.Screen.width == width
            && UnityEngine.Screen.height == height
            && UnityEngine.Screen.fullScreenMode == UnityEngine.FullScreenMode.Windowed)
            return;

        UnityEngine.Screen.SetResolution(width, height, UnityEngine.FullScreenMode.Windowed);
        if (displaySettingsRequestLogged)
            return;

        displaySettingsRequestLogged = true;
        SrLogger.LogMessage(
            $"[SR2MP_TEST] requested {width}x{height} windowed mode; " +
            $"initial={UnityEngine.Screen.width}x{UnityEngine.Screen.height}/{UnityEngine.Screen.fullScreenMode}");
    }

    private static void TrySkipIntroSequence()
    {
        if (introSkipAttempted
            || Environment.GetEnvironmentVariable("SR2MP_TEST_SKIP_INTRO") != "1"
            || UnityEngine.Time.unscaledTime < nextIntroSkipAttempt)
            return;

        nextIntroSkipAttempt = UnityEngine.Time.unscaledTime + 1f;
        var roots = Resources.FindObjectsOfTypeAll<IntroSequenceUIRoot>();
        foreach (var root in roots)
        {
            if (!root || !root.gameObject.activeInHierarchy)
                continue;

            var frame = root._frameIndex;
            var inputEnabled = root._isInputEnabled;
            root.EndSequence();
            introSkipAttempted = true;
            SrLogger.LogMessage(
                $"[SR2MP_TEST] intro sequence ended by test harness frame={frame} inputEnabled={inputEnabled}");
            return;
        }
    }

    private static void TryAutoStartNewGame()
    {
        if (newGameStartAttempted
            || Environment.GetEnvironmentVariable("SR2MP_TEST_AUTO_START_NEW_GAME") != "1")
            return;

        var roots = Resources.FindObjectsOfTypeAll<NewGameOptionsUIRoot>();
        foreach (var root in roots)
        {
            if (!root || !root.gameObject.activeInHierarchy || !root.startGameButton)
                continue;

            var button = root.startGameButton.GetComponent<UnityEngine.UI.Button>();
            if (!button || !button.interactable || root._gameSettingsModel == null ||
                root.SaveSlotIndex < 0 || !GameContext.Instance || GameContext.Instance.AutoSaveDirector == null ||
                SystemContext.Instance.SceneLoader.IsSceneLoadInProgress)
                continue;

            // Use the native save-start entry point with the currently displayed
            // slot/settings; a UnityEvent can have no bound persistent listener.
            newGameStartAttempted = true;
            GameContext.Instance.AutoSaveDirector.StartNewGame(root.SaveSlotIndex, root._gameSettingsModel);
            SrLogger.LogMessage($"[SR2MP_TEST] native StartNewGame requested slot={root.SaveSlotIndex}");
            return;
        }
    }

    private static void TraceMainMenu()
    {
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_MENU") != "1"
            || UnityEngine.Time.unscaledTime < nextMenuTrace)
            return;

        nextMenuTrace = UnityEngine.Time.unscaledTime + 5f;
        var menus = Resources.FindObjectsOfTypeAll<MainMenuLandingRootUI>();
        var states = new List<string>();
        for (var menuIndex = 0; menuIndex < menus.Length; menuIndex++)
        {
            var menu = menus[menuIndex];
            if (!menu)
            {
                states.Add($"menu[{menuIndex}]=null");
                continue;
            }

            var models = menu._models;
            var modelCount = models == null ? -1 : Math.Min(models.Count, 64);
            var modelTypes = new List<string>();
            for (var modelIndex = 0; modelIndex < modelCount; modelIndex++)
            {
                var model = models![modelIndex];
                modelTypes.Add(model == null ? "null" : model.GetType().Name);
            }

            states.Add($"menu[{menuIndex}]={menu.gameObject.name},active={menu.gameObject.activeInHierarchy},enabled={menu.enabled},models={modelCount}[{string.Join(",", modelTypes)}]");
        }

        SrLogger.LogMessage($"[SR2MP_TEST] main-menu roots={menus.Length} {string.Join(";", states)}");
    }
}
#endif

using System.Collections;
using System.Net;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Loading;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;
using SR2MP.Shared.Utils;

namespace SR2MP.Handlers.Actor;

[PacketHandler((byte)PacketType.InitialActors, HandlerType.Client)]
internal sealed class ActorsLoadHandler : BasePacketHandler<InitialActorsPacket>
{
    private const int MaximumAutomaticSnapshotRetries = 2;
    private static int consecutiveSnapshotRuntimeFailures;

    static ActorsLoadHandler()
    {
        Main.Client.OnDisconnected += ResetSnapshotFailureCount;
    }

    protected override bool Handle(InitialActorsPacket packet, IPEndPoint? _)
    {
        var snapshotGeneration = ActorSnapshotDeltaBuffer.BeginSnapshot(packet.SyncId);
        ActorSnapshotDeltaBuffer.SetSnapshotRevision(snapshotGeneration,
            packet.HasWorldRevision, packet.WorldRevision);
        StartCoroutine(SpawnWhenReady(packet, snapshotGeneration));

        return false;
    }

    private static bool IsSaveLoaded()
    {
        try
        {
            return SceneContext.Instance != null
                   && SceneContext.Instance.player
                   && !SystemContext.Instance.SceneLoader.IsSceneLoadInProgress;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerator SpawnWhenReady(InitialActorsPacket packet, long snapshotGeneration)
    {
        var snapshotApplied = false;
        var retryRequested = false;
        try
        {
            var waited = 0f;
            while (!IsSaveLoaded())
            {
                if (!ActorSnapshotDeltaBuffer.IsCurrent(snapshotGeneration) ||
                    !Main.Client.IsConnected && !Main.Server.IsRunning)
                    yield break;

                if (waited > 180f)
                {
                    SrLogger.LogWarning("InitialActors: world never loaded, dropping initial actor packet.");
                    yield break;
                }

                waited += UnityEngine.Time.unscaledDeltaTime;
                yield return null;
            }

            if (!ActorSnapshotDeltaBuffer.IsCurrent(snapshotGeneration))
                yield break;

            var destroyedSomething = false;
            var destroyPhaseFailed = false;
            try
            {
                destroyedSomething = DestroyExistingActors();
            }
            catch (Exception ex)
            {
                SrLogger.LogError($"InitialActors: destroy phase failed: {ex.Message}");
                destroyPhaseFailed = true;
            }

            // We need to wait for the Teleporters to correctly unlink their old stuff.
            // Live actor mutations stay queued during these frame boundaries.
            if (!destroyPhaseFailed && destroyedSomething)
            {
                yield return null;
                if (!ActorSnapshotDeltaBuffer.IsCurrent(snapshotGeneration))
                    yield break;
                yield return null;
                if (!ActorSnapshotDeltaBuffer.IsCurrent(snapshotGeneration))
                    yield break;
            }

            if (!destroyPhaseFailed)
            {
                try
                {
                    snapshotApplied = SpawnInitialActors(packet);
                }
                catch (Exception ex)
                {
                    SrLogger.LogError($"InitialActors: spawn phase failed: {ex.Message}");
                }
            }

            if (!snapshotApplied && ActorSnapshotDeltaBuffer.IsCurrent(snapshotGeneration))
                retryRequested = TryRequestSnapshotRetry(snapshotGeneration);
        }
        finally
        {
            if (snapshotApplied)
            {
                consecutiveSnapshotRuntimeFailures = 0;
                ActorSnapshotDeltaBuffer.CompleteSnapshot(snapshotGeneration);
            }
            else if (!retryRequested)
            {
                ActorSnapshotDeltaBuffer.AbortSnapshot(snapshotGeneration);
            }
        }
    }

    private static bool TryRequestSnapshotRetry(long snapshotGeneration)
    {
        if (!ActorSnapshotDeltaBuffer.IsCurrent(snapshotGeneration) || !Main.Client.IsConnected)
            return false;

        consecutiveSnapshotRuntimeFailures++;
        if (!InitialActorSnapshotPolicy.ShouldAutomaticallyRetry(
                consecutiveSnapshotRuntimeFailures, MaximumAutomaticSnapshotRetries))
        {
            SrLogger.LogError(
                $"InitialActors failed after {consecutiveSnapshotRuntimeFailures} runtime attempts; " +
                "automatic retries are exhausted. The snapshot watermark was not advanced; request another resync after fixing the failure.");
            return false;
        }

        SrLogger.LogWarning(
            $"InitialActors had a runtime spawn failure; requesting bounded retry " +
            $"{consecutiveSnapshotRuntimeFailures}/{MaximumAutomaticSnapshotRetries}.");
        Main.Server.ReSyncManager.RequestResync();
        return true;
    }

    private static void ResetSnapshotFailureCount()
        => consecutiveSnapshotRuntimeFailures = 0;

    private static bool DestroyExistingActors()
    {
        ActorManager.Actors.Clear();

        NetworkGadgetManager.CacheTeleporterStates();

        var toRemove = new CppCollections.Dictionary<ActorId, IdentifiableModel>(
            GameState.identifiables
                .Cast<CppCollections.IDictionary<ActorId, IdentifiableModel>>());

        var destroyed = false;

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            foreach (var (id, value) in toRemove)
            {
                if (value.ident.IsPlayer)
                    continue;

                if (value.ident.IsGadget())
                {
                    NetworkActorManager.RemoveExistingGadgetModel(id);
                    destroyed = true;
                }

                var gameObject = value.GetGameObject();

                if (gameObject)
                {
                    Destroyer.DestroyAny(gameObject, "SR2MP.InitialActors");
                    destroyed = true;
                }
                else if (!value.ident.IsGadget())
                {
                    // Hibernation caused problems, no gameObj
                    GameState.DestroyIdentifiableModel(value);
                    destroyed = true;
                }
            }
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        return destroyed;
    }

    private static bool SpawnInitialActors(InitialActorsPacket packet)
    {
        ActorManager.ReplaceOwnershipSnapshot(packet.Ownership, packet.HasOwnershipSnapshot);
        GameState._actorIdProvider._nextActorId = packet.StartingActorID;
        GameState.world.worldTime = packet.WorldTime;

        var seenActorIds = new HashSet<long>();
        var hasRuntimeFailure = false;
        foreach (var actor in packet.Actors)
        {
            if (actor == null)
            {
                SrLogger.LogWarning("InitialActors skipped a null actor entry as invalid snapshot data.");
                continue;
            }

            var actorIdValid = actor.ActorId > 0;
            var actorIdUnique = actorIdValid && seenActorIds.Add(actor.ActorId);
            var typeId = Main.RockPlortBug ? 25 : actor.ActorTypeId;
            var typeSupported = ActorManager.ActorTypes.TryGetValue(typeId, out var type) && type != null;
            var hasPrefab = typeSupported && type!.prefab;
            var playerIdCollision = actorIdValid &&
                                    GameState.TryGetIdentifiableModel(new ActorId(actor.ActorId), out var existingModel) &&
                                    existingModel != null && existingModel.ident?.IsPlayer == true;

            var expectedSkip = InitialActorSnapshotPolicy.IsExpectedSkip(
                actorIdValid, actorIdUnique, typeSupported, hasPrefab, playerIdCollision);
            if (expectedSkip)
            {
                var reason = !actorIdValid ? "invalid actor id" :
                    !actorIdUnique ? "duplicate actor id in snapshot" :
                    !typeSupported ? "unsupported actor type" :
                    !hasPrefab ? "actor prefab unavailable" : "actor id belongs to a player";
                SrLogger.LogWarning(
                    $"InitialActors skipped unsupported/invalid actor id={actor.ActorId} type={actor.ActorTypeId}: {reason}.");
#if SR2MP_DEEPLOG || SR2MP_TEST
                ActorDiag.Log("initial-skipped", actor.ActorId, $"reason={reason}", actor.ActorTypeId,
                    sceneId: actor.Scene, positionHint: actor.Position);
#endif
#if SR2MP_TEST
                if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") == "1")
                    SrLogger.LogMessage(
                        $"[SR2MP_TEST] initial actor skipped id={actor.ActorId} type={actor.ActorTypeId} " +
                        $"payload={actor.GetType().Name} incomingPosition={actor.Position} reason={reason}");
#endif
                continue;
            }

            var spawnSucceeded = false;
            var spawnThrew = false;
            try
            {
                spawnSucceeded = ActorManager.TrySpawnInitialActor(actor, out _);
            }
            catch (Exception ex)
            {
                spawnThrew = true;
                SrLogger.LogError(
                    $"InitialActors runtime exception spawning id={actor.ActorId} type={actor.ActorTypeId}: {ex}");
            }

            var result = InitialActorSnapshotPolicy.ClassifyAttempt(
                expectedSkip: false, spawnSucceeded: spawnSucceeded, threw: spawnThrew);
            if (result == InitialActorSnapshotEntryResult.RuntimeFailure)
            {
                hasRuntimeFailure = true;
                if (!spawnThrew)
                    SrLogger.LogError(
                        $"InitialActors runtime spawn failure id={actor.ActorId} type={actor.ActorTypeId}; snapshot will not commit.");
                break;
            }

            ApplySnapshotOwner(actor.ActorId);
        }

        if (!InitialActorSnapshotPolicy.ShouldCommitSnapshot(hasRuntimeFailure))
            return false;

        PruneOrphanGadgets(seenActorIds);

        StartCoroutine(NetworkGadgetManager.RelinkGadgets());
        StartCoroutine((NetworkGadgetManager.RepairTeleporterLinks()));
        return true;
    }

    private static void PruneOrphanGadgets(HashSet<long> snapshotActorIds)
    {
        if (!Main.Client.IsConnected || Main.Server.IsRunning)
            return;

        try
        {
            var pruned = 0;
            foreach (var gadget in GameState.AllGadgets().ToArray())
            {
                if (gadget == null || gadget.actorId.Value == 0 || snapshotActorIds.Contains(gadget.actorId.Value))
                    continue;

                SrLogger.LogWarning($"Pruning orphan local gadget {gadget.actorId.Value:X} (not in host snapshot)");
                NetworkActorManager.RemoveExistingGadgetModel(gadget.actorId);
                pruned++;
            }

            SrLogger.LogMessage($"InitialActors: pruned {pruned} orphan gadget(s) not present on the host");
            Logger.LogDiagnostic("gadget", "orphans-pruned", $"count={pruned}");
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"InitialActors: orphan gadget prune failed: {ex.Message}");
        }
    }

    private static void ApplySnapshotOwner(long actorId)
    {
        if (!ActorManager.OwnershipByActorId.TryGetValue(actorId, out var ownerId) ||
            !ActorManager.Actors.TryGetValue(actorId, out var model) ||
            !model.TryGetNetworkComponent(out var networkActor))
            return;

        networkActor.CurrentOwnerId = ownerId;
        networkActor.LocallyOwned = ownerId == LocalID;
#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") == "1")
            SrLogger.LogMessage($"[SR2MP_TEST] actor ownership snapshot id={actorId} owner={ownerId} local={networkActor.LocallyOwned}");
#endif
    }
}

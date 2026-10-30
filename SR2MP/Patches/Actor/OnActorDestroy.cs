using HarmonyLib;
using SR2MP.Components.Actor;
using SR2MP.Packets.Actor;
using SR2MP.Shared.Managers;
using SR2MP.Shared.Utils;

namespace SR2MP.Patches.Actor;

[HarmonyPatch(typeof(Destroyer), nameof(Destroyer.DestroyActor), typeof(GameObject), typeof(string), typeof(bool))]
internal static class OnActorDestroy
{
#if SR2MP_TEST
    private static readonly Dictionary<long, (float NextAt, int Suppressed)> nonOwnerDestroyTraces = new();
#endif

    public static bool Prefix(GameObject actorObj, string source)
    {
        if (SystemContext.Instance.SceneLoader.IsSceneLoadInProgress) return true;

        try
        {
            if (Main.Server.IsRunning || Main.Client.IsConnected)
            {
                if (source is "SlimeFeral.Awake")
                {
                    return false;
                }

                // Population culls count replicas and other peers' actors as local population and
                // deleted ranch slimes/largos in co-op. Slimes are never culled while connected.
                if (source is "CellDirector.Despawn" or "MaybeCullOnReenable.MaybeDestroy" && actorObj &&
                    actorObj.GetComponent<SlimeEat>())
                {
                    LogProtected(actorObj, "population-cull", source);
                    return false;
                }

                // The ranch is untouchable by cleanups: hens, slimes and plorts there were culled,
                // timed out or dropped through the floor in co-op. Tarr and food keep vanilla rules.
                if (source is "CellDirector.Despawn" or "MaybeCullOnReenable.MaybeDestroy"
                        or "DestroyAfterTime.RegistryUpdate" or "KillOnTrigger.OnTriggerEnter" &&
                    actorObj && RanchProtection.IsProtectedRanchActor(actorObj))
                {
                    if (source is not "KillOnTrigger.OnTriggerEnter")
                    {
                        LogProtected(actorObj, "ranch-cleanup", source);
                        return false;
                    }

                    if (RanchProtection.TryRescueFall(actorObj))
                    {
                        LogProtected(actorObj, "ranch-fall-rescued", source);
                        return false;
                    }
                }
            }
        }
        catch
        {
            // ignored
        }

        if (HandlingPacket || !actorObj)
            return true;

        if (source is "SR2MP.ActorDestroyHandler" or "SR2MP.InitialActors" or "SR2MP.RemoveExistingGadgetModel" or "SR2MP.ReplaceDrone")
            return true;

        var actor = actorObj.GetComponent<IdentifiableActor>();
        if (!actor)
            return true;
        
        var netActor = actorObj.GetComponent<NetworkActor>();

        // Drone destructions are handled differently
        if (actorObj.GetComponent<Il2CppMonomiPark.SlimeRancher.Drone.RanchDrone>() ||
            actorObj.GetComponent<Il2CppMonomiPark.SlimeRancher.Drone.ExplorerDrone>())
            return true;

        var actorId = actor.GetActorId();
        if (NetworkActorCaptureManager.TryMarkReplayConsumeDestroy(actorId.Value, actorObj, source))
        {
            // Native consume may destroy the object before the host has committed
            // ownership. Allow only this exact replay, defer the packet until the
            // committed host ACK, and avoid the component's duplicate OnDestroy send.
            ActorManager.Actors.Remove(actorId.Value);
            ActorManager.OwnershipByActorId.Remove(actorId.Value);
            ActorManager.EpochByActorId.Remove(actorId.Value);
            if (netActor != null)
                netActor.SuppressDestroyPacket = true;
            return true;
        }

        var hasCachedOwner = ActorManager.OwnershipByActorId.TryGetValue(actorId.Value, out var cachedOwnerId);
        var isNetworkedActor = netActor != null || hasCachedOwner;
        var currentOwnerId = netActor && !string.IsNullOrWhiteSpace(netActor.CurrentOwnerId)
            ? netActor.CurrentOwnerId
            : cachedOwnerId ?? string.Empty;
        var locallyOwned = string.Equals(currentOwnerId, LocalID, StringComparison.Ordinal);
        var isRottenResource = netActor != null && netActor.isResource && netActor.IsRotten;

        if (ActorDestroyPolicy.IsLocalOnlyRottenResourceCleanup(
                Main.Server.IsRunning || Main.Client.IsConnected,
                isNetworkedActor,
                locallyOwned,
                isRottenResource))
        {
            // The owning host already replicated the ROTTEN state. Each non-owner
            // removes this stale visual locally; it must not claim ownership or
            // broadcast a destroy packet for an actor it does not own.
            ActorManager.Actors.Remove(actorId.Value);
            ActorManager.OwnershipByActorId.Remove(actorId.Value);
            ActorManager.EpochByActorId.Remove(actorId.Value);
            if (netActor != null)
                netActor.SuppressDestroyPacket = true;
            return true;
        }

        if (ActorDestroyPolicy.ShouldSuppress(Main.Server.IsRunning || Main.Client.IsConnected,
                isNetworkedActor, locallyOwned, isRottenResource))
        {
#if SR2MP_DEEPLOG || SR2MP_TEST
            ActorDiag.Log("destroy-suppressed", actorId.Value,
                $"reason=non-owner; source={source}", ownerHint: currentOwnerId, perActorMilliseconds: 2000);
#endif
#if SR2MP_TEST
            LogSuppressedNonOwnerDestroy(actorId.Value,
                cachedOwnerId ?? netActor?.CurrentOwnerId ?? "<none>");
#endif
            return false;
        }

        ActorManager.Actors.Remove(actorId.Value);
        ActorManager.OwnershipByActorId.Remove(actorId.Value);
        ActorManager.EpochByActorId.Remove(actorId.Value);

        // NetworkActor.OnDestroy also sends a destroy packet for locally owned
        // actors. This prefix already sends one below, so suppress that duplicate.
        if (netActor != null)
            netActor.SuppressDestroyPacket = true;

        if (!Main.Server.IsRunning && !Main.Client.IsConnected)
            return true;

        try
        {
            var ownerId = string.Equals(currentOwnerId, LocalID, StringComparison.Ordinal)
                ? currentOwnerId
                : LocalID;
            var packet = new ActorDestroyPacket { ActorId = actorId, OwnerId = ownerId };
#if SR2MP_DEEPLOG || SR2MP_TEST
            ActorDiag.Log("destroy-send", actorId.Value, $"source={source}", ownerHint: ownerId);
#endif
            Main.SendToAllOrServer(packet);
        }
        catch (Exception ex)
        {
            SrLogger.LogError($"Failed to send ActorDestroy packet: {ex.Message}");
        }

        return true;
    }

    private static void LogProtected(GameObject actorObj, string reason, string source)
    {
#if SR2MP_DEEPLOG || SR2MP_TEST
        var actor = actorObj.GetComponent<IdentifiableActor>();
        if (actor)
            ActorDiag.Log("destroy-suppressed", actor.GetActorId().Value,
                $"reason={reason}; source={source}", perActorMilliseconds: 10_000);
#endif
    }

#if SR2MP_TEST
    private static void LogSuppressedNonOwnerDestroy(long actorId, string ownerId)
    {
        var now = UnityEngine.Time.unscaledTime;
        if (nonOwnerDestroyTraces.Count >= 1024 && !nonOwnerDestroyTraces.ContainsKey(actorId))
            nonOwnerDestroyTraces.Clear();

        if (!nonOwnerDestroyTraces.TryGetValue(actorId, out var trace))
        {
            nonOwnerDestroyTraces[actorId] = (now + 2f, 0);
            SrLogger.LogMessage($"[SR2MP_TEST] actor-destroy suppressed non-owner actor={actorId:X} owner={ownerId}");
            return;
        }

        if (now < trace.NextAt)
        {
            nonOwnerDestroyTraces[actorId] = (trace.NextAt, trace.Suppressed + 1);
            return;
        }

        nonOwnerDestroyTraces[actorId] = (now + 2f, 0);
        SrLogger.LogMessage($"[SR2MP_TEST] actor-destroy suppressed non-owner actor={actorId:X} " +
                            $"owner={ownerId} repeats={trace.Suppressed + 1} window=2s");
    }
#endif
}

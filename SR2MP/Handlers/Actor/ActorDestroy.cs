using System.Collections;
using System.Net;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Actor;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;
using SR2MP.Shared.Utils;

namespace SR2MP.Handlers.Actor;

[PacketHandler((byte)PacketType.ActorDestroy)]
internal sealed class ActorDestroyHandler : BasePacketHandler<ActorDestroyPacket>
{
    private const int MaxDeferredDestroyPackets = 512;
    private static readonly Dictionary<long, ActorDestroyPacket> deferredDestroyPackets = new();
    private static readonly Queue<long> deferredDestroyOrder = new();

    static ActorDestroyHandler()
    {
        Main.Client.OnDisconnected += ClearDeferredDestroyPackets;
        Main.Server.OnServerStarted += ClearDeferredDestroyPackets;
    }

    protected override bool Handle(ActorDestroyPacket packet, IPEndPoint? senderEndpoint)
    {
        if (packet.ActorId.Value == 0)
            return false;

        // A client can only destroy an actor that it currently owns. The endpoint,
        // not a packet-supplied identity, is bound to the connected player on host.
        if (Main.Server.IsRunning)
        {
            if (senderEndpoint == null ||
                !Main.Server.ClientManager.TryGetClient(senderEndpoint, out var sender) || sender == null ||
                (packet.HasOwnerId && packet.OwnerId != sender.PlayerId) ||
                (!NetworkActorManager.IsCurrentActorOwner(packet.ActorId, senderEndpoint) &&
                 !IsPlacedGadget(packet.ActorId)))
            {
                SrLogger.LogWarning($"Rejected ActorDestroy for {packet.ActorId.Value} from a non-owner");
#if SR2MP_DEEPLOG || SR2MP_TEST
                ActorDiag.Log("destroy-rejected", packet.ActorId.Value, $"reason=non-owner; claimed={packet.OwnerId}",
                    perActorMilliseconds: 1000);
#endif
                return false;
            }
        }

        if (!Main.Server.IsRunning && packet.HasOwnerId &&
            ActorDestroyOrderingPolicy.ShouldDefer(packet.OwnerId, GetCurrentOwner(packet.ActorId.Value)))
        {
#if SR2MP_DEEPLOG || SR2MP_TEST
            ActorDiag.Log("destroy-deferred", packet.ActorId.Value,
                $"reason=owner-token-ahead; token={packet.OwnerId}; current={GetCurrentOwner(packet.ActorId.Value)}",
                perActorMilliseconds: 1000);
#endif
            DeferDestroy(packet);
            return false;
        }

        return ApplyDestroy(packet);
    }

    /// <summary>
    /// Placed gadgets have no physics owner (the placer or host holds a stale claim), so any connected
    /// player may pick one up. Rejecting it left the gadget on the host while the picker's +1 build
    /// inventory count still synced, duplicating the item.
    /// </summary>
    private static bool IsPlacedGadget(ActorId actorId)
    {
        if (!GameState.TryGetIdentifiableModel(actorId, out var model) || model == null ||
            model.TryCast<GadgetModel>() == null)
            return false;

        Logger.LogDiagnostic("gadget", "client-pickup-accepted", $"actorId={actorId.Value:X}");
        return true;
    }

    /// <summary>
    /// Called after the host's ownership packet is applied. The transport orders
    /// ActorTransfer and ActorDestroy independently, so a peer may have received
    /// the destroy first. Its owner token makes that early destroy safe to defer.
    /// </summary>
    internal static void ResolveDeferredDestroy(long actorId, string appliedOwnerId)
    {
        if (!deferredDestroyPackets.TryGetValue(actorId, out var packet) ||
            !ActorDestroyOrderingPolicy.CanResolve(packet.OwnerId, appliedOwnerId))
            return;

        if (packet.HasWorldRevision && !ActorSnapshotDeltaBuffer.ShouldApplyActorRevision(
                PacketType.ActorDestroy, actorId, packet.WorldRevision))
        {
            RemoveDeferredDestroy(actorId);
            return;
        }

        RemoveDeferredDestroy(actorId);
        if (ApplyDestroy(packet) && packet.HasWorldRevision)
            ActorSnapshotDeltaBuffer.RecordAppliedActorDelta(
                PacketType.ActorDestroy, actorId, packet.WorldRevision);
    }

    private static bool ApplyDestroy(ActorDestroyPacket packet)
    {
        Logger.LogDiagnostic("actor", "destroy-received",
            $"actorId={packet.ActorId.Value:X}; owner={packet.OwnerId}; hasOwner={packet.HasOwnerId}; " +
            $"worldRevision={(packet.HasWorldRevision ? packet.WorldRevision.ToString() : "none")}");
#if SR2MP_DEEPLOG || SR2MP_TEST
        // Logged before the model is torn down so type, scene and position are still resolvable.
        ActorDiag.Log("destroy-apply", packet.ActorId.Value, $"packetOwner={packet.OwnerId}");
#endif
        IdentifiableModel? actor = null;
        if (GameState.TryGetIdentifiableModel(packet.ActorId, out var worldActor))
            actor = worldActor;
        else if (ActorManager.Actors.TryGetValue(packet.ActorId.Value, out var trackedActor))
            actor = trackedActor;

        // Ignore an unknown ID instead of rebroadcasting arbitrary destroy packets.
        if (actor == null && !ActorManager.OwnershipByActorId.ContainsKey(packet.ActorId.Value))
            return false;

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            if (actor != null)
            {
                // Remove the drone station if it's a drone
                if (actor.TryCast<DroneStationGadgetModel>() != null)
                    NetworkDroneManager.RemoveStationDrone(packet.ActorId);

                NetworkGadgetManager.RemoveTeleporterGadget(actor);
                NetworkGadgetManager.ForgetGadgetLink(packet.ActorId);

                GameState.identifiables.Remove(packet.ActorId);
                if (GameState.identifiablesByIdent.TryGetValue(actor.ident, out var actorsByIdent))
                    actorsByIdent.Remove(actor);
                GameState.DestroyIdentifiableModel(actor);

                StartCoroutine(DestroyActor(actor));
            }

            ActorManager.Actors.Remove(packet.ActorId.Value);
            ActorManager.OwnershipByActorId.Remove(packet.ActorId.Value);
            ActorManager.EpochByActorId.Remove(packet.ActorId.Value);
            return true;
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }
    }

    /// <summary>
    /// Applies the actor removal for an authenticated, host-validated consume
    /// commit. This bypasses client packet authorization only after the capture
    /// transaction has already verified its endpoint, request, reservation, and
    /// committed owner.
    /// </summary>
    internal static bool ApplyCommittedCaptureDestroy(ActorId actorId, string committedOwnerId)
    {
        if (!Main.Server.IsRunning || actorId.Value == 0 || string.IsNullOrWhiteSpace(committedOwnerId) ||
            !ActorManager.Actors.TryGetValue(actorId.Value, out var trackedActor) || trackedActor == null ||
            !trackedActor.TryGetNetworkComponent(out var networkActor) ||
            !string.Equals(networkActor.CurrentOwnerId, committedOwnerId, StringComparison.Ordinal) ||
            !GameState.TryGetIdentifiableModel(actorId, out var worldActor) || worldActor == null ||
            worldActor.actorId.Value != actorId.Value || worldActor.Pointer != trackedActor.Pointer)
            return false;

        var previousSuppressDestroyPacket = networkActor.SuppressDestroyPacket;
        networkActor.SuppressDestroyPacket = true;
        try
        {
            var applied = ApplyDestroy(new ActorDestroyPacket
            {
                ActorId = actorId,
                OwnerId = committedOwnerId,
                HasOwnerId = true
            });
            var modelRemains = GameState.TryGetIdentifiableModel(actorId, out var remainingActor) &&
                               remainingActor != null;
            var removalConfirmed = applied && !modelRemains &&
                                   !ActorManager.Actors.ContainsKey(actorId.Value) &&
                                   !ActorManager.OwnershipByActorId.ContainsKey(actorId.Value);
            if (!removalConfirmed && networkActor)
                networkActor.SuppressDestroyPacket = previousSuppressDestroyPacket;
            return removalConfirmed;
        }
        catch (Exception exception)
        {
            if (networkActor)
                networkActor.SuppressDestroyPacket = previousSuppressDestroyPacket;
            SrLogger.LogError(
                $"Could not atomically remove consumed actor {actorId.Value} for owner {committedOwnerId}: {exception}");
            return false;
        }
    }

    private static string GetCurrentOwner(long actorId)
    {
        if (ActorManager.Actors.TryGetValue(actorId, out var actor) &&
            actor.TryGetNetworkComponent(out var networkActor) &&
            !string.IsNullOrWhiteSpace(networkActor.CurrentOwnerId))
            return networkActor.CurrentOwnerId;

        return ActorManager.OwnershipByActorId.GetValueOrDefault(actorId, string.Empty);
    }

    private static void DeferDestroy(ActorDestroyPacket packet)
    {
        if (deferredDestroyPackets.TryGetValue(packet.ActorId.Value, out var existing))
        {
            // Actor ids are destroyed at most once. Keep the first authoritative
            // token if a malformed/conflicting duplicate arrives.
            if (existing.OwnerId != packet.OwnerId)
                SrLogger.LogWarning($"Ignoring conflicting deferred destroy for actor {packet.ActorId.Value}");
            return;
        }

        deferredDestroyPackets.Add(packet.ActorId.Value, packet);
        deferredDestroyOrder.Enqueue(packet.ActorId.Value);
        while (deferredDestroyPackets.Count > MaxDeferredDestroyPackets && deferredDestroyOrder.Count > 0)
        {
            var oldestActorId = deferredDestroyOrder.Dequeue();
            if (deferredDestroyPackets.Remove(oldestActorId))
                SrLogger.LogWarning($"Dropped oldest deferred ActorDestroy for actor {oldestActorId}; queue limit reached");
        }
    }

    private static void ClearDeferredDestroyPackets()
    {
        deferredDestroyPackets.Clear();
        deferredDestroyOrder.Clear();
    }

    private static void RemoveDeferredDestroy(long actorId)
    {
        if (!deferredDestroyPackets.Remove(actorId))
            return;

        // Keep the FIFO bookkeeping bounded too; resolved packets must not leave
        // stale queue entries behind for the lifetime of a long session.
        var queued = deferredDestroyOrder.Count;
        for (var i = 0; i < queued; i++)
        {
            var queuedActorId = deferredDestroyOrder.Dequeue();
            if (queuedActorId != actorId)
                deferredDestroyOrder.Enqueue(queuedActorId);
        }
    }

    private static IEnumerator DestroyActor(IdentifiableModel actor)
    {
        for (int i = 0; i < 20; i++)
        {
            try
            {
                var obj = actor.GetGameObject();

                if (obj)
                {
                    Destroyer.DestroyAny(obj, "SR2MP.ActorDestroyHandler");
                    yield break;
                }
            }
            catch
            {
                // ignored
            }

            yield return null; // wait one frame
        }
    }
}

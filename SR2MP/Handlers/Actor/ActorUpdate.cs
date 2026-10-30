using System.Net;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Player.PlayerItems;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Actor;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Actor;

[PacketHandler((byte)PacketType.ActorUpdate)]
internal sealed class ActorUpdateHandler : BasePacketHandler<ActorUpdatePacket>
{
    private static readonly Dictionary<long, float> nextPullDiagnosticAt = new();
    private static readonly Dictionary<long, float> nextOwnerTransformDiagnosticAt = new();

    protected override bool Handle(ActorUpdatePacket packet, IPEndPoint? clientEndpoint)
    {
        if (!ActorManager.Actors.TryGetValue(packet.ActorId.Value, out var model))
            return false;

        var actor = model.Cast<ActorModel>();
        if (!actor.TryGetNetworkComponent(out var networkComponent))
            return false;

        if (!NetworkActorManager.IsCurrentActorOwner(packet.ActorId, clientEndpoint))
        {
            if (!TryApplyApprovedConsumePull(packet, clientEndpoint, networkComponent))
                return false;
            return true;
        }

        var storedEpoch = ActorManager.GetEpoch(packet.ActorId.Value);
        if (!OwnershipEpochPolicy.ShouldAcceptUpdate(packet.HasEpoch, packet.Epoch, storedEpoch))
        {
            Logger.LogDiagnostic("actor", "ownership-stale-dropped",
                $"actorId={packet.ActorId.Value:X}; reason=update-older-epoch; packetEpoch={packet.Epoch}; storedEpoch={storedEpoch}",
                rateKey: "actor/ownership-stale-dropped", minimumIntervalMilliseconds: 500);
            return false;
        }

        actor.lastPosition = packet.Position;
        actor.lastRotation = packet.Rotation;

        networkComponent.OnNetworkWorldUpdate(packet);

        if (Main.Server.IsRunning && clientEndpoint != null &&
            Main.Server.ClientManager.TryGetClient(clientEndpoint, out var sender) && sender != null &&
            (!nextOwnerTransformDiagnosticAt.TryGetValue(packet.ActorId.Value, out var nextAt) ||
             UnityEngine.Time.unscaledTime >= nextAt))
        {
            nextOwnerTransformDiagnosticAt[packet.ActorId.Value] = UnityEngine.Time.unscaledTime + 1f;
            Logger.LogDiagnostic("actor", "owner-transform-received",
                $"actorId={packet.ActorId.Value:X}; sender={sender.PlayerId}; owner={networkComponent.CurrentOwnerId}; " +
                $"position={packet.Position}; velocity={packet.Velocity}");
        }

        if (networkComponent.RegionMember?._hibernating == true)
        {
            networkComponent.transform.position = packet.Position;
            networkComponent.transform.rotation = packet.Rotation;
        }

        return true;
    }

    private static bool TryApplyApprovedConsumePull(ActorUpdatePacket packet, IPEndPoint? clientEndpoint,
        Components.Actor.NetworkActor networkActor)
    {
        if (!Main.Server.IsRunning || clientEndpoint == null ||
            !Main.Server.ClientManager.TryGetClient(clientEndpoint, out var sender) || sender == null ||
            !float.IsFinite(packet.Position.x) || !float.IsFinite(packet.Position.y) ||
            !float.IsFinite(packet.Position.z) || !float.IsFinite(packet.Velocity.x) ||
            !float.IsFinite(packet.Velocity.y) || !float.IsFinite(packet.Velocity.z) ||
            !float.IsFinite(packet.Rotation.x) || !float.IsFinite(packet.Rotation.y) ||
            !float.IsFinite(packet.Rotation.z) || !float.IsFinite(packet.Rotation.w) ||
            packet.Rotation.x * packet.Rotation.x + packet.Rotation.y * packet.Rotation.y +
            packet.Rotation.z * packet.Rotation.z + packet.Rotation.w * packet.Rotation.w < 0.25f ||
            !NetworkActorCaptureManager.IsApprovedConsumePullUpdate(sender.PlayerId,
                packet.ActorId.Value, out var expectedOwnerId) ||
            !string.Equals(networkActor.CurrentOwnerId, expectedOwnerId, StringComparison.Ordinal))
            return false;

        var remotePlayer = PlayerManager.GetPlayer(sender.PlayerId);
        var localPlayer = SceneContext.Instance ? SceneContext.Instance.Player : null;
        var itemController = localPlayer ? localPlayer.GetComponent<PlayerItemController>() : null;
        var localVacuum = itemController ? itemController._vacuumItem : null;
        if (remotePlayer == null || !localVacuum || !networkActor.gameObject.activeInHierarchy ||
            networkActor.IsDestroyed)
            return false;

        var maxReach = localVacuum.MaxVacDist + 3f;
        if (!float.IsFinite(maxReach) || maxReach <= 0f ||
            Vector3.Distance(remotePlayer.Position, packet.Position) > maxReach)
            return false;

        // If the host had the actor in its own vacuum, the approved guest pull
        // takes precedence. Native Release destroys only the host's old spring;
        // the actor keeps the replicated guest trajectory from this sample on.
        var vacuumable = networkActor.GetComponent<Vacuumable>();
        try
        {
            if (vacuumable && vacuumable.IsCaptive())
                vacuumable.Release();
        }
        catch (Exception exception)
        {
            SrLogger.LogWarning(
                $"Could not release host vacuum joint for approved guest pull actor={packet.ActorId.Value:X}: " +
                exception.GetType().Name);
        }

        var body = networkActor.GetComponent<Rigidbody>();
        networkActor.transform.SetPositionAndRotation(packet.Position, packet.Rotation);
        if (body)
        {
            body.position = packet.Position;
            body.rotation = packet.Rotation;
            body.velocity = packet.Velocity;
            body.WakeUp();
        }

        if (GameState.TryGetIdentifiableModel(packet.ActorId, out var model) && model != null)
        {
            var actor = model.TryCast<ActorModel>();
            if (actor != null)
            {
                actor.lastPosition = packet.Position;
                actor.lastRotation = packet.Rotation;
            }
        }

        // Returning true lets BasePacket relay this accepted update once to
        // observers. Do not broadcast here as well, or peers get duplicate transforms.
        if (!nextPullDiagnosticAt.TryGetValue(packet.ActorId.Value, out var nextAt) ||
            UnityEngine.Time.unscaledTime >= nextAt)
        {
            nextPullDiagnosticAt[packet.ActorId.Value] = UnityEngine.Time.unscaledTime + 1f;
            Logger.LogDiagnostic("vacuum", "approved-remote-pull-update",
                $"actorId={packet.ActorId.Value:X}; claimant={sender.PlayerId}; owner={expectedOwnerId}; " +
                $"position={packet.Position}; velocity={packet.Velocity}; " +
                $"playerDistance={Vector3.Distance(remotePlayer.Position, packet.Position):F2}");
        }
        return true;
    }
}

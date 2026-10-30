using System.Net;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Actor;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;
using SR2MP.Shared.Utils;

namespace SR2MP.Handlers.Actor;

[PacketHandler((byte)PacketType.ActorSpawn)]
internal sealed class ActorSpawnHandler : BasePacketHandler<ActorSpawnPacket>
{
    /// <summary>
    /// Host only: tell the originating client to remove its orphan gadget model.
    /// </summary>
    private static void RejectGadget(ActorSpawnPacket packet, IPEndPoint? clientEndpoint, string reason)
    {
        if (packet.SpawnType != (byte)ActorSpawnType.Gadget)
            return;

        SrLogger.LogWarning(
            $"Gadget spawn REJECTED actor={packet.ActorId.Value:X} type={packet.ActorType} reason={reason}");
        Logger.LogDiagnostic("gadget", "spawn-rejected",
            $"actorId={packet.ActorId.Value:X}; type={packet.ActorType}; reason={reason}");

        if (!Main.Server.IsRunning || clientEndpoint == null)
            return;

        // If the host really owns this gadget (same id/type), do not make the client delete it.
        if (GameState.TryGetIdentifiableModel(packet.ActorId, out var existing) && existing != null)
            return;

        Main.Server.SendToClient(new ActorDestroyPacket { ActorId = packet.ActorId }, clientEndpoint);
        Logger.LogDiagnostic("gadget", "reject-destroy-sent", $"actorId={packet.ActorId.Value:X}");
    }

    protected override bool Handle(ActorSpawnPacket packet, IPEndPoint? clientEndpoint)
    {
        if (packet.ActorId.Value == 0)
        {
            SrLogger.LogWarning("Rejected actor spawn with actor ID zero");
#if SR2MP_DEEPLOG || SR2MP_TEST
            ActorDiag.Log("spawn-rejected", 0, "reason=actor-id-zero", packet.ActorType, packet.OwnerId,
                packet.SceneGroup, packet.Position, 1000);
#endif
            return false;
        }

        if (Main.Server.IsRunning)
        {
            if (clientEndpoint == null ||
                !Main.Server.ClientManager.TryGetClient(clientEndpoint, out var sender) ||
                sender == null ||
                string.IsNullOrWhiteSpace(packet.OwnerId) ||
                packet.OwnerId != sender.PlayerId)
            {
                SrLogger.LogWarning($"Rejected actor spawn from unauthenticated owner '{packet.OwnerId}'");
#if SR2MP_DEEPLOG || SR2MP_TEST
                ActorDiag.Log("spawn-rejected", packet.ActorId.Value, "reason=unauthenticated-owner",
                    packet.ActorType, packet.OwnerId, packet.SceneGroup, packet.Position, 1000);
#endif
                return false;
            }

            // Clients receive their own ActorId range in InitialActors. IDs below
            // that range are already part of the client's independently loaded save
            // and must not be reintroduced into the host world as newly spawned
            // actors during scene activation or resync.
            var rangeStart = (long)PlayerIdGenerator.GetPlayerIDNumber(sender.PlayerId) * ActorIdOffset;
            var rangeEnd = rangeStart + ActorIdOffset;
            if (packet.ActorId.Value < rangeStart || packet.ActorId.Value >= rangeEnd)
            {
                SrLogger.LogWarning(
                    $"Rejected actor spawn outside sender range actor={packet.ActorId.Value:X} " +
                    $"sender={sender.PlayerId} range=[{rangeStart:X},{rangeEnd:X})");
                RejectGadget(packet, clientEndpoint, "outside-sender-range");
#if SR2MP_DEEPLOG || SR2MP_TEST
                ActorDiag.Log("spawn-rejected", packet.ActorId.Value,
                    $"reason=outside-sender-range; sender={sender.PlayerId}; range=[{rangeStart:X},{rangeEnd:X})",
                    packet.ActorType, packet.OwnerId, packet.SceneGroup, packet.Position, 1000);
#endif
                return false;
            }
        }

        if (ActorManager.Actors.ContainsKey(packet.ActorId.Value))
        {
            if (packet.SpawnType == (byte)ActorSpawnType.Gadget)
            {
                SrLogger.LogWarning($"Gadget spawn ignored, actor {packet.ActorId.Value:X} already exists (type={packet.ActorType})");
                Logger.LogDiagnostic("gadget", "spawn-duplicate", $"actorId={packet.ActorId.Value:X}; type={packet.ActorType}");
            }
            else
                SrLogger.LogDebug($"Actor {packet.ActorId.Value} already exists");
#if SR2MP_DEEPLOG || SR2MP_TEST
            ActorDiag.Log("spawn-duplicate", packet.ActorId.Value,
                $"endpoint={clientEndpoint?.ToString() ?? "server"}", packet.ActorType, packet.OwnerId,
                packet.SceneGroup, packet.Position, 1000);
#endif
#if SR2MP_TEST
            if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") == "1")
                SrLogger.LogWarning(
                    $"[SR2MP_TEST] actor-spawn duplicate actor={packet.ActorId.Value:X} type={packet.ActorType} " +
                    $"owner={packet.OwnerId} endpoint={clientEndpoint?.ToString() ?? "server"}");
#endif
            return false;
        }

        var spawned = ActorManager.TrySpawnNetworkActor(
            packet.ActorId, packet.Position, packet.Rotation, packet.ActorType, packet.SceneGroup, out var actor,
            packet.FirstAppearance, packet.SecondAppearance, packet.Emotions, packet.Sleeping,
            packet.SpawnType == (byte)ActorSpawnType.Gadget ? packet.ChargeupTime : 0);

        if (!spawned || actor == null)
        {
            RejectGadget(packet, clientEndpoint, $"TrySpawnNetworkActor-failed spawned={spawned}");
#if SR2MP_DEEPLOG || SR2MP_TEST
            ActorDiag.Log("spawn-rejected", packet.ActorId.Value,
                $"reason=TrySpawnNetworkActor-failed; spawned={spawned}; endpoint={clientEndpoint?.ToString() ?? "server"}",
                packet.ActorType, packet.OwnerId, packet.SceneGroup, packet.Position, 1000);
#endif
#if SR2MP_TEST
            if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") == "1")
                SrLogger.LogWarning(
                    $"[SR2MP_TEST] actor-spawn rejected actor={packet.ActorId.Value:X} type={packet.ActorType} " +
                    $"owner={packet.OwnerId} position={packet.Position} " +
                    $"endpoint={clientEndpoint?.ToString() ?? "server"} spawned={spawned}");
#endif
            return false;
        }

        var appliedOwnerId = Main.Server.IsRunning
            ? packet.OwnerId
            : ActorSnapshotDeltaBuffer.GetOwnerForSpawn(
                packet.ActorId.Value, packet.WorldRevision, packet.OwnerId ?? string.Empty);
        
        // Gadgets have no NetworkActor component
        if (actor.TryGetNetworkComponent(out var networkComponent))
        {
            networkComponent.CurrentOwnerId = appliedOwnerId;
            networkComponent.LocallyOwned = appliedOwnerId == LocalID;
            networkComponent.CachedLocallyOwned = networkComponent.LocallyOwned;
        }

        ActorManager.OwnershipByActorId[packet.ActorId.Value] = appliedOwnerId;
        // A (possibly reused) id starts a fresh epoch history on every peer.
        ActorManager.EpochByActorId.Remove(packet.ActorId.Value);
        Logger.LogDiagnostic("actor", "spawned",
            $"actorId={packet.ActorId.Value:X}; type={packet.ActorType}; owner={appliedOwnerId}; " +
            $"sceneGroup={packet.SceneGroup}; spawnType={packet.SpawnType}; position={packet.Position.ToString("F2")}");

#if SR2MP_DEEPLOG || SR2MP_TEST
        ActorDiag.Log("spawn-accepted", packet.ActorId.Value,
            $"spawnType={packet.SpawnType}; endpoint={clientEndpoint?.ToString() ?? "server"}",
            packet.ActorType, appliedOwnerId, packet.SceneGroup, packet.Position);
#endif
#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") == "1")
            SrLogger.LogMessage(
                $"[SR2MP_TEST] actor-spawn accepted actor={packet.ActorId.Value:X} type={packet.ActorType} " +
                $"spawnType={packet.SpawnType} owner={appliedOwnerId} position={packet.Position} " +
                $"endpoint={clientEndpoint?.ToString() ?? "server"}");
#endif

        switch (packet.SpawnType)
        {
            case (byte)ActorSpawnType.Sprinkle when packet.MaterialIndex != (byte)SprinkleMaterialType.none:
            {
                var gameObj = actor.GetGameObject();
                if (gameObj)
                    StartCoroutine(NetworkActorManager.ApplySprinkleMaterial(gameObj, (SprinkleMaterialType)packet.MaterialIndex));
                break;
            }

            case (byte)ActorSpawnType.Slime when packet.Radiancy != (byte)ActorAppearanceType.Default:
            {
                var slime = actor.TryCast<SlimeModel>();
                if (slime != null)
                    NetworkActorManager.ApplyRadiancy(slime, (ActorAppearanceType)packet.Radiancy);
                break;
            }
        }

        return true;
    }
}

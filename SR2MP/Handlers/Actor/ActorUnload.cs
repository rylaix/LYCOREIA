using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Actor;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;
using SR2MP.Shared.Utils;

namespace SR2MP.Handlers.Actor;

[PacketHandler((byte)PacketType.ActorUnload)]
internal sealed class ActorUnloadHandler : BasePacketHandler<ActorUnloadPacket>
{
    protected override bool Handle(ActorUnloadPacket packet, IPEndPoint? senderEndpoint)
    {
        // SenderId is serialized by the client, so never use it as an identity
        // claim on the host without binding it to the authenticated UDP endpoint.
        if (Main.Server.IsRunning &&
            (senderEndpoint == null ||
             !Main.Server.ClientManager.TryGetClient(senderEndpoint, out var sender) ||
             sender == null ||
             sender.PlayerId != packet.SenderId))
        {
            Diag("unload-rejected", packet, "unauthenticated-sender");
            return false;
        }

        if (!ActorManager.Actors.TryGetValue(packet.ActorId.Value, out var actor))
        {
            Diag("unload-ignored", packet, "actor-unknown");
            return false;
        }

        if (!actor.TryGetNetworkComponent(out var component))
        {
            Diag("unload-ignored", packet, "no-network-component");
            return false;
        }

        if (!component.RegionMember)
        {
            Diag("unload-ignored", packet, "no-region-member");
            return false;
        }
        
        if (!string.IsNullOrEmpty(component.CurrentOwnerId) &&
            component.CurrentOwnerId != packet.SenderId)
        {
            Diag("unload-rejected", packet, $"sender-not-owner; owner={component.CurrentOwnerId}");
            return false;
        }

        if (!component.RegionMember!._hibernating)
        {
            // The owner let go but this peer's region is awake: the owner has released the actor. Record that and
            // express interest; the host arbiter reassigns (no direct claim from here).
            Diag("unload-interest", packet, "region-awake-release");
            NetworkActorManager.MarkReleasedByOwner(packet.ActorId.Value, packet.SenderId);
            NetworkActorManager.ExpressOwnershipInterest(packet.ActorId, component.CurrentOwnerId);
            return false;
        }
        
        Diag("unload-accepted", packet, "region-hibernating");
        component.LocallyOwned = false;
        NetworkActorManager.MarkReleasedByOwner(packet.ActorId.Value, packet.SenderId);
        // Host: reassign to whoever already showed interest, without waiting for the next arbiter tick.
        OwnershipArbiter.EvaluateNow(packet.ActorId.Value);
        return true;
    }

    private static void Diag(string eventName, ActorUnloadPacket packet, string reason)
    {
#if SR2MP_DEEPLOG || SR2MP_TEST
        ActorDiag.Log(eventName, packet.ActorId.Value, $"reason={reason}; sender={packet.SenderId}",
            perActorMilliseconds: 2000);
#endif
    }
}

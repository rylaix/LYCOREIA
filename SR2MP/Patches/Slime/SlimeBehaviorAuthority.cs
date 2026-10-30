using SR2MP.Components.Actor;
using SR2MP.Packets.Actor;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Slime;

/// <summary>
/// Slime behavior components continue ticking on every peer even while the
/// corresponding actor's physics is remotely owned. Keep behavior side effects
/// on the actor owner; transforms and spawned actors already use the normal
/// actor replication path.
/// </summary>
internal static class SlimeBehaviorAuthority
{
    internal static bool ShouldPublishBehaviorChange(bool multiplayer, bool handlingPacket, bool locallyOwned)
        => multiplayer && !handlingPacket && locallyOwned;

    internal static bool VerifyBehaviorEmissionPolicy()
        => ShouldPublishBehaviorChange(multiplayer: true, handlingPacket: false, locallyOwned: true)
           && !ShouldPublishBehaviorChange(multiplayer: true, handlingPacket: true, locallyOwned: true)
           && !ShouldPublishBehaviorChange(multiplayer: true, handlingPacket: false, locallyOwned: false)
           && !ShouldPublishBehaviorChange(multiplayer: false, handlingPacket: false, locallyOwned: true);

    internal static bool ShouldRun(UnityEngine.Component behaviour)
    {
        if (!Main.Server.IsRunning && !Main.Client.IsConnected)
            return true;

        var actor = behaviour.GetComponentInParent<NetworkActor>();
        if (actor)
            return actor.LocallyOwned;

        // A remote actor is instantiated while HandlingPacket is true, before
        // TrySpawnNetworkActor attaches its NetworkActor. Do not let its first
        // behavior tick create a second copy of a transient result.
        return !HandlingPacket;
    }

    internal static void TransferTargetToLocalOwner(GameObject target)
    {
        if (!Main.Server.IsRunning && !Main.Client.IsConnected)
            return;

        if (HandlingPacket || !target)
            return;

        var actor = target.GetComponentInParent<NetworkActor>();
        if (!actor || actor.ActorId.Value == 0 || actor.LocallyOwned || actor.CurrentOwnerId == LocalID)
            return;

        NetworkActorManager.ExpressOwnershipInterest(actor.ActorId, actor.CurrentOwnerId);
    }
}

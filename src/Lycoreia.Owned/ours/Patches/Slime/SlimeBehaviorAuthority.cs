// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Owned.Patches.Slime;

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
        if (!ModFacade.Server.IsRunning && !ModFacade.Client.IsConnected)
            return true;

        var actor = Lycoreia.Game.ActorLink.Of(behaviour);
        if (actor)
            return actor.LocallyOwned;

        // A remote actor is instantiated while HandlingPacket is true, before
        // the actor table knows its id. Do not let its first
        // behavior tick create a second copy of a transient result.
        return !HandlingPacket;
    }

    internal static void TransferTargetToLocalOwner(GameObject target)
    {
        if (!ModFacade.Server.IsRunning && !ModFacade.Client.IsConnected)
            return;

        if (HandlingPacket || !target)
            return;

        var actor = Lycoreia.Game.ActorLink.Of(target);
        if (!actor || actor.ActorId.Value == 0 || actor.LocallyOwned || actor.CurrentOwnerId == LocalID)
            return;

        Lycoreia.Game.ActorRuntime.NoteActivity(target);   // interest goes through the arbiter
    }
}

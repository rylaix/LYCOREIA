using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Components.Actor;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Actor;

[HarmonyPatch(typeof(SceneContext), nameof(SceneContext.Start))]
internal static class OnGameLoadPatch
{
    public static void Postfix() => Main.Server.OnServerStarted += () =>
    {
        foreach (var actor in GameState.identifiables)
        {
            // Player avatars use the dedicated NetworkPlayer path. Treating the
            // player's backing ActorModel as a world actor makes it enter actor
            // snapshots even though NetworkActor.Start rejects SRCharacterController.
            if (actor.value.ident.IsPlayer) continue;

            if (actor.value.TryCast<ActorModel>() == null) continue;

            // Drones dont get NetworkActor
            if (NetworkDroneManager.IsDroneModel(actor.value)) continue;

            var transform = actor.value.Transform;

            if (!transform)
                continue;
            
            var networkComponent = transform.GetComponent<NetworkActor>();

            if (networkComponent)
            {
                networkComponent.CurrentOwnerId = LocalID;
                networkComponent.LocallyOwned = true;
                ActorManager.OwnershipByActorId[actor.value.actorId.Value] = LocalID;
                continue;
            }
            
            var networkComponent2 = transform.gameObject.AddComponent<NetworkActor>();
            networkComponent2.LocallyOwned = true;
            networkComponent2.CurrentOwnerId = LocalID;

            ActorManager.Actors[actor.value.actorId.Value] = actor.value;
            ActorManager.OwnershipByActorId[actor.value.actorId.Value] = LocalID;
        }

        GameState._actorIdProvider._nextActorId =
            NetworkActorManager.GetHighestActorIdInRange(0, ActorIdOffset) + 10;
    };
}

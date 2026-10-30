using HarmonyLib;
using SR2MP.Components.Actor;
using SR2MP.Handlers.GordoSlime;
using SR2MP.Packets.FX;
using SR2MP.Packets.GordoSlime;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.GordoSlime;

[HarmonyPatch(typeof(GordoEat), nameof(GordoEat.DoEat))]
internal static class OnGordoSlimeFed
{
    public static void Postfix(GordoEat __instance, GameObject __0)
    {
        var foodActorId = GetFoodActorId(__0);
        var gordoId = __instance.Id;
        var targetCount = __instance.GordoModel.targetCount;
        var localCount = Math.Max(0, __instance.GordoModel.GordoEatenCount);

        if (Main.Server.IsRunning)
        {
            if (foodActorId != 0)
            {
                if (!NetworkGordoSlimeManager.TryAcceptFeedActor(
                        gordoId, foodActorId, Math.Min(localCount, targetCount), out var acceptedCount))
                {
                    localCount = GordoSlimeAuthorityPolicy.ReconcileHostLocalDuplicate(
                        localCount, acceptedCount, targetCount);
                    __instance.GordoModel.GordoEatenCount = localCount;
                }
            }
            else
            {
                SrLogger.LogWarning($"Gordo feed for {gordoId} has no stable food ActorId; host state will still be broadcast, but actor deduplication is unavailable.");
            }
        }
        else if (Main.Client.IsConnected && foodActorId == 0)
        {
            SrLogger.LogWarning($"Guest Gordo feed for {gordoId} has no stable food ActorId; request was not sent to the host.");
        }

        var packet = new GordoSlimeFeedPacket
        {
            ID = gordoId,
            RequestId = Guid.NewGuid().ToString("N"),
            FoodActorId = foodActorId,
            NewFoodCount = Math.Min(localCount, targetCount),
            RequiredFoodCount = targetCount,
            GordoSlimeType = NetworkActorManager.GetPersistentID(__instance.GordoModel.identifiableType)
        };

        if (Main.Server.IsRunning)
            Main.Server.SendToAll(packet);
        else if (Main.Client.IsConnected && foodActorId != 0)
            Main.Client.SendPacket(packet);

        var soundPacket = new WorldFXPacket
        {
            Position = __instance.transform.position,
            FX = WorldFXType.GordoFoodEatenSound
        };
        Main.SendToAllOrServer(soundPacket);
    }

    private static long GetFoodActorId(GameObject foodActor)
    {
        if (!foodActor)
            return 0;

        try
        {
            var networkActor = foodActor.GetComponent<NetworkActor>();
            if (networkActor)
            {
                var networkActorId = networkActor.ActorId.Value;
                if (networkActorId != 0)
                    return networkActorId;
            }

            // DoEat's postfix runs after the native destroy call has been queued;
            // the IdentifiableActor retains the stable world ActorId during it.
            var identifiableActor = foodActor.GetComponent<IdentifiableActor>();
            return identifiableActor ? identifiableActor.GetActorId().Value : 0;
        }
        catch (Exception exception)
        {
            SrLogger.LogDebug($"Could not read consumed Gordo food ActorId: {exception.Message}");
            return 0;
        }
    }
}

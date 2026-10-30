using HarmonyLib;
using SR2MP.Handlers.LandPlots;
using SR2MP.Components.LandPlots;
using SR2MP.Packets.LandPlots;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.LandPlots;

[HarmonyPatch(typeof(GardenCatcher), nameof(GardenCatcher.Plant))]
internal static class PlantGarden
{
    public static void Postfix(GardenCatcher __instance, IdentifiableType cropId, GameObject __result)
    {
        if (HandlingPacket)
            return;

        var plotLocation = __instance ? __instance.GetComponentInParent<LandPlotLocation>() : null;
        var plantedResource = __result ? __result.GetComponent<SpawnResource>() : null;
        var plantedCrop = plantedResource?._resourceGrowerDefinition?._primaryResourceType;
        if (!GardenCropMutationPolicy.CanPublishLocalPlant(
                hasPlotId: plotLocation && !string.IsNullOrWhiteSpace(plotLocation._id),
                hasPlantResult: !!__result,
                returnedCropMatches: plantedCrop != null && plantedCrop == cropId))
            return;

        var plotId = plotLocation!._id;
        var networkGarden = plantedResource!.GetComponent<NetworkGarden>();
        var packet = new GardenPlantPacket
        {
            ActorType = NetworkActorManager.GetPersistentID(cropId),
            ID = plotId
        };

        if (!NetworkGardenManager.PrepareLocalCropUpdate(packet, networkGarden))
            return;

#if SR2MP_TEST
        if (!Main.Server.IsRunning && Main.Client.IsJoined &&
            Environment.GetEnvironmentVariable("SR2MP_TEST_DENY_GARDEN_PLANT_RELAY") == "1")
        {
            SrLogger.LogMessage(
                $"[SR2MP_TEST] denied garden plant relay plot={plotId} crop={packet.ActorType}");
            return;
        }
#endif

        Main.SendToAllOrServer(packet);

        networkGarden?.ClaimOnReady();
    }
}

using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Drone;
using SR2MP.Packets.World;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Drone;

[HarmonyPatch(typeof(DiscoverableResourceDataStore), nameof(DiscoverableResourceDataStore.UpdateResourceData))]
internal static class OnDroneResourceUpdate
{
    public static void Postfix(DiscoverableResourceDataStore __instance, int index, ref DiscoverableResourceData updatedData)
    {
        if (HandlingPacket) return;

        if (!Main.Server.IsRunning && !Main.Client.IsConnected) return;

        try
        {
            var sceneId = NetworkSceneManager.GetPersistentID(__instance.SceneGroup);
            if (!NetworkDroneManager.TryGetUniqueOwnedExplorerStationInScene(LocalID, sceneId, out var stationId))
            {
                SrLogger.LogDebug(
                    $"Skipped drone-resource update scene={sceneId}, index={index}: no unique locally owned ExplorerDrone station was available.");
                return;
            }

            Main.SendToAllOrServer(new DroneResourcePacket
            {
                Scene = sceneId,
                StationId = stationId,
                Index = index,
                Entry = NetworkDroneManager.CreateEntry(updatedData)
            });
        }
        catch (Exception ex)
        {
            SrLogger.LogDebug($"Failed to sync drone resource update: {ex.Message}");
        }
    }
}

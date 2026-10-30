// Do not Harmony-patch TeleporterModel.RemoveDestination: this target caused a
// native access violation on the game build. NetworkGadgetManager monitors links
// recorded by AddDestination and publishes reliable unlink packets after removal.
/*using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Packets.Gadget;

namespace SR2MP.Patches.Gadget;

[HarmonyPatch(typeof(TeleporterModel), nameof(TeleporterModel.RemoveDestination))]
internal static class OnTeleporterUnlinked
{
    public static void Postfix(TeleporterModel __instance, TeleporterNodeModel nodeModel)
    {
        if (!Main.Server.IsRunning && !Main.Client.IsConnected) return;
        if (HandlingPacket) return;
        
        if (SystemContext.Instance.SceneLoader.IsSceneLoadInProgress) return;

        try
        {
            var sourceNode = __instance.source?.TeleporterNodeModel;
            if (sourceNode == null || nodeModel == null) return;

            SrLogger.LogDebug($"OnTeleporterUnlinked: broadcasting {sourceNode.NodeId} -/-> {nodeModel.NodeId}.");

            Main.SendToAllOrServer(new TeleporterUnlinkPacket
            {
                SourceNodeId = sourceNode.NodeId,
                DestinationNodeId = nodeModel.NodeId
            });
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"OnTeleporterUnlinked: failed to broadcast unlink: {ex.Message}");
        }
    }
}*/

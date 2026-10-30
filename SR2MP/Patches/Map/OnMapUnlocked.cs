using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.UI.Map;
using SR2MP.Packets.World;

namespace SR2MP.Patches.Map;

[HarmonyPatch(typeof(MapNodeActivator), nameof(MapNodeActivator.Activate))]
internal static class OnMapUnlocked
{
    public static void Postfix(MapNodeActivator __instance)
    {
        if (HandlingPacket || __instance == null || __instance._fogRevealEvent == null)
            return;

        var nodeId = __instance._fogRevealEvent.DataKey;
        if (string.IsNullOrWhiteSpace(nodeId))
            return;

        var packet = new MapUnlockPacket
        {
            NodeID = nodeId
        };

        Main.SendToAllOrServer(packet);

        // The host relays this unlock to everyone except the sender, so a guest that pressed the node
        // must finish its own icon: record the unlock if native did not, then redraw/animate the node.
        if (Main.Client.IsConnected && !Main.Server.IsRunning)
            StartCoroutine(FinishGuestActivation(__instance, nodeId));
    }

    private static System.Collections.IEnumerator FinishGuestActivation(MapNodeActivator node, string nodeId)
    {
        var waited = 0f;
        while (waited < 1f)
        {
            waited += UnityEngine.Time.unscaledDeltaTime;
            yield return null;
        }

        if (!node)
            yield break;

        try
        {
            var model = SceneContext.Instance ? SceneContext.Instance.eventDirector?._model : null;
            if (model != null)
            {
                if (!model.table.TryGetValue(MapEventKey, out var table))
                {
                    table = new CppCollections.Dictionary<string, Il2CppMonomiPark.SlimeRancher.Event.EventRecordModel.Entry>();
                    model.table[MapEventKey] = table;
                }

                if (!table.TryGetValue(nodeId, out var entry) || entry == null || entry.count <= 0)
                {
                    table[nodeId] = new Il2CppMonomiPark.SlimeRancher.Event.EventRecordModel.Entry
                    {
                        count = 1,
                        dataKey = nodeId,
                        eventKey = MapEventKey
                    };
                    model.NotifyParticipants();
                }
            }

            node.UpdateHologramState();
            if (node._hologram && node._hologram.activeSelf)
                node.StartCoroutine(node.ActivateHologramAnimation());
            Logger.LogDiagnostic("map", "guest-node-finished",
                $"node={nodeId}; hologramActive={(node._hologram ? node._hologram.activeSelf : false)}");
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"Could not finish map node {nodeId}: {ex.Message}");
        }
    }
}

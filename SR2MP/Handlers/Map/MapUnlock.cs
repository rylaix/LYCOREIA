using System.Net;
using Il2CppMonomiPark.SlimeRancher.Event;
using Il2CppMonomiPark.SlimeRancher.UI.Map;
using SR2MP.Handlers;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Utils;
using SR2MP.Packets.World;

namespace SR2MP.Handlers.Map;

[PacketHandler((byte)PacketType.MapUnlock)]
internal sealed class MapUnlockHandler : BasePacketHandler<MapUnlockPacket>
{
    protected override bool Handle(MapUnlockPacket packet, IPEndPoint? _)
    {
        if (!ProgressionPacketInputPolicy.HasIdentifier(packet.NodeID))
            return false;

        var scene = SceneContext.Instance;
        if (scene == null || scene.MapDirector == null || scene.eventDirector == null)
            return false;

        var eventDirModel = scene.eventDirector._model;
        var alreadyUnlocked = eventDirModel.table.TryGetValue(MapEventKey, out var currentNodes) &&
                              currentNodes.TryGetValue(packet.NodeID, out var currentEntry) &&
                              currentEntry != null && currentEntry.count > 0;

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            if (MapUnlockPolicy.ShouldNotifyMapDirector(alreadyUnlocked))
            {
                var gameEvent = Resources.FindObjectsOfTypeAll<StaticGameEvent>()
                    .FirstOrDefault(x => x != null && x._dataKey == packet.NodeID);
                if (gameEvent != null)
                    scene.MapDirector.NotifyZoneUnlocked(gameEvent, false, 0);

                var activator = Resources.FindObjectsOfTypeAll<MapNodeActivator>()
                    .FirstOrDefault(x => x != null && x._fogRevealEvent != null &&
                                         x._fogRevealEvent._dataKey == packet.NodeID);
                activator?.StartCoroutine(activator.ActivateHologramAnimation());
            }

            if (!eventDirModel.table.TryGetValue(MapEventKey, out var table))
            {
                table = new CppCollections.Dictionary<string, EventRecordModel.Entry>();
                eventDirModel.table[MapEventKey] = table;
            }

            table[packet.NodeID] = new EventRecordModel.Entry
            {
                count = 1,
                createdRealTime = 0,
                createdGameTime = 0,
                dataKey = packet.NodeID,
                eventKey = MapEventKey,
                updatedRealTime = 0,
                updatedGameTime = 0
            };
            eventDirModel.NotifyParticipants();

            // The in-world node only redraws through UpdateHologramState; a network unlock must call it
            // or the icon stays visible (and non-interactive) on peers that did not press it.
            foreach (var node in Resources.FindObjectsOfTypeAll<MapNodeActivator>())
            {
                if (node && node._fogRevealEvent != null && node._fogRevealEvent._dataKey == packet.NodeID)
                    node.UpdateHologramState();
            }
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        return true;
    }
}

using System.Net;
using Il2CppMonomiPark.SlimeRancher.Event;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Loading;
using SR2MP.Packets.Utils;

namespace SR2MP.Handlers.Map;

[PacketHandler((byte)PacketType.InitialMapEntries, HandlerType.Client)]
internal sealed class InitialMapLoadHandler : BasePacketHandler<InitialMapPacket>
{
    protected override bool Handle(InitialMapPacket packet, IPEndPoint? _)
    {
        if (packet.UnlockedNodes == null)
            return false;

        var scene = SceneContext.Instance;
        if (!scene || !scene.eventDirector)
            return false;

        var eventModel = scene.eventDirector._model;
        var previouslyUnlocked = new HashSet<string>();
        if (eventModel.table.TryGetValue(MapEventKey, out var priorMapEntries) && priorMapEntries != null)
        {
            foreach (var (nodeId, entry) in priorMapEntries)
            {
                if (entry != null && entry.count > 0 && !string.IsNullOrWhiteSpace(nodeId))
                    previouslyUnlocked.Add(nodeId);
            }
        }

        var dict = new CppCollections.Dictionary<string, EventRecordModel.Entry>();

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            foreach (var node in packet.UnlockedNodes)
            {
                if (string.IsNullOrWhiteSpace(node))
                    continue;

                // Live map unlocks notify MapDirector before recording the same
                // event-table entry. Reproduce that state transition for a late
                // joiner, but omit the live hologram animation.
                if (MapUnlockPolicy.ShouldNotifyMapDirector(previouslyUnlocked.Contains(node))
                    && scene.MapDirector != null)
                {
                    var gameEvent = Resources.FindObjectsOfTypeAll<StaticGameEvent>()
                        .FirstOrDefault(x => x != null && x._dataKey == node);
                    if (gameEvent != null)
                        scene.MapDirector.NotifyZoneUnlocked(gameEvent, false, 0);
                }

                dict[node] = new EventRecordModel.Entry
                {
                    count = 1,
                    createdRealTime = 0,
                    createdGameTime = 0,
                    dataKey = node,
                    eventKey = MapEventKey,
                    updatedRealTime = 0,
                    updatedGameTime = 0
                };
            }

            eventModel.table[MapEventKey] = dict;
            eventModel.NotifyParticipants();
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        return false;
    }
}

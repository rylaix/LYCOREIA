using Il2CppMonomiPark.SlimeRancher.Event;
using Il2CppMonomiPark.SlimeRancher.World;
using SR2MP.Packets.Loading;
using SR2MP.Packets.World;

namespace SR2MP.Shared.Managers;

internal enum FilterType
{
    Unfiltered,
    Whitelist,
    Blacklist
}

internal static class NetworkEventManager
{
    internal static void Initialize()
    {
        _ = ProducersByKey;

#if SR2MP_TEST
        var progressionPacketInputs = SR2MP.Handlers.ProgressionPacketInputPolicy.VerifyRules();
        var countPolicy = EventSyncPolicy.VerifyRules();
        var zoneUnlockPolicy = EventSyncPolicy.VerifyZoneUnlockRules();
        var objectDiscoveryPolicy = VerifyObjectDiscoveredPolicy();
        var mapPolicy = SR2MP.Handlers.Map.MapUnlockPolicy.VerifyRules();
        var eventCodec = EventRaisedPacket.VerifySerializationRoundTrip();
        var eventSnapshotCodec = InitialEventsRaisedPacket.VerifySerializationRoundTrip();
        var conversationCodec = ConversationPlayedPacket.VerifySerializationRoundTrip();
        var conversationSnapshotCodec = InitialConversationsPacket.VerifySerializationRoundTrip();
        var mapCodec = MapUnlockPacket.VerifySerializationRoundTrip();
        var mapSnapshotCodec = InitialMapPacket.VerifySerializationRoundTrip();
        SrLogger.LogMessage(
            $"[SR2MP_TEST] progression-packet-inputs={(progressionPacketInputs ? "PASS" : "FAIL")} " +
            $"event-count-policy={(countPolicy ? "PASS" : "FAIL")} " +
            $"zone-unlock-policy={(zoneUnlockPolicy ? "PASS" : "FAIL")} " +
            $"object-discovery-policy={(objectDiscoveryPolicy ? "PASS" : "FAIL")} " +
            $"map-unlock-policy={(mapPolicy ? "PASS" : "FAIL")} " +
            $"event-codec={(eventCodec ? "PASS" : "FAIL")} " +
            $"event-snapshot-codec={(eventSnapshotCodec ? "PASS" : "FAIL")} " +
            $"conversation-codec={(conversationCodec ? "PASS" : "FAIL")} " +
            $"conversation-snapshot-codec={(conversationSnapshotCodec ? "PASS" : "FAIL")} " +
            $"map-codec={(mapCodec ? "PASS" : "FAIL")} " +
            $"map-snapshot-codec={(mapSnapshotCodec ? "PASS" : "FAIL")}");
#endif
    }
    
    private const FilterType EventKeyFilter = FilterType.Whitelist;
    private const FilterType DataKeyFilter = FilterType.Unfiltered;

    // Whitelist
    private static readonly HashSet<string> EventKeyWhitelist = new()
    {
        "endGameRewardGranted", // End Game Reward (notification?)
        "objectDiscovered",     // A LOT of unlocked things
        "Gift",                 // NPC gift progression (uses a static event, not StringEventProducer)
        EventSyncPolicy.ZoneUnlockedEventKey
    };

    // Blacklist
    private static readonly HashSet<string> EventKeyBlacklist = new()
    {
        MapEventKey,                // already synced
        "gordoBurst",               // already synced
        "puzzleSlotUnlocked",       // already synced
        "teleported",               // already synced
        "jump",                     // no need to sync
        "uiDisplayOpened",          // no need to sync
        "uiDisplayClosed",          // no need to sync
        "timePassed",               // already synced via time sync
        "areaEnter",                // no need to sync (?)
        "areaExit",                 // no need to sync (?)
        "groupshot",                // already synced via actor spawn
        "groupvacced",              // already synced via actor destroy
        "shot",                     // already synced via actor spawn
        "vacced",                   // already synced via actor destroy
        "convoPlayed",              // already synced
        "convoRancherPlayed",       // already synced
        "tutorialCompleted",        // no need to sync YET
        "pediaEntryPopupClosed",    // no need to sync
        "ranchExpansionPurchased",  // already synced via AccessDoor
        "plotPurchased",            // already synced via LandPlots
        "plotDemolished",           // already synced via LandPlots
        "cropPlanted",              // already synced via GardenPlant
        "gadgetPlaced",             // already synced via actor spawn
        "gadgetObtained",           // already synced via actor destroy
        "gadgetPickedUpOrStored",   // already synced via refinery sync
        "gadgetBlueprintObtained",  // no need to sync (?)
        "corralled",                // no need to sync (analytics)
        "groupcorralled",           // no need to sync (analytics)
        "objectSuctioned",          // already synced via actor update
        "selected",                 // no need to sync (gadget menu)
        "modeOpened",               // already synchronized / no need (gadget placement mode)
        "modeClosed",               // already synchronized / no need (gadget placement mode)
        "restocked",                // no need to sync YET (shop restocks)
        "refined"                   // already synced via refinery sync
    };

    private static readonly HashSet<string> DataKeyWhitelist = new();

    private static readonly HashSet<string> DataKeyBlacklist = new();
    
    // These event types are emitted as StaticGameEvent instances and therefore do not
    // appear in the StringEventProducer resource scan.
    private static readonly HashSet<string> StaticEventWhitelist = new(StringComparer.Ordinal)
    {
        "Gift"
    };

    private static Dictionary<string, StringEventProducer>? producersByKey;

    private static Dictionary<string, StringEventProducer> ProducersByKey
    {
        get
        {
            if (producersByKey != null)
                return producersByKey;

            producersByKey = new Dictionary<string, StringEventProducer>();
            
            foreach (var producer in Resources.FindObjectsOfTypeAll<StringEventProducer>())
            {
                if (!string.IsNullOrEmpty(producer.KeyPrefix))
                    producersByKey[producer.KeyPrefix] = producer;
            }

            return producersByKey;
        }
    }

    internal static bool HasProducer(string eventKey)
        => ProducersByKey.ContainsKey(eventKey);

    internal static bool CanApplyEvent(string eventKey)
        => EventSyncPolicy.IsZoneUnlockedEvent(eventKey)
            ? EventSyncPolicy.CanApplyZoneUnlockState(
                SceneContext.Instance?.eventDirector?._model != null)
            : HasProducer(eventKey) || StaticEventWhitelist.Contains(eventKey);

    private static bool CanSnapshotEvent(string eventKey)
        // A snapshot stores the event table directly and notifies its consumers;
        // unlike a live transition, it does not need the runtime producer instance.
        => EventSyncPolicy.IsZoneUnlockedEvent(eventKey) || CanApplyEvent(eventKey);

    private static bool ShouldSyncEventKey(string eventKey) => EventKeyFilter switch
    {
        FilterType.Whitelist => EventKeyWhitelist.Contains(eventKey),
        FilterType.Blacklist => !EventKeyBlacklist.Contains(eventKey),
        _ => true
    };

    private static bool ShouldSyncDataKey(string dataKey) => DataKeyFilter switch
    {
        FilterType.Whitelist => DataKeyWhitelist.Contains(dataKey),
        FilterType.Blacklist => !DataKeyBlacklist.Contains(dataKey),
        _ => true
    };

    internal static bool ShouldSync(string eventKey, string dataKey)
    {
        if (!ShouldSyncEventKey(eventKey) || !ShouldSyncDataKey(dataKey))
            return false;

        if (EventSyncPolicy.IsZoneUnlockedEvent(eventKey))
            return EventSyncPolicy.IsValidZoneDataKey(dataKey);

        if (eventKey == "objectDiscovered")
            return !string.IsNullOrWhiteSpace(dataKey);

        return true;
    }

#if SR2MP_TEST
    private static bool VerifyObjectDiscoveredPolicy()
        => ShouldSync("objectDiscovered", "_f79f91ec-b69a-4443-8f82-af6a187e1edb") &&
           ShouldSync("objectDiscovered", "another-discovered-world-object") &&
           !ShouldSync("objectDiscovered", string.Empty) &&
           !ShouldSync("unregistered-event", "another-discovered-world-object");
#endif

    private static StringEventProducer? FindProducer(string eventKey)
        => ProducersByKey.TryGetValue(eventKey, out var producer) ? producer : null;

    private static ZoneEventProducer? FindZoneUnlockProducer()
        => SceneContext.Instance?.zoneUnlockEvent;

    private static ZoneDefinition? FindZoneDefinition(string dataKey)
    {
        foreach (var zone in Resources.FindObjectsOfTypeAll<ZoneDefinition>())
        {
            if (zone && string.Equals(zone.name, dataKey, StringComparison.Ordinal))
                return zone;
        }

        return null;
    }

    internal static void SendEventRaised(string eventKey, string dataKey, int count)
    {
        count = EventSyncPolicy.NormalizeTargetCount(eventKey, count);
        if (count <= 0)
            return;

        Main.SendToAllOrServer(new EventRaisedPacket { EventKey = eventKey, DataKey = dataKey, Count = count });
    }

    internal static int GetEventCount(string eventKey, string dataKey)
    {
        var eventModel = SceneContext.Instance?.eventDirector?._model;
        var table = eventModel?.table;
        if (table == null || !table.TryGetValue(eventKey, out var dataEntries) || dataEntries == null ||
            !dataEntries.TryGetValue(dataKey, out var entry) || entry == null)
            return 0;

        return Math.Max(0, entry.count);
    }

    internal static bool ApplyEventRaised(string eventKey, string dataKey, int targetCount)
    {
        if (!CanApplyEvent(eventKey) || !ShouldSync(eventKey, dataKey))
            return false;

        targetCount = EventSyncPolicy.NormalizeTargetCount(eventKey, targetCount);
        if (targetCount <= 0)
            return false;

        var scene = SceneContext.Instance;
        if (!scene || !scene.eventDirector)
            return false;

        var currentCount = GetEventCount(eventKey, dataKey);
        if (EventSyncPolicy.IsZoneUnlockedEvent(eventKey))
            return ApplyZoneUnlock(dataKey, currentCount, targetCount);

        var increment = targetCount - currentCount;
        if (increment <= 0)
            return true;

        // Clamp network-controlled work. Normal gameplay event counts are far below this.
        increment = EventSyncPolicy.GetIncrement(currentCount, targetCount);
        if (increment <= 0)
            return true;

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            var producer = FindProducer(eventKey);
            if (producer != null && increment == 1)
            {
                producer.RaiseEventForData(dataKey);
                return true;
            }

            var gameEvent = StaticGameEvent.CreateStaticGameEvent(eventKey, dataKey);
            scene.eventDirector.RaiseEvent(gameEvent.Cast<IGameEvent>(), increment);
            return true;
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }
    }

    private static bool ApplyZoneUnlock(string dataKey, int currentCount, int targetCount)
    {
        if (!EventSyncPolicy.ShouldApplyZoneUnlock(currentCount, targetCount))
            return currentCount > 0;

        var producer = FindZoneUnlockProducer();
        var zone = FindZoneDefinition(dataKey);
        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            if (producer != null && zone != null)
            {
                producer.RaiseEventForData(zone);
                return GetEventCount(EventSyncPolicy.ZoneUnlockedEventKey, dataKey) > 0;
            }

            // Scene streaming can expose the event model before the zone producer.
            // Persist the same idempotent state and notify model participants so
            // the unlock is not lost just because that producer has not loaded yet.
            return SetEventCount(EventSyncPolicy.ZoneUnlockedEventKey, dataKey,
                EventSyncPolicy.UnlockedZoneCount);
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }
    }

    private static bool SetEventCount(string eventKey, string dataKey, int count)
    {
        var eventModel = SceneContext.Instance?.eventDirector?._model;
        if (eventModel == null)
            return false;

        var table = eventModel.table;
        if (!table.TryGetValue(eventKey, out var dataEntries) || dataEntries == null)
        {
            dataEntries = new CppCollections.Dictionary<string, EventRecordModel.Entry>();
            table[eventKey] = dataEntries;
        }

        dataEntries[dataKey] = new EventRecordModel.Entry
        {
            count = count,
            createdRealTime = 0,
            createdGameTime = 0,
            dataKey = dataKey,
            eventKey = eventKey,
            updatedRealTime = 0,
            updatedGameTime = 0
        };
        eventModel.NotifyParticipants();
        return true;
    }

    internal static void ApplyEventSnapshot(List<InitialEventsRaisedPacket.Entry> entries)
    {
        var scene = SceneContext.Instance;
        if (!scene || !scene.eventDirector || scene.eventDirector._model == null)
            return;

        var eventModel = scene.eventDirector._model;
        var table = eventModel.table;
        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;

        try
        {
            var synchronizedEventKeys = EventKeyWhitelist.ToArray();
            foreach (var eventKey in synchronizedEventKeys)
            {
                if (!ShouldSyncEventKey(eventKey) || !table.TryGetValue(eventKey, out var dataEntries))
                    continue;

                var staleKeys = new List<string>();
                foreach (var dataEntry in dataEntries)
                {
                    if (ShouldSync(eventKey, dataEntry.Key))
                        staleKeys.Add(dataEntry.Key);
                }

                foreach (var dataKey in staleKeys)
                    dataEntries.Remove(dataKey);

                if (dataEntries.Count == 0)
                    table.Remove(eventKey);
            }

            foreach (var entry in entries)
            {
                // Snapshots replace recorded state without invoking event producers again.
                // Some producers may grant rewards or trigger other non-idempotent effects.
                if (!CanSnapshotEvent(entry.EventKey) || !ShouldSync(entry.EventKey, entry.DataKey))
                    continue;

                if (EventSyncPolicy.IsZoneUnlockedEvent(entry.EventKey)
                        ? !EventSyncPolicy.ShouldIncludeZoneUnlockSnapshot(entry.Count)
                        : entry.Count <= 0)
                    continue;

                var count = EventSyncPolicy.NormalizeTargetCount(entry.EventKey, entry.Count);
                if (count <= 0)
                    continue;

                if (!table.TryGetValue(entry.EventKey, out var dataEntries))
                {
                    dataEntries = new CppCollections.Dictionary<string, EventRecordModel.Entry>();
                    table[entry.EventKey] = dataEntries;
                }

                dataEntries[entry.DataKey] = new EventRecordModel.Entry
                {
                    count = count,
                    createdRealTime = 0,
                    createdGameTime = 0,
                    dataKey = entry.DataKey,
                    eventKey = entry.EventKey,
                    updatedRealTime = 0,
                    updatedGameTime = 0
                };
            }

            eventModel.NotifyParticipants();
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }
    }

    internal static List<InitialEventsRaisedPacket.Entry> GetRaisedEvents()
    {
        var entries = new List<InitialEventsRaisedPacket.Entry>();
        var table = SceneContext.Instance.eventDirector._model.table;

        var eventKeys = new List<string>();
        foreach (var dataEntry in table)
            eventKeys.Add(dataEntry.Key);

        foreach (var eventKey in eventKeys)
        {
            if (!CanSnapshotEvent(eventKey) || !ShouldSyncEventKey(eventKey))
                continue;

            if (!table.TryGetValue(eventKey, out var dataEntries))
                continue;

            foreach (var dataEntry in dataEntries)
            {
                // Unsure if we should not sync all initially once
                if (!ShouldSyncDataKey(dataEntry.Key))
                    continue;

                if (!ShouldSync(eventKey, dataEntry.Key))
                    continue;

                if (dataEntry.Value == null)
                    continue;

                if (EventSyncPolicy.IsZoneUnlockedEvent(eventKey)
                        ? !EventSyncPolicy.ShouldIncludeZoneUnlockSnapshot(dataEntry.Value.count)
                        : dataEntry.Value.count <= 0)
                    continue;

                entries.Add(new InitialEventsRaisedPacket.Entry
                {
                    EventKey = eventKey,
                    DataKey = dataEntry.Key,
                    Count = EventSyncPolicy.NormalizeTargetCount(eventKey, dataEntry.Value.count)
                });
            }
        }

        return entries;
    }
}

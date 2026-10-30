using System.Net;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Economy;
using Il2CppMonomiPark.SlimeRancher.Event;
using Il2CppMonomiPark.SlimeRancher.Pedia;
using Il2CppMonomiPark.SlimeRancher.Weather;
using SR2MP.Components.LandPlots;
using SR2MP.Components.UI;
using SR2MP.Packets;
using SR2MP.Packets.Actor;
using SR2MP.Packets.Ammo;
using SR2MP.Packets.Economy;
using SR2MP.Packets.GordoSlime;
using SR2MP.Packets.Internal;
using SR2MP.Packets.LandPlots;
using SR2MP.Packets.Loading;
using SR2MP.Packets.Slime;
using SR2MP.Packets.TreasurePod;
using SR2MP.Packets.Utils;
using SR2MP.Packets.World;
using SR2MP.Shared.Utils;

namespace SR2MP.Shared.Managers;

internal sealed class ReSyncManager
{
    private readonly Dictionary<string, DateTime> cooldowns = new();
    private static readonly TimeSpan CooldownDuration = TimeSpan.FromMinutes(2);
    private static long nextSyncId;

    private DateTime lastSynchronizeAll = DateTime.MinValue;
    private static readonly TimeSpan SynchronizeAllCooldown = TimeSpan.FromSeconds(5);

    public bool CanResync(IPEndPoint endPoint)
    {
        var key = endPoint.ToString();
        return !cooldowns.TryGetValue(key, out var last) || (DateTime.UtcNow - last) >= CooldownDuration;
    }

    public void MarkResynced(IPEndPoint endPoint)
    {
        cooldowns[endPoint.ToString()] = DateTime.UtcNow;
    }

    internal static long CreateSyncId() => Interlocked.Increment(ref nextSyncId);

    public static void SynchronizeClient(string playerId, IPEndPoint endPoint)
        => SynchronizeClient(playerId, endPoint, CreateSyncId());

    internal static void SynchronizeClient(string playerId, IPEndPoint endPoint, long syncId)
    {
        TraceJoinSync("sync-client-begin", playerId);
        TraceJoinSync("sync-client-currency-capture", playerId);
        var currencyBalances = ConnectionApprovePacket.CaptureCurrencyBalances();
        TraceJoinSync($"sync-client-currency-captured count={currencyBalances.Count}", playerId);
        var approvePacket = new ConnectionApprovePacket
        {
            InitialJoin = false,
            PlayerId = playerId,
            OtherPlayers = Array.ConvertAll(PlayerManager.GetAllPlayers().ToArray(),
                p => (p.PlayerId, p.Username)),
            CurrencyBalances = currencyBalances,
            CurrencyRevisions = ConnectionApprovePacket.CaptureCurrencyRevisions(currencyBalances.Keys),
            AllowCheats = Main.AllowCheats,
            SyncId = syncId,
            HostSessionId = Main.Server.PlayerId
        };
        TraceJoinSync("sync-client-approve-send", playerId);
        Main.Server.SendToClient(approvePacket, endPoint);

        TraceJoinSync("sync-client-plots", playerId);
        SendPlotsPacket(endPoint);
        TraceJoinSync("sync-client-garden-ownership", playerId);
        SendGardenOwnershipPacket(endPoint);
        TraceJoinSync("sync-client-gordo", playerId);
        SendGordoSlimesPacket(endPoint);
        TraceJoinSync("sync-client-switches", playerId);
        SendSwitchesPacket(endPoint);
        TraceJoinSync("sync-client-refinery", playerId);
        SendRefineryPacket(endPoint);
        TraceJoinSync("sync-client-pedia", playerId);
        SendPediaPacket(endPoint);
        TraceJoinSync("sync-client-map", playerId);
        SendMapPacket(endPoint);
        TraceJoinSync("sync-client-access-doors", playerId);
        SendAccessDoorsPacket(endPoint);
        TraceJoinSync("sync-client-resource-nodes", playerId);
        SendResourceNodesPacket(endPoint);
        NetworkVacPaletteManager.SendAllTo(endPoint);
        TraceJoinSync("sync-client-progression-unlocks", playerId);
        Main.Server.SendToClient(NetworkProgressionUnlockManager.CreateSnapshot(), endPoint);
        TraceJoinSync("sync-client-treasure-pods", playerId);
        SendTreasurePodsPacket(endPoint);
        TraceJoinSync("sync-client-upgrades", playerId);
        SendUpgradesPacket(endPoint);
        TraceJoinSync("sync-client-drone-resources", playerId);
        SendDroneResourcesPacket(endPoint);
        TraceJoinSync("sync-client-actors", playerId);
        SendActorsPacket(endPoint, PlayerIdGenerator.GetPlayerIDNumber(playerId), syncId);
        TraceJoinSync("sync-client-slime-behaviors", playerId);
        SendSlimeBehaviorPacket(endPoint);
        TraceJoinSync("sync-client-gadget-links", playerId);
        SendGadgetLinksPacket(endPoint);
        TraceJoinSync("sync-client-teleporter-links", playerId);
        SendTeleporterLinksPacket(endPoint);
        TraceJoinSync("sync-client-market-prices", playerId);
        SendPricesPacket(endPoint);
        TraceJoinSync("sync-client-shop", playerId);
        NetworkShopManager.SendSnapshot(endPoint);
        TraceJoinSync("sync-client-weather", playerId);
        SendWeatherPacket(endPoint);
        TraceJoinSync("sync-client-puzzle-slots", playerId);
        SendPuzzleSlotsPacket(endPoint);
        TraceJoinSync("sync-client-plort-depositors", playerId);
        SendPlortDepositorsPacket(endPoint);
        TraceJoinSync("sync-client-prisma-barriers", playerId);
        SendPrismaBarriersPacket(endPoint);
        TraceJoinSync("sync-client-drone-cloud", playerId);
        SendDroneCloudPacket(endPoint);
        TraceJoinSync("sync-client-components", playerId);
        SendComponentsPacket(endPoint);
        TraceJoinSync("sync-client-conversations", playerId);
        SendConversationsPacket(endPoint);
        TraceJoinSync("sync-client-raised-events", playerId);
        SendEventsRaisedPacket(endPoint);
        TraceJoinSync("sync-client-tornados", playerId);
        SendTornadosPacket(endPoint);
        TraceJoinSync("sync-client-sanctuary", playerId);
        Main.Server.SendToClient(NetworkSanctuaryManager.CreateSnapshot(), endPoint);
        TraceJoinSync("sync-client-prisma-disruption", playerId);
        if (NetworkPrismaDisruptionManager.CreateSnapshot() is { } disruptionSnapshot)
            Main.Server.SendToClient(disruptionSnapshot, endPoint);
        TraceJoinSync("sync-client-stabilizing-field", playerId);
        if (NetworkStabilizingFieldSpawnerManager.CreateSnapshot() is { } stabilizingSpawnerSnapshot)
            Main.Server.SendToClient(stabilizingSpawnerSnapshot, endPoint);

        TraceJoinSync("sync-client-complete", playerId);
        SrLogger.LogMessage($"Player {playerId} resynced!", $"Player {playerId} ({endPoint}) resynced!");
    }

    internal static void TraceJoinSync(string phase, string playerId)
    {
#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_JOIN_SYNC") == "1")
            SrLogger.LogMessage($"[SR2MP_TEST] join-sync player={playerId} phase={phase}");
#endif
    }

    public void RequestResync()
    {
        if (Main.Client.IsConnected)
        {
            NetworkActorCaptureManager.CancelClientCapturesForResync();
            var resyncPacket = new ResyncRequestPacket();
            Main.Client.SendPacket(resyncPacket);
            MultiplayerUI.Instance.RegisterSystemMessage("Resync requested...",
                $"SYSTEM_RESYNC_REQUEST_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                MultiplayerUI.SystemMessageNormal);
        }
    }

    public void LogResyncRequest(string playerId, IPEndPoint endPoint)
    {
        SrLogger.LogMessage($"Player {playerId} requested a resync", $"Player {playerId} ({endPoint}) requested a resync");
    }

    public void SynchronizeAll()
    {
        var now = DateTime.UtcNow;
        if ((now - lastSynchronizeAll) < SynchronizeAllCooldown)
        {
            SynchronizeAllCooldownMessage();
            return;
        }
        lastSynchronizeAll = now;

        var clients = Main.Server.ClientManager.GetAllClients().ToList();

        SrLogger.LogMessage("Resyncing all players...");

        foreach (var client in clients)
            SynchronizeClient(client.PlayerId, client.EndPoint);

        SrLogger.LogMessage($"Resynced {clients.Count} players!");

        var chatPacket = new ChatMessagePacket
        {
            Username = "SYSTEM",
            Message = "The server issued a resync for all players.",
            MessageID = $"SYSTEM_RESYNC_ALL_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            MessageType = MultiplayerUI.SystemMessageConnect
        };
        Main.Server.SendToAll(chatPacket);
    }

    public void SendCooldownMessage(IPEndPoint endPoint)
    {
        var chatPacket = new ChatMessagePacket
        {
            Username = "SYSTEM",
            Message = "Resync is on cooldown. Please wait before requesting another resync.",
            MessageID = $"SYSTEM_RESYNC_COOLDOWN_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            MessageType = MultiplayerUI.SystemMessageDisconnect
        };
        Main.Server.SendToClient(chatPacket, endPoint);
    }

    public void SendSuccessMessage(IPEndPoint endPoint)
    {
        var chatPacket = new ChatMessagePacket
        {
            Username = "SYSTEM",
            Message = "Your client has been resynced.",
            MessageID = $"SYSTEM_RESYNC_DONE_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            MessageType = MultiplayerUI.SystemMessageConnect
        };
        Main.Server.SendToClient(chatPacket, endPoint);
    }

    private static void SynchronizeAllCooldownMessage()
    {
        var chatPacket = new ChatMessagePacket
        {
            Username = "SYSTEM",
            Message = "Resync all is on cooldown. Please wait before resyncing all players.",
            MessageID = $"SYSTEM_RESYNCALL_COOLDOWN_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            MessageType = MultiplayerUI.SystemMessageDisconnect
        };
        MultiplayerUI.Instance.RegisterSystemMessage(chatPacket.Message, chatPacket.MessageID, chatPacket.MessageType);
    }

    private static void SendUpgradesPacket(IPEndPoint client)
        => Main.Server.SendToClient(CreateUpgradesPacket(), client);

    private static void SendSlimeBehaviorPacket(IPEndPoint client)
        => Main.Server.SendToClient(NetworkSlimeBehaviorManager.CreateSnapshot(), client);

    private static InitialUpgradesPacket CreateUpgradesPacket()
    {
        var upgrades = new Dictionary<byte, sbyte>();

        foreach (var upgrade in GameContext.Instance.LookupDirector._upgradeDefinitions.items)
        {
            upgrades.Add((byte)upgrade._uniqueId,
                (sbyte)SceneContext.Instance.PlayerState._model.upgradeModel.GetUpgradeLevel(upgrade));
        }

        return new InitialUpgradesPacket
        {
            Upgrades = upgrades,
            Revisions = NetworkUpgradeManager.CreateHostRevisionSnapshot(upgrades.Keys)
        };
    }

    private static void SendRefineryPacket(IPEndPoint client)
        => Main.Server.SendToClient(CreateRefineryPacket(), client);

    private static InitialRefineryPacket CreateRefineryPacket()
    {
        var refineryItems = new Dictionary<int, int>();

        foreach (var item in SceneContext.Instance.GadgetDirector._model._itemCounts)
        {
            var itemId = NetworkActorManager.GetPersistentID(item.Key);
            refineryItems.Add(itemId, item.Value);
        }

        return new InitialRefineryPacket
        {
            Items = refineryItems,
            Revision = NetworkRefineryManager.CurrentHostRevision
        };
    }

    private static void SendWeatherPacket(IPEndPoint client)
    {
        var weatherRegistry = Resources.FindObjectsOfTypeAll<WeatherRegistry>().FirstOrDefault();
        if (weatherRegistry == null || weatherRegistry._model == null)
        {
            SrLogger.LogError("WeatherRegistry or model not found!");
            return;
        }

        var packet = WeatherPacket.CreateFromModel(weatherRegistry._model, PacketType.InitialWeather);
        Main.Server.SendToClient(packet, client);
    }

    private static void SendPediaPacket(IPEndPoint client)
        => Main.Server.SendToClient(CreatePediaPacket(), client);

    private static InitialPediaPacket CreatePediaPacket()
    {
        var unlocked = SceneContext.Instance.PediaDirector._pediaModel.unlocked;

        var unlockedArray = Il2CppSystem.Linq.Enumerable
            .ToArray(unlocked.Cast<CppCollections.IEnumerable<PediaEntry>>());

        var unlockedIDs = unlockedArray.Select(entry => entry.TryCast<PediaEntry>()?.PersistenceId).ToList();

        return new InitialPediaPacket { Entries = unlockedIDs };
    }

    private static void SendMapPacket(IPEndPoint client)
        => Main.Server.SendToClient(CreateMapPacket(), client);

    private static InitialMapPacket CreateMapPacket()
    {
        if (!SceneContext.Instance.eventDirector._model.table.TryGetValue(MapEventKey, out var maps))
        {
            maps = new CppCollections.Dictionary<string, EventRecordModel.Entry>();
            SceneContext.Instance.eventDirector._model.table[MapEventKey] = maps;
        }

        var mapsList = new List<string?>();

        foreach (var map in maps)
        {
            if (!string.IsNullOrWhiteSpace(map.Key) && map.Value != null && map.Value.count > 0)
                mapsList.Add(map.Key);
        }

        return new InitialMapPacket { UnlockedNodes = mapsList };
    }

    private static void SendEventsRaisedPacket(IPEndPoint client)
        => Main.Server.SendToClient(CreateEventsRaisedPacket(), client);

    private static InitialEventsRaisedPacket CreateEventsRaisedPacket()
        => new() { Entries = NetworkEventManager.GetRaisedEvents() };

    private static void SendAccessDoorsPacket(IPEndPoint client)
        => Main.Server.SendToClient(CreateAccessDoorsPacket(), client);

    private static InitialAccessDoorsPacket CreateAccessDoorsPacket()
    {
        var accessDoorsList = new List<InitialAccessDoorsPacket.Door>();

        foreach (var door in GameState.doors)
        {
            accessDoorsList.Add(new InitialAccessDoorsPacket.Door
            {
                ID = door.Key,
                State = door.Value.state
            });
        }

        return new InitialAccessDoorsPacket { Doors = accessDoorsList };
    }
    
    private static void SendResourceNodesPacket(IPEndPoint client)
        => Main.Server.SendToClient(CreateResourceNodesPacket(), client);
    
    private static InitialResourceNodesPacket CreateResourceNodesPacket()
    {
        var nodesList = new List<ResourceNodePlacement>();

        var spawners = Il2CppSystem.Linq.Enumerable
            .ToArray(GameState.AllResourceNodes()
                .Cast<Il2CppSystem.Collections.Generic.IEnumerable<ResourceNodeSpawnerModel>>());

        foreach (var spawner in spawners)
        {
            if (spawner == null)
                continue;

            nodesList.Add(ResourceNodeManager.CreatePlacement(spawner));
        }

        return new InitialResourceNodesPacket { Nodes = nodesList };
    }

    private static void SendPuzzleSlotsPacket(IPEndPoint client)
        => Main.Server.SendToClient(CreatePuzzleSlotsPacket(), client);

    private static InitialPuzzleSlotsPacket CreatePuzzleSlotsPacket()
    {
        var slotsList = new List<InitialPuzzleSlotsPacket.Slot>();

        foreach (var slot in GameState.slots)
        {
            slotsList.Add(new InitialPuzzleSlotsPacket.Slot
            {
                ID = slot.Key,
                Filled = slot.Value.filled
            });
        }

        foreach (var pending in NetworkPuzzleSlotManager.PendingSlotStates)
        {
            if (GameState.slots.ContainsKey(pending.Key))
                continue;

            slotsList.Add(new InitialPuzzleSlotsPacket.Slot
            {
                ID = pending.Key,
                Filled = pending.Value
            });
        }

        return new InitialPuzzleSlotsPacket { Slots = slotsList };
    }

    private static void SendPlortDepositorsPacket(IPEndPoint client)
        => Main.Server.SendToClient(CreatePlortDepositorsPacket(), client);

    private static InitialPlortDepositorsPacket CreatePlortDepositorsPacket()
    {
        var depositorsList = new List<InitialPlortDepositorsPacket.Depositor>();

        foreach (var depositor in GameState.depositors)
        {
            depositorsList.Add(new InitialPlortDepositorsPacket.Depositor
            {
                ID = depositor.Key,
                AmountDeposited = depositor.Value.AmountDeposited
            });
        }

        foreach (var pending in NetworkPlortDepositorManager.PendingDepositorStates)
        {
            if (GameState.depositors.ContainsKey(pending.Key))
                continue;

            depositorsList.Add(new InitialPlortDepositorsPacket.Depositor
            {
                ID = pending.Key,
                AmountDeposited = pending.Value
            });
        }

        return new InitialPlortDepositorsPacket { Depositors = depositorsList };
    }

    private static void SendPrismaBarriersPacket(IPEndPoint client)
        => Main.Server.SendToClient(CreatePrismaBarriersPacket(), client);

    private static InitialPrismaBarriersPacket CreatePrismaBarriersPacket()
    {
        var barriersList = new List<InitialPrismaBarriersPacket.Barrier>();

        foreach (var barrier in GameState.AllPrismaBarriers())
        {
            barriersList.Add(new InitialPrismaBarriersPacket.Barrier
            {
                ID = barrier.Key,
                ActivationTime = barrier.Value.ActivationTime
            });
        }

        return new InitialPrismaBarriersPacket { Barriers = barriersList };
    }

    private static void SendActorsPacket(IPEndPoint client, ushort playerIndex, long syncId)
    {
        // SynchronizeClient is invoked by the Unity-main-thread connection/resync
        // handlers. Actor mutation hooks and inbound server handlers also run on
        // that thread, so no actor mutation/send can interleave with this
        // enumeration and the watermark read below.
        var actorsList = new List<InitialActorsPacket.ActorBase>();
        var ownership = new Dictionary<long, InitialActorsPacket.OwnershipEntry>();

        foreach (var (_, model) in ActorManager.Actors)
        {
            // Player models are already represented by PlayerManager/NetworkPlayer;
            // serializing them as actors collides with the joining local player model.
            if (model.ident?.IsPlayer == true)
                continue;

            if (model.TryCast<GadgetModel>(out _))
                continue;
            
            if (NetworkDroneManager.IsDroneModel(model))
                continue;

            actorsList.Add(NetworkActorManager.CreateInitialActor(model));
            ownership[model.actorId.Value] = new InitialActorsPacket.OwnershipEntry(
                GetSnapshotOwner(model), ActorManager.GetEpoch(model.actorId.Value));
        }

        foreach (var model in GameState.AllGadgets())
        {
            if (!model.TryCast<GadgetModel>(out var gadget))
                continue;

            actorsList.Add(NetworkActorManager.CreateInitialGadget(gadget));
            ownership[gadget.actorId.Value] = new InitialActorsPacket.OwnershipEntry(
                GetSnapshotOwner(gadget), ActorManager.GetEpoch(gadget.actorId.Value));
        }

        var actorsPacket = new InitialActorsPacket
        {
            StartingActorID =
                NetworkActorManager.GetHighestActorIdInRange((long)playerIndex * ActorIdOffset,
                    ((long)playerIndex * ActorIdOffset) + ActorIdOffset) + 10,
            Actors = actorsList,
            WorldTime = GameState.world.worldTime,
            HasOwnershipSnapshot = true,
            Ownership = ownership,
            HasWorldRevision = true,
            WorldRevision = ActorWorldRevision.Current,
            SyncId = syncId
        };

        Main.Server.SendToClient(actorsPacket, client);
    }

    private static string GetSnapshotOwner(IdentifiableModel model)
    {
        if (model.TryGetNetworkComponent(out var networkActor) &&
            !string.IsNullOrWhiteSpace(networkActor.CurrentOwnerId))
            return networkActor.CurrentOwnerId;

        if (ActorManager.OwnershipByActorId.TryGetValue(model.actorId.Value, out var cachedOwnerId) &&
            !string.IsNullOrWhiteSpace(cachedOwnerId))
            return cachedOwnerId;

        return LocalID;
    }

    private static void SendGadgetLinksPacket(IPEndPoint client)
    {
        var packet = new InitialGadgetLinksPacket { Links = NetworkGadgetManager.GetAllGadgetLinks() };
        Main.Server.SendToClient(packet, client);
    }

    private static void SendTeleporterLinksPacket(IPEndPoint client)
    {
        var packet = new InitialTeleporterLinksPacket { Links = NetworkGadgetManager.GetAllTeleporterLinks() };
        Main.Server.SendToClient(packet, client);
    }

    private static void SendTornadosPacket(IPEndPoint client)
        => NetworkTornadoManager.SendTornadosTo(client);

    private static void SendDroneCloudPacket(IPEndPoint client)
    {
        var cloud = GameState.droneModel?.GetCloudModel();
        var packet = new InitialDroneCloudPacket();

        if (cloud?._amounts != null)
        {
            foreach (var pair in cloud._amounts)
            {
                if (pair.Key != null)
                    packet.Amounts[NetworkActorManager.GetPersistentID(pair.Key)] = pair.Value;
            }
        }

        Main.Server.SendToClient(packet, client);
    }

    private static void SendDroneResourcesPacket(IPEndPoint client)
    {
        var packet = NetworkDroneManager.CreateInitialPacket();

        if (packet.Scenes.Count > 0)
            Main.Server.SendToClient(packet, client);
    }
    
    private static void SendComponentsPacket(IPEndPoint client)
        => Main.Server.SendToClient(CreateComponentsPacket(), client);

    private static InitialComponentsPacket CreateComponentsPacket()
    {
        var componentItems = new Dictionary<string, byte>();

        foreach (var item in GameState.PlayerModel.UpgradeComponentsModel._ownedComponents)
            componentItems.Add(item.Key._referenceId, (byte)item.Value);

        return new InitialComponentsPacket { Items = componentItems };
    }

    private static void SendConversationsPacket(IPEndPoint client)
        => Main.Server.SendToClient(
            new InitialConversationsPacket { ConversationNames = NetworkConversationManager.GetPlayedConversations() },
            client);

    private static void SendSwitchesPacket(IPEndPoint client)
        => Main.Server.SendToClient(CreateSwitchesPacket(), client);

    private static InitialSwitchesPacket CreateSwitchesPacket()
    {
        var switchesList = new List<InitialSwitchesPacket.Switch>();

        foreach (var switchKeyValuePair in GameState.switches)
        {
            switchesList.Add(new InitialSwitchesPacket.Switch
            {
                ID = switchKeyValuePair.key,
                State = switchKeyValuePair.value.state
            });
        }

        return new InitialSwitchesPacket { Switches = switchesList };
    }

    private static void SendGordoSlimesPacket(IPEndPoint client)
        => Main.Server.SendToClient(CreateGordoSlimesPacket(), client);

    private static InitialGordoSlimesPacket CreateGordoSlimesPacket()
    {
        var gordoSlimeList = new List<InitialGordoSlimesPacket.GordoSlime>();

        foreach (var gordoSlime in GameState.gordos)
        {
            if (!gordoSlime.value.identifiableType)
            {
                SrLogger.LogWarning($"CreateGordoSlimesPacket: skipping Gordo '{gordoSlime.key}' with no identifiable type.");
                continue;
            }

            var eatCount = gordoSlime.value.GordoEatenCount;
            if (eatCount == -1)
                eatCount = gordoSlime.value.targetCount;
            var popped = NetworkGordoSlimeManager.HasPopped(gordoSlime.key) || gordoSlime.value.HasPopped();
            if (popped)
                eatCount = Math.Max(eatCount, gordoSlime.value.targetCount);

            gordoSlimeList.Add(new InitialGordoSlimesPacket.GordoSlime
            {
                Id = gordoSlime.key,
                EatenCount = eatCount,
                RequiredEatCount = gordoSlime.value.targetCount,
                GordoSlimeType = NetworkActorManager.GetPersistentID(gordoSlime.value.identifiableType),
                WasSeen = gordoSlime.value.GordoSeen,
                Popped = popped,
                ActiveRewards = popped
                    ? NetworkGordoSlimeManager.CaptureActiveRewardSnapshots(gordoSlime.key)
                    : new List<GordoSlimeRewardPacket>()
            });
        }

        return new InitialGordoSlimesPacket { GordoSlimes = gordoSlimeList };
    }

    private static void SendPlotsPacket(IPEndPoint client)
        => Main.Server.SendToClient(CreatePlotsPacket(), client);

    private static void SendGardenOwnershipPacket(IPEndPoint client)
    {
        var packet = NetworkGardenManager.CreateOwnershipSnapshot();
        SrLogger.LogGarden($"Sending initial garden ownership snapshot entries={packet.Entries.Count}");
        if (packet.Entries.Count > 0)
            Main.Server.SendToClient(packet, client);
    }

    private static InitialLandPlotsPacket CreatePlotsPacket()
    {
        var landPlotsList = new List<InitialLandPlotsPacket.BasePlot>();

        foreach (var (id, plot) in GameState.landPlots)
        {
            var gardenData = plot.typeId == LandPlot.Id.GARDEN
                ? CreateGardenData(id, plot.resourceGrowerDefinition?._primaryResourceType, plot.gameObj)
                : (InitialLandPlotsPacket.GardenData?)null;

            INetObject? data = plot.typeId switch
            {
                LandPlot.Id.GARDEN => gardenData!.Value,
                LandPlot.Id.POND => new InitialLandPlotsPacket.CoopPondData
                {
                    CollectorAmmo = SerializeAmmo(plot.siloAmmo, PlortCollectorAmmo)
                },
                LandPlot.Id.COOP => new InitialLandPlotsPacket.CoopPondData
                {
                    CollectorAmmo = SerializeAmmo(plot.siloAmmo, CoopAmmo)
                },
                LandPlot.Id.INCINERATOR => new InitialLandPlotsPacket.IncineratorData
                {
                    AshLevel = plot.ashUnits,
                    PlortCollectorAmmo = SerializeAmmo(plot.siloAmmo, PlortCollectorAmmo)
                },
                LandPlot.Id.SILO => new InitialLandPlotsPacket.SiloData
                {
                    SelectedSlots = plot.siloStorageIndices.ToList().ConvertAll(val => (byte)val),
                    Ammo = SerializeAmmo(plot.siloAmmo, SiloAmmo)
                },
                LandPlot.Id.CORRAL => new InitialLandPlotsPacket.CorralData
                {
                    AutoFeederSpeed = (byte)plot.feederCycleSpeed,
                    PlortCollectorAmmo = SerializeAmmo(plot.siloAmmo, PlortCollectorAmmo),
                    AutoFeederAmmo = SerializeAmmo(plot.siloAmmo, FeederAmmo)
                },
                _ => null
            };

            landPlotsList.Add(new InitialLandPlotsPacket.BasePlot
            {
                ID = id,
                Type = plot.typeId,
                Upgrades = plot.upgrades,
                Data = data
            });
        }

        return new InitialLandPlotsPacket { LandPlots = landPlotsList };
    }

    private static InitialLandPlotsPacket.GardenData CreateGardenData(
        string plotId, IdentifiableType? cropType, GameObject? plotObject)
    {
        const int EmptyCrop = 9;
        const int UnknownCrop = int.MinValue;

        var cropId = cropType == null ? EmptyCrop : UnknownCrop;
        if (cropType != null)
        {
            try
            {
                cropId = NetworkActorManager.GetPersistentID(cropType);
            }
            catch (Exception exception)
            {
                SrLogger.LogWarning($"InitialLandPlots: could not identify garden crop in plot '{plotId}': {exception.Message}");
            }
        }

        // Garden simulation continues while its region is hibernated. The default overload
        // excludes inactive descendants and would serialize zeroes for those plots.
        var spawnResource = plotObject
            ? plotObject.GetComponentInChildren<SpawnResource>(true)
            : null;
        var networkGarden = spawnResource ? spawnResource.GetComponent<NetworkGarden>() : null;
        var snapshot = NetworkGardenManager.CreateSnapshotState(
            plotId, networkGarden, spawnResource, cropId != EmptyCrop && cropId != UnknownCrop);
        NetworkGardenManager.RecordSnapshotCrop(snapshot.Entry.GardenId, snapshot.Entry.Revision, cropId);

        return new InitialLandPlotsPacket.GardenData
        {
            Crop = cropId,
            NextSpawnTime = snapshot.Entry.NextSpawnTime,
            StoredWater = snapshot.Entry.StoredWater,
            NextSpawnRipens = snapshot.Entry.NextSpawnRipens,
            GardenId = snapshot.Entry.GardenId,
            OwnerId = NetworkGardenManager.GetOwnerForSnapshot(snapshot.Entry.GardenId, networkGarden),
            Revision = snapshot.Entry.Revision,
            HasState = snapshot.HasState
        };
    }

    private static void SendPricesPacket(IPEndPoint client)
        => Main.Server.SendToClient(CreatePricesPacket(), client);

    private static MarketPricePacket CreatePricesPacket()
        => new MarketPricePacket { Prices = MarketPricesArray! };

    private static void SendTreasurePodsPacket(IPEndPoint client)
        => Main.Server.SendToClient(CreateTreasurePodsPacket(), client);

    private static InitialTreasurePodsPacket CreateTreasurePodsPacket()
    {
        var treasurePods = new Dictionary<int, TreasurePod.State>();

        foreach (var treasurePod in GameState.pods)
        {
            if (int.TryParse(treasurePod.key.Replace("pod", string.Empty), out var podId))
                treasurePods.Add(podId, treasurePod.value.state);
        }

        var packet = new InitialTreasurePodsPacket { TreasurePods = treasurePods };
        NetworkTreasurePodManager.PopulateInitialRewardSnapshot(packet);
        return packet;
    }
    
    private static NetworkAmmo SerializeAmmo(
        CppCollections.Dictionary<string, AmmoModel> siloAmmo,
        string ammoKey)
    {
        if (!siloAmmo.ContainsKey(ammoKey))
            return new NetworkAmmo { AmmoSlots = new Dictionary<int, NetworkAmmoSlot>() };

        var slots = siloAmmo[ammoKey].Slots;
        var ammoSlots = new Dictionary<int, NetworkAmmoSlot>();

        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];

            if (slot == null)
                continue;

            if (slot.Definition == null || slot.Definition.name == null)
                continue;

            ammoSlots[i] = new NetworkAmmoSlot
            {
                Count = slot.Count,
                MaxCount = slot.MaxCount,
                Identifiable = NetworkActorManager.GetPersistentID(slot._id),
                SlotDefinition = NetworkAmmoManager.GetId(slot.Definition),
                Radiant = slot.Radiant
            };
        }

        return new NetworkAmmo { AmmoSlots = ammoSlots };
    }
}

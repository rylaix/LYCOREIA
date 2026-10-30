using System.Net;
using SR2MP.Components.LandPlots;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.LandPlots;
using SR2MP.Packets.Loading;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.LandPlots;

// todo: review
// not sure about the whole coroutine and inactive stuff

[PacketHandler((byte)PacketType.InitialLandPlots, HandlerType.Client)]
internal sealed class InitialLandPlotsHandler : BasePacketHandler<InitialLandPlotsPacket>
{
    protected override bool Handle(InitialLandPlotsPacket packet, IPEndPoint? _)
    {
        var initializedMissingModels = 0;
        var unresolvedModels = 0;

        foreach (var plot in packet.LandPlots)
        {
            if (!GameState.landPlots.TryGetValue(plot.ID, out var model) || model == null)
            {
                try
                {
                    // The guest may have loaded a less advanced save than the host.
                    // Create the missing model now; LandPlotLocation.RegisterLandPlot
                    // binds its scene object when that area is loaded later.
                    model = GameState.InitializeLandPlotModel(plot.ID);
                }
                catch (Exception ex)
                {
                    unresolvedModels++;
                    SrLogger.LogWarning(
                        $"InitialLandPlots: could not initialize host plot '{plot.ID}': {ex.Message}");
                    continue;
                }

                if (model == null)
                {
                    unresolvedModels++;
                    SrLogger.LogWarning(
                        $"InitialLandPlots: host plot '{plot.ID}' could not be initialized; skipping it.");
                    continue;
                }

                initializedMissingModels++;
#if SR2MP_TEST
                if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_LANDPLOTS") == "1")
                    SrLogger.LogMessage(
                        $"[SR2MP_TEST] land-plot model initialized from host snapshot id={plot.ID}");
#endif
            }

            if (model.gameObj)
            {
                var wasHandlingPacket = HandlingPacket;
                HandlingPacket = true;
                try
                {
                    var location = model.gameObj.GetComponent<LandPlotLocation>();
                    var landPlotComponent = model.gameObj.GetComponentInChildren<LandPlot>(true);
                    var plotPrefabs = GameContext.Instance.LookupDirector._plotPrefabDict;
                    if (location && landPlotComponent && plotPrefabs.ContainsKey(plot.Type))
                    {
                        location.Replace(landPlotComponent, plotPrefabs[plot.Type]);
                        var replacement = model.gameObj.GetComponentInChildren<LandPlot>(true);
                        if (replacement)
                            replacement.ApplyUpgrades(plot.Upgrades.Cast<CppCollections.IEnumerable<LandPlot.Upgrade>>(), false);
                    }
                    else
                    {
                        SrLogger.LogWarning($"InitialLandPlots: plot prefab or component missing for '{plot.ID}' ({plot.Type}).");
                    }
                }
                finally
                {
                    HandlingPacket = wasHandlingPacket;
                }
            }

            model.typeId = plot.Type;
            model.upgrades = plot.Upgrades;

            if (plot.Data is InitialLandPlotsPacket.GardenData gardenSnapshot)
            {
                NetworkGardenManager.ApplySnapshotOwnership(gardenSnapshot.GardenId, gardenSnapshot.OwnerId);
                NetworkGardenManager.ApplySnapshotState(
                    plot.ID, ToGardenUpdate(gardenSnapshot), gardenSnapshot.HasState);

                // A runtime plant/clear delta can be delivered before this plot model exists.
                // Preserve its newer crop revision instead of letting the join snapshot roll it back.
                gardenSnapshot.Crop = NetworkGardenManager.SelectSnapshotCrop(
                    gardenSnapshot.GardenId, gardenSnapshot.Revision, gardenSnapshot.Crop) ?? int.MinValue;
                plot.Data = gardenSnapshot;
            }

            switch (plot.Data)
            {
                case InitialLandPlotsPacket.GardenData { Crop: 9 } garden:
                {
                    model.resourceGrowerDefinition = null;
                    if (!model.gameObj)
                        continue;
                    var gardenPlot = model.gameObj.GetComponentInChildren<LandPlot>(true);
                    var wasHandlingPacket = HandlingPacket;
                    HandlingPacket = true;
                    try
                    {
                        if (gardenPlot)
                            gardenPlot.DestroyAttached();
                    }
                    finally
                    {
                        HandlingPacket = wasHandlingPacket;
                    }
                    break;
                }

                case InitialLandPlotsPacket.GardenData unknownCrop when unknownCrop.Crop == int.MinValue:
                {
                    SrLogger.LogWarning($"InitialLandPlots: could not resolve host crop for garden plot '{plot.ID}'; preserving the local crop.");
                    break;
                }

                case InitialLandPlotsPacket.GardenData garden:
                {
                    if (!ActorManager.ActorTypes.TryGetValue(garden.Crop, out var actor) || actor == null)
                    {
                        SrLogger.LogWarning($"InitialLandPlots: host crop ID {garden.Crop} is unknown for garden plot '{plot.ID}'; preserving the local crop.");
                        break;
                    }

                    var saveReferenceTranslation = GameContext.Instance?.AutoSaveDirector?._saveReferenceTranslation;
                    var translation = saveReferenceTranslation?._resourceGrowerTranslation?.RawLookupDictionary;
                    if (translation == null)
                    {
                        SrLogger.LogWarning($"InitialLandPlots: resource grower translation is unavailable for garden plot '{plot.ID}'.");
                        break;
                    }

                    var growerDefinition = translation._entries
                        .FirstOrDefault(x => x.value != null && x.value._primaryResourceType == actor).value;
                    if (growerDefinition == null)
                    {
                        SrLogger.LogWarning($"InitialLandPlots: no grower definition for host crop ID {garden.Crop} in plot '{plot.ID}'.");
                        break;
                    }

                    if (!model.gameObj)
                    {
                        model.resourceGrowerDefinition = growerDefinition;
                        continue;
                    }

                    var gardenPlot = model.gameObj.GetComponentInChildren<LandPlot>(true);
                    var existingSpawner = model.gameObj.GetComponentInChildren<SpawnResource>(true);
                    var existingCrop = existingSpawner?._resourceGrowerDefinition?._primaryResourceType;

                    var wasHandlingPacket = HandlingPacket;
                    HandlingPacket = true;
                    try
                    {
                        if (existingCrop != actor)
                            gardenPlot?.DestroyAttached();

                        model.resourceGrowerDefinition = growerDefinition;

                        var gardenCatcher = model.gameObj.GetComponentInChildren<GardenCatcher>(true);
                        if (gardenCatcher && gardenCatcher.CanAccept(actor))
                        {
                            var plantedObject = gardenCatcher.Plant(actor, true);

                            if (plantedObject != null)
                            {
                                var spawnResource = plantedObject.GetComponent<SpawnResource>();
                                if (spawnResource != null && spawnResource._model != null)
                                {
                                    foreach (var joint in spawnResource.SpawnJoints)
                                    {
                                        if (joint == null || joint.connectedBody == null)
                                            continue;

                                        var connectedObj = joint.connectedBody.gameObject;
                                        Destroyer.Destroy(connectedObj, "InitialLandPlotsHandler.OnDestroy");
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        HandlingPacket = wasHandlingPacket;
                    }
                    
                    break;
                }

                case InitialLandPlotsPacket.SiloData silo:
                {
                    var ammo = silo.Ammo.ToGameAmmo();
                    model.siloAmmo[SiloAmmo] = ammo._ammoModel;
                    model.siloStorageIndices = Array.ConvertAll(silo.SelectedSlots.ToArray(), input => (int)input);

                    if (!model.gameObj) break;

                    var storage = model.gameObj.GetComponentInChildren<SiloStorage>();
                    storage.Ammo = ammo;
                    storage.SetModel(model);
                    storage.Ammo.ApplySlotData(silo.Ammo);

                    foreach (var activator in model.gameObj.GetComponentsInChildren<SiloStorageActivator>())
                        activator.OnActiveSlotChanged();

                    break;
                }

                case InitialLandPlotsPacket.CoopPondData pond:
                {
                    var ammoType = plot.Type == LandPlot.Id.COOP
                        ? CoopAmmo
                        : PlortCollectorAmmo;
                    var collectorAmmo = pond.CollectorAmmo!.ToGameAmmo();
                    model.siloAmmo[ammoType] = collectorAmmo._ammoModel;

                    if (!model.gameObj) break;

                    var pondStorage = model.gameObj.GetComponentInChildren<SiloStorage>(true);
                    if (pondStorage)
                    {
                        pondStorage.Ammo = collectorAmmo;
                        pondStorage.SetModel(model);
                        pondStorage.Ammo.ApplySlotData(pond.CollectorAmmo);
                    }
                    break;
                }

                case InitialLandPlotsPacket.CorralData corral:
                {
                    var plortCollectorAmmo = corral.PlortCollectorAmmo.ToGameAmmo();
                    model.siloAmmo[PlortCollectorAmmo] = plortCollectorAmmo._ammoModel;

                    var feederAmmo = corral.AutoFeederAmmo.ToGameAmmo();
                    model.siloAmmo[FeederAmmo] = feederAmmo._ammoModel;

                    model.feederCycleSpeed = (SlimeFeeder.FeedSpeed)corral.AutoFeederSpeed;

                    if (!model.gameObj) break;

                    foreach (var corralStorage in model.gameObj.GetComponentsInChildren<SiloStorage>(true))
                    {
                        switch (corralStorage.AmmoSetReference.Guid)
                        {
                            case PlortCollectorAmmo:
                                corralStorage.Ammo = plortCollectorAmmo;
                                corralStorage.SetModel(model);
                                corralStorage.Ammo.ApplySlotData(corral.PlortCollectorAmmo);
                                break;

                            case FeederAmmo:
                                corralStorage.Ammo = feederAmmo;
                                corralStorage.SetModel(model);
                                corralStorage.Ammo.ApplySlotData(corral.AutoFeederAmmo);
                                break;
                        }
                    }

                    var feeder = model.gameObj.GetComponentInChildren<FeederUpgrader>(true)
                        .Feeder
                        .transform
                        .GetChild(0)
                        .GetComponent<SlimeFeeder>();

                    if (!feeder)
                    {
                        SrLogger.LogError($"Feeder for {plot.ID} is null, tried to set it to {(SlimeFeeder.FeedSpeed)corral.AutoFeederSpeed}");
                        break;
                    }

                    var wasHandlingPacket = HandlingPacket;
                    HandlingPacket = true;
                    try
                    {
                        feeder?.SetFeederSpeed((SlimeFeeder.FeedSpeed)corral.AutoFeederSpeed);
                        feeder?.SetFeederSpeedIcon((SlimeFeeder.FeedSpeed)corral.AutoFeederSpeed);
                    }
                    finally
                    {
                        HandlingPacket = wasHandlingPacket;
                    }

                    break;
                }

                case InitialLandPlotsPacket.IncineratorData incinerator:
                {
                    var incineratorAmmo = incinerator.PlortCollectorAmmo!.ToGameAmmo();
                    model.siloAmmo[PlortCollectorAmmo] = incineratorAmmo._ammoModel;
                    try { model.ashUnits = incinerator.AshLevel; } catch { /* ignored */ }

                    if (!model.gameObj) break;

                    var incineratorStorage = model.gameObj.GetComponentInChildren<SiloStorage>(true);
                    if (incineratorStorage)
                    {
                        incineratorStorage.Ammo = incineratorAmmo;
                        incineratorStorage.SetModel(model);
                        incineratorStorage.Ammo.ApplySlotData(incinerator.PlortCollectorAmmo);
                    }

                    model.gameObj.GetComponentInChildren<FillableAshSource>().SetModel(model);
                    break;
                }
            }
        }

#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_LANDPLOTS") == "1")
            SrLogger.LogMessage(
                $"[SR2MP_TEST] land-plots snapshot={packet.LandPlots.Count} " +
                $"initializedMissing={initializedMissingModels} unresolved={unresolvedModels} " +
                $"localModels={GameState.landPlots.Count}");
#endif

        return false;
    }

    private static GardenUpdatePacket.Entry ToGardenUpdate(InitialLandPlotsPacket.GardenData garden)
        => new()
        {
            GardenId = garden.GardenId,
            NextSpawnTime = garden.NextSpawnTime,
            StoredWater = garden.StoredWater,
            NextSpawnRipens = garden.NextSpawnRipens,
            Revision = garden.Revision
        };
}

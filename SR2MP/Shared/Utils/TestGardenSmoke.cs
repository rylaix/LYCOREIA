#if SR2MP_TEST
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Shared.Managers;

namespace SR2MP.Shared.Utils;

/// <summary>
/// Opt-in test fixture/action for one native GardenCatcher.Plant call on a joined
/// guest. The normal PlantGarden postfix and GardenPlant packet path remain in use.
/// </summary>
internal static class TestGardenSmoke
{
    private static int lastActionSequence;
    private static int lastActionSequenceBefore;
    private static int lastActionSequenceAfter;
    private static string lastActionState = "none";
    private static string? lastActionError;
    private static string? lastPlotId;
    private static int? lastCropTypeKey;
    private static bool? lastNativePlantReturnedCrop;
    private static int fixtureSequence;
    private static bool fixtureAttempted;
    private static string fixtureState = "none";
    private static string? fixtureError;
    private static string? fixturePlotId;

    internal static TestGardenSmokeState ReadState()
    {
        var state = CreateBaseState();
        state.Plots = ReadLoadedPlots();
        if (!string.IsNullOrWhiteSpace(lastPlotId))
        {
            state.Ready = TryReadPlot(lastPlotId!, out var modelCrop, out var physicalCrop, out var priorPlotError);
            state.ReadinessError = priorPlotError;
            state.PlotId = lastPlotId;
            state.CropTypeKey = lastCropTypeKey;
            state.ModelCropTypeKey = modelCrop;
            state.PhysicalCropTypeKey = physicalCrop;
        }
        else if (TryFindEmptyTarget(out var plotId, out _, out var cropKey, out _, out var targetError))
        {
            state.Ready = true;
            state.PlotId = plotId;
            state.CropTypeKey = cropKey;
            state.ModelCropTypeKey = null;
            state.PhysicalCropTypeKey = null;
        }
        else
        {
            state.ReadinessError = targetError;
        }

        CopyLastAction(state);
        return state;
    }

    /// <summary>
    /// Test-only free fixture: use the game's native plot replacement on one existing
    /// EMPTY plot. This is not a purchase and does not change currency or inventory.
    /// </summary>
    internal static TestGardenFixtureState PrepareHostFixture()
    {
        if (fixtureAttempted)
            return ReadFixtureState();

        fixtureAttempted = true;
        fixtureSequence++;
        fixtureState = "validating";
        fixtureError = null;
        fixturePlotId = null;

        if (!Main.Server.IsRunning)
            return FailFixture("fixture requires the host server to be running");

        if (HandlingPacket)
            return FailFixture("fixture refused while HandlingPacket is true");

        var system = SystemContext.Instance;
        if (!system || system.SceneLoader == null || system.SceneLoader.IsSceneLoadInProgress ||
            !system.SceneLoader.IsCurrentSceneGroupGameplay())
            return FailFixture("gameplay scene is unavailable or still loading");

        if (TryFindEmptyGardenPlot(out var existingGardenId))
        {
            fixturePlotId = existingGardenId;
            fixtureState = "existing-empty-garden-available-no-change";
            return ReadFixtureState();
        }

        if (!TryFindEmptyPlotLocation(out var plotId, out var location, out var emptyLandPlot, out var error))
            return FailFixture(error);

        fixturePlotId = plotId;
        var lookup = GameContext.Instance?.LookupDirector;
        var plotPrefabs = lookup?._plotPrefabDict;
        if (plotPrefabs == null || !plotPrefabs.ContainsKey(LandPlot.Id.GARDEN))
            return FailFixture("native plot prefab lookup has no GARDEN entry");

        var gardenPrefab = plotPrefabs[LandPlot.Id.GARDEN];
        var prefabPlot = gardenPrefab ? gardenPrefab.GetComponent<LandPlot>() : null;
        if (!gardenPrefab || !prefabPlot || prefabPlot.TypeId != LandPlot.Id.GARDEN)
            return FailFixture("native GARDEN prefab is unavailable or has the wrong LandPlot type");

        fixtureState = "calling-native-empty-plot-replace";
        try
        {
            // This is deliberately a free test fixture, not BuyPlot: the normal
            // ReplaceLandPlot postfix emits NewLandPlotPacket for peer replication.
            var replacement = location.Replace(emptyLandPlot, gardenPrefab);
            var replacementPlot = replacement ? replacement.GetComponent<LandPlot>() : null;
            if (!replacement || !replacementPlot || replacementPlot.TypeId != LandPlot.Id.GARDEN)
                return FailFixture("native Replace returned without creating a GARDEN plot");

            fixtureState = "native-replace-completed-awaiting-peer-state";
            return ReadFixtureState();
        }
        catch (Exception exception)
        {
            return FailFixture($"LandPlotLocation.Replace threw {exception.GetType().Name}: {exception.Message}");
        }
    }

    internal static TestGardenFixtureState ReadFixtureState()
        => new()
        {
            FixtureSequence = fixtureSequence,
            FixtureState = fixtureState,
            FixtureError = fixtureError,
            FixturePlotId = fixturePlotId,
            FixtureMode = "free-native-replace-no-purchase"
        };

    private static TestGardenFixtureState FailFixture(string error)
    {
        fixtureState = "failed";
        fixtureError = error;
        return ReadFixtureState();
    }

    private static bool TryFindEmptyGardenPlot(out string plotId)
    {
        plotId = string.Empty;
        foreach (var id in GetSortedPlotIds())
        {
            if (!GameState.landPlots.TryGetValue(id, out var model) || model == null ||
                model.typeId != LandPlot.Id.GARDEN || !model.gameObj ||
                model.resourceGrowerDefinition != null)
                continue;

            var location = model.gameObj.GetComponent<LandPlotLocation>();
            var landPlot = model.gameObj.GetComponentInChildren<LandPlot>(true);
            var garden = model.gameObj.GetComponentInChildren<GardenCatcher>(true);
            if (!location || !string.Equals(location._id, id, StringComparison.Ordinal) ||
                !landPlot || landPlot.TypeId != LandPlot.Id.GARDEN || !garden ||
                model.gameObj.GetComponentInChildren<SpawnResource>(true))
                continue;

            plotId = id;
            return true;
        }

        return false;
    }

    private static bool TryFindEmptyPlotLocation(
        out string plotId,
        out LandPlotLocation location,
        out LandPlot landPlot,
        out string error)
    {
        plotId = string.Empty;
        location = null!;
        landPlot = null!;
        error = "no loaded empty LandPlot location with no attached crop is available";

        foreach (var id in GetSortedPlotIds())
        {
            if (!GameState.landPlots.TryGetValue(id, out var model) || model == null ||
                model.typeId != LandPlot.Id.EMPTY || !model.gameObj ||
                model.resourceGrowerDefinition != null)
                continue;

            var candidateLocation = model.gameObj.GetComponent<LandPlotLocation>();
            var candidatePlot = model.gameObj.GetComponentInChildren<LandPlot>(true);
            if (!candidateLocation || !string.Equals(candidateLocation._id, id, StringComparison.Ordinal) ||
                !candidatePlot || candidatePlot.TypeId != LandPlot.Id.EMPTY ||
                model.gameObj.GetComponentInChildren<SpawnResource>(true))
                continue;

            plotId = id;
            location = candidateLocation;
            landPlot = candidatePlot;
            error = string.Empty;
            return true;
        }

        return false;
    }

    /// <summary>Calls the native planting method exactly once after validating an empty plot.</summary>
    internal static TestGardenSmokeState ApplyGuestPlant()
    {
        lastActionSequenceBefore = lastActionSequence;
        lastActionSequenceAfter = ++lastActionSequence;
        lastPlotId = null;
        lastCropTypeKey = null;
        lastNativePlantReturnedCrop = null;
        lastActionState = "validating";
        lastActionError = null;

        if (!Main.Client.IsConnected || !Main.Client.IsJoined || Main.Server.IsRunning)
            return FailAction("action requires a connected, joined guest");

        if (HandlingPacket)
            return FailAction("action refused while HandlingPacket is true");

        var system = SystemContext.Instance;
        if (!system || system.SceneLoader == null || system.SceneLoader.IsSceneLoadInProgress ||
            !system.SceneLoader.IsCurrentSceneGroupGameplay())
            return FailAction("gameplay scene is unavailable or still loading");

        if (!TryFindEmptyTarget(out var plotId, out var cropType, out var cropKey,
                out var catcher, out var error))
            return FailAction(error);

        lastPlotId = plotId;
        lastCropTypeKey = cropKey;
        lastActionState = "calling-native-plant";
        try
        {
            // Do not set HandlingPacket: PlantGarden's production postfix must publish
            // this normal native mutation through the existing GardenPlant packet path.
            var plantedObject = catcher.Plant(cropType, true);
            var plantedSpawner = plantedObject ? plantedObject.GetComponent<SpawnResource>() : null;
            var plantedCrop = plantedSpawner?._resourceGrowerDefinition?._primaryResourceType;
            int? plantedKey = TryGetTypeKey(plantedCrop);
            lastNativePlantReturnedCrop = plantedKey == cropKey;

            TryReadPlot(plotId, out var modelCropKey, out var physicalCropKey, out _);
            if (lastNativePlantReturnedCrop != true || modelCropKey != cropKey ||
                physicalCropKey != cropKey)
            {
                lastActionState = "native-plant-postcondition-failed";
                lastActionError = $"native plant result/model/physical crop did not all match expected type {cropKey}";
                return ReadState();
            }

            lastActionState = "submitted-awaiting-peer-state";
            return ReadState();
        }
        catch (Exception exception)
        {
            lastActionState = "native-plant-threw";
            lastActionError = $"GardenCatcher.Plant threw {exception.GetType().Name}: {exception.Message}";
            return ReadState();
        }
    }

    private static TestGardenSmokeState FailAction(string error)
    {
        lastActionState = "failed";
        lastActionError = error;
        return ReadState();
    }

    private static TestGardenSmokeState CreateBaseState()
        => new()
        {
            Connected = Main.Client.IsConnected,
            Joined = Main.Client.IsJoined,
            ServerRunning = Main.Server.IsRunning,
            HandlingPacket = HandlingPacket
        };

    private static void CopyLastAction(TestGardenSmokeState state)
    {
        state.ActionSequenceBefore = lastActionSequenceBefore;
        state.ActionSequenceAfter = lastActionSequenceAfter;
        state.LastActionState = lastActionState;
        state.LastActionError = lastActionError;
        state.LastNativePlantReturnedCrop = lastNativePlantReturnedCrop;
        if (lastPlotId != null)
            state.PlotId = lastPlotId;
        if (lastCropTypeKey.HasValue)
            state.CropTypeKey = lastCropTypeKey;
    }

    private static List<TestGardenPlotSmokeState> ReadLoadedPlots()
    {
        var plots = new List<TestGardenPlotSmokeState>();
        foreach (var id in GetSortedPlotIds())
        {
            if (!GameState.landPlots.TryGetValue(id, out var model) || model == null || !model.gameObj)
                continue;

            if (!model.gameObj.GetComponentInChildren<GardenCatcher>(true))
                continue;

            var readable = TryReadPlot(id, out var modelCropKey, out var physicalCropKey, out var error);
            plots.Add(new TestGardenPlotSmokeState
            {
                PlotId = id,
                ModelPlotType = model.typeId.ToString(),
                PhysicalPlotType = model.gameObj.GetComponentInChildren<LandPlot>(true)?.TypeId.ToString(),
                ModelCropTypeKey = modelCropKey,
                PhysicalCropTypeKey = physicalCropKey,
                ReadError = readable ? null : error
            });
        }

        return plots;
    }

    private static bool TryFindEmptyTarget(
        out string plotId,
        out IdentifiableType cropType,
        out int cropKey,
        out GardenCatcher catcher,
        out string error)
    {
        plotId = string.Empty;
        cropType = null!;
        cropKey = -1;
        catcher = null!;
        error = string.Empty;

        var gameContext = GameContext.Instance;
        var translation = gameContext?.AutoSaveDirector?._saveReferenceTranslation
            ?._resourceGrowerTranslation?.RawLookupDictionary;
        if (translation == null)
        {
            error = "native resource grower translation is unavailable";
            return false;
        }

        var crops = new List<(int Key, string Name, IdentifiableType Type)>();
        foreach (var entry in translation._entries)
        {
            var grower = entry.value;
            var type = grower != null ? grower._primaryResourceType : null;
            var key = TryGetTypeKey(type);
            if (type == null || !key.HasValue ||
                !ActorManager.ActorTypes.TryGetValue(key.Value, out var registered) ||
                registered == null || registered.Pointer != type.Pointer)
                continue;

            crops.Add((key.Value, type.name ?? string.Empty, type));
        }

        if (crops.Count == 0)
        {
            error = "no registered crop type has a native resource grower definition";
            return false;
        }

        foreach (var id in GetSortedPlotIds())
        {
            if (!GameState.landPlots.TryGetValue(id, out var model) || model == null || !model.gameObj ||
                model.resourceGrowerDefinition != null)
                continue;

            var location = model.gameObj.GetComponentInParent<LandPlotLocation>(true);
            var garden = model.gameObj.GetComponentInChildren<GardenCatcher>(true);
            var physical = model.gameObj.GetComponentInChildren<SpawnResource>(true);
            if (!location || !string.Equals(location._id, id, StringComparison.Ordinal) || !garden || physical)
                continue;

            foreach (var candidate in crops.OrderBy(crop => crop.Key).ThenBy(crop => crop.Name, StringComparer.Ordinal))
            {
                try
                {
                    if (!garden.CanAccept(candidate.Type))
                        continue;
                }
                catch
                {
                    continue;
                }

                plotId = id;
                cropType = candidate.Type;
                cropKey = candidate.Key;
                catcher = garden;
                return true;
            }
        }

        error = "no loaded, truly empty garden plot with a registered CanAccept crop is available";
        return false;
    }

    private static List<string> GetSortedPlotIds()
    {
        var ids = new List<string>();
        foreach (var (key, _) in GameState.landPlots)
        {
            var id = key?.ToString();
            if (!string.IsNullOrWhiteSpace(id))
                ids.Add(id);
        }

        ids.Sort(StringComparer.Ordinal);
        return ids;
    }

    private static bool TryReadPlot(
        string plotId,
        out int? modelCropKey,
        out int? physicalCropKey,
        out string error)
    {
        modelCropKey = null;
        physicalCropKey = null;
        error = string.Empty;

        if (!GameState.landPlots.TryGetValue(plotId, out var model) || model == null || !model.gameObj)
        {
            error = $"garden plot '{plotId}' is not loaded with a scene object";
            return false;
        }

        modelCropKey = TryGetTypeKey(model.resourceGrowerDefinition?._primaryResourceType);
        var spawner = model.gameObj.GetComponentInChildren<SpawnResource>(true);
        physicalCropKey = TryGetTypeKey(spawner?._resourceGrowerDefinition?._primaryResourceType);
        return true;
    }

    private static int? TryGetTypeKey(IdentifiableType? type)
    {
        if (!type)
            return null;
        try
        {
            return NetworkActorManager.GetPersistentID(type);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Plain CLR values suitable for lifecycle-control JSON snapshots.</summary>
internal sealed class TestGardenSmokeState
{
    public bool Ready { get; set; }
    public bool Connected { get; set; }
    public bool Joined { get; set; }
    public bool ServerRunning { get; set; }
    public bool HandlingPacket { get; set; }
    public string? PlotId { get; set; }
    public int? CropTypeKey { get; set; }
    public int? ModelCropTypeKey { get; set; }
    public int? PhysicalCropTypeKey { get; set; }
    public List<TestGardenPlotSmokeState> Plots { get; set; } = new();
    public int ActionSequenceBefore { get; set; }
    public int ActionSequenceAfter { get; set; }
    public bool? LastNativePlantReturnedCrop { get; set; }
    public string LastActionState { get; set; } = "none";
    public string? LastActionError { get; set; }
    public string? ReadinessError { get; set; }
}

internal sealed class TestGardenPlotSmokeState
{
    public string PlotId { get; set; } = string.Empty;
    public string? ModelPlotType { get; set; }
    public string? PhysicalPlotType { get; set; }
    public int? ModelCropTypeKey { get; set; }
    public int? PhysicalCropTypeKey { get; set; }
    public string? ReadError { get; set; }
}

/// <summary>Plain CLR result for the opt-in free host-side garden fixture action.</summary>
internal sealed class TestGardenFixtureState
{
    public int FixtureSequence { get; set; }
    public string FixtureState { get; set; } = "none";
    public string? FixtureError { get; set; }
    public string? FixturePlotId { get; set; }
    public string FixtureMode { get; set; } = "free-native-replace-no-purchase";
}
#endif

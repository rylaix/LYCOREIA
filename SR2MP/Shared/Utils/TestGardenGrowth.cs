#if SR2MP_TEST
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Components.LandPlots;
using SR2MP.Shared.Managers;

namespace SR2MP.Shared.Utils;

/// <summary>
/// Read-only garden growth observations and one bounded native peer time skip for
/// disposable-client QA. This never edits producer/model state or spawns resources.
/// </summary>
internal static class TestGardenGrowth
{
    private const float DeadlineMarginHours = 0.1f;
    private const float MaximumAdvanceHours = 24f;
    private static bool advanceAttempted;
    private static int advanceSequence;
    private static string advanceState = "none";
    private static string? advanceError;
    private static string? advancePlotId;
    private static double? advanceWorldTimeBefore;
    private static double? advanceDeadline;
    private static double? advanceTargetTime;
    private static double? advanceHours;

    internal static TestGardenGrowthState ReadState()
    {
        var state = new TestGardenGrowthState
        {
            Connected = Main.Client.IsConnected,
            Joined = Main.Client.IsJoined,
            ServerRunning = Main.Server.IsRunning,
            LocalId = LocalID,
            HandlingPacket = HandlingPacket,
            AdvanceSequence = advanceSequence,
            AdvanceState = advanceState,
            AdvanceError = advanceError,
            AdvancePlotId = advancePlotId,
            AdvanceWorldTimeBefore = advanceWorldTimeBefore,
            AdvanceDeadline = advanceDeadline,
            AdvanceTargetTime = advanceTargetTime,
            AdvanceHours = advanceHours
        };

        try
        {
            var scene = SceneContext.Instance;
            var timeDirector = scene ? scene.TimeDirector : null;
            if (timeDirector != null)
            {
                var now = timeDirector.WorldTime();
                if (IsFinite(now))
                    state.CurrentWorldTime = now;
                if (advanceAttempted && advanceTargetTime.HasValue && IsFinite(now))
                    state.AdvanceWorldTimeObserved = now;
            }
        }
        catch (Exception exception)
        {
            state.ReadError = $"could not read current game time ({exception.GetType().Name}: {exception.Message})";
        }

        foreach (var plotId in GetSortedPlotIds())
        {
            if (!GameState.landPlots.TryGetValue(plotId, out var model) || model == null ||
                model.typeId != LandPlot.Id.GARDEN || !model.gameObj ||
                model.resourceGrowerDefinition == null)
                continue;

            state.Gardens.Add(ReadGarden(plotId, model, state.CurrentWorldTime));
        }

        ReadActorModels(state);

        state.Ready = state.Gardens.Any(garden => garden.ReadError == null);
        if (!state.Ready && string.IsNullOrEmpty(state.ReadError))
            state.ReadError = "no loaded planted garden with a readable native producer was found";
        return state;
    }

    /// <summary>
    /// Asks the current owner's TimeDirector to advance through one eligible producer deadline.
    /// The native fast-forward path performs world simulation and the normal sync hooks.
    /// </summary>
    internal static TestGardenGrowthState AdvanceLocallyOwnedGardenOneCycle()
    {
        if (advanceAttempted)
            return Fail("one native garden-growth advance was already attempted in this client process");

        advanceAttempted = true;
        advanceSequence++;
        advanceState = "validating";
        advanceError = null;
        advancePlotId = null;
        advanceWorldTimeBefore = null;
        advanceDeadline = null;
        advanceTargetTime = null;
        advanceHours = null;

        if (!Main.Server.IsRunning && !Main.Client.IsJoined)
            return Fail("growth advance requires a running host or a joined guest session");
        if (HandlingPacket)
            return Fail("growth advance refused while HandlingPacket is true");

        var system = SystemContext.Instance;
        if (!system || system.SceneLoader == null || system.SceneLoader.IsSceneLoadInProgress ||
            !system.SceneLoader.IsCurrentSceneGroupGameplay())
            return Fail("gameplay scene is unavailable or still loading");

        var scene = SceneContext.Instance;
        var timeDirector = scene ? scene.TimeDirector : null;
        if (timeDirector == null)
            return Fail("native TimeDirector is unavailable");

        double now;
        try
        {
            now = timeDirector.WorldTime();
        }
        catch (Exception exception)
        {
            return Fail($"native WorldTime read threw {exception.GetType().Name}: {exception.Message}");
        }
        if (!IsFinite(now))
            return Fail("native WorldTime is not finite");

        var candidates = ReadState().Gardens;
        TestGardenGrowthPlotState? selected = null;
        foreach (var candidate in candidates.OrderBy(garden => garden.PlotId, StringComparer.Ordinal))
        {
            if (candidate.ReadError != null || !candidate.NetworkGardenPresent ||
                !candidate.LocallyOwned || candidate.IsHibernated || candidate.OwnerId != LocalID ||
                !candidate.SpawnResourcePresent || !candidate.ModelCropTypeKey.HasValue ||
                candidate.ModelCropTypeKey != candidate.PhysicalCropTypeKey ||
                !candidate.NextSpawnTime.HasValue)
                continue;

            var deadline = candidate.NextSpawnTime.Value;
            if (!IsFinite(deadline) || deadline <= now)
                continue;

            double hoursUntil;
            double target;
            double targetHours;
            double maximumTarget;
            try
            {
                // TimeDirector.HoursUntil and HoursFromTime are the game's native
                // game-hour conversion APIs. SpawnResourceAwake.ResetGrowTime stores
                // the matching absolute deadline through HoursFromNow(interval).
                hoursUntil = timeDirector.HoursUntil(deadline);
                if (!IsFinite(hoursUntil) || hoursUntil <= 0 ||
                    hoursUntil + DeadlineMarginHours > MaximumAdvanceHours)
                    continue;

                target = TimeDirector.HoursFromTime(DeadlineMarginHours, deadline);
                targetHours = timeDirector.HoursUntil(target);
                maximumTarget = timeDirector.HoursFromNow(MaximumAdvanceHours);
            }
            catch
            {
                continue;
            }

            if (!IsFinite(target) || !IsFinite(targetHours) || !IsFinite(maximumTarget) ||
                target <= deadline || targetHours <= 0 || targetHours > MaximumAdvanceHours ||
                target > maximumTarget)
                continue;

            selected = candidate;
            advanceHours = targetHours;
            advanceDeadline = deadline;
            advanceTargetTime = target;
            break;
        }

        if (selected == null)
            return Fail("no planted garden has a finite future spawn deadline owned locally by this peer within 24 game hours");

        advancePlotId = selected.PlotId;
        advanceWorldTimeBefore = now;
        advanceState = "calling-native-time-director-fast-forward";
        try
        {
            timeDirector.FastForwardTo(advanceTargetTime!.Value);
            advanceState = "native-fast-forward-requested-awaiting-growth-observation";
            return ReadState();
        }
        catch (Exception exception)
        {
            return Fail($"TimeDirector.FastForwardTo threw {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static TestGardenGrowthState Fail(string error)
    {
        advanceState = "failed";
        advanceError = error;
        return ReadState();
    }

    private static TestGardenGrowthPlotState ReadGarden(string plotId, LandPlotModel model, double? now)
    {
        var state = new TestGardenGrowthPlotState { PlotId = plotId };
        try
        {
            var location = model.gameObj.GetComponent<LandPlotLocation>();
            if (!location || !string.Equals(location._id, plotId, StringComparison.Ordinal))
            {
                state.ReadError = "loaded garden has no matching LandPlotLocation ID";
                return state;
            }

            var plotPosition = location.transform.position;
            state.PlotPositionX = plotPosition.x;
            state.PlotPositionY = plotPosition.y;
            state.PlotPositionZ = plotPosition.z;

            var physicalCrop = model.gameObj.GetComponentInChildren<SpawnResource>(true);
            if (!physicalCrop || physicalCrop._resourceGrowerDefinition == null || physicalCrop._model == null)
            {
                state.ReadError = "planted garden has no physical SpawnResource, grower definition, or model";
                return state;
            }

            var modelCropKey = TryGetTypeKey(model.resourceGrowerDefinition._primaryResourceType);
            var physicalCropKey = TryGetTypeKey(physicalCrop._resourceGrowerDefinition._primaryResourceType);
            state.ModelCropTypeKey = modelCropKey;
            state.PhysicalCropTypeKey = physicalCropKey;
            if (!modelCropKey.HasValue || modelCropKey != physicalCropKey)
            {
                state.ReadError = "saved garden crop and physical grower crop are missing or disagree";
                return state;
            }

            state.SpawnResourcePresent = true;
            state.SpawnResourceId = physicalCrop._id ?? string.Empty;
            state.NextSpawnTime = physicalCrop._model.nextSpawnTime;
            state.StoredWater = physicalCrop._model.storedWater;
            state.NextSpawnRipens = physicalCrop._model.nextSpawnRipens;
            if (!state.NextSpawnTime.HasValue || !IsFinite(state.NextSpawnTime.Value))
            {
                state.ReadError = "planted garden has no finite native next-spawn deadline";
                return state;
            }

            if (now.HasValue)
            {
                try
                {
                    state.HoursUntilSpawn = SceneContext.Instance.TimeDirector.HoursUntil(state.NextSpawnTime.Value);
                }
                catch (Exception exception)
                {
                    state.ReadError = $"native HoursUntil failed ({exception.GetType().Name})";
                    return state;
                }
            }

            var networkGarden = physicalCrop.GetComponent<NetworkGarden>();
            state.NetworkGardenPresent = !!networkGarden;
            if (networkGarden)
            {
                state.GardenId = networkGarden.Id;
                state.OwnerId = networkGarden.OwnerId;
                state.LocallyOwned = networkGarden.LocallyOwned;
                state.IsHibernated = networkGarden.IsHibernated;
            }

            ReadProducedResources(physicalCrop, state);
            if (state.ResourceCycles.Any(resource => resource.ReadError != null))
                state.ReadError = "one or more produced ResourceCycle actors could not be read";
        }
        catch (Exception exception)
        {
            state.ReadError = $"garden snapshot failed ({exception.GetType().Name}: {exception.Message})";
        }

        return state;
    }

    private static void ReadProducedResources(SpawnResource spawner, TestGardenGrowthPlotState state)
    {
        var joints = spawner.SpawnJoints;
        var spawned = spawner._spawned;
        if (spawned == null)
            return;

        foreach (var actorObject in spawned)
        {
            if (!actorObject)
                continue;

            var resource = new TestGardenResourceState();
            state.ResourceCycles.Add(resource);
            try
            {
                var cycle = actorObject.GetComponent<ResourceCycle>();
                var identifiableActor = actorObject.GetComponent<IdentifiableActor>();
                var identifiable = actorObject.GetComponent<Identifiable>();
                if (!cycle || !identifiableActor || identifiableActor._model == null || !identifiable)
                {
                    resource.ReadError = "spawned object lacks ResourceCycle, IdentifiableActor model, or Identifiable";
                    continue;
                }

                resource.ActorId = identifiableActor._model.actorId.Value;
                resource.TypeKey = TryGetTypeKey(identifiable.identType);
                var produceModel = identifiableActor._model.TryCast<ProduceModel>();
                if (produceModel != null)
                {
                    resource.State = produceModel._state.ToString();
                    resource.ProgressTime = produceModel.progressTime;
                }

                var jointWrapper = cycle._joint;
                if (jointWrapper != null && jointWrapper.Joint)
                {
                    var joint = jointWrapper.Joint;
                    resource.JointIndex = joints.IndexOf(joint);
                    var attachedLocation = joint.gameObject.GetComponentInParent<LandPlotLocation>(true);
                    resource.AttachedPlotId = attachedLocation ? attachedLocation._id : null;
                    resource.Attached = resource.JointIndex >= 0;
                }
                else
                {
                    resource.Attached = false;
                }
            }
            catch (Exception exception)
            {
                resource.ReadError = $"ResourceCycle snapshot failed ({exception.GetType().Name}: {exception.Message})";
            }
        }
    }

    private static void ReadActorModels(TestGardenGrowthState state)
    {
        foreach (var entry in ActorManager.Actors.OrderBy(entry => entry.Key))
        {
            var model = entry.Value;
            if (model == null)
                continue;

            var actor = new TestGardenActorState { ActorId = entry.Key };
            state.Actors.Add(actor);
            try
            {
                actor.TypeKey = TryGetTypeKey(model.ident);
                actor.ModelType = model.GetType().Name;
                var position = model.lastPosition;
                actor.PositionX = position.x;
                actor.PositionY = position.y;
                actor.PositionZ = position.z;
                if (ActorManager.OwnershipByActorId.TryGetValue(entry.Key, out var ownerId))
                    actor.OwnerId = ownerId;

                var gameObject = model.GetGameObject();
                actor.GameObjectPresent = !!gameObject;
                actor.GameObjectActive = gameObject && gameObject.activeInHierarchy;
                if (!gameObject)
                    continue;

                var identifiableActor = gameObject.GetComponent<IdentifiableActor>();
                actor.IdentifiableActorPresent = !!identifiableActor;
                actor.IdentifiableActorMatchesModel = identifiableActor && identifiableActor._model != null &&
                    identifiableActor._model.actorId.Value == entry.Key;
                var identifiable = gameObject.GetComponent<Identifiable>();
                actor.IdentifiablePresent = !!identifiable;
                if (identifiable)
                    actor.PhysicalTypeKey = TryGetTypeKey(identifiable.identType);
                var cycle = gameObject.GetComponent<ResourceCycle>();
                actor.ResourceCyclePresent = !!cycle;
                if (cycle)
                {
                    var produceModel = model.TryCast<ProduceModel>();
                    if (produceModel != null)
                    {
                        actor.ProduceState = produceModel._state.ToString();
                        actor.ProduceProgressTime = produceModel.progressTime;
                    }

                    var jointWrapper = cycle._joint;
                    if (jointWrapper != null && jointWrapper.Joint)
                    {
                        var joint = jointWrapper.Joint;
                        actor.ResourceCycleJointConnected = true;
                        var attachedSpawner = joint.gameObject.GetComponentInParent<SpawnResource>(true);
                        var attachedLocation = joint.gameObject.GetComponentInParent<LandPlotLocation>(true);
                        if (attachedSpawner != null)
                            actor.ResourceCycleJointIndex = attachedSpawner.SpawnJoints.IndexOf(joint);
                        if (attachedLocation)
                            actor.ResourceCycleAttachedPlotId = attachedLocation._id;
                    }
                }
            }
            catch (Exception exception)
            {
                actor.ReadError = $"actor model snapshot failed ({exception.GetType().Name})";
            }
        }
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

    private static bool IsFinite(double value)
        => !double.IsNaN(value) && !double.IsInfinity(value);
}

/// <summary>Plain CLR observation for one growth advance and all loaded planted gardens.</summary>
internal sealed class TestGardenGrowthState
{
    public bool Ready { get; set; }
    public bool Connected { get; set; }
    public bool Joined { get; set; }
    public bool ServerRunning { get; set; }
    public string LocalId { get; set; } = string.Empty;
    public bool HandlingPacket { get; set; }
    public double? CurrentWorldTime { get; set; }
    public double? AdvanceWorldTimeObserved { get; set; }
    public List<TestGardenGrowthPlotState> Gardens { get; set; } = new();
    public int AdvanceSequence { get; set; }
    public string AdvanceState { get; set; } = "none";
    public string? AdvanceError { get; set; }
    public string? AdvancePlotId { get; set; }
    public double? AdvanceWorldTimeBefore { get; set; }
    public double? AdvanceDeadline { get; set; }
    public double? AdvanceTargetTime { get; set; }
    public double? AdvanceHours { get; set; }
    public string? ReadError { get; set; }
    public List<TestGardenActorState> Actors { get; set; } = new();
}

internal sealed class TestGardenGrowthPlotState
{
    public string PlotId { get; set; } = string.Empty;
    public string? GardenId { get; set; }
    public string? SpawnResourceId { get; set; }
    public int? ModelCropTypeKey { get; set; }
    public int? PhysicalCropTypeKey { get; set; }
    public bool SpawnResourcePresent { get; set; }
    public double? NextSpawnTime { get; set; }
    public double? HoursUntilSpawn { get; set; }
    public float? StoredWater { get; set; }
    public bool? NextSpawnRipens { get; set; }
    public float? PlotPositionX { get; set; }
    public float? PlotPositionY { get; set; }
    public float? PlotPositionZ { get; set; }
    public string? OwnerId { get; set; }
    public bool NetworkGardenPresent { get; set; }
    public bool LocallyOwned { get; set; }
    public bool IsHibernated { get; set; }
    public List<TestGardenResourceState> ResourceCycles { get; set; } = new();
    public string? ReadError { get; set; }
}

internal sealed class TestGardenActorState
{
    public long ActorId { get; set; }
    public int? TypeKey { get; set; }
    public int? PhysicalTypeKey { get; set; }
    public string? ModelType { get; set; }
    public string? OwnerId { get; set; }
    public float? PositionX { get; set; }
    public float? PositionY { get; set; }
    public float? PositionZ { get; set; }
    public bool GameObjectPresent { get; set; }
    public bool GameObjectActive { get; set; }
    public bool IdentifiableActorPresent { get; set; }
    public bool IdentifiableActorMatchesModel { get; set; }
    public bool IdentifiablePresent { get; set; }
    public bool ResourceCyclePresent { get; set; }
    public bool ResourceCycleJointConnected { get; set; }
    public string? ResourceCycleAttachedPlotId { get; set; }
    public int? ResourceCycleJointIndex { get; set; }
    public string? ProduceState { get; set; }
    public double? ProduceProgressTime { get; set; }
    public string? ReadError { get; set; }
}

internal sealed class TestGardenResourceState
{
    public long? ActorId { get; set; }
    public int? TypeKey { get; set; }
    public string? State { get; set; }
    public double? ProgressTime { get; set; }
    public bool? Attached { get; set; }
    public string? AttachedPlotId { get; set; }
    public int? JointIndex { get; set; }
    public string? ReadError { get; set; }
}
#endif

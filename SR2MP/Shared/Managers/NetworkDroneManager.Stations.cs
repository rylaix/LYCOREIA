using System.Collections;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Drone;
using SR2MP.Components.Drone;
using SR2MP.Packets.Loading;
using SR2MP.Packets.World;

namespace SR2MP.Shared.Managers;

internal static partial class NetworkDroneManager
{
    private static readonly Dictionary<long, DroneProgramPacket> PendingDroneTasks = new();
    private static bool droneTaskPumpRunning;

    internal static bool ReceiveDroneTask(DroneProgramPacket packet)
    {
        PendingDroneTasks[packet.ActorId] = new DroneProgramPacket
        {
            ActorId = packet.ActorId,
            TargetIdent = packet.TargetIdent,
            Target = packet.Target,
            Source = packet.Source,
            Sink = packet.Sink
        };

        TryApplyPendingDroneTask(packet.ActorId);
        StartDroneTaskPumpIfNeeded();
        return true;
    }

    internal static void ClearPendingSyncState()
    {
        PendingDroneTasks.Clear();
        ClearPendingResourceState();
    }

    private static bool TryApplyPendingDroneTask(long stationId)
    {
        if (!PendingDroneTasks.TryGetValue(stationId, out var packet))
            return true;

        DroneStationGadgetModel? stationModel;
        try
        {
            stationModel = GameState.GetIdentifiableModel(new ActorId(stationId))?.TryCast<DroneStationGadgetModel>();
        }
        catch
        {
            return false;
        }

        if (stationModel == null)
            return false;

        IdentifiableType? targetIdentType = null;
        if (packet.TargetIdent != -1 &&
            (!ActorManager.ActorTypes.TryGetValue(packet.TargetIdent, out targetIdentType) || targetIdentType == null))
            return false;

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            ApplyDroneTask(
                stationModel,
                (DroneTaskTargetType)packet.Target,
                targetIdentType,
                (DroneTaskSourceType)packet.Source,
                (DroneTaskSinkType)packet.Sink);
            PendingDroneTasks.Remove(stationId);
            return true;
        }
        catch (Exception ex)
        {
            SrLogger.LogDebug($"Drone task for station {stationId} is waiting for its model: {ex.Message}");
            return false;
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }
    }

    private static void StartDroneTaskPumpIfNeeded()
    {
        if (droneTaskPumpRunning || PendingDroneTasks.Count == 0)
            return;

        droneTaskPumpRunning = true;
        StartCoroutine(PumpPendingDroneTasks());
    }

    private static IEnumerator PumpPendingDroneTasks()
    {
        while (PendingDroneTasks.Count > 0)
        {
            foreach (var stationId in PendingDroneTasks.Keys.ToArray())
                TryApplyPendingDroneTask(stationId);

            if (PendingDroneTasks.Count == 0)
                break;

            yield return new WaitFrames(15);
        }

        droneTaskPumpRunning = false;
    }

    internal static IEnumerator EnsureStation(DroneStationGadgetModel stationModel)
    {
        yield return new WaitFrames(3);

        SpawnStationDrone(stationModel);
    }

    private static void SpawnStationDrone(DroneStationGadgetModel? stationModel)
    {
        if (stationModel == null)
            return;

        var stationObj = stationModel.GetGameObject();
        if (!stationObj)
            return;

        var station = stationObj!.GetComponentInChildren<DroneStation>();
        if (!station)
            return;

        // Drone already exists
        if (NetworkDrone.Drones.TryGetValue(stationModel.actorId.Value, out var existingDrone) && existingDrone)
            return;

        if (station!.RanchDrone != null || station.ExplorerDrone != null)
            return;

        HandlingPacket = true;

        try
        {
            if (stationModel._type == DroneType.RANCH_DRONE)
            {
                if (!GameState.droneModel.TryGetRanchDroneByStationId(stationModel.actorId, out _))
                    station.RegisterStationAndSpawnRanchDrone();
            }
            else
            {
                if (!GameState.droneModel.TryGetExplorerDroneByStationId(stationModel.actorId, out _))
                    station.RegisterStationAndSpawnExplorerDrone();
            }
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"Failed to spawn drone for station {stationModel.actorId.Value}: {ex.Message}");
        }

        HandlingPacket = false;
    }

    internal static void ApplyDroneTask(
        DroneStationGadgetModel stationModel,
        DroneTaskTargetType targetType,
        IdentifiableType? targetIdentType,
        DroneTaskSourceType sourceType,
        DroneTaskSinkType sinkType)
    {
        if (AssignTaskToStation(stationModel, targetType, targetIdentType, sourceType, sinkType))
            return;

        var taskData = new DroneTaskData
        {
            TargetType = targetType,
            TargetIdentType = targetIdentType,
            SinkType = sinkType,
            SourceType = sourceType
        };

        stationModel.SetTaskData(taskData, SceneContext.Instance.TimeDirector.WorldTime());

        UpdateStationIcon(stationModel, targetType, targetIdentType);
        StartCoroutine(RetryAssignTaskToStation(stationModel, targetType, targetIdentType, sourceType, sinkType));
    }

    private static void UpdateStationIcon(
        DroneStationGadgetModel stationModel,
        DroneTaskTargetType targetType,
        IdentifiableType? targetIdentType)
    {
        try
        {
            var stationObj = stationModel.GetGameObject();
            if (!stationObj)
                return;

            var display = stationObj!.GetComponentInChildren<DroneStationTargetDisplay>(true);
            if (display)
                display!.SetIcon(targetType, targetIdentType);
        }
        catch (Exception ex)
        {
            SrLogger.LogDebug($"Failed to update drone station icon: {ex.Message}");
        }
    }

    private static IEnumerator RetryAssignTaskToStation(
        DroneStationGadgetModel stationModel,
        DroneTaskTargetType targetType,
        IdentifiableType? targetIdentType,
        DroneTaskSourceType sourceType,
        DroneTaskSinkType sinkType)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            yield return new WaitFrames(5);

            if (stationModel == null)
                yield break;

            // A newer assignment happened
            var current = stationModel._taskData;
            if (current.TargetType != targetType || current.TargetIdentType != targetIdentType ||
                current.SourceType != sourceType || current.SinkType != sinkType)
                yield break;

            var assigned = false;
            HandlingPacket = true;

            try
            {
                assigned = AssignTaskToStation(stationModel, targetType, targetIdentType, sourceType, sinkType);
            }
            catch { /* station still initializing */ }
            finally
            {
                HandlingPacket = false;
            }

            if (assigned)
                yield break;
        }
    }

    private static bool AssignTaskToStation(
        DroneStationGadgetModel stationModel,
        DroneTaskTargetType targetType,
        IdentifiableType? targetIdentType,
        DroneTaskSourceType sourceType,
        DroneTaskSinkType sinkType)
    {
        var gameObj = stationModel.GetGameObject();
        if (!gameObj)
            return false;

        var station = gameObj!.GetComponentInChildren<DroneStation>();
        if (!station)
            return false;

        if (targetType == DroneTaskTargetType.NONE)
        {
            station!.AssignTaskNone();
            return true;
        }

        var ranchDrone = station!.RanchDrone;
        if (ranchDrone != null)
        {
            var target = ranchDrone.GatherTargetTask(targetType, targetIdentType);
            var source = ranchDrone.GatherSourceTask(sourceType);
            var sink = ranchDrone.GatherSinkTask(sinkType);

            station.AssignRanchTask(target, source, sink, false);
            return true;
        }

        var explorerDrone = station.ExplorerDrone;
        if (explorerDrone != null)
        {
            var target = explorerDrone.GatherTargetTask(targetType, targetIdentType);

            station.AssignExplorerTask(target, false);
            return true;
        }

        return false;
    }

    internal static IEnumerator SetupInitialStation(DroneStationGadgetModel stationModel, InitialActorsPacket.DroneStation actorData)
    {
        var snapshotOwner = ReconcileOwnerSnapshot(actorData.ActorId, actorData.DroneOwnerId);
        if (!string.IsNullOrEmpty(snapshotOwner))
        {
            PendingOwners[actorData.ActorId] = snapshotOwner;
            CachedOwners[actorData.ActorId] = snapshotOwner;
        }
        else
        {
            PendingOwners.Remove(actorData.ActorId);
            CachedOwners.Remove(actorData.ActorId);
        }

        yield return new WaitFrames(3);

        SpawnStationDrone(stationModel);

        yield return new WaitFrames(2);

        if (stationModel == null)
            yield break;

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;

        try
        {
            if (stationModel._type == DroneType.RANCH_DRONE &&
                actorData.Ammo != null && actorData.Ammo.AmmoSlots.Count > 0)
            {
                var stationObj = stationModel.GetGameObject();
                var station = stationObj ? stationObj!.GetComponentInChildren<DroneStation>() : null;
                var ranchDrone = station ? station!.RanchDrone : null;

                if (ranchDrone != null)
                    ApplyDroneAmmo(ranchDrone, actorData.Ammo);
            }

            var targetIdent = actorData.Task.TargetIdent != -1 &&
                              ActorManager.ActorTypes.TryGetValue(actorData.Task.TargetIdent, out var type)
                ? type
                : null;

            ApplyDroneTask(stationModel, actorData.Task.Target, targetIdent, actorData.Task.Source, actorData.Task.Sink);
            TryApplyPendingDroneTask(actorData.ActorId);
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"Failed to set up initial drone station {stationModel.actorId.Value}: {ex.Message}");
        }

        finally
        {
            HandlingPacket = wasHandlingPacket;
        }
    }
}

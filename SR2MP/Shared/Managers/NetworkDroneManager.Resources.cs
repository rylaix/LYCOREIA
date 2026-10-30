using System.Collections;
using Il2CppMonomiPark.SlimeRancher.Drone;
using SR2MP.Packets.Loading;
using SR2MP.Packets.World;
using SR2MP.Shared.Utils;

namespace SR2MP.Shared.Managers;

internal static partial class NetworkDroneManager
{
    private static readonly Dictionary<int, OrderedSnapshotDeltaQueue<List<DroneResourcePacket.DroneResourceEntry>, DroneResourcePacket>>
        PendingResourceStates = new();
    private static bool resourcePumpRunning;

    internal static InitialDroneResourcesPacket CreateInitialPacket()
    {
        var packet = new InitialDroneResourcesPacket();

        foreach (var (sceneId, group) in NetworkSceneManager.GetAllSceneGroups())
        {
            try
            {
                var store = group?.DiscoverableResources;
                var data = store?._resourceData;
                if (data == null || data.Length == 0)
                    continue;

                var scene = new InitialDroneResourcesPacket.SceneEntries { Scene = sceneId };

                foreach (var slot in data)
                    scene.Entries.Add(CreateEntry(slot));

                packet.Scenes.Add(scene);
            }
            catch (Exception ex)
            {
                SrLogger.LogDebug($"Failed to get drone resources for scene {sceneId}: {ex.Message}");
            }
        }

        return packet;
    }

    internal static DroneResourcePacket.DroneResourceEntry CreateEntry(DiscoverableResourceData? data)
    {
        if (data?._initialized != true)
            return new DroneResourcePacket.DroneResourceEntry();

        var entry = new DroneResourcePacket.DroneResourceEntry
        {
            Valid = true,
            Id = data._id ?? string.Empty,
            Location = data._location,
            OffsetLocation = data._offsetLocation
        };

        var types = data._identifiableTypes;
        if (types != null)
        {
            foreach (var type in types)
            {
                if (type != null)
                    entry.TypeIds.Add(NetworkActorManager.GetPersistentID(type));
            }
        }

        return entry;
    }

    internal static void ReceiveInitialResources(InitialDroneResourcesPacket packet)
    {
        if (packet.Scenes == null)
            return;

        foreach (var scene in packet.Scenes)
        {
            if (scene == null || scene.Entries == null)
                continue;

            var queue = GetPendingResourceQueue(scene.Scene);
            queue.SetSnapshot(scene.Entries.ToList());
            TryDrainResourceQueue(scene.Scene, queue);
        }

        StartResourcePumpIfNeeded();
    }

    internal static bool ReceiveResourceUpdate(DroneResourcePacket packet)
    {
        if (!PendingResourceStates.TryGetValue(packet.Scene, out var queue))
        {
            if (TryApplyResourceUpdate(packet))
                return true;

            queue = GetPendingResourceQueue(packet.Scene);
        }

        queue.EnqueueDelta(packet);
        TryDrainResourceQueue(packet.Scene, queue);
        StartResourcePumpIfNeeded();
        return true;
    }

    internal static void ClearPendingResourceState()
    {
        PendingResourceStates.Clear();
    }

    private static OrderedSnapshotDeltaQueue<List<DroneResourcePacket.DroneResourceEntry>, DroneResourcePacket>
        GetPendingResourceQueue(int sceneId)
    {
        if (!PendingResourceStates.TryGetValue(sceneId, out var queue))
        {
            queue = new OrderedSnapshotDeltaQueue<List<DroneResourcePacket.DroneResourceEntry>, DroneResourcePacket>();
            PendingResourceStates[sceneId] = queue;
        }

        return queue;
    }

    private static void TryDrainResourceQueue(
        int sceneId,
        OrderedSnapshotDeltaQueue<List<DroneResourcePacket.DroneResourceEntry>, DroneResourcePacket> queue)
    {
        queue.TryDrain(
            entries => TryApplyResourceSnapshot(sceneId, entries),
            TryApplyResourceUpdate);

        if (!queue.HasPending)
            PendingResourceStates.Remove(sceneId);
    }

    private static bool TryApplyResourceSnapshot(int sceneId, List<DroneResourcePacket.DroneResourceEntry> entries)
    {
        var store = TryGetResourceStore(sceneId);
        if (store == null)
            return false;

        foreach (var entry in entries)
        {
            if (!CanResolveResourceTypes(entry))
                return false;
        }

        try
        {
            var data = new CppCollections.List<DiscoverableResourceData>(entries.Count);
            foreach (var entry in entries)
                data.Add(ToResourceData(entry));

            var wasHandlingPacket = HandlingPacket;
            HandlingPacket = true;
            try
            {
                store.SetAllDiscoverableResourceData(data);
            }
            finally
            {
                HandlingPacket = wasHandlingPacket;
            }

            return true;
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"Failed to apply discoverable-resource snapshot for scene {sceneId}; it will be retried: {ex.Message}");
            return false;
        }
    }

    private static bool TryApplyResourceUpdate(DroneResourcePacket packet)
    {
        var store = TryGetResourceStore(packet.Scene);
        if (store == null)
            return false;

        if (store._resourceData == null)
            return false;

        if (packet.Index < 0)
        {
            SrLogger.LogWarning(
                $"Ignored drone-resource update with invalid index {packet.Index} for scene {packet.Scene} " +
                $"(resource count {store._resourceData.Length}).");
            return true;
        }

        if (DroneMutationPolicy.ShouldDeferResourceIndex(packet.Index, store._resourceData.Length))
        {
            SrLogger.LogDebug(
                $"Deferred drone-resource update for scene {packet.Scene}, index {packet.Index}; " +
                $"local resource count is {store._resourceData.Length} and a newer snapshot may still arrive.");
            return false;
        }

        if (!CanResolveResourceTypes(packet.Entry))
            return false;

        try
        {
            var data = ToResourceData(packet.Entry);
            var wasHandlingPacket = HandlingPacket;
            HandlingPacket = true;
            try
            {
                store.UpdateResourceData(packet.Index, ref data);
            }
            finally
            {
                HandlingPacket = wasHandlingPacket;
            }

            return true;
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning(
                $"Failed to apply discoverable-resource update for scene {packet.Scene}, index {packet.Index}; it will be retried: {ex.Message}");
            return false;
        }
    }

    private static bool CanResolveResourceTypes(DroneResourcePacket.DroneResourceEntry entry)
    {
        if (entry.TypeIds == null)
            return false;

        foreach (var typeId in entry.TypeIds)
        {
            if (!ActorManager.ActorTypes.TryGetValue(typeId, out var type) || type == null)
            {
                SrLogger.LogDebug($"Drone-resource state is waiting for identifiable type {typeId}.");
                return false;
            }
        }

        return true;
    }

    private static DiscoverableResourceDataStore? TryGetResourceStore(int sceneId)
    {
        try
        {
            return NetworkSceneManager.GetSceneGroup(sceneId)?.DiscoverableResources;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void StartResourcePumpIfNeeded()
    {
        if (resourcePumpRunning || PendingResourceStates.Count == 0)
            return;

        resourcePumpRunning = true;
        StartCoroutine(PumpPendingResources());
    }

    private static IEnumerator PumpPendingResources()
    {
        while (PendingResourceStates.Count > 0)
        {
            foreach (var (sceneId, queue) in PendingResourceStates.ToArray())
                TryDrainResourceQueue(sceneId, queue);

            if (PendingResourceStates.Count == 0)
                break;

            yield return new WaitFrames(15);
        }

        resourcePumpRunning = false;
    }

    internal static DiscoverableResourceData ToResourceData(DroneResourcePacket.DroneResourceEntry entry)
    {
        var types = new CppCollections.List<IdentifiableType>();

        foreach (var typeId in entry.TypeIds)
        {
            if (ActorManager.ActorTypes.TryGetValue(typeId, out var type) && type != null)
                types.Add(type);
        }

        var data = new DiscoverableResourceData(entry.Id, entry.Location, entry.OffsetLocation, types)
        {
            _initialized = entry.Valid
        };

        return data;
    }
}

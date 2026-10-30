using System.Collections;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Drone;
using SR2MP.Components.Drone;

namespace SR2MP.Shared.Managers;

internal static partial class NetworkDroneManager
{
    internal static readonly Dictionary<long, string> PendingOwners = new();
    internal static readonly Dictionary<long, List<(string ClaimerId, string PreviousOwnerId)>> PendingOwnerTransitions = new();
    
    // Last tracked owner per Drone Station
    internal static readonly Dictionary<long, string> CachedOwners = new();
    private static readonly Dictionary<long, string> ExplicitlyReleasedOwners = new();

    private static bool subscribedToServerStart;
    private static bool initialized;

    internal static void CheckSubscribed()
    {
        if (subscribedToServerStart)
            return;

        Main.Server.OnServerStarted += OnServerStarted;
        subscribedToServerStart = true;
    }
    
    internal static void Initialize()
    {
        if (initialized)
            return;

        initialized = true;

        Main.Client.OnConnected += _ =>
        {
            PendingOwnerTransitions.Clear();
            ExplicitlyReleasedOwners.Clear();
            ClearPendingOwnerClaims();
            ClearPendingSyncState();
        };
        Main.Client.OnDisconnected += () =>
        {
            PendingOwnerTransitions.Clear();
            ExplicitlyReleasedOwners.Clear();
            ClearPendingOwnerClaims();
            ClearPendingSyncState();
        };
        Main.Server.OnServerStarted += () =>
        {
            PendingOwnerTransitions.Clear();
            ExplicitlyReleasedOwners.Clear();
            ClearPendingOwnerClaims();
            ClearPendingSyncState();
        };

        StartCoroutine(OwnUnownedDronesLoop());
    }

    internal static bool TryGetCurrentOwner(long stationId, out string ownerId)
    {
        if (NetworkDrone.Drones.TryGetValue(stationId, out var drone) && drone)
        {
            ownerId = drone.CurrentOwnerId ?? string.Empty;
            return true;
        }

        if (PendingOwners.TryGetValue(stationId, out ownerId))
        {
            ownerId ??= string.Empty;
            return true;
        }

        if (CachedOwners.TryGetValue(stationId, out ownerId))
        {
            ownerId ??= string.Empty;
            return true;
        }

        ownerId = string.Empty;
        return false;
    }

    internal static bool IsCurrentOwner(long stationId, string playerId)
        => !string.IsNullOrWhiteSpace(playerId) &&
           TryGetCurrentOwner(stationId, out var ownerId) &&
           string.Equals(ownerId, playerId, StringComparison.Ordinal);

    internal static bool IsDroneOwnerInScene(string playerId, int sceneId)
    {
        if (string.IsNullOrWhiteSpace(playerId))
            return false;

        foreach (var drone in NetworkDrone.Drones.Values)
        {
            if (drone &&
                string.Equals(drone.CurrentOwnerId, playerId, StringComparison.Ordinal) &&
                drone.IsInSceneGroup(sceneId))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The generic resource-store callback has no drone argument. Attribute it only
    /// when one locally owned ExplorerDrone station can be identified in that scene;
    /// ambiguous updates are suppressed instead of guessing an owner.
    /// </summary>
    internal static bool TryGetUniqueOwnedExplorerStationInScene(
        string playerId, int sceneId, out long stationId)
    {
        stationId = 0;
        if (string.IsNullOrWhiteSpace(playerId))
            return false;

        foreach (var drone in NetworkDrone.Drones.Values)
        {
            if (!drone || !string.Equals(drone.CurrentOwnerId, playerId, StringComparison.Ordinal) ||
                !drone.GetComponent<ExplorerDrone>() || !drone.IsInSceneGroup(sceneId))
                continue;

            if (stationId != 0)
            {
                stationId = 0;
                return false;
            }

            stationId = drone.StationId;
        }

        return stationId != 0;
    }

    internal static bool IsOwnedExplorerStationInScene(string playerId, long stationId, int sceneId)
    {
        if (string.IsNullOrWhiteSpace(playerId) || stationId == 0 ||
            !TryGetCurrentOwner(stationId, out var ownerId) ||
            !string.Equals(ownerId, playerId, StringComparison.Ordinal))
            return false;

        var stationModel = GameState.GetIdentifiableModel(new ActorId(stationId))?.TryCast<DroneStationGadgetModel>();
        if (stationModel == null || stationModel._type != DroneType.EXPLORER_DRONE)
            return false;

        if (!GameState.droneModel.TryGetExplorerDroneByStationId(new ActorId(stationId), out var explorerModel) ||
            explorerModel == null)
            return false;

        try
        {
            return stationModel.sceneGroup != null &&
                   NetworkSceneManager.GetPersistentID(stationModel.sceneGroup) == sceneId;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsExplicitlyReleasedOwner(long stationId, string ownerId)
        => ExplicitlyReleasedOwners.TryGetValue(stationId, out var releasedOwnerId) &&
           string.Equals(releasedOwnerId, ownerId, StringComparison.Ordinal);

    internal static void RecordExplicitOwnerRelease(long stationId, string ownerId)
    {
        if (stationId != 0 && !string.IsNullOrWhiteSpace(ownerId))
            ExplicitlyReleasedOwners[stationId] = ownerId;
    }

    internal static void ClearExplicitOwnerRelease(long stationId)
        => ExplicitlyReleasedOwners.Remove(stationId);

    private static void ClearPendingOwnerClaims()
    {
        foreach (var drone in NetworkDrone.Drones.Values)
            if (drone)
                drone.CancelPendingOwnerClaim();
    }

    internal static string ReconcileOwnerSnapshot(long stationId, string snapshotOwnerId)
    {
        snapshotOwnerId ??= string.Empty;

        if (!PendingOwnerTransitions.Remove(stationId, out var transitions))
            return snapshotOwnerId;

        return ReplayOwnerTransitions(snapshotOwnerId, transitions);
    }

    private static string ReplayOwnerTransitions(
        string snapshotOwnerId,
        IEnumerable<(string ClaimerId, string PreviousOwnerId)> transitions)
    {
        // Ownership updates can arrive on WorldState before the ActorCritical station
        // snapshot. These updates were already authenticated by the host, so replay
        // them over the possibly older snapshot in their reliable channel order.
        // A release keeps PreviousOwnerId as the authority identity until a replacement
        // claim arrives; treating release as an empty owner makes that claim fail.
        var ownerId = snapshotOwnerId;
        foreach (var transition in transitions)
        {
            if (string.IsNullOrEmpty(transition.ClaimerId))
            {
                if (string.IsNullOrEmpty(ownerId))
                    ownerId = transition.PreviousOwnerId;
                continue;
            }

            ownerId = transition.ClaimerId;
        }

        return ownerId;
    }

#if SR2MP_TEST
    internal static bool VerifyOwnerSnapshotReconciliation()
    {
        var releaseThenClaim = new[]
        {
            (ClaimerId: string.Empty, PreviousOwnerId: "owner-a"),
            (ClaimerId: "owner-b", PreviousOwnerId: "owner-a")
        };

        return ReplayOwnerTransitions("owner-a", releaseThenClaim) == "owner-b"
               && ReplayOwnerTransitions(string.Empty, releaseThenClaim) == "owner-b"
               && ReplayOwnerTransitions(
                   "owner-a",
                   new[] { (ClaimerId: string.Empty, PreviousOwnerId: "owner-a") }) == "owner-a"
               && ReplayOwnerTransitions(
                   "owner-b",
                   new[] { (ClaimerId: "owner-b", PreviousOwnerId: "owner-a") }) == "owner-b";
    }
#endif

    private static IEnumerator OwnUnownedDronesLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(2f);

            if (!Main.Server.IsRunning && !Main.Client.IsConnected)
                continue;

            if (!SystemContext.Instance.SceneLoader.IsCurrentSceneGroupGameplay())
                continue;

            TakeOwnershipOfNearby();
        }
    }
    
    private static void TakeOwnershipOfNearby()
    {
        if (SceneContext.Instance?.player == null)
            return;

        var bounds = new Bounds(SceneContext.Instance.player.transform.position, new Vector3(600, 1250, 600));

        foreach (var drone in NetworkDrone.Drones.Values.ToArray())
        {
            try
            {
                if (!drone || drone.IsHibernated || drone.LocallyOwned)
                    continue;

                if (!bounds.Contains(drone.transform.position))
                    continue;

                var ownerId = drone.CurrentOwnerId;
                if (!string.IsNullOrEmpty(ownerId) && PlayerManager.CheckPlayerExists(ownerId))
                    continue;

                drone.ClaimOwnership();
            }
            catch { /* ignored */ }
        }
    }

    private static void OnServerStarted()
    {
        ExplicitlyReleasedOwners.Clear();
        ClaimLoadedDrones(NetworkActorManager.GetAllOfType<RanchDroneModel>());
        ClaimLoadedDrones(NetworkActorManager.GetAllOfType<ExplorerDroneModel>());
    }

    private static void ClaimLoadedDrones(IEnumerable<IdentifiableModel> drones)
    {
        foreach (var drone in drones)
        {
            try
            {
                var droneObject = drone.GetGameObject();

                if (!droneObject)
                    continue;

                var networkDrone = GetNetworkComponent(droneObject);
                networkDrone.LocallyOwned = true;
                networkDrone.CurrentOwnerId = Main.Server.PlayerId;

                if (networkDrone.StationId != 0)
                    CachedOwners[networkDrone.StationId] = Main.Server.PlayerId;
            }
            catch { /* ignored */ }
        }
    }

    internal static NetworkDrone GetNetworkComponent(GameObject droneObject)
        => droneObject.GetOrAddComponent<NetworkDrone>();

    internal static bool IsDroneModel(IdentifiableModel model)
        => model.TryCast<RanchDroneModel>() != null || model.TryCast<ExplorerDroneModel>() != null;

    internal static void RemoveStationDrone(ActorId stationId)
    {
        var allDrones = GameState.droneModel;

        if (allDrones.TryGetRanchDroneByStationId(stationId, out var ranchModel) && ranchModel != null)
            DestroyDrone(ranchModel.TryCast<IdentifiableModel>());
        allDrones._ranchDrones.Remove(stationId);

        if (allDrones.TryGetExplorerDroneByStationId(stationId, out var explorerModel) && explorerModel != null)
            DestroyDrone(explorerModel.TryCast<IdentifiableModel>());
        allDrones._explorerDrones.Remove(stationId);
        
        if (NetworkDrone.Drones.TryGetValue(stationId.Value, out var networkDrone) && networkDrone)
        {
            HandlingPacket = true;
            try { Destroyer.DestroyAny(networkDrone.gameObject, "SR2MP.ReplaceDrone"); }
            catch { /* ignored */ }
            HandlingPacket = false;
        }

        NetworkDrone.Drones.Remove(stationId.Value);
    }

    private static void DestroyDrone(IdentifiableModel? model)
    {
        if (model == null)
            return;

        HandlingPacket = true;

        try
        {
            var gameObj = model.GetGameObject();

            if (gameObj)
                Destroyer.DestroyAny(gameObj, "SR2MP.ReplaceDrone");
            else
                GameState.DestroyIdentifiableModel(model);
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"Failed to destroy drone {model.actorId.Value}: {ex.Message}");
        }

        HandlingPacket = false;

        ActorManager.Actors.Remove(model.actorId.Value);
    }

    internal static void AssignOwnershipOfUnowned()
    {
        if (!Main.Server.IsRunning)
            return;

        if (SceneContext.Instance?.player == null)
            return;

        var allPlayers = new List<(string PlayerId, Vector3 Position)>
        {
            (LocalID, SceneContext.Instance.player.transform.position)
        };

        foreach (var (playerId, playerObject) in PlayerObjects)
        {
            if (playerObject)
                allPlayers.Add((playerId, playerObject.transform.position));
        }

        foreach (var (stationId, drone) in NetworkDrone.Drones.ToArray())
        {
            try
            {
                if (!drone || drone.IsHibernated)
                    continue;

                if (drone.CurrentOwnerId == LocalID || PlayerManager.CheckPlayerExists(drone.CurrentOwnerId))
                    continue;

                var position = drone.transform.position;
                var previousOwnerId = drone.CurrentOwnerId;
                var newOwner = LocalID;
                var bestDistance = float.MaxValue;

                foreach (var (playerId, playerPosition) in allPlayers)
                {
                    var distance = (playerPosition - position).sqrMagnitude;

                    if (distance >= bestDistance)
                        continue;

                    bestDistance = distance;
                    newOwner = playerId;
                }

                drone.CurrentOwnerId = newOwner;
                drone.LocallyOwned = newOwner == LocalID;
                CachedOwners[stationId] = newOwner;

                Main.SendToAllOrServer(new Packets.Drone.DroneOwnershipPacket
                {
                    StationId = stationId,
                    ClaimerID = newOwner,
                    PreviousOwnerID = previousOwnerId
                });
            }
            catch { /* ignored */ }
        }
    }
}

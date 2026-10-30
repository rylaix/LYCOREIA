using SR2MP.Components.LandPlots;
using SR2MP.Packets.LandPlots;

namespace SR2MP.Shared.Managers;

internal static class NetworkGardenManager
{
    private static readonly Dictionary<string, NetworkGarden> Gardens = new();
    
    private static readonly Dictionary<string, string> KnownOwners = new();

    private static readonly HashSet<string> PendingClaims = new();
    private static readonly Dictionary<string, string> GardenIdsByPlot = new(StringComparer.Ordinal);
    private static readonly GardenStateCache StateCache = new();
    private static readonly GardenCropStateCache CropStateCache = new();

    private static int lastTickFrame = -1;
    private static float nextClaimFlush;
    private static float nextStateFlush;
    private const float ClaimFlushInterval = 1f;
    private const float StateFlushInterval = 5f;

    internal static void OnServerStarted()
    {
        KnownOwners.Clear();
        PendingClaims.Clear();
        StateCache.Clear();
        CropStateCache.Clear();

        EnsureNetworkGardenComponents();

        var claimed = 0;
        foreach (var garden in Gardens.Values)
        {
            if (!garden || garden.IsHibernated)
                continue;

            garden.TakeOwnership(broadcast: false);
            claimed++;
        }

        SrLogger.LogDebug($"Host took ownership of {claimed} registered gardens on server start.");
    }

    internal static void OnClientConnected()
    {
        StateCache.Clear();
        CropStateCache.Clear();
        EnsureNetworkGardenComponents();
    }

    private static void EnsureNetworkGardenComponents()
    {
        foreach (var spawnResource in Object.FindObjectsOfType<SpawnResource>(true))
        {
            if (spawnResource.GetComponent<NetworkGarden>() == null)
                spawnResource.gameObject.AddComponent<NetworkGarden>();
        }
    }

    internal static void OnDisconnected()
    {
        KnownOwners.Clear();
        PendingClaims.Clear();
        StateCache.Clear();
        CropStateCache.Clear();

        foreach (var garden in Gardens.Values)
        {
            if (garden)
                garden.ResetToVanilla();
        }
    }

    internal static void Register(NetworkGarden garden)
    {
        var id = garden.Id!;
        Gardens[id] = garden;
        var plotId = garden.LandPlotId;
        if (!string.IsNullOrWhiteSpace(plotId))
            GardenIdsByPlot[plotId] = id;
        SrLogger.LogGarden($"Garden registered in manager: id='{id}', total={Gardens.Count}");

        if (!string.IsNullOrEmpty(garden.OwnerId))
            KnownOwners[id] = garden.OwnerId;

        if (KnownOwners.TryGetValue(id, out var owner) && !string.IsNullOrEmpty(owner))
        {
            SrLogger.LogGarden($"Garden '{id}' restoring known owner '{owner}' on register");
            garden.SetOwner(owner);
        }

        if (StateCache.TryGetLatest(id, out var latestState) && !garden.LocallyOwned)
        {
            SrLogger.LogGarden($"Garden '{id}' applying cached revision {latestState.Revision} on register");
            garden.ApplyUpdate(latestState.NextSpawnTime, latestState.StoredWater, latestState.NextSpawnRipens);
        }
    }

    internal static void Unregister(NetworkGarden garden)
    {
        if (garden.Id != null && Gardens.TryGetValue(garden.Id, out var registered) && registered == garden)
            Gardens.Remove(garden.Id);
    }

    internal static void QueueClaim(string gardenId)
        => PendingClaims.Add(gardenId);

    internal static void ApplyOwnership(string gardenId, string ownerId)
    {
        KnownOwners[gardenId] = ownerId;

        if (!Gardens.TryGetValue(gardenId, out var garden) || !garden)
            return;

        // An empty owner is a release (the owner hibernated)
        // Others are free to take over
        if (string.IsNullOrEmpty(ownerId))
        {
            SrLogger.LogGarden($"Garden '{gardenId}' ownership release received");
            garden.OnOwnerReleased();
            ApplyLatestState(garden);
        }
        else
        {
            SrLogger.LogGarden($"Garden '{gardenId}' ownership claim received from '{ownerId}'");
            garden.SetOwner(ownerId);
            ApplyLatestState(garden);
        }
    }

    internal static void ApplySnapshotOwnership(string gardenId, string ownerId)
    {
        if (string.IsNullOrWhiteSpace(gardenId))
            return;

        ApplyOwnership(gardenId, ownerId ?? string.Empty);
    }

    internal static void RecordOwner(string? gardenId, string ownerId)
    {
        if (!string.IsNullOrWhiteSpace(gardenId))
            KnownOwners[gardenId] = ownerId ?? string.Empty;
    }

    internal static string GetOwnerForSnapshot(string gardenId, NetworkGarden? garden)
    {
        if (KnownOwners.TryGetValue(gardenId, out var ownerId))
            return ownerId;

        return garden?.OwnerId ?? string.Empty;
    }

    internal static GardenOwnershipPacket CreateOwnershipSnapshot()
    {
        var owners = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (gardenId, ownerId) in KnownOwners)
        {
            if (!string.IsNullOrWhiteSpace(gardenId))
                owners[gardenId] = ownerId ?? string.Empty;
        }

        foreach (var (gardenId, garden) in Gardens)
        {
            if (string.IsNullOrWhiteSpace(gardenId) || !garden)
                continue;

            owners[gardenId] = GetOwnerForSnapshot(gardenId, garden);
        }

        return new GardenOwnershipPacket
        {
            Entries = owners.Select(entry => new GardenOwnershipPacket.Entry
            {
                GardenId = entry.Key,
                OwnerId = entry.Value
            }).ToList()
        };
    }

    internal static bool TryCreateOwnerCorrection(
        string gardenId,
        string senderId,
        string requestedOwnerId,
        out GardenOwnershipPacket.Entry correction)
    {
        correction = default;
        if (string.IsNullOrWhiteSpace(gardenId) ||
            !KnownOwners.TryGetValue(gardenId, out var currentOwnerId) ||
            !ShouldSendOwnerCorrection(true, currentOwnerId, senderId, requestedOwnerId))
            return false;

        correction = new GardenOwnershipPacket.Entry
        {
            GardenId = gardenId,
            OwnerId = currentOwnerId
        };
        return true;
    }

    internal static bool ShouldSendOwnerCorrection(
        bool hasKnownOwner,
        string currentOwnerId,
        string senderId,
        string requestedOwnerId)
        => hasKnownOwner &&
           !string.IsNullOrWhiteSpace(currentOwnerId) &&
           !string.IsNullOrWhiteSpace(senderId) &&
           string.Equals(requestedOwnerId, senderId, StringComparison.Ordinal) &&
           !string.Equals(currentOwnerId, senderId, StringComparison.Ordinal);

    internal static bool IsCurrentOwner(string gardenId, string playerId)
        => !string.IsNullOrWhiteSpace(gardenId) &&
           !string.IsNullOrWhiteSpace(playerId) &&
           KnownOwners.TryGetValue(gardenId, out var currentOwner) &&
           string.Equals(currentOwner, playerId, StringComparison.Ordinal);

    internal static bool ShouldReclaimOwnershipOnWake(string currentOwnerId, string localId)
        => !string.IsNullOrWhiteSpace(localId) &&
           string.Equals(currentOwnerId, localId, StringComparison.Ordinal);

    internal static bool CanAcceptOwnershipChange(string gardenId, string senderId, string requestedOwnerId)
    {
        if (string.IsNullOrWhiteSpace(gardenId) || string.IsNullOrWhiteSpace(senderId))
            return false;

        KnownOwners.TryGetValue(gardenId, out var currentOwnerId);
        currentOwnerId ??= string.Empty;

        if (string.IsNullOrEmpty(requestedOwnerId))
            return string.Equals(currentOwnerId, senderId, StringComparison.Ordinal);

        if (!string.Equals(requestedOwnerId, senderId, StringComparison.Ordinal))
            return false;

        var ownerConnected = IsOwnerConnected(
            currentOwnerId,
            LocalID,
            !string.IsNullOrEmpty(currentOwnerId) && PlayerManager.CheckPlayerExists(currentOwnerId));
        var ownerHibernated = Gardens.TryGetValue(gardenId, out var currentGarden) &&
                              currentGarden && currentGarden.IsHibernated;
        return IsValidOwnershipTransition(currentOwnerId, senderId, requestedOwnerId, ownerConnected, ownerHibernated);
    }

    internal static bool IsValidOwnershipTransition(
        string currentOwnerId,
        string senderId,
        string requestedOwnerId,
        bool ownerConnected,
        bool ownerHibernated)
    {
        if (string.IsNullOrWhiteSpace(senderId))
            return false;

        if (string.IsNullOrEmpty(requestedOwnerId))
            return string.Equals(currentOwnerId, senderId, StringComparison.Ordinal);

        if (!string.Equals(requestedOwnerId, senderId, StringComparison.Ordinal))
            return false;

        return string.Equals(currentOwnerId, senderId, StringComparison.Ordinal) ||
               string.IsNullOrEmpty(currentOwnerId) || !ownerConnected || ownerHibernated;
    }

    internal static bool IsOwnerConnected(string? ownerId, string? localPlayerId, bool remoteOwnerConnected)
        => !string.IsNullOrWhiteSpace(ownerId) &&
           (string.Equals(ownerId, localPlayerId, StringComparison.Ordinal) || remoteOwnerConnected);

#if SR2MP_TEST
    internal static bool VerifyOwnershipAuthorization()
        => IsValidOwnershipTransition(string.Empty, "owner-a", "owner-a", false, false)
           && !IsValidOwnershipTransition("owner-a", "owner-b", "owner-b", true, false)
           && IsValidOwnershipTransition("owner-a", "owner-b", "owner-b", true, true)
           && IsValidOwnershipTransition("owner-a", "owner-b", "owner-b", false, false)
           && IsValidOwnershipTransition(string.Empty, "owner-b", "owner-b", false, false)
           && IsValidOwnershipTransition("owner-b", "owner-b", string.Empty, true, false)
           && !IsValidOwnershipTransition("owner-a", "owner-b", string.Empty, true, false)
           && !IsValidOwnershipTransition("owner-a", "owner-b", "owner-a", true, false)
           && ShouldSendOwnerCorrection(true, "owner-a", "owner-b", "owner-b")
           && !ShouldSendOwnerCorrection(false, "owner-a", "owner-b", "owner-b")
           && !ShouldSendOwnerCorrection(true, string.Empty, "owner-b", "owner-b")
           && !ShouldSendOwnerCorrection(true, "owner-a", "owner-b", "owner-a")
           && !ShouldSendOwnerCorrection(true, "owner-a", "owner-a", "owner-a")
           && ShouldReclaimOwnershipOnWake("owner-a", "owner-a")
           && !ShouldReclaimOwnershipOnWake("owner-a", "owner-b")
           && !ShouldReclaimOwnershipOnWake("", "owner-b")
           // PlayerManager only tracks remote peers; the host's local owner must still
           // count as connected when the remote-player lookup returns false.
           && IsOwnerConnected("host", "host", remoteOwnerConnected: false)
           && !IsValidOwnershipTransition("host", "guest", "guest",
               IsOwnerConnected("host", "host", remoteOwnerConnected: false), ownerHibernated: false)
           && IsOwnerConnected("guest", "host", remoteOwnerConnected: true)
           && !IsOwnerConnected("", "host", remoteOwnerConnected: true);
#endif
    
    internal static void BroadcastRelease(string gardenId, GardenUpdatePacket.Entry? finalState)
    {
        KnownOwners[gardenId] = string.Empty;

        SrLogger.LogGarden($"Broadcasting garden release: id='{gardenId}'");

        var entry = new GardenOwnershipPacket.Entry
        {
            GardenId = gardenId,
            OwnerId = string.Empty
        };
        if (finalState.HasValue && IsValidFinalStateReleaseEntry(gardenId, string.Empty, finalState.Value))
        {
            var state = finalState.Value;
            if (Main.Server.IsRunning)
            {
                state.Revision = AssignServerRevision(state);
                ApplyState(state);
            }

            entry.HasFinalState = true;
            entry.FinalState = state;
        }
        else if (finalState.HasValue)
        {
            SrLogger.LogWarning($"Garden '{gardenId}' release omitted an invalid final state.");
        }

        Main.SendToAllOrServer(new GardenOwnershipPacket
        {
            Entries = new List<GardenOwnershipPacket.Entry> { entry }
        });
    }

    internal static bool IsValidFinalStateReleaseEntry(
        string gardenId,
        string requestedOwnerId,
        GardenUpdatePacket.Entry finalState)
        => string.IsNullOrEmpty(requestedOwnerId) && IsValidFinalStatePayload(gardenId, finalState);

    internal static bool IsValidFinalStatePayload(string gardenId, GardenUpdatePacket.Entry finalState)
        => !string.IsNullOrWhiteSpace(gardenId) &&
           string.Equals(gardenId, finalState.GardenId, StringComparison.Ordinal) &&
           double.IsFinite(finalState.NextSpawnTime) && float.IsFinite(finalState.StoredWater);

#if SR2MP_TEST
    internal static bool VerifyFinalStateReleaseRules()
    {
        var state = new GardenUpdatePacket.Entry
        {
            GardenId = "plot-a_0",
            NextSpawnTime = 123,
            StoredWater = 0.5f,
            NextSpawnRipens = true
        };

        return IsValidFinalStateReleaseEntry("plot-a_0", string.Empty, state) &&
               !IsValidFinalStateReleaseEntry("plot-b_0", string.Empty, state) &&
               !IsValidFinalStateReleaseEntry("plot-a_0", "PLAYER_OWNER_123", state) &&
               !IsValidFinalStateReleaseEntry("plot-a_0", string.Empty,
                   new GardenUpdatePacket.Entry { GardenId = "plot-a_0", NextSpawnTime = double.NaN }) &&
               !IsValidFinalStateReleaseEntry("plot-a_0", string.Empty,
                   new GardenUpdatePacket.Entry { GardenId = "plot-a_0", StoredWater = float.PositiveInfinity });
    }
#endif

    internal static void ApplyState(GardenUpdatePacket.Entry entry)
    {
        if (!StateCache.TryRemember(entry))
        {
            SrLogger.LogGarden($"Garden update ignored as invalid or stale: id='{entry.GardenId}', revision={entry.Revision}");
            return;
        }
        var found = Gardens.TryGetValue(entry.GardenId, out var garden) && garden;
        SrLogger.LogGarden($"Garden update received: id='{entry.GardenId}', revision={entry.Revision}, found={found}, nextSpawnTime={entry.NextSpawnTime}, storedWater={entry.StoredWater}");
        if (found)
        {
            if (!garden!.LocallyOwned)
                garden.ApplyUpdate(entry.NextSpawnTime, entry.StoredWater, entry.NextSpawnRipens);
            else if (KnownOwners.TryGetValue(entry.GardenId, out var owner) && owner != LocalID)
                garden.ApplyAuthoritativeUpdate(entry.NextSpawnTime, entry.StoredWater, entry.NextSpawnRipens);
        }
    }

    private static void ApplyLatestState(NetworkGarden garden)
    {
        if (string.IsNullOrEmpty(garden.Id) ||
            !StateCache.TryGetLatest(garden.Id!, out var latestState))
            return;

        if (garden.LocallyOwned)
            garden.ApplyAuthoritativeUpdate(latestState.NextSpawnTime, latestState.StoredWater, latestState.NextSpawnRipens);
        else
            garden.ApplyUpdate(latestState.NextSpawnTime, latestState.StoredWater, latestState.NextSpawnRipens);
    }

    internal static long AssignServerRevision(GardenUpdatePacket.Entry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.GardenId))
            return 0;

        return StateCache.NextRevision(entry.GardenId);
    }

    internal static string ResolveGardenIdForPlot(string plotId, NetworkGarden? preferredGarden = null)
    {
        if (string.IsNullOrWhiteSpace(plotId))
            return string.Empty;

        if (preferredGarden && !string.IsNullOrWhiteSpace(preferredGarden.Id))
            return preferredGarden.Id!;

        if (GardenIdsByPlot.TryGetValue(plotId, out var knownId) && !string.IsNullOrWhiteSpace(knownId))
            return knownId;

        if (GameState.landPlots.TryGetValue(plotId, out var plot) && plot != null && plot.gameObj)
        {
            var spawnResource = plot.gameObj.GetComponentInChildren<SpawnResource>(true);
            var garden = spawnResource ? spawnResource.GetComponent<NetworkGarden>() : null;
            if (garden && !string.IsNullOrWhiteSpace(garden.Id))
                return garden.Id!;
        }

        return $"{plotId}_0";
    }

    /// <summary>
    /// Assigns a host revision to a local plant/clear event. The same sequence is used by
    /// garden timer updates and the initial snapshot, so all garden state shares one order.
    /// </summary>
    internal static bool PrepareLocalCropUpdate(GardenPlantPacket packet, NetworkGarden? preferredGarden = null)
    {
        packet.GardenId = ResolveGardenIdForPlot(packet.ID, preferredGarden);
        if (string.IsNullOrWhiteSpace(packet.GardenId))
            return false;

        if (!Main.Server.IsRunning)
            return true;

        var revision = StateCache.NextRevision(packet.GardenId);
        if (!StateCache.AdvanceRevision(packet.GardenId, revision) ||
            !CropStateCache.TryRemember(packet.GardenId, revision, packet.ActorType))
            return false;

        packet.Revision = revision;
        return true;
    }

    /// <summary>Accepts a host-authored live crop delta, rejecting stale revisions.</summary>
    internal static bool TryAcceptCropUpdate(GardenPlantPacket packet)
    {
        if (string.IsNullOrWhiteSpace(packet.ID) || string.IsNullOrWhiteSpace(packet.GardenId) ||
            !StateCache.CanAcceptRevision(packet.GardenId, packet.Revision) ||
            !CropStateCache.TryRemember(packet.GardenId, packet.Revision, packet.ActorType))
            return false;

        return StateCache.AdvanceRevision(packet.GardenId, packet.Revision);
    }

    /// <summary>
    /// Applies the crop revision from a snapshot unless a newer live update already arrived.
    /// A null result means preserve the current local crop (unknown host crop or stale snapshot).
    /// </summary>
    internal static int? SelectSnapshotCrop(string gardenId, long revision, int snapshotCrop)
        => CropStateCache.SelectSnapshotCrop(gardenId, revision, snapshotCrop);

    /// <summary>Seeds the host's crop cache with the revision carried by a join snapshot.</summary>
    internal static void RecordSnapshotCrop(string gardenId, long revision, int crop)
    {
        var recorded = crop == int.MinValue
            ? CropStateCache.Invalidate(gardenId, revision)
            : CropStateCache.TryRemember(gardenId, revision, crop);
        if (!recorded)
            SrLogger.LogGarden($"Garden crop snapshot was already superseded: id='{gardenId}', revision={revision}");
    }

    internal static (GardenUpdatePacket.Entry Entry, bool HasState) CreateSnapshotState(
        string plotId, NetworkGarden? garden, SpawnResource? source, bool hasCrop)
    {
        GardenUpdatePacket.Entry? knownState = null;
        if (TryGetLatestForPlot(plotId, out var cachedState))
            knownState = cachedState;

        var gardenId = garden?.Id;
        if (string.IsNullOrWhiteSpace(gardenId) && knownState.HasValue)
            gardenId = knownState.Value.GardenId;
        if (string.IsNullOrWhiteSpace(gardenId))
            gardenId = $"{plotId}_0";

        var hasState = false;
        var entry = new GardenUpdatePacket.Entry { GardenId = gardenId };
        if (hasCrop)
        {
            if (garden && garden.LocallyOwned && source?._model != null)
            {
                entry.NextSpawnTime = source._model.nextSpawnTime;
                entry.StoredWater = source._model.storedWater;
                entry.NextSpawnRipens = source._model.nextSpawnRipens;
                hasState = true;
            }
            else if (knownState.HasValue && knownState.Value.GardenId == gardenId &&
                     knownState.Value.Revision >= CropStateCache.GetHighestRevision(gardenId))
            {
                entry.NextSpawnTime = knownState.Value.NextSpawnTime;
                entry.StoredWater = knownState.Value.StoredWater;
                entry.NextSpawnRipens = knownState.Value.NextSpawnRipens;
                hasState = true;
            }
            else if (source?._model != null)
            {
                entry.NextSpawnTime = source._model.nextSpawnTime;
                entry.StoredWater = source._model.storedWater;
                entry.NextSpawnRipens = source._model.nextSpawnRipens;
                hasState = true;
            }
        }

        entry.Revision = StateCache.NextRevision(gardenId);
        if (hasState)
            StateCache.TryRemember(entry);
        else
            StateCache.Invalidate(gardenId, entry.Revision);

        GardenIdsByPlot[plotId] = gardenId;
        return (entry, hasState);
    }

    internal static void ApplySnapshotState(string plotId, GardenUpdatePacket.Entry entry, bool hasState)
    {
        if (string.IsNullOrWhiteSpace(plotId) || string.IsNullOrWhiteSpace(entry.GardenId))
        {
            SrLogger.LogWarning($"InitialLandPlots: garden snapshot has no stable ID for plot '{plotId}'.");
            return;
        }

        GardenIdsByPlot[plotId] = entry.GardenId;
        if (hasState)
        {
            ApplyState(entry);
            return;
        }

        if (!StateCache.Invalidate(entry.GardenId, entry.Revision))
            return;

        SrLogger.LogGarden($"Garden snapshot invalidated stale cached state: id='{entry.GardenId}', revision={entry.Revision}");
    }

    private static bool TryGetLatestForPlot(string plotId, out GardenUpdatePacket.Entry entry)
    {
        if (GardenIdsByPlot.TryGetValue(plotId, out var knownId) && StateCache.TryGetLatest(knownId, out entry))
            return true;

        return StateCache.TryGetLatestWithPrefix($"{plotId}_", out entry);
    }
    
    internal static void Tick()
    {
        if (lastTickFrame == Time.frameCount)
            return;
        lastTickFrame = Time.frameCount;

        if (!Main.Server.IsRunning && !Main.Client.IsConnected)
            return;

        var now = Time.time;

        if (now >= nextClaimFlush)
        {
            nextClaimFlush = now + ClaimFlushInterval;
            FlushClaims();
        }

        if (now >= nextStateFlush)
        {
            nextStateFlush = now + StateFlushInterval;
            FlushStates();
        }
    }

    private static void FlushClaims()
    {
        if (PendingClaims.Count == 0)
            return;

        var entries = new List<GardenOwnershipPacket.Entry>(PendingClaims.Count);
        foreach (var id in PendingClaims)
        {
            entries.Add(new GardenOwnershipPacket.Entry { GardenId = id, OwnerId = LocalID });

            KnownOwners[id] = LocalID;
        }
        PendingClaims.Clear();

        SrLogger.LogGarden($"Broadcasting {entries.Count} garden claim(s)");
        Main.SendToAllOrServer(new GardenOwnershipPacket { Entries = entries });
    }

    private static void FlushStates()
    {
        var entriesByGarden = new Dictionary<string, GardenUpdatePacket.Entry>(StringComparer.Ordinal);

        foreach (var garden in Gardens.Values)
        {
            if (!garden || !garden.LocallyOwned || garden.IsHibernated)
                continue;

            var entry = garden.CaptureState();
            if (entry != null)
                entriesByGarden[entry.Value.GardenId] = entry.Value;
        }

        if (entriesByGarden.Count > 0)
        {
            var entries = entriesByGarden.Values.ToList();
            if (Main.Server.IsRunning)
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    entry.Revision = AssignServerRevision(entry);
                    entries[i] = entry;
                    ApplyState(entry);
                }
            }

            SrLogger.LogGarden($"Broadcasting {entries.Count} garden state(s)");
            Main.SendToAllOrServer(new GardenUpdatePacket { Entries = entries });
        }
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

        List<GardenOwnershipPacket.Entry>? entries = null;

        foreach (var (gardenId, owner) in KnownOwners.ToArray())
        {
            if (owner == LocalID || PlayerManager.CheckPlayerExists(owner))
                continue;

            Gardens.TryGetValue(gardenId, out var garden);

            var newOwner = LocalID;

            if (garden)
            {
                var position = garden!.transform.position;
                var bestDistance = float.MaxValue;

                foreach (var (playerId, playerPosition) in allPlayers)
                {
                    var distance = (playerPosition - position).sqrMagnitude;

                    if (distance >= bestDistance)
                        continue;

                    bestDistance = distance;
                    newOwner = playerId;
                }
            }

            KnownOwners[gardenId] = newOwner;

            SrLogger.LogGarden($"Garden '{gardenId}' reassigned from missing owner '{owner}' to '{newOwner}'");

            if (garden)
                garden!.SetOwner(newOwner);

            (entries ??= new()).Add(new GardenOwnershipPacket.Entry { GardenId = gardenId, OwnerId = newOwner });
        }

        if (entries != null)
        {
            SrLogger.LogGarden($"Broadcasting {entries.Count} garden reassignment(s) after owner left");
            Main.SendToAllOrServer(new GardenOwnershipPacket { Entries = entries });
        }
    }

    internal static void RestoreAfterTimeSkip()
    {
        foreach (var garden in Gardens.Values)
        {
            if (garden && !garden.LocallyOwned)
                garden.RestoreCachedState();
        }
    }
}

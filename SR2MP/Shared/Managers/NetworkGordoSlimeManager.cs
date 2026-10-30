using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.World;
using SR2MP.Handlers.GordoSlime;
using SR2MP.Patches.GordoSlime;
using SR2MP.Packets.GordoSlime;
using SR2MP.Packets.Loading;

namespace SR2MP.Shared.Managers;

internal static class NetworkGordoSlimeManager
{
    private static readonly HashSet<string> PoppedGordoSlimeIds = new();
    private static readonly GordoRewardGenerationLedger RewardGeneration = new();
    private static readonly HashSet<string> PendingRewardIds = new();
    private static readonly Dictionary<string, string> RewardAuthorities = new(StringComparer.Ordinal);
    private static readonly HashSet<GordoRewardIdentity> SpawnedRewardIds = new();
    private static readonly Dictionary<GordoRewardIdentity, BreakOnImpact> SpawnedRewardObjects = new();
    private static readonly GordoFeedRequestLedger AcceptedFeedRequests = new();
    private static readonly GordoFeedActorLedger AcceptedFeedActors = new();

    static NetworkGordoSlimeManager()
    {
        Main.Server.OnServerStarted += ResetFeedSession;
        Main.Client.OnConnected += _ => ResetFeedSession();
        Main.Client.OnDisconnected += ResetFeedSession;

#if SR2MP_TEST
        var passed = VerifyTestPolicies();
        SrLogger.LogMessage($"[SR2MP_TEST] gordo-reward-protocol-selftest={(passed ? "PASS" : "FAIL")}");
#endif
    }

#if SR2MP_TEST
    internal static bool VerifyTestPolicies()
        => VerifyRewardDeduplication()
           && GordoRewardGenerationLedger.VerifyRules()
           && GordoRewardCapturePolicy.VerifyRules()
           && SR2MP.Handlers.GordoSlime.GordoSlimeAuthorityPolicy.VerifyRules()
           && SR2MP.Handlers.GordoSlime.GordoFeedRequestLedger.VerifyRules()
           && SR2MP.Handlers.GordoSlime.GordoFeedActorLedger.VerifyOrderings()
           && SR2MP.Packets.GordoSlime.GordoSlimeFeedPacket.VerifySerializationRoundTrip()
           && VerifyRewardSnapshotPolicy()
           && GordoSlimeBurstPacket.VerifySerializationRoundTrip()
           && GordoSlimeRewardPacket.VerifySerializationRoundTrip()
           && InitialGordoSlimesPacket.VerifySerializationRoundTrip();
#endif

    internal static HashSet<string> ResetSnapshotState()
    {
        var previouslyPopped = new HashSet<string>(PoppedGordoSlimeIds);
        PoppedGordoSlimeIds.Clear();
        RewardGeneration.Clear();
        PendingRewardIds.Clear();
        RewardAuthorities.Clear();
        return previouslyPopped;
    }

    internal static void MarkPopped(string id) => PoppedGordoSlimeIds.Add(id);

    internal static bool TryMarkPopped(string id)
        => !string.IsNullOrWhiteSpace(id) && PoppedGordoSlimeIds.Add(id);

    internal static bool TryAcceptFeedRequest(string gordoId, string requestId)
    {
        return AcceptedFeedRequests.TryAccept(gordoId, requestId);
    }

    internal static bool HasAcceptedFeedRequest(string gordoId, string requestId)
        => AcceptedFeedRequests.Contains(gordoId, requestId);

    internal static bool TryGetAcceptedFeedActorCount(string gordoId, long actorId, out int acceptedCount)
        => AcceptedFeedActors.TryGetAcceptedCount(gordoId, actorId, out acceptedCount);

    internal static bool TryAcceptFeedActor(string gordoId, long actorId, int canonicalCount,
        out int acceptedCount)
        => AcceptedFeedActors.TryAccept(gordoId, actorId, canonicalCount, out acceptedCount);

    private static void ResetFeedSession()
    {
        AcceptedFeedRequests.Clear();
        AcceptedFeedActors.Clear();
    }

    internal static bool TryAcceptBurst(string gordoId, string playerId)
        => TryAcceptBurst(PoppedGordoSlimeIds, RewardAuthorities, gordoId, playerId);

    internal static bool TryAcceptBurst(ISet<string> poppedIds, IDictionary<string, string> rewardAuthorities,
        string gordoId, string rewardAuthorityId)
    {
        if (string.IsNullOrWhiteSpace(gordoId) || string.IsNullOrWhiteSpace(rewardAuthorityId) ||
            poppedIds.Contains(gordoId) || rewardAuthorities.ContainsKey(gordoId))
            return false;

        rewardAuthorities.Add(gordoId, rewardAuthorityId);
        poppedIds.Add(gordoId);
        return true;
    }

    internal static bool HasPopped(string id) => PoppedGordoSlimeIds.Contains(id);

    internal static bool HasGivenRewards(string id) => RewardGeneration.IsComplete(id);

    internal static bool TryBeginRewardGeneration(string id) => RewardGeneration.TryBegin(id);

    internal static void CompleteRewardGeneration(string id)
    {
        RewardGeneration.Complete(id);
        PendingRewardIds.Remove(id);
    }

    internal static void AbortRewardGeneration(string id) => RewardGeneration.Abort(id);

    internal static bool IsRewardGenerationInProgress(string id) => RewardGeneration.IsInProgress(id);

    internal static bool IsRewardGenerationUncertain(string id) => RewardGeneration.IsUncertain(id);

    internal static void MarkRewardGenerationUncertain(string id)
    {
        RewardGeneration.MarkUncertain(id);
        PendingRewardIds.Remove(id);
    }

    internal static void MarkRewarded(string id)
    {
        RewardGeneration.Complete(id);
        PendingRewardIds.Remove(id);
    }

    internal static void MarkRewardPending(string gordoId)
    {
        if (RewardGeneration.CanRetry(gordoId))
            PendingRewardIds.Add(gordoId);
    }

    internal static bool HasPendingHostReward(string gordoId)
        => Main.Server.IsRunning && !string.IsNullOrWhiteSpace(gordoId) &&
           PendingRewardIds.Contains(gordoId) && RewardGeneration.CanRetry(gordoId) &&
           IsRewardAuthority(gordoId, Main.Server.PlayerId);

    internal static bool TrySetRewardAuthority(string gordoId, string playerId)
        => TrySetRewardAuthority(RewardAuthorities, gordoId, playerId);

    private static bool TrySetRewardAuthority(IDictionary<string, string> authorities, string gordoId, string playerId)
    {
        if (string.IsNullOrWhiteSpace(gordoId) || string.IsNullOrWhiteSpace(playerId))
            return false;

        if (authorities.TryGetValue(gordoId, out var currentOwner))
            return string.Equals(currentOwner, playerId, StringComparison.Ordinal);

        authorities.Add(gordoId, playerId);
        return true;
    }

    internal static bool IsRewardAuthority(string gordoId, string playerId)
        => !string.IsNullOrWhiteSpace(playerId) && RewardAuthorities.TryGetValue(gordoId, out var owner)
           && string.Equals(owner, playerId, StringComparison.Ordinal);

    internal static bool TryMarkRewardSpawned(string gordoId, int prefabIndex, int rewardOrdinal)
        => !string.IsNullOrWhiteSpace(gordoId) && prefabIndex >= 0 && rewardOrdinal >= 0
           && SpawnedRewardIds.Add(new GordoRewardIdentity(gordoId, prefabIndex, rewardOrdinal));

    internal static void RegisterRewardObject(string gordoId, int prefabIndex, int rewardOrdinal,
        BreakOnImpact rewardObject)
    {
        if (!string.IsNullOrWhiteSpace(gordoId) && prefabIndex >= 0 && rewardOrdinal >= 0 && rewardObject)
            SpawnedRewardObjects[new GordoRewardIdentity(gordoId, prefabIndex, rewardOrdinal)] = rewardObject;
    }

    internal static SnapshotRewardRestoreDecision PrepareSnapshotReward(string gordoId, int prefabIndex,
        int rewardOrdinal, out BreakOnImpact? existingReward)
    {
        existingReward = null;
        if (string.IsNullOrWhiteSpace(gordoId) || prefabIndex < 0 || rewardOrdinal < 0)
            return SnapshotRewardRestoreDecision.Skip;

        var identity = new GordoRewardIdentity(gordoId, prefabIndex, rewardOrdinal);
        if (SpawnedRewardObjects.TryGetValue(identity, out var existing) && existing)
        {
            var decision = DecideSnapshotRewardRestore(
                hasExisting: true,
                activeInHierarchy: existing.gameObject.activeInHierarchy,
                breaking: existing._breaking);
            if (decision == SnapshotRewardRestoreDecision.Skip)
                return decision;

            if (decision is SnapshotRewardRestoreDecision.UpdateExisting or SnapshotRewardRestoreDecision.Reactivate)
            {
                existingReward = existing;
                return decision;
            }
        }

        SpawnedRewardObjects.Remove(identity);
        SpawnedRewardIds.Add(identity);
        return SnapshotRewardRestoreDecision.Instantiate;
    }

    internal static void ForgetRewardSpawned(string gordoId, int prefabIndex, int rewardOrdinal)
    {
        var identity = new GordoRewardIdentity(gordoId, prefabIndex, rewardOrdinal);
        SpawnedRewardIds.Remove(identity);
        SpawnedRewardObjects.Remove(identity);
    }

    internal static List<GordoSlimeRewardPacket> CaptureActiveRewardSnapshots(string gordoId)
    {
        var rewards = new List<GordoSlimeRewardPacket>();
        if (string.IsNullOrWhiteSpace(gordoId))
            return rewards;

        foreach (var pair in SpawnedRewardObjects)
        {
            if (!string.Equals(pair.Key.GordoId, gordoId, StringComparison.Ordinal) || !pair.Value ||
                !ShouldKeepLiveReward(pair.Value.gameObject.activeInHierarchy, pair.Value._breaking))
                continue;

            rewards.Add(new GordoSlimeRewardPacket
            {
                GordoSlimeId = pair.Key.GordoId,
                PrefabIndex = pair.Key.PrefabIndex,
                RewardOrdinal = pair.Key.RewardOrdinal,
                Position = pair.Value.transform.position,
                Rotation = pair.Value.transform.rotation
            });
        }

        rewards.Sort((left, right) =>
        {
            var prefab = left.PrefabIndex.CompareTo(right.PrefabIndex);
            return prefab != 0 ? prefab : left.RewardOrdinal.CompareTo(right.RewardOrdinal);
        });
        return rewards;
    }

    private static bool ShouldKeepLiveReward(bool activeInHierarchy, bool breaking)
        => activeInHierarchy && !breaking;

    internal static bool VerifyRewardSnapshotPolicy()
        => ShouldKeepLiveReward(activeInHierarchy: true, breaking: false)
           && !ShouldKeepLiveReward(activeInHierarchy: true, breaking: true)
           && !ShouldKeepLiveReward(activeInHierarchy: false, breaking: false)
           && DecideSnapshotRewardRestore(true, activeInHierarchy: true, breaking: false) == SnapshotRewardRestoreDecision.UpdateExisting
           && DecideSnapshotRewardRestore(true, activeInHierarchy: true, breaking: true) == SnapshotRewardRestoreDecision.Skip
           && DecideSnapshotRewardRestore(true, activeInHierarchy: false, breaking: false) == SnapshotRewardRestoreDecision.Reactivate
           && DecideSnapshotRewardRestore(false, activeInHierarchy: false, breaking: false) == SnapshotRewardRestoreDecision.Instantiate;

    private static SnapshotRewardRestoreDecision DecideSnapshotRewardRestore(bool hasExisting, bool activeInHierarchy,
        bool breaking)
    {
        if (!hasExisting)
            return SnapshotRewardRestoreDecision.Instantiate;
        if (breaking)
            return SnapshotRewardRestoreDecision.Skip;
        if (activeInHierarchy)
            return SnapshotRewardRestoreDecision.UpdateExisting;
        return SnapshotRewardRestoreDecision.Reactivate;
    }

    internal static bool VerifyRewardDeduplication()
    {
        var seen = new HashSet<GordoRewardIdentity>();
        var first = new GordoRewardIdentity("gordo-a", 3, 0);
        var secondOccurrence = new GordoRewardIdentity("gordo-a", 3, 1);
        var otherPrefab = new GordoRewardIdentity("gordo-a", 4, 0);
        var otherGordo = new GordoRewardIdentity("gordo-b", 3, 0);
        var authorities = new Dictionary<string, string>(StringComparer.Ordinal);
        var poppedIds = new HashSet<string>(StringComparer.Ordinal);
        var burstAcceptedByHost = TryAcceptBurst(poppedIds, authorities, "gordo-disconnect", "host-player");

        return seen.Add(first)
               && !seen.Add(first)
               && seen.Add(secondOccurrence)
               && seen.Add(otherPrefab)
               && seen.Add(otherGordo)
               && TrySetRewardAuthority(authorities, "gordo-a", "player-a")
               && TrySetRewardAuthority(authorities, "gordo-a", "player-a")
               && !TrySetRewardAuthority(authorities, "gordo-a", "player-b")
               && burstAcceptedByHost
               && authorities.TryGetValue("gordo-disconnect", out var authority) && authority == "host-player"
               && poppedIds.Contains("gordo-disconnect")
               && !TryAcceptBurst(poppedIds, authorities, "gordo-disconnect", "guest-player");
    }

    internal static void ApplySnapshotState(string id, GordoModel model, bool wasPreviouslyPopped)
    {
        if (!model.gameObj) return;

        var gordoSlimeComponent = model.gameObj.GetComponent<GordoEat>();
        if (!gordoSlimeComponent) return;

        gordoSlimeComponent.SetModel(model);

        if (PoppedGordoSlimeIds.Contains(id))
            model.gameObj.SetActive(false);
        else if (wasPreviouslyPopped)
            model.gameObj.SetActive(true);
    }
    
    internal static void ReapplyOnSpawn(GordoEat gordoSlime)
    {
        if (!Main.Server.IsRunning && !Main.Client.IsConnected) return;

        if (PoppedGordoSlimeIds.Contains(gordoSlime.Id))
        {
            if (!HasPendingHostReward(gordoSlime.Id))
                gordoSlime.gameObject.SetActive(false);
        }
    }
    
    internal static string? ResolveGordoSlimeId(GordoModel model)
    {
        if (model.gameObj)
        {
            var gordoSlime = model.gameObj.GetComponent<GordoEat>();
            if (gordoSlime != null && !string.IsNullOrEmpty(gordoSlime.Id))
                return gordoSlime.Id;
        }

        foreach (var pair in GameState.gordos)
        {
            if (pair.value.Equals(model))
                return pair.key;
        }

        return null;
    }

    private readonly record struct GordoRewardIdentity(string GordoId, int PrefabIndex, int RewardOrdinal);
}

internal enum SnapshotRewardRestoreDecision
{
    Skip,
    UpdateExisting,
    Reactivate,
    Instantiate
}

/// <summary>Tracks reward generation attempts without committing completion before native execution returns.</summary>
internal sealed class GordoRewardGenerationLedger
{
    private readonly HashSet<string> completed = new(StringComparer.Ordinal);
    private readonly HashSet<string> inProgress = new(StringComparer.Ordinal);
    private readonly HashSet<string> uncertain = new(StringComparer.Ordinal);

    internal bool TryBegin(string gordoId)
        => !string.IsNullOrWhiteSpace(gordoId) && !completed.Contains(gordoId) &&
           !uncertain.Contains(gordoId) && inProgress.Add(gordoId);

    internal void Complete(string gordoId)
    {
        if (string.IsNullOrWhiteSpace(gordoId))
            return;

        inProgress.Remove(gordoId);
        uncertain.Remove(gordoId);
        completed.Add(gordoId);
    }

    internal void Abort(string gordoId)
    {
        if (!string.IsNullOrWhiteSpace(gordoId))
            inProgress.Remove(gordoId);
    }

    internal bool IsComplete(string gordoId)
        => !string.IsNullOrWhiteSpace(gordoId) && completed.Contains(gordoId);

    internal bool IsInProgress(string gordoId)
        => !string.IsNullOrWhiteSpace(gordoId) && inProgress.Contains(gordoId);

    internal bool IsUncertain(string gordoId)
        => !string.IsNullOrWhiteSpace(gordoId) && uncertain.Contains(gordoId);

    internal bool CanRetry(string gordoId)
        => !string.IsNullOrWhiteSpace(gordoId) && !completed.Contains(gordoId) &&
           !inProgress.Contains(gordoId) && !uncertain.Contains(gordoId);

    internal bool MarkUncertain(string gordoId)
    {
        if (string.IsNullOrWhiteSpace(gordoId) || completed.Contains(gordoId))
            return false;

        inProgress.Remove(gordoId);
        return uncertain.Add(gordoId);
    }

    internal void Clear()
    {
        completed.Clear();
        inProgress.Clear();
        uncertain.Clear();
    }

    internal static bool VerifyRules()
    {
        var ledger = new GordoRewardGenerationLedger();
        return !ledger.TryBegin(string.Empty)
               && ledger.TryBegin("gordo-a")
               && ledger.IsInProgress("gordo-a")
               && !ledger.CanRetry("gordo-a")
               && !ledger.TryBegin("gordo-a")
               && !ledger.IsComplete("gordo-a")
               && ledger.AbortAndBegin("gordo-a")
               && !ledger.IsComplete("gordo-a")
               && ledger.CompleteAndIsComplete("gordo-a")
               && !ledger.TryBegin("gordo-a")
               && ledger.TryBegin("gordo-uncertain")
               && ledger.MarkUncertain("gordo-uncertain")
               && ledger.IsUncertain("gordo-uncertain")
               && !ledger.CanRetry("gordo-uncertain")
               && !ledger.TryBegin("gordo-uncertain");
    }

    private bool AbortAndBegin(string gordoId)
    {
        Abort(gordoId);
        return TryBegin(gordoId);
    }

    private bool CompleteAndIsComplete(string gordoId)
    {
        Complete(gordoId);
        return IsComplete(gordoId);
    }
}

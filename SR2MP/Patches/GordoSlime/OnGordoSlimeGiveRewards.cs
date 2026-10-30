using System.Collections;
using HarmonyLib;
using SR2MP.Packets.GordoSlime;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.GordoSlime;

[HarmonyPatch(typeof(GordoRewardsBase), nameof(GordoRewardsBase.GiveRewards))]
internal static class OnGordoSlimeGiveRewards
{
    private const float RewardCaptureRadius = 12f;

    internal sealed class RewardSnapshot
    {
        public string GordoSlimeId = string.Empty;
        public Vector3 GordoPosition;
        public GordoRewardsBase RewardsComponent = null!;
        public readonly HashSet<int> ActorIds = new();
        public readonly HashSet<int> BreakableIds = new();
        public readonly Dictionary<string, int> RewardPrefabIndexes = new(StringComparer.Ordinal);
        public readonly HashSet<int> PublishedActorIds = new();
        public readonly Dictionary<int, GordoRewardInstanceIdentity> RewardIdentitiesByInstance = new();
        public readonly Dictionary<int, int> NextRewardOrdinalByPrefab = new();
        public readonly HashSet<int> MarkedRewardInstances = new();
        public readonly HashSet<int> PublishedRewardInstances = new();
        public bool Networked;
    }

    public static bool Prefix(GordoRewardsBase __instance, out RewardSnapshot? __state)
    {
        __state = null;

        var eat = __instance._eat;
        if (!eat || string.IsNullOrWhiteSpace(eat.Id))
            return true;

        var id = eat.Id;
        var networked = Main.Server.IsRunning || Main.Client.IsConnected;

        // A client sends a burst request first. Only the peer selected by the host
        // may execute the vanilla reward routine for that Gordo.
        if (networked && !NetworkGordoSlimeManager.IsRewardAuthority(
                id, Main.Server.IsRunning ? Main.Server.PlayerId : Main.Client.PlayerId))
            return false;

        if (!NetworkGordoSlimeManager.TryBeginRewardGeneration(id))
            return false;

        try
        {
            var snapshot = new RewardSnapshot
            {
                GordoSlimeId = id,
                GordoPosition = eat.transform.position,
                RewardsComponent = __instance,
                Networked = networked
            };

            var rewards = __instance.TryCast<GordoRewards>()?.RewardPrefabs;
            if (rewards != null)
            {
                for (var i = 0; i < rewards.Length; i++)
                {
                    var prefab = rewards[i];
                    if (prefab && !snapshot.RewardPrefabIndexes.ContainsKey(prefab.name))
                        snapshot.RewardPrefabIndexes.Add(prefab.name, i);
                }
            }

            foreach (var actor in Resources.FindObjectsOfTypeAll<IdentifiableActor>())
            {
                if (actor)
                    snapshot.ActorIds.Add(actor.GetInstanceID());
            }

            foreach (var breakable in Resources.FindObjectsOfTypeAll<BreakOnImpact>())
            {
                if (breakable)
                    snapshot.BreakableIds.Add(breakable.GetInstanceID());
            }

            __state = snapshot;
            return true;
        }
        catch
        {
            NetworkGordoSlimeManager.AbortRewardGeneration(id);
            if (Main.Server.IsRunning)
                OnGordoSlimeRewardRecovery.Schedule(__instance, id);
            throw;
        }
    }

    public static void Postfix(RewardSnapshot? __state)
    {
        if (__state == null)
            return;

        // GiveRewards returned normally, so never rerun native payout just because
        // object indexing needs additional frames to finish.
        NetworkGordoSlimeManager.CompleteRewardGeneration(__state.GordoSlimeId);
        try
        {
            StartCoroutine(SpawnOverNetwork(__state));
        }
        catch (Exception exception)
        {
            // The native payout is already complete. Make one immediate indexing
            // attempt if Unity refused to schedule the retry coroutine.
            try
            {
                CaptureNewRewards(__state, reconcilePotentialSideEffects: false);
            }
            catch (Exception captureException)
            {
                SrLogger.LogWarning(
                    $"Gordo reward for {__state.GordoSlimeId} was generated but could not be indexed: " +
                    captureException.Message);
            }

            SrLogger.LogWarning(
                $"Could not schedule repeated reward indexing for Gordo {__state.GordoSlimeId}: {exception.Message}");
        }
    }

    [HarmonyFinalizer]
    public static Exception? Finalizer(Exception? __exception, RewardSnapshot? __state)
    {
        if (__exception != null && __state != null)
        {
            try
            {
                // Native reward creation may have thrown after spawning some items.
                // Inspect the delta before allowing another payout attempt. If the
                // scan is inconclusive or finds any spawned reward, block retries to
                // avoid duplicating a possibly partial reward bundle.
                StartCoroutine(SpawnOverNetwork(__state, matchingRewardCount =>
                {
                    var outcome = GordoRewardCapturePolicy.ClassifyFailureReconciliation(
                        scanSucceeded: matchingRewardCount.HasValue,
                        matchingRewardCount: matchingRewardCount.GetValueOrDefault());
                    if (outcome == GordoRewardFailureOutcome.SafeToRetry)
                    {
                        NetworkGordoSlimeManager.AbortRewardGeneration(__state.GordoSlimeId);
                        SrLogger.LogWarning(
                            $"Gordo reward generation for {__state.GordoSlimeId} threw before any configured reward appeared; retry remains pending.");
                        OnGordoSlimeRewardRecovery.Schedule(__state.RewardsComponent, __state.GordoSlimeId);
                    }
                    else
                    {
                        NetworkGordoSlimeManager.MarkRewardGenerationUncertain(__state.GordoSlimeId);
                        SrLogger.LogWarning(
                            $"Gordo reward generation for {__state.GordoSlimeId} threw after a matching reward appeared or reconciliation failed; automatic retry was stopped to prevent duplicates.");
                    }
                }));
            }
            catch (Exception reconciliationError)
            {
                NetworkGordoSlimeManager.MarkRewardGenerationUncertain(__state.GordoSlimeId);
                SrLogger.LogWarning(
                    $"Gordo reward generation for {__state.GordoSlimeId} failed and its outcome could not be reconciled: {reconciliationError.Message}");
            }
        }

        return __exception;
    }

    private static IEnumerator SpawnOverNetwork(RewardSnapshot snapshot, Action<int?>? onReconciled = null)
    {
        if (onReconciled != null)
            yield return new WaitForSecondsRealtime(GordoRewardCapturePolicy.FailureReconciliationDelaySeconds);

        var attempts = onReconciled == null ? GordoRewardCapturePolicy.IndexingAttempts : 1;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0)
                yield return new WaitForSecondsRealtime(GordoRewardCapturePolicy.IndexingRetryDelaySeconds);

            int? matchingRewardCount = null;
            try
            {
                matchingRewardCount = CaptureNewRewards(snapshot, reconcilePotentialSideEffects: onReconciled != null);
            }
            catch (Exception exception)
            {
                SrLogger.LogWarning(
                    $"Could not reconcile spawned rewards for Gordo {snapshot.GordoSlimeId} " +
                    $"(scan {attempt + 1}/{attempts}): {exception.Message}");
            }

            if (onReconciled == null)
                continue;

            onReconciled(matchingRewardCount);
            yield break;
        }
    }

    private static int CaptureNewRewards(RewardSnapshot snapshot, bool reconcilePotentialSideEffects)
    {
        var spawnedCount = 0;
        foreach (var actor in Resources.FindObjectsOfTypeAll<IdentifiableActor>())
        {
            if (!actor || snapshot.ActorIds.Contains(actor.GetInstanceID()) ||
                !TryGetPrefabIndex(actor.gameObject, snapshot.RewardPrefabIndexes, out _))
                continue;

            var nearGordo = IsNearGordo(actor.transform.position, snapshot.GordoPosition);
            if (!nearGordo && !reconcilePotentialSideEffects)
                continue;

            spawnedCount++;
            var actorId = actor.GetInstanceID();
            if (snapshot.Networked && nearGordo && !snapshot.PublishedActorIds.Contains(actorId))
            {
                ActorManager.RegisterSpawnOverNetwork(actor.gameObject);
                snapshot.PublishedActorIds.Add(actorId);
            }
        }

        var breakables = new List<(BreakOnImpact Breakable, int PrefabIndex)>();
        foreach (var breakable in Resources.FindObjectsOfTypeAll<BreakOnImpact>())
        {
            if (!breakable || snapshot.BreakableIds.Contains(breakable.GetInstanceID()) ||
                breakable.GetComponent<IdentifiableActor>())
                continue;

            if (!TryGetPrefabIndex(breakable.gameObject, snapshot.RewardPrefabIndexes, out var prefabIndex))
                continue;

            var nearGordo = IsNearGordo(breakable.transform.position, snapshot.GordoPosition);
            if (!nearGordo && !reconcilePotentialSideEffects)
                continue;

            // Count a configured reward even if it is already breaking: its
            // existence means replaying GiveRewards could duplicate a partial payout.
            spawnedCount++;
            if (nearGordo)
                breakables.Add((breakable, prefabIndex));
        }

        breakables.Sort((left, right) => CompareRewardSpawn(left, right));
        foreach (var (breakable, prefabIndex) in breakables)
        {
            if (!breakable)
                continue;

            var instanceId = breakable.GetInstanceID();
            if (snapshot.RewardIdentitiesByInstance.TryGetValue(instanceId, out var existingIdentity))
            {
                if (existingIdentity.PrefabIndex != prefabIndex)
                    continue;
            }
            else
            {
                var identity = GordoRewardCapturePolicy.GetOrAssignIdentity(
                    snapshot.RewardIdentitiesByInstance,
                    snapshot.NextRewardOrdinalByPrefab,
                    instanceId,
                    prefabIndex);
                if (identity.PrefabIndex < 0 || identity.RewardOrdinal < 0)
                    continue;
            }

            var rewardIdentity = snapshot.RewardIdentitiesByInstance[instanceId];
            if (breakable._breaking)
                continue;

            if (!snapshot.MarkedRewardInstances.Contains(instanceId))
            {
                if (!NetworkGordoSlimeManager.TryMarkRewardSpawned(
                        snapshot.GordoSlimeId, rewardIdentity.PrefabIndex, rewardIdentity.RewardOrdinal))
                    continue;

                snapshot.MarkedRewardInstances.Add(instanceId);
            }

            var rewardPacket = new GordoSlimeRewardPacket
            {
                GordoSlimeId = snapshot.GordoSlimeId,
                PrefabIndex = rewardIdentity.PrefabIndex,
                RewardOrdinal = rewardIdentity.RewardOrdinal,
                Position = breakable.transform.position,
                Rotation = breakable.transform.rotation
            };
            NetworkGordoSlimeManager.RegisterRewardObject(
                snapshot.GordoSlimeId, rewardIdentity.PrefabIndex, rewardIdentity.RewardOrdinal, breakable);
            if (snapshot.Networked && !snapshot.PublishedRewardInstances.Contains(instanceId))
            {
                Main.SendToAllOrServer(rewardPacket);
                snapshot.PublishedRewardInstances.Add(instanceId);
            }
        }

        return spawnedCount;
    }

    private static bool IsNearGordo(Vector3 position, Vector3 gordoPosition)
        => (position - gordoPosition).sqrMagnitude <= RewardCaptureRadius * RewardCaptureRadius;

    private static bool TryGetPrefabIndex(GameObject instance, Dictionary<string, int> prefabIndexes, out int prefabIndex)
    {
        var name = instance.name;
        const string cloneSuffix = "(Clone)";
        if (name.EndsWith(cloneSuffix, StringComparison.Ordinal))
            name = name[..^cloneSuffix.Length];

        return prefabIndexes.TryGetValue(name, out prefabIndex);
    }

    private static int CompareRewardSpawn((BreakOnImpact Breakable, int PrefabIndex) left,
        (BreakOnImpact Breakable, int PrefabIndex) right)
    {
        var prefab = left.PrefabIndex.CompareTo(right.PrefabIndex);
        if (prefab != 0) return prefab;

        var leftPosition = left.Breakable.transform.position;
        var rightPosition = right.Breakable.transform.position;
        var x = leftPosition.x.CompareTo(rightPosition.x);
        if (x != 0) return x;
        var y = leftPosition.y.CompareTo(rightPosition.y);
        if (y != 0) return y;
        return leftPosition.z.CompareTo(rightPosition.z);
    }
}

// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Lycoreia.Snapshot;

public enum ActorDeltaKind { ActorSpawn, ActorDestroy, ActorTransfer, ActorUpdate, ActorState, ActorUnload, ResourceAttach }


/// <summary>
/// Buffers live actor mutations while a client replaces its world from an
/// authoritative InitialActors snapshot. The queue itself is pure so ordering,
/// cancellation and overflow behavior can be checked without Unity.
/// </summary>
public sealed class ActorSnapshotDeltaQueue<T>
{
    private readonly record struct Entry(
        T Value, long Revision, bool HasRevision, int ArrivalOrder, bool AlreadyApplied);

    private readonly List<Entry> pending = new();
    private readonly List<Entry> coveredBySnapshot = new();
    private readonly int maximumPending;
    private long generation;
    private long snapshotRevision;
    private int nextArrivalOrder;
    private bool snapshotRevisionKnown;
    private bool hasSnapshotRevision;
    private bool buffering;

    public ActorSnapshotDeltaQueue(int maximumPending = 262_144)
    {
        this.maximumPending = Math.Max(1, maximumPending);
    }

    public bool IsBuffering => buffering;

    public long Begin()
    {
        if (!buffering)
        {
            buffering = true;
            generation++;
            coveredBySnapshot.Clear();
            snapshotRevisionKnown = false;
            hasSnapshotRevision = false;
            snapshotRevision = 0;
            nextArrivalOrder = 0;
        }

        return generation;
    }

    public long BeginSnapshot()
    {
        if (!buffering)
        {
            buffering = true;
            coveredBySnapshot.Clear();
            nextArrivalOrder = 0;
        }

        snapshotRevisionKnown = false;
        hasSnapshotRevision = false;
        snapshotRevision = 0;

        // A newer snapshot supersedes a coroutine still waiting on an older one,
        // but keeps deltas buffered since the approval/resync barrier.
        return ++generation;
    }

    public bool IsCurrent(long snapshotGeneration)
        => buffering && snapshotGeneration == generation;

    public int SetSnapshotRevision(long snapshotGeneration, bool hasRevision, long revision)
    {
        if (!IsCurrent(snapshotGeneration))
            return 0;

        snapshotRevisionKnown = true;
        hasSnapshotRevision = hasRevision;
        snapshotRevision = revision;
        if (!hasRevision)
            return 0;

        var staleEntries = pending.Where(entry => entry.HasRevision &&
            !ActorRevisionOrderingPolicy.ShouldApplyDelta(true, snapshotRevision, true, entry.Revision)).ToArray();
        coveredBySnapshot.AddRange(staleEntries);
        var staleCount = pending.RemoveAll(entry => entry.HasRevision &&
            !ActorRevisionOrderingPolicy.ShouldApplyDelta(true, snapshotRevision, true, entry.Revision));
        return staleCount;
    }

    public bool TryEnqueue(T delta, bool hasRevision, long revision, out T[] overflowReplay,
        bool alreadyApplied = false)
    {
        overflowReplay = Array.Empty<T>();
        if (!buffering)
            return false;

        var coveredBySnapshot = snapshotRevisionKnown && hasSnapshotRevision && hasRevision &&
                                !ActorRevisionOrderingPolicy.ShouldApplyDelta(true, snapshotRevision, true, revision);
        if (coveredBySnapshot && pending.Count + this.coveredBySnapshot.Count < maximumPending)
        {
            // The snapshot normally makes this event stale, but retain it until
            // the snapshot actually applies so a timeout can roll back safely.
            this.coveredBySnapshot.Add(new Entry(delta, revision, hasRevision, nextArrivalOrder++, alreadyApplied));
            return true;
        }

        if (!coveredBySnapshot && pending.Count + this.coveredBySnapshot.Count < maximumPending)
        {
            pending.Add(new Entry(delta, revision, hasRevision, nextArrivalOrder++, alreadyApplied));
            return true;
        }

        // Never silently discard a reliable mutation. Replaying all retained
        // entries invalidates the in-flight snapshot; the caller logs the abort.
        var overflowEntry = new Entry(delta, revision, hasRevision, nextArrivalOrder++, alreadyApplied);
        if (coveredBySnapshot)
            this.coveredBySnapshot.Add(overflowEntry);
        else
            pending.Add(overflowEntry);
        // Seeded journal entries have already been applied to the pre-snapshot
        // world. If this snapshot is abandoned, only intercepted live packets
        // should be replayed; the seed remains in the journal for the next sync.
        overflowReplay = TakeReplay(filterStale: false, includeCovered: true, onlyUnapplied: true);
        pending.Clear();
        this.coveredBySnapshot.Clear();
        buffering = false;
        generation++;
        snapshotRevisionKnown = false;
        hasSnapshotRevision = false;
        snapshotRevision = 0;
        return true;
    }

    public bool Complete(long snapshotGeneration, out T[] replay)
    {
        replay = Array.Empty<T>();
        if (!IsCurrent(snapshotGeneration))
            return false;

        replay = TakeReplay(filterStale: true);
        pending.Clear();
        coveredBySnapshot.Clear();
        buffering = false;
        generation++;
        return true;
    }

    public bool Abort(long snapshotGeneration, out T[] replay)
    {
        replay = Array.Empty<T>();
        if (!IsCurrent(snapshotGeneration))
            return false;

        // A snapshot that timed out or failed before application cannot be used
        // as a watermark. Replay every buffered event against the existing world.
        replay = TakeReplay(filterStale: false, includeCovered: true, onlyUnapplied: true);
        pending.Clear();
        coveredBySnapshot.Clear();
        buffering = false;
        generation++;
        snapshotRevisionKnown = false;
        hasSnapshotRevision = false;
        snapshotRevision = 0;
        nextArrivalOrder = 0;
        return true;
    }

    public void Cancel()
    {
        pending.Clear();
        coveredBySnapshot.Clear();
        buffering = false;
        generation++;
        snapshotRevisionKnown = false;
        hasSnapshotRevision = false;
        snapshotRevision = 0;
        nextArrivalOrder = 0;
    }

    private T[] TakeReplay(bool filterStale, bool includeCovered = false, bool onlyUnapplied = false)
    {
        IEnumerable<Entry> entries = includeCovered ? pending.Concat(coveredBySnapshot) : pending;
        if (filterStale && snapshotRevisionKnown && hasSnapshotRevision)
            entries = entries.Where(entry => !entry.HasRevision ||
                ActorRevisionOrderingPolicy.ShouldApplyDelta(true, snapshotRevision, true, entry.Revision));
        if (onlyUnapplied)
            entries = entries.Where(entry => !entry.AlreadyApplied);

        var ordered = entries.All(entry => entry.HasRevision)
            ? entries.OrderBy(entry => entry.Revision)
            : entries.OrderBy(entry => entry.ArrivalOrder);
        return ordered.Select(entry => entry.Value).ToArray();
    }

#if LYC_TEST
    public static bool VerifyOrderingAndCleanup()
    {
        // This ordering case retains two deltas before the snapshot watermark,
        // then buffers a stale and a newer delta while keeping rollback possible.
        // Separate cases below exercise overflow at the smaller limit.
        var queue = new ActorSnapshotDeltaQueue<int>(maximumPending: 4);
        // Client.Connect opens the barrier before ReceiveLoop. An actor delta can
        // therefore arrive before ConnectionApprove is dispatched; approval must
        // preserve that same queue until the snapshot watermark is known.
        var connectGeneration = queue.Begin();
        if (!queue.TryEnqueue(1, hasRevision: true, revision: 8, out var overflow) || overflow.Length != 0 ||
            !queue.TryEnqueue(11, hasRevision: true, revision: 11, out overflow) || overflow.Length != 0)
            return false;

        var approvalGeneration = queue.Begin();
        if (approvalGeneration != connectGeneration)
            return false;

        var firstSnapshot = queue.BeginSnapshot();
        if (queue.IsCurrent(approvalGeneration) || queue.SetSnapshotRevision(firstSnapshot, true, 10) != 1 ||
            !queue.TryEnqueue(9, hasRevision: true, revision: 9, out overflow) || overflow.Length != 0 ||
            !queue.TryEnqueue(12, hasRevision: true, revision: 12, out overflow) || overflow.Length != 0)
            return false;

        if (!queue.Complete(firstSnapshot, out var ordered) || ordered.Length != 2 ||
            ordered[0] != 11 || ordered[1] != 12 || queue.IsBuffering)
            return false;

        queue.Begin();
        queue.BeginSnapshot();
        queue.TryEnqueue(4, hasRevision: true, revision: 4, out _);
        queue.TryEnqueue(3, hasRevision: true, revision: 3, out _);
        if (!queue.Complete(queue.BeginSnapshot(), out var reordered) || reordered.Length != 2 ||
            reordered[0] != 3 || reordered[1] != 4)
            return false;

        queue.Begin();
        var cancelledSnapshot = queue.BeginSnapshot();
        queue.TryEnqueue(6, hasRevision: false, revision: 0, out _);
        queue.Cancel();
        if (queue.IsBuffering || queue.TryEnqueue(7, hasRevision: false, revision: 0, out _) ||
            queue.Complete(cancelledSnapshot, out var afterCancel) || afterCancel.Length != 0)
            return false;

        // The ordering checks above need room to retain rollback deltas; this
        // case deliberately uses a smaller queue to verify bounded overflow.
        queue = new ActorSnapshotDeltaQueue<int>(maximumPending: 2);
        queue.Begin();
        queue.BeginSnapshot();
        queue.TryEnqueue(13, hasRevision: true, revision: 13, out _);
        queue.TryEnqueue(14, hasRevision: true, revision: 14, out _);
        if (!queue.TryEnqueue(15, hasRevision: true, revision: 15, out var overflowReplay) ||
            queue.IsBuffering || overflowReplay.Length != 3 || overflowReplay[0] != 13 ||
            overflowReplay[1] != 14 || overflowReplay[2] != 15)
            return false;

        queue.Begin();
        var timedOutSnapshot = queue.BeginSnapshot();
        queue.SetSnapshotRevision(timedOutSnapshot, true, 20);
        queue.TryEnqueue(19, hasRevision: true, revision: 19, out _);
        queue.TryEnqueue(21, hasRevision: true, revision: 21, out _);
        if (!queue.Abort(timedOutSnapshot, out var timeoutReplay) ||
            queue.IsBuffering || timeoutReplay.Length != 2 ||
            timeoutReplay[0] != 19 || timeoutReplay[1] != 21)
            return false;

        queue = new ActorSnapshotDeltaQueue<int>(maximumPending: 4);
        queue.Begin();
        var failedSnapshot = queue.BeginSnapshot();
        if (!queue.TryEnqueue(90, true, 9, out overflow, alreadyApplied: true) || overflow.Length != 0 ||
            !queue.TryEnqueue(110, true, 11, out overflow, alreadyApplied: true) || overflow.Length != 0 ||
            queue.SetSnapshotRevision(failedSnapshot, true, 10) != 1 ||
            !queue.TryEnqueue(120, true, 12, out overflow) || overflow.Length != 0 ||
            !queue.Abort(failedSnapshot, out var retryReplay) || retryReplay.Length != 1 || retryReplay[0] != 120)
            return false;

        queue = new ActorSnapshotDeltaQueue<int>(maximumPending: 2);
        queue.Begin();
        queue.BeginSnapshot();
        if (!queue.TryEnqueue(100, true, 10, out overflow, alreadyApplied: true) || overflow.Length != 0 ||
            !queue.TryEnqueue(11, true, 11, out overflow) || overflow.Length != 0 ||
            !queue.TryEnqueue(12, true, 12, out var overflowAfterSeed) ||
            overflowAfterSeed.Length != 2 || overflowAfterSeed[0] != 11 || overflowAfterSeed[1] != 12)
            return false;

        return true;
    }
#endif
}


public readonly record struct ActorDeltaJournalKey(ActorDeltaKind ActorDeltaKind, long ActorId, long Revision);

public enum InitialActorSnapshotEntryResult
{
    ExpectedSkip,
    Spawned,
    RuntimeFailure
}

public static class InitialActorSnapshotPolicy
{
    public static bool IsExpectedSkip(bool validActorId, bool uniqueSnapshotId,
        bool supportedType, bool hasPrefab, bool playerIdCollision)
        => !validActorId || !uniqueSnapshotId || !supportedType || !hasPrefab || playerIdCollision;

    public static InitialActorSnapshotEntryResult ClassifyAttempt(
        bool expectedSkip, bool spawnSucceeded, bool threw)
    {
        if (expectedSkip)
            return InitialActorSnapshotEntryResult.ExpectedSkip;

        return threw || !spawnSucceeded
            ? InitialActorSnapshotEntryResult.RuntimeFailure
            : InitialActorSnapshotEntryResult.Spawned;
    }

    public static bool ShouldCommitSnapshot(bool hasRuntimeFailure)
        => !hasRuntimeFailure;

    public static bool ShouldAutomaticallyRetry(int failureNumber, int maximumRetries)
        => failureNumber > 0 && failureNumber <= maximumRetries;

#if LYC_TEST
    public static bool VerifyRules()
    {
        const int retries = 2;
        return IsExpectedSkip(false, true, true, true, false) &&
               IsExpectedSkip(true, false, true, true, false) &&
               IsExpectedSkip(true, true, false, false, false) &&
               IsExpectedSkip(true, true, true, true, true) &&
               !IsExpectedSkip(true, true, true, true, false) &&
               ClassifyAttempt(true, false, false) == InitialActorSnapshotEntryResult.ExpectedSkip &&
               ClassifyAttempt(false, true, false) == InitialActorSnapshotEntryResult.Spawned &&
               ClassifyAttempt(false, false, false) == InitialActorSnapshotEntryResult.RuntimeFailure &&
               ClassifyAttempt(false, true, true) == InitialActorSnapshotEntryResult.RuntimeFailure &&
               ShouldCommitSnapshot(hasRuntimeFailure: false) &&
               !ShouldCommitSnapshot(hasRuntimeFailure: true) &&
               ShouldAutomaticallyRetry(1, retries) && ShouldAutomaticallyRetry(2, retries) &&
               !ShouldAutomaticallyRetry(3, retries) && !ShouldAutomaticallyRetry(0, retries);
    }
#endif
}

public static class ActorSyncGenerationPolicy
{
    public static bool ShouldBeginSync(long incomingSyncId, long latestSyncId, long closedSnapshotSyncId)
        => incomingSyncId <= 0 || incomingSyncId > latestSyncId ||
           (incomingSyncId == latestSyncId && incomingSyncId != closedSnapshotSyncId);

    public static bool ShouldBeginSnapshot(long incomingSyncId, long latestSyncId,
        long closedSnapshotSyncId, long activeSnapshotSyncId, bool activeSnapshotPacketReceived)
        => incomingSyncId <= 0 || incomingSyncId > latestSyncId ||
           (incomingSyncId == latestSyncId && incomingSyncId != closedSnapshotSyncId &&
            !(incomingSyncId == activeSnapshotSyncId && activeSnapshotPacketReceived));

#if LYC_TEST
    public static bool VerifyRules()
    {
        const long active = 7;
        const long newer = 8;
        var approvalFirstQueue = new ActorSnapshotDeltaQueue<int>();
        var connectGeneration = approvalFirstQueue.Begin();
        if (!approvalFirstQueue.TryEnqueue(101, true, 101, out var overflow) || overflow.Length != 0 ||
            !ShouldBeginSync(active, latestSyncId: 0, closedSnapshotSyncId: 0) ||
            approvalFirstQueue.Begin() != connectGeneration ||
            !ShouldBeginSnapshot(active, latestSyncId: active, closedSnapshotSyncId: 0,
                activeSnapshotSyncId: 0, activeSnapshotPacketReceived: false))
            return false;

        var approvalFirstSnapshot = approvalFirstQueue.BeginSnapshot();
        if (approvalFirstQueue.SetSnapshotRevision(approvalFirstSnapshot, true, 100) != 0 ||
            !approvalFirstQueue.Complete(approvalFirstSnapshot, out var replay) || replay.Length != 1 ||
            replay[0] != 101 || approvalFirstQueue.IsBuffering)
            return false;

        // If the snapshot finishes first, the matching late approval is already
        // complete and must not open a fresh empty barrier.
        var snapshotFirstQueue = new ActorSnapshotDeltaQueue<int>();
        var snapshotFirst = snapshotFirstQueue.BeginSnapshot();
        // This actor delta was applied before the sync marker arrived, but the
        // client retained it in the applied-delta journal and seeds the barrier.
        var preBarrierJournal = new[] { (Value: 202, Revision: 202L) };
        foreach (var delta in preBarrierJournal)
        {
            if (!snapshotFirstQueue.TryEnqueue(delta.Value, true, delta.Revision, out overflow) ||
                overflow.Length != 0)
                return false;
        }

        if (
            snapshotFirstQueue.SetSnapshotRevision(snapshotFirst, true, 200) != 0 ||
            !snapshotFirstQueue.Complete(snapshotFirst, out var snapshotFirstReplay) ||
            snapshotFirstReplay.Length != 1 || snapshotFirstReplay[0] != 202 ||
            snapshotFirstQueue.IsBuffering ||
            ShouldBeginSync(newer, latestSyncId: newer, closedSnapshotSyncId: newer))
            return false;

        return ShouldBeginSync(active, active, closedSnapshotSyncId: 0) &&
               !ShouldBeginSync(active, active, closedSnapshotSyncId: active) &&
               !ShouldBeginSync(active - 1, active, closedSnapshotSyncId: 0) &&
               ShouldBeginSync(newer, active, closedSnapshotSyncId: active) &&
               !ShouldBeginSnapshot(active, active, closedSnapshotSyncId: active,
                   activeSnapshotSyncId: 0, activeSnapshotPacketReceived: false) &&
               ShouldBeginSnapshot(active, active, closedSnapshotSyncId: 0,
                   activeSnapshotSyncId: active, activeSnapshotPacketReceived: false) &&
               !ShouldBeginSnapshot(active, active, closedSnapshotSyncId: 0,
                   activeSnapshotSyncId: active, activeSnapshotPacketReceived: true) &&
               ShouldBeginSnapshot(newer, active, closedSnapshotSyncId: active,
                   activeSnapshotSyncId: active, activeSnapshotPacketReceived: true);
    }
#endif
}

public static class ActorDeltaJournalPolicy
{
    public static bool ShouldReplay(bool hasSnapshotRevision, long snapshotRevision,
        bool hasDeltaRevision, long deltaRevision)
        => ActorRevisionOrderingPolicy.ShouldApplyDelta(
            hasSnapshotRevision, snapshotRevision, hasDeltaRevision, deltaRevision);

    public static bool ShouldReplaceTransform(long existingRevision, long incomingRevision)
        => incomingRevision > existingRevision;

    public static bool ShouldClearTransformOnLifecycle(long transformRevision, long lifecycleRevision)
        => transformRevision <= lifecycleRevision;

    public static bool ShouldAbortForJournalLoss(bool historyTruncated,
        long highestEvictedRevision, long snapshotRevision)
        => historyTruncated && highestEvictedRevision > snapshotRevision;

#if LYC_TEST
    public static bool VerifyRules()
    {
        var queue = new ActorSnapshotDeltaQueue<int>();
        var snapshotGeneration = queue.BeginSnapshot();
        const long snapshotWatermark = 10;
        const long preBarrierRevision = 11;
        if (!queue.TryEnqueue(1, true, preBarrierRevision, out var overflow) || overflow.Length != 0 ||
            queue.SetSnapshotRevision(snapshotGeneration, true, snapshotWatermark) != 0 ||
            !queue.Complete(snapshotGeneration, out var replay) || replay.Length != 1 || replay[0] != 1)
            return false;

        return !ShouldReplay(true, 10, true, 9) && !ShouldReplay(true, 10, true, 10) &&
               ShouldReplay(true, 10, true, 11) && ShouldReplay(true, 10, false, 0) &&
               ShouldReplaceTransform(10, 11) && !ShouldReplaceTransform(11, 10) &&
               ShouldClearTransformOnLifecycle(10, 11) &&
               !ShouldClearTransformOnLifecycle(12, 11) &&
               VerifyEventDeduplication() &&
               ShouldAbortForJournalLoss(true, 12, 10) &&
               !ShouldAbortForJournalLoss(true, 10, 10) &&
               !ShouldAbortForJournalLoss(false, 12, 10);
    }

    private static bool VerifyEventDeduplication()
    {
        var keys = new HashSet<ActorDeltaJournalKey>();
        return keys.Add(new ActorDeltaJournalKey(ActorDeltaKind.ActorDestroy, 5, 11)) &&
               !keys.Add(new ActorDeltaJournalKey(ActorDeltaKind.ActorDestroy, 5, 11)) &&
               keys.Add(new ActorDeltaJournalKey(ActorDeltaKind.ActorSpawn, 5, 11)) &&
               keys.Add(new ActorDeltaJournalKey(ActorDeltaKind.ActorDestroy, 6, 11));
    }
#endif
}

public readonly record struct ActorDeltaRevisionState(
    long LatestAppliedRevision,
    long LastSpawnRevision,
    long LastDestroyRevision,
    long TransferRevision,
    string TransferOwnerId,
    long UpdateRevision = 0, long StateRevision = 0, long UnloadRevision = 0);

/// <summary>
/// Per-actor cross-packet-type ordering. Destroy is a lifecycle tombstone and
/// wins over older spawn packets even when its packet type arrived first.
/// </summary>
public static class ActorDeltaRevisionPolicy
{
    public static bool ShouldApply(ActorDeltaRevisionState state, ActorDeltaKind packetType,
        long revision, bool actorCurrentlyKnown)
    {
        if (packetType == ActorDeltaKind.ActorSpawn)
        {
            if (revision <= state.LastSpawnRevision || revision <= state.LastDestroyRevision)
                return false;

            // A transfer/state delta can overtake a new spawn on another stream.
            // If no model exists yet, permit the spawn; transfer ownership is
            // reapplied from its newer cached revision by GetOwnerForSpawn.
            return !actorCurrentlyKnown || revision > state.LatestAppliedRevision;
        }

        if (packetType == ActorDeltaKind.ActorDestroy)
        {
            // Host destruction ends this incarnation, regardless of another stream's pose/state.
            return revision > state.LastDestroyRevision && revision > state.LastSpawnRevision;
        }

        if (state.LastDestroyRevision > state.LastSpawnRevision)
            return false;

        long floor = Math.Max(state.LastSpawnRevision, state.LastDestroyRevision);
        return revision > Math.Max(floor, packetType switch
        {
            ActorDeltaKind.ActorTransfer => state.TransferRevision,
            ActorDeltaKind.ActorUpdate => Math.Max(state.UpdateRevision, Math.Max(state.TransferRevision, state.UnloadRevision)),
            ActorDeltaKind.ActorState => state.StateRevision,
            ActorDeltaKind.ActorUnload => Math.Max(state.UnloadRevision, Math.Max(state.UpdateRevision, state.TransferRevision)),
            _ => state.LatestAppliedRevision
        });
    }

    public static ActorDeltaRevisionState Record(ActorDeltaRevisionState state,
        ActorDeltaKind packetType, long revision, string transferOwnerId)
    {
        var latest = Math.Max(state.LatestAppliedRevision, revision);
        return packetType switch
        {
            ActorDeltaKind.ActorSpawn => state with
            {
                LatestAppliedRevision = latest,
                LastSpawnRevision = Math.Max(state.LastSpawnRevision, revision)
            },
            ActorDeltaKind.ActorDestroy => state with
            {
                LatestAppliedRevision = latest,
                LastDestroyRevision = Math.Max(state.LastDestroyRevision, revision)
            },
            ActorDeltaKind.ActorTransfer when revision >= state.TransferRevision => state with
            {
                LatestAppliedRevision = latest,
                TransferRevision = revision,
                TransferOwnerId = transferOwnerId ?? string.Empty
            },
            ActorDeltaKind.ActorUpdate => state with { LatestAppliedRevision = latest, UpdateRevision = Math.Max(state.UpdateRevision, revision) },
            ActorDeltaKind.ActorState => state with { LatestAppliedRevision = latest, StateRevision = Math.Max(state.StateRevision, revision) },
            ActorDeltaKind.ActorUnload => state with { LatestAppliedRevision = latest, UnloadRevision = Math.Max(state.UnloadRevision, revision) },
            _ => state with { LatestAppliedRevision = latest }
        };
    }

    public static bool HasNewerTransferOwner(ActorDeltaRevisionState state, long spawnRevision)
        => state.TransferRevision > spawnRevision;

    public static string ResolveOwnerForSpawn(ActorDeltaRevisionState state,
        long spawnRevision, string fallbackOwnerId)
        => HasNewerTransferOwner(state, spawnRevision)
            ? state.TransferOwnerId ?? string.Empty
            : fallbackOwnerId;

#if LYC_TEST
    public static bool VerifyPermutations()
    {
        var empty = default(ActorDeltaRevisionState);
        var spawned = Record(empty, ActorDeltaKind.ActorSpawn, 4, null);
        if (!ShouldApply(spawned, ActorDeltaKind.ActorDestroy, 5, actorCurrentlyKnown: true))
            return false;

        var destroyed = Record(spawned, ActorDeltaKind.ActorDestroy, 5, null);
        if (ShouldApply(destroyed, ActorDeltaKind.ActorSpawn, 4, actorCurrentlyKnown: false) ||
            !ShouldApply(destroyed, ActorDeltaKind.ActorSpawn, 6, actorCurrentlyKnown: false))
            return false;

        var transferFirst = Record(empty, ActorDeltaKind.ActorTransfer, 5, "PLAYER_GUEST");
        if (!ShouldApply(transferFirst, ActorDeltaKind.ActorSpawn, 4, actorCurrentlyKnown: false) ||
            !HasNewerTransferOwner(transferFirst, 4) ||
            ResolveOwnerForSpawn(transferFirst, 4, "PLAYER_HOST") != "PLAYER_GUEST")
            return false;

        var updateFirst = Record(spawned, ActorDeltaKind.ActorUpdate, 7, null);
        if (!ShouldApply(updateFirst, ActorDeltaKind.ActorDestroy, 5, actorCurrentlyKnown: true) ||
            !ShouldApply(updateFirst, ActorDeltaKind.ActorDestroy, 8, actorCurrentlyKnown: true))
            return false;

        var newerDestroy = Record(updateFirst, ActorDeltaKind.ActorDestroy, 8, null);
        if (ShouldApply(newerDestroy, ActorDeltaKind.ActorTransfer, 9, actorCurrentlyKnown: false) ||
            ShouldApply(newerDestroy, ActorDeltaKind.ActorUpdate, 7, actorCurrentlyKnown: true) ||
            !ShouldApply(newerDestroy, ActorDeltaKind.ActorSpawn, 10, actorCurrentlyKnown: false))
            return false;

        return !ShouldApply(Record(newerDestroy, ActorDeltaKind.ActorSpawn, 10, null),
            ActorDeltaKind.ActorDestroy, 8, actorCurrentlyKnown: true);
    }

#endif
}

public static class ActorRevisionOrderingPolicy
{
    public static bool ShouldApplyDelta(bool hasSnapshotRevision, long snapshotRevision,
        bool hasDeltaRevision, long deltaRevision)
        => !IsCoveredBySnapshot(hasSnapshotRevision, snapshotRevision, hasDeltaRevision, deltaRevision);

    public static bool IsCoveredBySnapshot(bool hasSnapshotRevision, long snapshotRevision,
        bool hasDeltaRevision, long deltaRevision)
        => hasSnapshotRevision && hasDeltaRevision && deltaRevision <= snapshotRevision;

#if LYC_TEST
    public static bool VerifyRules()
        => !ShouldApplyDelta(true, 10, true, 9) && !ShouldApplyDelta(true, 10, true, 10) &&
           ShouldApplyDelta(true, 10, true, 11) && ShouldApplyDelta(true, 10, false, 0) &&
           ShouldApplyDelta(false, 0, true, 1);
#endif
}

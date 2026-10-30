using SR2MP.Packets.Actor;
using SR2MP.Packets.LandPlots;
using SR2MP.Packets.Utils;

namespace SR2MP.Shared.Utils;

/// <summary>
/// Buffers live actor mutations while a client replaces its world from an
/// authoritative InitialActors snapshot. The queue itself is pure so ordering,
/// cancellation and overflow behavior can be checked without Unity.
/// </summary>
internal sealed class ActorSnapshotDeltaQueue<T>
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

    internal ActorSnapshotDeltaQueue(int maximumPending = 262_144)
    {
        this.maximumPending = Math.Max(1, maximumPending);
    }

    internal bool IsBuffering => buffering;

    internal long Begin()
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

    internal long BeginSnapshot()
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

    internal bool IsCurrent(long snapshotGeneration)
        => buffering && snapshotGeneration == generation;

    internal int SetSnapshotRevision(long snapshotGeneration, bool hasRevision, long revision)
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

    internal bool TryEnqueue(T delta, bool hasRevision, long revision, out T[] overflowReplay,
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

    internal bool Complete(long snapshotGeneration, out T[] replay)
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

    internal bool Abort(long snapshotGeneration, out T[] replay)
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

    internal void Cancel()
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

#if SR2MP_TEST
    internal static bool VerifyOrderingAndCleanup()
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

internal static class ActorSnapshotDeltaBuffer
{
    private readonly record struct JournalEntry(
        PacketType PacketType, long ActorId, long Revision, bool HasRevision, int ArrivalOrder, Func<bool> Apply);

    private static readonly ActorSnapshotDeltaQueue<Func<bool>> queue = new();
    private const int MaximumJournalEvents = 131_072;
    private const int MaximumJournalTransforms = 131_072;
    private static readonly List<JournalEntry> journalEvents = new();
    private static readonly HashSet<ActorDeltaJournalKey> journalEventKeys = new();
    private static readonly Dictionary<long, JournalEntry> latestTransformByActor = new();
    private static readonly Dictionary<long, ActorDeltaRevisionState> appliedActorRevisions = new();
    private static int nextJournalArrivalOrder;
    private static bool journalHistoryTruncated;
    private static long highestEvictedJournalRevision;
    private static bool subscribedToDisconnect;
    private static bool snapshotInvalidated;
    private static bool hasCompletedSnapshotRevision;
    private static long completedSnapshotRevision;
    private static bool hasActiveSnapshotRevision;
    private static long activeSnapshotRevision;
    private static long activeSnapshotGeneration;
    private static long latestSyncId;
    private static long activeSyncId;
    private static long closedSnapshotSyncId;
    private static long activeSnapshotSyncId;
    private static bool activeSnapshotPacketReceived;

    internal static long BeginSync()
    {
        SubscribeToDisconnect();
        ClearInvalidatedBufferIfNeeded();

        var wasBuffering = queue.IsBuffering;
        var generation = queue.Begin();
        if (!wasBuffering)
            SeedJournalIntoBuffer();
        return generation;
    }

    internal static long BeginSync(long syncId)
    {
        if (syncId <= 0)
            return BeginSync();

        SubscribeToDisconnect();
        if (!ActorSyncGenerationPolicy.ShouldBeginSync(syncId, latestSyncId, closedSnapshotSyncId))
            return 0;
        ClearInvalidatedBufferIfNeeded();

        if (syncId > latestSyncId)
        {
            latestSyncId = syncId;
            activeSyncId = syncId;

            // Supersede an older snapshot coroutine while keeping its buffered
            // deltas. Otherwise its finally block could close the new sync's
            // barrier before the matching InitialActors packet arrives.
            if (activeSnapshotGeneration != 0 && queue.IsCurrent(activeSnapshotGeneration))
            {
                activeSnapshotGeneration = queue.BeginSnapshot();
                activeSnapshotSyncId = syncId;
                activeSnapshotPacketReceived = false;
                hasActiveSnapshotRevision = false;
                activeSnapshotRevision = 0;
            }
            else
            {
                activeSnapshotGeneration = 0;
                activeSnapshotSyncId = 0;
                activeSnapshotPacketReceived = false;
                hasActiveSnapshotRevision = false;
                activeSnapshotRevision = 0;
            }
        }
        else
        {
            activeSyncId = syncId;
        }

        var wasBuffering = queue.IsBuffering;
        var generation = queue.Begin();
        if (!wasBuffering)
            SeedJournalIntoBuffer();
        return generation;
    }

    internal static long BeginSnapshot()
    {
        SubscribeToDisconnect();
        if (snapshotInvalidated)
        {
            SrLogger.LogWarning("InitialActors ignored because its delta buffer overflowed; waiting for a fresh resync snapshot.");
            return 0;
        }

        var wasBuffering = queue.IsBuffering;
        activeSnapshotGeneration = queue.BeginSnapshot();
        hasActiveSnapshotRevision = false;
        activeSnapshotRevision = 0;
        if (!wasBuffering)
            SeedJournalIntoBuffer();
        return activeSnapshotGeneration;
    }

    internal static long BeginSnapshot(long syncId)
    {
        if (syncId <= 0)
            return BeginSnapshot();

        SubscribeToDisconnect();
        if (!ActorSyncGenerationPolicy.ShouldBeginSnapshot(syncId, latestSyncId,
                closedSnapshotSyncId, activeSnapshotSyncId, activeSnapshotPacketReceived))
            return 0;

        // A snapshot carrying a newer host generation is itself proof that the
        // requested recovery arrived. Accept it even if it overtakes the
        // Important-channel approval that would otherwise clear invalidation.
        if (snapshotInvalidated)
            ClearInvalidatedBufferIfNeeded();

        if (syncId > latestSyncId)
        {
            latestSyncId = syncId;
            activeSyncId = syncId;
        }

        if (activeSnapshotSyncId == syncId && activeSnapshotGeneration != 0 &&
            queue.IsCurrent(activeSnapshotGeneration) && !activeSnapshotPacketReceived)
        {
            // The approval opened a staged generation before this packet.
            // Reuse it instead of invalidating any deltas received meanwhile.
            activeSnapshotPacketReceived = true;
            hasActiveSnapshotRevision = false;
            activeSnapshotRevision = 0;
            return activeSnapshotGeneration;
        }

        activeSyncId = syncId;
        activeSnapshotSyncId = syncId;
        activeSnapshotPacketReceived = true;
        var wasBuffering = queue.IsBuffering;
        activeSnapshotGeneration = queue.BeginSnapshot();
        hasActiveSnapshotRevision = false;
        activeSnapshotRevision = 0;
        if (!wasBuffering)
            SeedJournalIntoBuffer();
        return activeSnapshotGeneration;
    }

    internal static bool IsCurrent(long generation) => queue.IsCurrent(generation);

    internal static void SetSnapshotRevision(long generation, bool hasRevision, long revision)
    {
        if (!queue.IsCurrent(generation))
            return;

        if (hasRevision && ActorDeltaJournalPolicy.ShouldAbortForJournalLoss(
                journalHistoryTruncated, highestEvictedJournalRevision, revision))
        {
            InvalidateSnapshotForJournalLoss(
                $"journal lost actor revision {highestEvictedJournalRevision} newer than snapshot watermark {revision}");
            return;
        }

        var staleDeltas = queue.SetSnapshotRevision(generation, hasRevision, revision);
        activeSnapshotGeneration = generation;
        hasActiveSnapshotRevision = hasRevision;
        activeSnapshotRevision = revision;
        if (staleDeltas > 0)
            SrLogger.LogMessage($"InitialActors watermark discarded {staleDeltas} stale actor deltas.");
    }

    internal static bool TryDefer(PacketType packetType, long actorId,
        bool hasRevision, long revision, Func<bool> apply, string? transferOwnerId = null)
    {
        if (!IsActorDelta(packetType))
            return false;

        // A packet from an independent channel can arrive after InitialActors
        // has finished. Keep the committed watermark after the queue closes so
        // a delayed pre-snapshot mutation cannot roll the client back. This also
        // rejects old events while a newer sync's approval is in flight.
        if (hasCompletedSnapshotRevision && hasRevision &&
            ActorRevisionOrderingPolicy.IsCoveredBySnapshot(true, completedSnapshotRevision,
                true, revision))
            return true;

        var guardedApply = (Func<bool>)(() => ApplyWithRevisionOrdering(
            packetType, actorId, hasRevision, revision, transferOwnerId, apply));
        if (RememberForFutureSnapshot(packetType, actorId, hasRevision, revision, guardedApply))
            return true;

        if (queue.TryEnqueue(guardedApply, hasRevision, revision, out var overflowReplay))
        {
            if (overflowReplay.Length > 0)
            {
                snapshotInvalidated = true;
                SrLogger.LogError(
                    $"Actor snapshot delta buffer reached its limit ({overflowReplay.Length}); " +
                    "aborting that snapshot and replaying only mutations intercepted after the barrier before requesting a fresh snapshot.");
                Replay(overflowReplay);
                if (Main.Client.IsConnected)
                    Main.Server.ReSyncManager.RequestResync();
            }

            return true;
        }

        guardedApply();
        return true;
    }

    private static bool ApplyWithRevisionOrdering(PacketType packetType, long actorId,
        bool hasRevision, long revision, string? transferOwnerId, Func<bool> apply)
    {
        if (!hasRevision || actorId == 0)
            return apply();

        if (!ShouldApplyActorRevision(packetType, actorId, revision))
        {
            var state = appliedActorRevisions.GetValueOrDefault(actorId);
            SrLogger.LogDebug(
                $"Dropped stale actor delta type={packetType} actor={actorId} revision={revision} " +
                $"last={state.LatestAppliedRevision} spawn={state.LastSpawnRevision} destroy={state.LastDestroyRevision}.");
            return false;
        }

        var currentState = appliedActorRevisions.GetValueOrDefault(actorId);
        var actorExists = HasActorModel(actorId);

        // A destroy is an authoritative tombstone even if this peer never saw
        // the actor's spawn. This prevents a delayed older spawn on another
        // packet-type stream from resurrecting it.
        if (packetType == PacketType.ActorDestroy && !actorExists &&
            !ActorManager.OwnershipByActorId.ContainsKey(actorId))
        {
            appliedActorRevisions[actorId] = ActorDeltaRevisionPolicy.Record(
                currentState, packetType, revision, null);
            return true;
        }

        var applied = apply();
        if (applied)
        {
            appliedActorRevisions[actorId] = ActorDeltaRevisionPolicy.Record(
                currentState, packetType, revision,
                packetType == PacketType.ActorTransfer ? transferOwnerId : null);
        }

        return applied;
    }

    private static bool HasActorModel(long actorId)
        => ActorManager.Actors.ContainsKey(actorId) ||
           GameState.TryGetIdentifiableModel(new Il2CppMonomiPark.SlimeRancher.DataModel.ActorId(actorId),
               out var model) && model != null;

    internal static void RecordAppliedActorDelta(PacketType packetType, long actorId,
        long revision, string? ownerId = null)
    {
        if (actorId == 0 || revision <= 0)
            return;

        var state = appliedActorRevisions.GetValueOrDefault(actorId);
        appliedActorRevisions[actorId] = ActorDeltaRevisionPolicy.Record(state, packetType, revision, ownerId);
    }

    internal static bool ShouldApplyActorRevision(PacketType packetType, long actorId, long revision)
    {
        if (actorId == 0 || revision <= 0)
            return true;

        return ActorDeltaRevisionPolicy.ShouldApply(
            appliedActorRevisions.GetValueOrDefault(actorId), packetType, revision,
            HasActorModel(actorId));
    }

    internal static string GetOwnerForSpawn(long actorId, long revision, string fallbackOwnerId)
    {
        if (appliedActorRevisions.TryGetValue(actorId, out var state) &&
            ActorDeltaRevisionPolicy.HasNewerTransferOwner(state, revision))
            return ActorDeltaRevisionPolicy.ResolveOwnerForSpawn(state, revision, fallbackOwnerId);

        return fallbackOwnerId;
    }

    internal static void CompleteSnapshot(long generation)
    {
        if (!queue.Complete(generation, out var replay))
            return;

        if (generation == activeSnapshotGeneration)
        {
            if (hasActiveSnapshotRevision)
            {
                hasCompletedSnapshotRevision = true;
                completedSnapshotRevision = activeSnapshotRevision;
                PruneJournalThrough(activeSnapshotRevision);
            }

            appliedActorRevisions.Clear();
        }

        if (generation == activeSnapshotGeneration && activeSnapshotSyncId > 0 &&
            activeSnapshotSyncId == activeSyncId)
            closedSnapshotSyncId = activeSnapshotSyncId;

        hasActiveSnapshotRevision = false;
        activeSnapshotRevision = 0;
        activeSnapshotGeneration = 0;
        activeSnapshotSyncId = 0;
        activeSnapshotPacketReceived = false;

        SrLogger.LogMessage($"InitialActors applied; replaying {replay.Length} buffered actor mutations.");
        Replay(replay);
    }

    internal static void AbortSnapshot(long generation)
    {
        if (!queue.Abort(generation, out var replay))
            return;

        // Keep the last successfully committed watermark and the journal. The
        // failed snapshot did not replace the world, so its watermark cannot
        // make earlier events stale. A later retry can seed this history.
        if (generation == activeSnapshotGeneration)
        {
            if (activeSnapshotSyncId > 0 && activeSnapshotSyncId == activeSyncId)
                closedSnapshotSyncId = activeSnapshotSyncId;
            hasActiveSnapshotRevision = false;
            activeSnapshotRevision = 0;
            activeSnapshotGeneration = 0;
            activeSnapshotSyncId = 0;
            activeSnapshotPacketReceived = false;
        }

        SrLogger.LogWarning($"InitialActors snapshot aborted; replaying {replay.Length} actor mutations without advancing the snapshot watermark.");
        Replay(replay);
    }

    internal static void Cancel()
    {
        queue.Cancel();
        ClearJournal();
        appliedActorRevisions.Clear();
        snapshotInvalidated = false;
        hasCompletedSnapshotRevision = false;
        completedSnapshotRevision = 0;
        hasActiveSnapshotRevision = false;
        activeSnapshotRevision = 0;
        activeSnapshotGeneration = 0;
        latestSyncId = 0;
        activeSyncId = 0;
        closedSnapshotSyncId = 0;
        activeSnapshotSyncId = 0;
        activeSnapshotPacketReceived = false;
    }

    private static void ClearInvalidatedBufferIfNeeded()
    {
        if (!snapshotInvalidated)
            return;

        queue.Cancel();
        snapshotInvalidated = false;
        hasActiveSnapshotRevision = false;
        activeSnapshotRevision = 0;
        activeSnapshotGeneration = 0;
        activeSnapshotSyncId = 0;
        activeSnapshotPacketReceived = false;
    }

    private static bool RememberForFutureSnapshot(PacketType packetType, long actorId,
        bool hasRevision, long revision, Func<bool> apply)
    {
        // Legacy actor packets have no causal revision. The bounded journal only
        // guarantees cross-channel replay for current revisioned peers.
        if (!hasRevision)
            return false;

        var entry = new JournalEntry(packetType, actorId, revision, true,
            nextJournalArrivalOrder++, apply);

        if (packetType == PacketType.ActorUpdate && actorId != 0)
        {
            if (latestTransformByActor.TryGetValue(actorId, out var existing) &&
                !ActorDeltaJournalPolicy.ShouldReplaceTransform(existing.Revision, revision))
                return true;

            latestTransformByActor[actorId] = entry;
            if (latestTransformByActor.Count > MaximumJournalTransforms)
            {
                var oldestActorId = 0L;
                var oldestEntry = default(JournalEntry);
                var oldestArrivalOrder = int.MaxValue;
                foreach (var (candidateActorId, candidate) in latestTransformByActor)
                {
                    if (candidate.ArrivalOrder >= oldestArrivalOrder)
                        continue;

                    oldestActorId = candidateActorId;
                    oldestEntry = candidate;
                    oldestArrivalOrder = candidate.ArrivalOrder;
                }

                latestTransformByActor.Remove(oldestActorId);
                RecordJournalLoss(oldestEntry.Revision);
            }
            return false;
        }

        var eventKey = new ActorDeltaJournalKey(packetType, actorId, revision);
        if (!journalEventKeys.Add(eventKey))
            return true;

        if ((packetType is PacketType.ActorSpawn or PacketType.ActorDestroy) &&
            latestTransformByActor.TryGetValue(actorId, out var lifecycleTransform) &&
            ActorDeltaJournalPolicy.ShouldClearTransformOnLifecycle(lifecycleTransform.Revision, revision))
            latestTransformByActor.Remove(actorId);

        journalEvents.Add(entry);
        if (journalEvents.Count <= MaximumJournalEvents)
            return false;

        var evicted = journalEvents[0];
        journalEventKeys.Remove(new ActorDeltaJournalKey(evicted.PacketType, evicted.ActorId, evicted.Revision));
        journalEvents.RemoveAt(0);
        RecordJournalLoss(evicted.Revision);
        return false;
    }

    private static void RecordJournalLoss(long revision)
    {
        journalHistoryTruncated = true;
        highestEvictedJournalRevision = Math.Max(highestEvictedJournalRevision, revision);
    }

    private static void SeedJournalIntoBuffer()
    {
        if (!queue.IsBuffering || journalEvents.Count == 0 && latestTransformByActor.Count == 0)
            return;

        var entries = journalEvents.Concat(latestTransformByActor.Values)
            .OrderBy(entry => entry.ArrivalOrder)
            .ToArray();
        foreach (var entry in entries)
        {
            if (!queue.TryEnqueue(entry.Apply, entry.HasRevision, entry.Revision,
                    out var overflowReplay, alreadyApplied: true))
                break;

            if (overflowReplay.Length == 0)
                continue;

            snapshotInvalidated = true;
            SrLogger.LogError(
                $"Actor pre-sync journal exceeded the delta buffer ({overflowReplay.Length}); " +
                "aborting this snapshot and replaying retained mutations before requesting a fresh snapshot.");
            Replay(overflowReplay);
            if (Main.Client.IsConnected)
                Main.Server.ReSyncManager.RequestResync();
            break;
        }
    }

    private static void PruneJournalThrough(long revision)
    {
        for (var index = journalEvents.Count - 1; index >= 0; index--)
        {
            var entry = journalEvents[index];
            if (ActorDeltaJournalPolicy.ShouldReplay(
                    hasSnapshotRevision: true, snapshotRevision: revision,
                    hasDeltaRevision: entry.HasRevision, deltaRevision: entry.Revision))
                continue;

            journalEventKeys.Remove(new ActorDeltaJournalKey(entry.PacketType, entry.ActorId, entry.Revision));
            journalEvents.RemoveAt(index);
        }

        foreach (var actorId in latestTransformByActor
                     .Where(pair => !ActorDeltaJournalPolicy.ShouldReplay(
                         hasSnapshotRevision: true, snapshotRevision: revision,
                         hasDeltaRevision: pair.Value.HasRevision, deltaRevision: pair.Value.Revision))
                     .Select(pair => pair.Key).ToArray())
            latestTransformByActor.Remove(actorId);

        if (journalHistoryTruncated && highestEvictedJournalRevision <= revision)
        {
            journalHistoryTruncated = false;
            highestEvictedJournalRevision = 0;
        }
    }

    private static void InvalidateSnapshotForJournalLoss(string reason)
    {
        snapshotInvalidated = true;
        queue.Cancel();
        hasActiveSnapshotRevision = false;
        activeSnapshotRevision = 0;
        activeSnapshotGeneration = 0;
        activeSnapshotSyncId = 0;
        activeSnapshotPacketReceived = false;
        SrLogger.LogError($"Actor snapshot aborted because {reason}; requesting another snapshot.");
        if (Main.Client.IsConnected)
            Main.Server.ReSyncManager.RequestResync();
    }

    internal static long GetActorId<T>(T packet) where T : IPacket
        => packet switch
        {
            ActorSpawnPacket spawn => spawn.ActorId.Value,
            ActorDestroyPacket destroy => destroy.ActorId.Value,
            ActorUpdatePacket update => update.ActorId.Value,
            ActorStatePacket state => state.ActorId.Value,
            ActorTransferPacket transfer => transfer.ActorId.Value,
            ActorUnloadPacket unload => unload.ActorId.Value,
            ResourceAttachPacket resourceAttach => resourceAttach.ActorId.Value,
            _ => 0
        };

    private static void ClearJournal()
    {
        journalEvents.Clear();
        journalEventKeys.Clear();
        latestTransformByActor.Clear();
        nextJournalArrivalOrder = 0;
        journalHistoryTruncated = false;
        highestEvictedJournalRevision = 0;
    }

    private static void Replay(IEnumerable<Func<bool>> replay)
    {
        foreach (var action in replay)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                SrLogger.LogError($"Failed to replay actor mutation after InitialActors: {ex}");
            }
        }
    }

    private static bool IsActorDelta(PacketType packetType)
        => packetType is PacketType.ActorSpawn or PacketType.ActorDestroy or PacketType.ActorUpdate or
            PacketType.ActorTransfer or PacketType.ActorUnload or PacketType.ActorState or PacketType.ResourceAttach;

    private static void SubscribeToDisconnect()
    {
        if (subscribedToDisconnect)
            return;

        Main.Client.OnDisconnected += Cancel;
        subscribedToDisconnect = true;
    }

#if SR2MP_TEST
    internal static bool VerifyPacketClassification()
    {
        var packetKinds = IsActorDelta(PacketType.ActorSpawn) && IsActorDelta(PacketType.ActorDestroy) &&
                          IsActorDelta(PacketType.ActorUpdate) && IsActorDelta(PacketType.ActorTransfer) &&
                          IsActorDelta(PacketType.ActorUnload) && IsActorDelta(PacketType.ActorState) &&
                          IsActorDelta(PacketType.ResourceAttach) && !IsActorDelta(PacketType.InitialActors) &&
                          !IsActorDelta(PacketType.ActorTypeRegistry);
        var queue = ActorSnapshotDeltaQueue<int>.VerifyOrderingAndCleanup();
        var revision = ActorRevisionOrderingPolicy.VerifyRules();
        var watermark = VerifyCompletedWatermarkOrdering();
        var generation = ActorSyncGenerationPolicy.VerifyRules();
        var journal = ActorDeltaJournalPolicy.VerifyRules();
        var actorRevision = ActorDeltaRevisionPolicy.VerifyPermutations();
        var initialSnapshot = InitialActorSnapshotPolicy.VerifyRules();

        SrLogger.LogMessage(
            $"[SR2MP_TEST] actor-snapshot-buffer-parts packet-kinds={(packetKinds ? "PASS" : "FAIL")} " +
            $"queue={(queue ? "PASS" : "FAIL")} revision={(revision ? "PASS" : "FAIL")} " +
            $"watermark={(watermark ? "PASS" : "FAIL")} generation={(generation ? "PASS" : "FAIL")} " +
            $"journal={(journal ? "PASS" : "FAIL")} actor-revision={(actorRevision ? "PASS" : "FAIL")} " +
            $"initial-snapshot={(initialSnapshot ? "PASS" : "FAIL")}");

        return packetKinds && queue && revision && watermark && generation && journal &&
               actorRevision && initialSnapshot;
    }

    private static bool VerifyCompletedWatermarkOrdering()
        => ActorRevisionOrderingPolicy.IsCoveredBySnapshot(true, 20, true, 20) &&
           ActorRevisionOrderingPolicy.IsCoveredBySnapshot(true, 20, true, 19) &&
           !ActorRevisionOrderingPolicy.IsCoveredBySnapshot(true, 20, true, 21) &&
           !ActorRevisionOrderingPolicy.IsCoveredBySnapshot(true, 20, false, 0);
#endif
}

internal readonly record struct ActorDeltaJournalKey(PacketType PacketType, long ActorId, long Revision);

internal enum InitialActorSnapshotEntryResult
{
    ExpectedSkip,
    Spawned,
    RuntimeFailure
}

internal static class InitialActorSnapshotPolicy
{
    internal static bool IsExpectedSkip(bool validActorId, bool uniqueSnapshotId,
        bool supportedType, bool hasPrefab, bool playerIdCollision)
        => !validActorId || !uniqueSnapshotId || !supportedType || !hasPrefab || playerIdCollision;

    internal static InitialActorSnapshotEntryResult ClassifyAttempt(
        bool expectedSkip, bool spawnSucceeded, bool threw)
    {
        if (expectedSkip)
            return InitialActorSnapshotEntryResult.ExpectedSkip;

        return threw || !spawnSucceeded
            ? InitialActorSnapshotEntryResult.RuntimeFailure
            : InitialActorSnapshotEntryResult.Spawned;
    }

    internal static bool ShouldCommitSnapshot(bool hasRuntimeFailure)
        => !hasRuntimeFailure;

    internal static bool ShouldAutomaticallyRetry(int failureNumber, int maximumRetries)
        => failureNumber > 0 && failureNumber <= maximumRetries;

#if SR2MP_TEST
    internal static bool VerifyRules()
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

internal static class ActorSyncGenerationPolicy
{
    internal static bool ShouldBeginSync(long incomingSyncId, long latestSyncId, long closedSnapshotSyncId)
        => incomingSyncId <= 0 || incomingSyncId > latestSyncId ||
           (incomingSyncId == latestSyncId && incomingSyncId != closedSnapshotSyncId);

    internal static bool ShouldBeginSnapshot(long incomingSyncId, long latestSyncId,
        long closedSnapshotSyncId, long activeSnapshotSyncId, bool activeSnapshotPacketReceived)
        => incomingSyncId <= 0 || incomingSyncId > latestSyncId ||
           (incomingSyncId == latestSyncId && incomingSyncId != closedSnapshotSyncId &&
            !(incomingSyncId == activeSnapshotSyncId && activeSnapshotPacketReceived));

#if SR2MP_TEST
    internal static bool VerifyRules()
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

internal static class ActorDeltaJournalPolicy
{
    internal static bool ShouldReplay(bool hasSnapshotRevision, long snapshotRevision,
        bool hasDeltaRevision, long deltaRevision)
        => ActorRevisionOrderingPolicy.ShouldApplyDelta(
            hasSnapshotRevision, snapshotRevision, hasDeltaRevision, deltaRevision);

    internal static bool ShouldReplaceTransform(long existingRevision, long incomingRevision)
        => incomingRevision > existingRevision;

    internal static bool ShouldClearTransformOnLifecycle(long transformRevision, long lifecycleRevision)
        => transformRevision <= lifecycleRevision;

    internal static bool ShouldAbortForJournalLoss(bool historyTruncated,
        long highestEvictedRevision, long snapshotRevision)
        => historyTruncated && highestEvictedRevision > snapshotRevision;

#if SR2MP_TEST
    internal static bool VerifyRules()
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
        return keys.Add(new ActorDeltaJournalKey(PacketType.ActorDestroy, 5, 11)) &&
               !keys.Add(new ActorDeltaJournalKey(PacketType.ActorDestroy, 5, 11)) &&
               keys.Add(new ActorDeltaJournalKey(PacketType.ActorSpawn, 5, 11)) &&
               keys.Add(new ActorDeltaJournalKey(PacketType.ActorDestroy, 6, 11));
    }
#endif
}

internal readonly record struct ActorDeltaRevisionState(
    long LatestAppliedRevision,
    long LastSpawnRevision,
    long LastDestroyRevision,
    long TransferRevision,
    string? TransferOwnerId);

/// <summary>
/// Per-actor cross-packet-type ordering. Destroy is a lifecycle tombstone and
/// wins over older spawn packets even when its packet type arrived first.
/// </summary>
internal static class ActorDeltaRevisionPolicy
{
    internal static bool ShouldApply(ActorDeltaRevisionState state, PacketType packetType,
        long revision, bool actorCurrentlyKnown)
    {
        if (packetType == PacketType.ActorSpawn)
        {
            if (revision <= state.LastSpawnRevision || revision <= state.LastDestroyRevision)
                return false;

            // A transfer/state delta can overtake a new spawn on another stream.
            // If no model exists yet, permit the spawn; transfer ownership is
            // reapplied from its newer cached revision by GetOwnerForSpawn.
            return !actorCurrentlyKnown || revision > state.LatestAppliedRevision;
        }

        if (packetType == PacketType.ActorDestroy)
        {
            // Revisions are assigned monotonically at the host send boundary.
            // A destroy from an earlier channel that arrives after a newer
            // transform or transfer must not erase that newer actor state.
            return revision > state.LastDestroyRevision &&
                   revision > state.LastSpawnRevision &&
                   revision > state.LatestAppliedRevision;
        }

        if (state.LastDestroyRevision > state.LastSpawnRevision)
            return false;

        return revision > state.LatestAppliedRevision;
    }

    internal static ActorDeltaRevisionState Record(ActorDeltaRevisionState state,
        PacketType packetType, long revision, string? transferOwnerId)
    {
        var latest = Math.Max(state.LatestAppliedRevision, revision);
        return packetType switch
        {
            PacketType.ActorSpawn => state with
            {
                LatestAppliedRevision = latest,
                LastSpawnRevision = Math.Max(state.LastSpawnRevision, revision)
            },
            PacketType.ActorDestroy => state with
            {
                LatestAppliedRevision = latest,
                LastDestroyRevision = Math.Max(state.LastDestroyRevision, revision)
            },
            PacketType.ActorTransfer when revision >= state.TransferRevision => state with
            {
                LatestAppliedRevision = latest,
                TransferRevision = revision,
                TransferOwnerId = transferOwnerId ?? string.Empty
            },
            _ => state with { LatestAppliedRevision = latest }
        };
    }

    internal static bool HasNewerTransferOwner(ActorDeltaRevisionState state, long spawnRevision)
        => state.TransferRevision > spawnRevision;

    internal static string ResolveOwnerForSpawn(ActorDeltaRevisionState state,
        long spawnRevision, string fallbackOwnerId)
        => HasNewerTransferOwner(state, spawnRevision)
            ? state.TransferOwnerId ?? string.Empty
            : fallbackOwnerId;

#if SR2MP_TEST
    internal static bool VerifyPermutations()
    {
        var empty = default(ActorDeltaRevisionState);
        var spawned = Record(empty, PacketType.ActorSpawn, 4, null);
        if (!ShouldApply(spawned, PacketType.ActorDestroy, 5, actorCurrentlyKnown: true))
            return false;

        var destroyed = Record(spawned, PacketType.ActorDestroy, 5, null);
        if (ShouldApply(destroyed, PacketType.ActorSpawn, 4, actorCurrentlyKnown: false) ||
            !ShouldApply(destroyed, PacketType.ActorSpawn, 6, actorCurrentlyKnown: false))
            return false;

        var transferFirst = Record(empty, PacketType.ActorTransfer, 5, "PLAYER_GUEST");
        if (!ShouldApply(transferFirst, PacketType.ActorSpawn, 4, actorCurrentlyKnown: false) ||
            !HasNewerTransferOwner(transferFirst, 4) ||
            ResolveOwnerForSpawn(transferFirst, 4, "PLAYER_HOST") != "PLAYER_GUEST")
            return false;

        var updateFirst = Record(spawned, PacketType.ActorUpdate, 7, null);
        if (ShouldApply(updateFirst, PacketType.ActorDestroy, 5, actorCurrentlyKnown: true) ||
            !ShouldApply(updateFirst, PacketType.ActorDestroy, 8, actorCurrentlyKnown: true))
            return false;

        var newerDestroy = Record(updateFirst, PacketType.ActorDestroy, 8, null);
        if (ShouldApply(newerDestroy, PacketType.ActorTransfer, 9, actorCurrentlyKnown: false) ||
            ShouldApply(newerDestroy, PacketType.ActorUpdate, 7, actorCurrentlyKnown: true) ||
            !ShouldApply(newerDestroy, PacketType.ActorSpawn, 10, actorCurrentlyKnown: false))
            return false;

        return !ShouldApply(Record(newerDestroy, PacketType.ActorSpawn, 10, null),
            PacketType.ActorDestroy, 8, actorCurrentlyKnown: true);
    }

#endif
}

internal static class ActorRevisionOrderingPolicy
{
    internal static bool ShouldApplyDelta(bool hasSnapshotRevision, long snapshotRevision,
        bool hasDeltaRevision, long deltaRevision)
        => !IsCoveredBySnapshot(hasSnapshotRevision, snapshotRevision, hasDeltaRevision, deltaRevision);

    internal static bool IsCoveredBySnapshot(bool hasSnapshotRevision, long snapshotRevision,
        bool hasDeltaRevision, long deltaRevision)
        => hasSnapshotRevision && hasDeltaRevision && deltaRevision <= snapshotRevision;

#if SR2MP_TEST
    internal static bool VerifyRules()
        => !ShouldApplyDelta(true, 10, true, 9) && !ShouldApplyDelta(true, 10, true, 10) &&
           ShouldApplyDelta(true, 10, true, 11) && ShouldApplyDelta(true, 10, false, 0) &&
           ShouldApplyDelta(false, 0, true, 1);
#endif
}

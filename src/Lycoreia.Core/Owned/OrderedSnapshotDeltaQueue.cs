// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;

namespace Lycoreia.Owned;

/// <summary>
/// Holds an authoritative snapshot and later deltas until a target model is ready.
/// Deltas received before a newer snapshot are discarded because that snapshot
/// already includes their effects; deltas received after it are replayed in order.
/// </summary>
public sealed class OrderedSnapshotDeltaQueue<TSnapshot, TDelta>
{
    private readonly List<TDelta> deltas = new();
    private TSnapshot snapshot = default!;
    private bool hasSnapshot;

    internal bool HasPending => hasSnapshot || deltas.Count > 0;

    internal void SetSnapshot(TSnapshot value)
    {
        snapshot = value;
        hasSnapshot = true;
        deltas.Clear();
    }

    internal void EnqueueDelta(TDelta value)
        => deltas.Add(value);

    /// <summary>
    /// Apply the snapshot first, then each delta in receive order. A callback that
    /// returns false leaves the unapplied state queued for a later retry.
    /// </summary>
    internal bool TryDrain(Func<TSnapshot, bool> applySnapshot, Func<TDelta, bool> applyDelta)
    {
        if (hasSnapshot)
        {
            if (!applySnapshot(snapshot))
                return false;

            hasSnapshot = false;
            snapshot = default!;
        }

        while (deltas.Count > 0)
        {
            if (!applyDelta(deltas[0]))
                return false;

            deltas.RemoveAt(0);
        }

        return true;
    }

    internal void Clear()
    {
        hasSnapshot = false;
        snapshot = default!;
        deltas.Clear();
    }

#if LYC_TEST
    internal static bool VerifyOrdering()
    {
        var queue = new OrderedSnapshotDeltaQueue<int, int>();
        var value = 0;
        queue.SetSnapshot(10);
        queue.EnqueueDelta(11);

        if (!queue.TryDrain(
                snapshotValue => { value = snapshotValue; return true; },
                delta => { value = delta; return true; }) ||
            value != 11 || queue.HasPending)
            return false;

        queue.EnqueueDelta(12);
        queue.SetSnapshot(20);
        if (queue.TryDrain(_ => false, _ => true) || !queue.HasPending)
            return false;

        if (!queue.TryDrain(
                snapshotValue => { value = snapshotValue; return true; },
                delta => { value = delta; return true; }) ||
            value != 20 || queue.HasPending)
            return false;

        return VerifyDroneCloudOrdering() && VerifyComponentOrdering();
    }

    private static bool VerifyDroneCloudOrdering()
    {
        var queue = new OrderedSnapshotDeltaQueue<Dictionary<int, int>, KeyValuePair<int, int>>();
        var cloud = new Dictionary<int, int>();
        queue.SetSnapshot(new Dictionary<int, int> { [7] = 2 });
        queue.EnqueueDelta(new KeyValuePair<int, int>(7, 3));

        return queue.TryDrain(
                   snapshot =>
                   {
                       cloud.Clear();
                       foreach (var (typeId, amount) in snapshot)
                           cloud[typeId] = amount;
                       return true;
                   },
                   delta =>
                   {
                       cloud[delta.Key] = delta.Value;
                       return true;
                   }) &&
               cloud.TryGetValue(7, out var amount) && amount == 3 && !queue.HasPending;
    }

    private static bool VerifyComponentOrdering()
    {
        var queue = new OrderedSnapshotDeltaQueue<Dictionary<string, byte>, KeyValuePair<string, byte>>();
        var components = new Dictionary<string, byte>();

        // A delta arriving while InitialComponentsHandler waits one frame must replay
        // after the snapshot instead of being overwritten by it.
        queue.SetSnapshot(new Dictionary<string, byte> { ["drone-core"] = 2 });
        queue.EnqueueDelta(new KeyValuePair<string, byte>("drone-core", 5));

        return queue.TryDrain(
                   snapshot =>
                   {
                       components.Clear();
                       foreach (var (componentId, count) in snapshot)
                           components[componentId] = count;
                       return true;
                   },
                   delta =>
                   {
                       components[delta.Key] = delta.Value;
                       return true;
                   }) &&
               components.TryGetValue("drone-core", out var count) && count == 5 && !queue.HasPending;
    }

#endif
}

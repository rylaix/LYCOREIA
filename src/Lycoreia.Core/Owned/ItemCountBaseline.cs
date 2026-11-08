// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System.Collections.Generic;

namespace Lycoreia.Owned;

/// <summary>
/// Last count of each refinery / gadget inventory item that this seat published or applied from the network. The reconcile publishes
/// only differences against it, so a network-applied count must be recorded here or it is echoed back as a local change.
/// </summary>
public sealed class ItemCountBaseline
{
    readonly Dictionary<int, int> _synced = new();
    public bool Ready { get; private set; }

    public void Record(int itemId, int count) { if (itemId != 0) _synced[itemId] = count; }
    public void Reset() { _synced.Clear(); Ready = false; }
    public bool TryGet(int itemId, out int count) => _synced.TryGetValue(itemId, out count);

    /// <summary>
    /// Local changes not yet published: current counts against the baseline. Before the first pass the current counts become the baseline
    /// (nothing is sent); an item missing from the game counts as 0. Unmapped items (id 0) are never published.
    /// </summary>
    public List<(int ItemId, int Delta, int Count)> Diff(IEnumerable<(int ItemId, int Count)> current)
    {
        var seen = new HashSet<int>(); var changes = new List<(int, int, int)>();
        foreach (var (id, count) in current)
        {
            if (id == 0 || !seen.Add(id)) continue;
            if (!_synced.TryGetValue(id, out var synced)) { synced = Ready ? 0 : count; _synced[id] = synced; }
            if (count != synced) changes.Add((id, count - synced, count));
        }
        foreach (var kv in _synced) if (kv.Value != 0 && !seen.Contains(kv.Key)) changes.Add((kv.Key, -kv.Value, 0));
        Ready = true;
        return changes;
    }
}

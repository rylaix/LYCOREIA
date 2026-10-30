using SR2MP.Packets.LandPlots;

namespace SR2MP.Shared.Managers;

/// <summary>
/// Tracks the newest authoritative garden state by revision. Revisions allow the initial
/// land-plot snapshot and periodic garden updates to arrive in either order.
/// </summary>
internal sealed class GardenStateCache
{
    private readonly Dictionary<string, GardenUpdatePacket.Entry> latest = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> highestRevision = new(StringComparer.Ordinal);

    internal long NextRevision(string gardenId)
    {
        if (string.IsNullOrWhiteSpace(gardenId))
            throw new ArgumentException("Garden ID cannot be empty.", nameof(gardenId));

        highestRevision.TryGetValue(gardenId, out var current);
        if (current == long.MaxValue)
            throw new InvalidOperationException($"Garden revision exhausted for '{gardenId}'.");

        return current + 1;
    }

    internal bool TryRemember(GardenUpdatePacket.Entry entry)
    {
        if (!HasFiniteState(entry) || entry.Revision <= 0)
            return false;

        highestRevision.TryGetValue(entry.GardenId, out var current);
        if (entry.Revision <= current)
            return false;

        highestRevision[entry.GardenId] = entry.Revision;
        latest[entry.GardenId] = entry;
        return true;
    }

    internal static bool HasFiniteState(GardenUpdatePacket.Entry entry)
        => !string.IsNullOrWhiteSpace(entry.GardenId) &&
           double.IsFinite(entry.NextSpawnTime) && float.IsFinite(entry.StoredWater);

    internal bool CanAcceptRevision(string gardenId, long revision)
    {
        if (string.IsNullOrWhiteSpace(gardenId) || revision <= 0)
            return false;

        highestRevision.TryGetValue(gardenId, out var current);
        return revision > current;
    }

    internal long GetHighestRevision(string gardenId)
        => highestRevision.TryGetValue(gardenId, out var revision) ? revision : 0;

    /// <summary>
    /// Advances the shared revision stream for state carried by another garden packet
    /// without replacing the cached growth-timer payload.
    /// </summary>
    internal bool AdvanceRevision(string gardenId, long revision)
    {
        if (!CanAcceptRevision(gardenId, revision))
            return false;

        highestRevision[gardenId] = revision;
        return true;
    }

    /// <summary>
    /// Records a newer empty-plot snapshot, invalidating old crop timing without applying
    /// those values to a future crop planted in the same garden.
    /// </summary>
    internal bool Invalidate(string gardenId, long revision)
    {
        if (string.IsNullOrWhiteSpace(gardenId) || revision <= 0)
            return false;

        highestRevision.TryGetValue(gardenId, out var current);
        if (revision <= current)
            return false;

        highestRevision[gardenId] = revision;
        latest.Remove(gardenId);
        return true;
    }

    internal bool TryGetLatest(string gardenId, out GardenUpdatePacket.Entry entry)
        => latest.TryGetValue(gardenId, out entry);

    internal bool TryGetLatestWithPrefix(string prefix, out GardenUpdatePacket.Entry entry)
    {
        entry = default;
        var found = false;

        foreach (var state in latest.Values)
        {
            if (!state.GardenId.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            if (!found || state.Revision > entry.Revision)
            {
                entry = state;
                found = true;
            }
        }

        return found;
    }

    internal void Clear()
    {
        latest.Clear();
        highestRevision.Clear();
    }

    internal static bool VerifyRevisionOrdering()
    {
        var cache = new GardenStateCache();
        var newer = new GardenUpdatePacket.Entry { GardenId = "plot-a_0", Revision = 2, StoredWater = 3 };
        var older = new GardenUpdatePacket.Entry { GardenId = "plot-a_0", Revision = 1, StoredWater = 1 };
        var tombstoneRevision = cache.NextRevision("plot-b_0");
        var staleAfterTombstone = new GardenUpdatePacket.Entry { GardenId = "plot-b_0", Revision = 1 };
        var freshAfterTombstone = new GardenUpdatePacket.Entry { GardenId = "plot-b_0", Revision = 2, StoredWater = 4 };

        return cache.TryRemember(newer)
               && !cache.TryRemember(older)
               && cache.TryGetLatest("plot-a_0", out var accepted)
               && accepted.Revision == 2
               && cache.Invalidate("plot-b_0", tombstoneRevision)
               && !cache.TryRemember(staleAfterTombstone)
               && cache.TryRemember(freshAfterTombstone)
               && cache.TryGetLatest("plot-b_0", out var afterTombstone)
               && afterTombstone.Revision == 2
               && !HasFiniteState(new GardenUpdatePacket.Entry
               {
                   GardenId = "plot-c_0",
                   NextSpawnTime = double.NaN,
                   StoredWater = 1
               })
               && !HasFiniteState(new GardenUpdatePacket.Entry
               {
                   GardenId = "plot-c_0",
                   NextSpawnTime = 1,
                   StoredWater = float.PositiveInfinity
               })
               && !cache.TryRemember(new GardenUpdatePacket.Entry
               {
                   GardenId = "plot-c_0",
                   Revision = 1,
                   NextSpawnTime = double.NegativeInfinity,
                   StoredWater = 1
               });
    }
}

namespace SR2MP.Shared.Managers;

/// <summary>
/// Keeps the newest authoritative planted-crop value per garden. The revision comes from
/// the garden's shared state stream, so a crop snapshot can be compared with live garden
/// timer updates and plant/clear deltas.
/// </summary>
internal sealed class GardenCropStateCache
{
    internal readonly record struct State(string GardenId, long Revision, int Crop);

    private readonly Dictionary<string, long> highestRevision = new(StringComparer.Ordinal);
    private readonly Dictionary<string, State> latest = new(StringComparer.Ordinal);

    internal bool CanAccept(string gardenId, long revision)
    {
        if (string.IsNullOrWhiteSpace(gardenId) || revision <= 0)
            return false;

        highestRevision.TryGetValue(gardenId, out var current);
        return revision > current;
    }

    internal bool TryRemember(string gardenId, long revision, int crop)
    {
        if (!CanAccept(gardenId, revision))
            return false;

        highestRevision[gardenId] = revision;
        latest[gardenId] = new State(gardenId, revision, crop);
        return true;
    }

    internal bool Invalidate(string gardenId, long revision)
    {
        if (!CanAccept(gardenId, revision))
            return false;

        highestRevision[gardenId] = revision;
        latest.Remove(gardenId);
        return true;
    }

    internal bool TryGetLatest(string gardenId, out State state)
        => latest.TryGetValue(gardenId, out state);

    internal long GetHighestRevision(string gardenId)
        => highestRevision.TryGetValue(gardenId, out var revision) ? revision : 0;

    internal int? SelectSnapshotCrop(string gardenId, long snapshotRevision, int snapshotCrop)
    {
        if (string.IsNullOrWhiteSpace(gardenId) || snapshotRevision <= 0)
            return null;

        // Timer-only garden revisions do not supersede the crop carried by this
        // snapshot. Only a newer crop event (including an unknown-crop tombstone)
        // can make the snapshot crop stale.
        if (GetHighestRevision(gardenId) > snapshotRevision)
        {
            return TryGetLatest(gardenId, out var newer) && newer.Revision > snapshotRevision
                ? newer.Crop
                : null;
        }

        if (snapshotCrop == int.MinValue)
        {
            Invalidate(gardenId, snapshotRevision);
            return null;
        }

        if (TryRemember(gardenId, snapshotRevision, snapshotCrop))
            return snapshotCrop;

        return TryGetLatest(gardenId, out var current) && current.Revision >= snapshotRevision
            ? current.Crop
            : null;
    }

    internal void Clear()
    {
        highestRevision.Clear();
        latest.Clear();
    }

    internal static bool VerifyRevisionOrdering()
    {
        var cache = new GardenCropStateCache();
        return cache.TryRemember("plot-a_0", 2, 42)
               && !cache.TryRemember("plot-a_0", 1, 9)
               && cache.TryGetLatest("plot-a_0", out var newest)
               && newest.Revision == 2 && newest.Crop == 42
               && cache.TryRemember("plot-a_0", 3, 9)
               && cache.TryGetLatest("plot-a_0", out var cleared)
               && cleared.Revision == 3 && cleared.Crop == 9
               && cache.Invalidate("plot-a_0", 4)
               && !cache.TryRemember("plot-a_0", 3, 42)
               && cache.GetHighestRevision("plot-a_0") == 4
               && cache.TryRemember("plot-a_0", 5, 42)
               && cache.TryGetLatest("plot-a_0", out var replanted)
               && replanted.Revision == 5 && replanted.Crop == 42
               && cache.SelectSnapshotCrop("plot-a_0", 4, 9) == 42
               // Crop reconciliation ignores timer revisions; only newer crop events
               // and crop tombstones can supersede a snapshot's crop value.
               && cache.SelectSnapshotCrop("plot-c_0", 2, 42) == 42
               && cache.SelectSnapshotCrop("plot-a_0", 6, 9) == 9
               && cache.SelectSnapshotCrop("plot-a_0", 7, int.MinValue) == null
               && !cache.TryGetLatest("plot-a_0", out _);
    }
}

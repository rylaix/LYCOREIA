// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Il2Cpp;
namespace Lycoreia.Owned;
using System.Collections;


/// <summary>
/// Owner 2026-11-01: after joining, what was planted in the host's gardens stayed invisible to the guest until the
/// host pressed "resync everyone". The join snapshot plants each garden right after replacing the guest's
/// (fresh-save, empty) plot with the host's plot, in the same frame, and that can silently plant nothing; the
/// host's produce then has no plant to attach to. A resync (plots already the right type) works.
/// Guest-side safety net: remember the crop each garden should have; ~2 s after the actor snapshot is applied,
/// any garden without its plant asks the host for one resync (once per connection, so it can never loop).
/// </summary>
public static class GardenJoinCheck
{
    private static readonly Dictionary<string, IdentifiableType> Expected = new();
    private static bool resyncRequested;
    private static bool scheduled;

    internal static void Reset()
    {
        Expected.Clear();
        resyncRequested = false;
        scheduled = false;
    }

    internal static void ExpectCrop(string plotId, IdentifiableType crop) => Expected[plotId] = crop;

    internal static void ClearExpectation(string plotId) => Expected.Remove(plotId);

    /// <summary>Called when the actor snapshot was applied on this guest.</summary>
    internal static void Schedule()
    {
        if (scheduled || Expected.Count == 0 || Mod.IsHost)
            return;
        scheduled = true;
        Lycoreia.Game.Coroutines.Start(CheckLater());
    }

    private static IEnumerator CheckLater()
    {
        var until = UnityEngine.Time.realtimeSinceStartup + 2f;
        while (UnityEngine.Time.realtimeSinceStartup < until)
            yield return null;
        scheduled = false;
        if (!Mod.Session.Connected)
            yield break;

        var missing = new List<string>();
        foreach (var (plotId, crop) in Expected)
        {
            if (!Lycoreia.Game.GameState.LandPlots.TryGetValue(plotId, out var model) || model == null || !model.gameObj)
                continue; // not loaded here: the game plants it from the model when the plot loads
            var spawner = model.gameObj.GetComponentInChildren<SpawnResource>(true);
            var planted = spawner ? spawner!._resourceGrowerDefinition?._primaryResourceType : null;
            if (planted == null || planted.Pointer != crop.Pointer)
                missing.Add($"{plotId}({crop.name})");
        }
        Expected.Clear();

        if (missing.Count == 0)
            yield break;
        Log.Warn($"Garden check: {missing.Count} garden(s) not planted after joining: {string.Join(", ", missing)}" +
                            (resyncRequested ? " (resync already requested once)" : "; requesting a resync."));
        if (resyncRequested)
            yield break;
        resyncRequested = true;
        Mod.Guest.RequestResync();
    }
}

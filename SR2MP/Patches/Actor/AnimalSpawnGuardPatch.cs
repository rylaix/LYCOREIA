using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.DataModel;

namespace SR2MP.Patches.Actor;

/// <summary>
/// "Chicken waterfall": in co-op a wild animal spawner kept spawning (~20 hens/s at one Gorge
/// spawner, 1.3k hens in 3 min) because its native population check stopped seeing the hens once
/// another peer owned/simulated them. While connected, a spawner may only spawn when the animals
/// actually near it (any owner, from the networked actor table) are below a cap. Singleplayer is
/// untouched. Only CellDirector.NeedsMoreAnimals is patched: patching DirectedActorSpawner.OnActorSpawned
/// crashes the game at load, and CanContinueSpawning / DirectedAnimalSpawner.CanSpawn (Nullable
/// parameter) throw inside the Il2Cpp trampoline on every call.
/// </summary>
internal static class AnimalSpawnGuard
{
    private const float NearbyRadius = 45f;
    private const int NearbyAnimalCap = 12;
    private const float CountCacheSeconds = 2f;

    private static readonly Dictionary<int, (float At, int Count)> CountCache = new();

    internal static bool IsBlocked(DirectedAnimalSpawner spawner, out int nearby)
    {
        nearby = 0;
        try
        {
            nearby = CountNearbyAnimals(spawner.GetInstanceID(), spawner.transform.position,
                UnityEngine.Time.unscaledTime);
            return nearby >= NearbyAnimalCap;
        }
        catch (Exception ex)
        {
            // Never throw into native spawner code: that crashes the game.
            SrLogger.LogWarning($"Animal spawn guard failed: {ex.Message}");
            return false;
        }
    }

    private static int CountNearbyAnimals(int key, Vector3 center, float now)
    {
        if (CountCache.TryGetValue(key, out var cached) && now - cached.At < CountCacheSeconds)
            return cached.Count;

        var count = 0;
        var radiusSq = NearbyRadius * NearbyRadius;
        foreach (var model in ActorManager.Actors.Values)
        {
            if (model == null || model.TryCast<AnimalModel>() == null)
                continue;
            if ((model.lastPosition - center).sqrMagnitude <= radiusSq)
                count++;
        }

        CountCache[key] = (now, count);
        return count;
    }
}

/// <summary>The spawn cap: a cell whose animal spawners are all saturated does not ask for more animals.</summary>
[HarmonyPatch(typeof(CellDirector), nameof(CellDirector.NeedsMoreAnimals))]
internal static class CellAnimalSpawnGuardPatch
{
    private static float nextBlockLogAt;

    public static void Postfix(CellDirector __instance, ref bool __result)
    {
        if (!__result || !Main.IsNetworkActive || !__instance)
            return;

        var spawners = __instance._animalSpawners;
        if (spawners == null || spawners.Count == 0)
            return;

        var nearby = 0;
        foreach (var spawner in spawners)
            if (spawner && !AnimalSpawnGuard.IsBlocked(spawner, out nearby))
                return;

        __result = false;
        var now = UnityEngine.Time.unscaledTime;
        if (now >= nextBlockLogAt)
        {
            nextBlockLogAt = now + 10f;
            Logger.LogDiagnostic("actor", "animal-spawn-blocked",
                $"cell={__instance.name}; spawners={spawners.Count}; nearby={nearby}");
        }
    }
}

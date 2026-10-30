using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Labyrinth;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Labyrinth;

[HarmonyPatch(typeof(StabilizingFieldSpawnerModel), nameof(StabilizingFieldSpawnerModel.NotifyParticipants))]
internal static class StabilizingFieldSpawnerModelNotifyPatch
{
    public static void Postfix(StabilizingFieldSpawnerModel __instance)
        => NetworkStabilizingFieldSpawnerManager.Broadcast(__instance);
}

[HarmonyPatch(typeof(StabilizingFieldSpawner), nameof(StabilizingFieldSpawner.Update))]
internal static class StabilizingFieldSpawnerUpdatePatch
{
    public static void Postfix(StabilizingFieldSpawner __instance)
        => NetworkStabilizingFieldSpawnerManager.Broadcast(__instance._model);
}

[HarmonyPatch(typeof(StabilizingFieldSpawner), nameof(StabilizingFieldSpawner.Spawn))]
internal static class StabilizingFieldSpawnerSpawnPatch
{
    public static void Postfix(StabilizingFieldSpawner __instance)
        => NetworkStabilizingFieldSpawnerManager.Broadcast(__instance._model);
}

[HarmonyPatch(typeof(StabilizingFieldSpawner), nameof(StabilizingFieldSpawner.DespawnAttached))]
internal static class StabilizingFieldSpawnerDespawnPatch
{
    public static void Postfix(StabilizingFieldSpawner __instance)
        => NetworkStabilizingFieldSpawnerManager.Broadcast(__instance._model);
}

[HarmonyPatch(typeof(StabilizingFieldSpawner), nameof(StabilizingFieldSpawner.MaybeSpawn))]
internal static class StabilizingFieldSpawnerMaybeSpawnPatch
{
    public static void Postfix(StabilizingFieldSpawner __instance)
        => NetworkStabilizingFieldSpawnerManager.Broadcast(__instance._model);
}

[HarmonyPatch(typeof(StabilizingFieldSpawner), nameof(StabilizingFieldSpawner.StabilizerOnObjStabilized))]
internal static class StabilizingFieldSpawnerStabilizedPatch
{
    public static void Postfix(StabilizingFieldSpawner __instance)
        => NetworkStabilizingFieldSpawnerManager.Broadcast(__instance._model);
}

[HarmonyPatch(typeof(StabilizingFieldSpawner), nameof(StabilizingFieldSpawner.OnTriggerEnter))]
internal static class StabilizingFieldSpawnerTriggerPatch
{
    public static void Postfix(StabilizingFieldSpawner __instance)
        => NetworkStabilizingFieldSpawnerManager.Broadcast(__instance._model);
}

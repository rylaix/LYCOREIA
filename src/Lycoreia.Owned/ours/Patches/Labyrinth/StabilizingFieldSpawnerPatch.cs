// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Labyrinth;
using Lycoreia.Owned.Shared.Managers;

namespace Lycoreia.Owned.Patches.Labyrinth;

[HarmonyPatch(typeof(StabilizingFieldSpawnerModel), nameof(StabilizingFieldSpawnerModel.NotifyParticipants))]
internal static class StabilizingFieldSpawnerModelNotifyPatch
{
    public static void Postfix(StabilizingFieldSpawnerModel __instance)
    {
        try { NetworkStabilizingFieldSpawnerManager.Broadcast(__instance); }
        catch (Exception e) { OwnedLog.LogWarning("NetworkStabilizingFieldSpawnerManager.Broadcast hook: " + e.Message); }
    }
}

[HarmonyPatch(typeof(StabilizingFieldSpawner), nameof(StabilizingFieldSpawner.Update))]
internal static class StabilizingFieldSpawnerUpdatePatch
{
    public static void Postfix(StabilizingFieldSpawner __instance)
    {
        try { NetworkStabilizingFieldSpawnerManager.Broadcast(__instance._model); }
        catch (Exception e) { OwnedLog.LogWarning("NetworkStabilizingFieldSpawnerManager.Broadcast hook: " + e.Message); }
    }
}

[HarmonyPatch(typeof(StabilizingFieldSpawner), nameof(StabilizingFieldSpawner.Spawn))]
internal static class StabilizingFieldSpawnerSpawnPatch
{
    public static void Postfix(StabilizingFieldSpawner __instance)
    {
        try { NetworkStabilizingFieldSpawnerManager.Broadcast(__instance._model); }
        catch (Exception e) { OwnedLog.LogWarning("NetworkStabilizingFieldSpawnerManager.Broadcast hook: " + e.Message); }
    }
}

[HarmonyPatch(typeof(StabilizingFieldSpawner), nameof(StabilizingFieldSpawner.DespawnAttached))]
internal static class StabilizingFieldSpawnerDespawnPatch
{
    public static void Postfix(StabilizingFieldSpawner __instance)
    {
        try { NetworkStabilizingFieldSpawnerManager.Broadcast(__instance._model); }
        catch (Exception e) { OwnedLog.LogWarning("NetworkStabilizingFieldSpawnerManager.Broadcast hook: " + e.Message); }
    }
}

[HarmonyPatch(typeof(StabilizingFieldSpawner), nameof(StabilizingFieldSpawner.MaybeSpawn))]
internal static class StabilizingFieldSpawnerMaybeSpawnPatch
{
    public static void Postfix(StabilizingFieldSpawner __instance)
    {
        try { NetworkStabilizingFieldSpawnerManager.Broadcast(__instance._model); }
        catch (Exception e) { OwnedLog.LogWarning("NetworkStabilizingFieldSpawnerManager.Broadcast hook: " + e.Message); }
    }
}

[HarmonyPatch(typeof(StabilizingFieldSpawner), nameof(StabilizingFieldSpawner.StabilizerOnObjStabilized))]
internal static class StabilizingFieldSpawnerStabilizedPatch
{
    public static void Postfix(StabilizingFieldSpawner __instance)
    {
        try { NetworkStabilizingFieldSpawnerManager.Broadcast(__instance._model); }
        catch (Exception e) { OwnedLog.LogWarning("NetworkStabilizingFieldSpawnerManager.Broadcast hook: " + e.Message); }
    }
}

[HarmonyPatch(typeof(StabilizingFieldSpawner), nameof(StabilizingFieldSpawner.OnTriggerEnter))]
internal static class StabilizingFieldSpawnerTriggerPatch
{
    public static void Postfix(StabilizingFieldSpawner __instance)
    {
        try { NetworkStabilizingFieldSpawnerManager.Broadcast(__instance._model); }
        catch (Exception e) { OwnedLog.LogWarning("NetworkStabilizingFieldSpawnerManager.Broadcast hook: " + e.Message); }
    }
}

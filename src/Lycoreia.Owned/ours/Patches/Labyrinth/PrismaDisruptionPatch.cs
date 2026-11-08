// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Lycoreia.Owned.Shared.Managers;

namespace Lycoreia.Owned.Patches.Labyrinth;

[HarmonyPatch(typeof(PrismaDisruptionModel), nameof(PrismaDisruptionModel.NotifyParticipants))]
internal static class PrismaDisruptionNotifyParticipantsPatch
{
    public static void Postfix(PrismaDisruptionModel __instance)
    {
        try { NetworkPrismaDisruptionManager.Broadcast(__instance); }
        catch (Exception e) { OwnedLog.LogWarning("NetworkPrismaDisruptionManager.Broadcast hook: " + e.Message); }
    }
}

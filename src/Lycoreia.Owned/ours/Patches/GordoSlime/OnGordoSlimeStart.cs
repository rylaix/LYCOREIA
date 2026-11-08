// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using HarmonyLib;

namespace Lycoreia.Owned.Patches.GordoSlime;

/// <summary>
/// Native GordoEat.Start bursts a gordo whose eat count is >= its model's target. A model created from a host
/// snapshot for a gordo the host never loaded carries target 0, so fill it from the gordo's own prefab target
/// before Start checks it (owner 2026-11-01, evidence/manual-20261101j).
/// </summary>
[HarmonyPatch(typeof(GordoEat), nameof(GordoEat.Start))]
internal static class OnGordoSlimeStart
{
    public static void Prefix(GordoEat __instance)
    {
        if (!ModFacade.Server.IsRunning && !ModFacade.Client.IsConnected) return;
        var model = __instance.GordoModel;
        if (model == null || model.targetCount > 0 || __instance.TargetCount <= 0) return;
        model.targetCount = __instance.TargetCount;
    }
}

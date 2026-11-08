// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Player.PlayerItems;

namespace Lycoreia.Owned.Patches.FX;

[HarmonyPatch(typeof(VacuumItem), "VacTriggered")]
internal static class OnVacuumSuctionStarted
{
    public static void Postfix(VacuumItem __instance)
    {
        if (__instance && __instance._vacMode == VacuumItem.VacMode.VAC)
            Publish(Lycoreia.Net.Messages.PlayerFxKind.RunStart);
    }

    internal static void Publish(Lycoreia.Net.Messages.PlayerFxKind type)
    {
        if (!ModFacade.Server.IsRunning && !ModFacade.Client.IsConnected)
            return;

        ModFacade.SendToAllOrServer(new Lycoreia.Net.Messages.PlayerFx { Kind = type, PlayerId = LocalID });
    }
}

[HarmonyPatch(typeof(VacuumItem), "VacCanceled")]
internal static class OnVacuumSuctionEnded
{
    public static void Prefix(VacuumItem __instance, out bool __state)
        => __state = __instance && __instance._vacMode == VacuumItem.VacMode.VAC;

    public static void Postfix(VacuumItem __instance, bool __state)
    {
        if (__state && (!__instance || __instance._vacMode != VacuumItem.VacMode.VAC))
            OnVacuumSuctionStarted.Publish(Lycoreia.Net.Messages.PlayerFxKind.RunEnd);
    }
}

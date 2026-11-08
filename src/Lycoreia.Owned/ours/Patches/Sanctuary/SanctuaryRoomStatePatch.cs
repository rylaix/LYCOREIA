// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.UI.Themes;
using Lycoreia.Owned.Shared.Managers;

namespace Lycoreia.Owned.Patches.Sanctuary;

// The model setter shares native code with CutsceneDirector.RegisterIntroSequenceUIRoot.
// Hook the controller transaction, which has its own native body.
[HarmonyPatch(typeof(Il2CppMonomiPark.SlimeRancher.SanctuaryRoomController), nameof(Il2CppMonomiPark.SlimeRancher.SanctuaryRoomController.SetTheme))]
internal static class SanctuaryRoomThemeChanged
{
    public static void Postfix(Il2CppMonomiPark.SlimeRancher.SanctuaryRoomController __instance)
    {
        try { NetworkSanctuaryManager.SendRoomState(__instance._model); }
        catch (Exception e) { OwnedLog.LogWarning("NetworkSanctuaryManager.SendRoomState hook: " + e.Message); }
    }
}

[HarmonyPatch(typeof(SanctuaryRoomModel), nameof(SanctuaryRoomModel.SetPreplacedGadget))]
internal static class SanctuaryRoomPreplacedGadgetSet
{
    public static void Postfix(SanctuaryRoomModel __instance)
    {
        try { NetworkSanctuaryManager.SendRoomState(__instance); }
        catch (Exception e) { OwnedLog.LogWarning("NetworkSanctuaryManager.SendRoomState hook: " + e.Message); }
    }
}

[HarmonyPatch(typeof(SanctuaryRoomModel), nameof(SanctuaryRoomModel.ClearPreplacedGadget))]
internal static class SanctuaryRoomPreplacedGadgetCleared
{
    public static void Postfix(SanctuaryRoomModel __instance)
    {
        try { NetworkSanctuaryManager.SendRoomState(__instance); }
        catch (Exception e) { OwnedLog.LogWarning("NetworkSanctuaryManager.SendRoomState hook: " + e.Message); }
    }
}

[HarmonyPatch(typeof(SanctuaryThemeInventoryModel), nameof(SanctuaryThemeInventoryModel.SetThemeOwned))]
internal static class SanctuaryThemeOwned
{
    public static void Postfix(ThemeDefinition __0)
    {
        try { NetworkSanctuaryManager.SendThemeOwnership(__0, true); }
        catch (Exception e) { OwnedLog.LogWarning("NetworkSanctuaryManager.SendThemeOwnership hook: " + e.Message); }
    }
}

[HarmonyPatch(typeof(SanctuaryThemeInventoryModel), nameof(SanctuaryThemeInventoryModel.SetThemeUnowned))]
internal static class SanctuaryThemeUnowned
{
    public static void Postfix(ThemeDefinition __0)
    {
        try { NetworkSanctuaryManager.SendThemeOwnership(__0, false); }
        catch (Exception e) { OwnedLog.LogWarning("NetworkSanctuaryManager.SendThemeOwnership hook: " + e.Message); }
    }
}

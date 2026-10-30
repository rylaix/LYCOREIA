using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.UI.Themes;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Sanctuary;

[HarmonyPatch(typeof(SanctuaryRoomModel), nameof(SanctuaryRoomModel.SetTheme))]
internal static class SanctuaryRoomThemeChanged
{
    public static void Postfix(SanctuaryRoomModel __instance, ThemeDefinition __0)
        => NetworkSanctuaryManager.SendRoomState(__instance);
}

[HarmonyPatch(typeof(SanctuaryRoomModel), nameof(SanctuaryRoomModel.SetPreplacedGadget))]
internal static class SanctuaryRoomPreplacedGadgetSet
{
    public static void Postfix(SanctuaryRoomModel __instance)
        => NetworkSanctuaryManager.SendRoomState(__instance);
}

[HarmonyPatch(typeof(SanctuaryRoomModel), nameof(SanctuaryRoomModel.ClearPreplacedGadget))]
internal static class SanctuaryRoomPreplacedGadgetCleared
{
    public static void Postfix(SanctuaryRoomModel __instance)
        => NetworkSanctuaryManager.SendRoomState(__instance);
}

[HarmonyPatch(typeof(SanctuaryThemeInventoryModel), nameof(SanctuaryThemeInventoryModel.SetThemeOwned))]
internal static class SanctuaryThemeOwned
{
    public static void Postfix(ThemeDefinition __0)
        => NetworkSanctuaryManager.SendThemeOwnership(__0, true);
}

[HarmonyPatch(typeof(SanctuaryThemeInventoryModel), nameof(SanctuaryThemeInventoryModel.SetThemeUnowned))]
internal static class SanctuaryThemeUnowned
{
    public static void Postfix(ThemeDefinition __0)
        => NetworkSanctuaryManager.SendThemeOwnership(__0, false);
}

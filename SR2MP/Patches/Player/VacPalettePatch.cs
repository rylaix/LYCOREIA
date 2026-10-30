using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Ranch;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Player;

[HarmonyPatch(typeof(PaletteDirector), nameof(PaletteDirector.SetPalette))]
internal static class VacPalettePatch
{
    public static void Postfix(PaletteTarget target, Palette palette)
        => NetworkVacPaletteManager.OnLocalPaletteSet(target, palette);
}

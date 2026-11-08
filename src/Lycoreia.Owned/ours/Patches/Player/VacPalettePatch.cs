// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Ranch;
using Lycoreia.Owned.Shared.Managers;

namespace Lycoreia.Owned.Patches.Player;

[HarmonyPatch(typeof(PaletteDirector), nameof(PaletteDirector.SetPalette))]
internal static class VacPalettePatch
{
    public static void Postfix(PaletteTarget target, Palette palette)
        => NetworkVacPaletteManager.OnLocalPaletteSet(target, palette);
}

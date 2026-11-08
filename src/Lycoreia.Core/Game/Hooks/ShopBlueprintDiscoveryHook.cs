// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.Pedia;
using Il2CppMonomiPark.SlimeRancher.Economy;
using Il2CppMonomiPark.SlimeRancher.Shop.Runtime;
using Il2CppMonomiPark.SlimeRancher.UI.Shop.Pages;

namespace Lycoreia.Game.Hooks
{
    [HarmonyPatch(typeof(ShopRuntimeItem), nameof(ShopRuntimeItem.ShouldUnlockPediaOnView))]
    static class ShopBlueprintDiscoveryHook
    {
        static void Postfix(ShopRuntimeItem __instance, ref bool __result)
        {
            // Individual gadget/decor items also have pedia entries. Browsing is not acquisition.
            if (Mod.IsNetworkActive)
                __result = false;
        }
    }

    /// <summary>Marks the native shop button display refresh, during which pedia unlocks are suppressed in a session.</summary>
    [HarmonyPatch(typeof(ShopItemButton), nameof(ShopItemButton.RepopulateDisplay))]
    static class ShopButtonViewScope
    {
        [ThreadStatic] static int _depth;
        internal static bool Active => _depth > 0;
        static void Prefix() => _depth++;
        static Exception Finalizer(Exception __exception) { if (_depth > 0) _depth--; return __exception; }
    }

    [HarmonyPatch(typeof(PediaDirector), nameof(PediaDirector.Unlock), new[] { typeof(PediaEntry), typeof(bool) })]
    static class ShopViewPediaUnlockGuard
    {
        static bool Prefix(PediaEntry entry, ref bool __result)
        {
            if (!ShopButtonViewScope.Active || !Mod.IsNetworkActive) return true;
            __result = false;
            Mod.Diagnostics?.Record("world", "pedia-unlock-shop-view-suppressed", "id=" + (entry != null ? entry.PersistenceId : "null"));
            return false;
        }
    }
}

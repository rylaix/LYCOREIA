// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Economy;
using Il2CppMonomiPark.SlimeRancher.Player.CharacterController;
using Il2CppMonomiPark.SlimeRancher.Ranch;
using Lycoreia.Net;
using Lycoreia.Net.Messages;

namespace Lycoreia.Game.Hooks
{
    /// <summary>The local player exists: start sending updates (the sender component is attached by the avatar module).</summary>
    [HarmonyPatch(typeof(SRCharacterController), nameof(SRCharacterController.Awake))]
    static class LocalPlayerAwakeHook
    {
        public static event Action LocalPlayerCreated;
        static void Postfix() { try { LocalPlayerCreated?.Invoke(); } catch (Exception e) { HookGuard.Fail("SRCharacterController.Awake", e); } }
    }

    /// <summary>Plort sales by a guest are sent to the host; the host's own sales are applied natively.</summary>
    [HarmonyPatch(typeof(PlortEconomyDirector), nameof(PlortEconomyDirector.RegisterSold))]
    static class PlortSoldHook
    {
        static void Postfix(IdentifiableType id, int count, int price)
        {
            try
            {
                if (!Mod.Initialised || !Mod.IsGuest || Applying.Active || id == null || count < 1) return;
                int itemId = ActorTypes.IdOf(id); if (itemId <= 0) return;
                Mod.Diagnostics?.Record("market", "sale-sent", $"item={itemId}; count={count}; local-price={price}");
                Mod.Node.SendToHost(new PlortSold { RequestId = Guid.NewGuid(), ItemId = itemId, Count = count, Price = price });
            }
            catch (Exception e) { HookGuard.Fail("RegisterSold", e); }
        }
    }

    // The vac palette hook is the Lycoreia VacPalettePatch + NetworkVacPaletteManager.

    /// <summary>Host only: a changed upgrade level is announced to everyone, unless it came from a received message.</summary>
    [HarmonyPatch(typeof(UpgradeModel), nameof(UpgradeModel.IncrementUpgradeLevel))]
    static class UpgradeLevelHook
    {
        public static event Action<UpgradeDefinition> LevelChanged;
        static void Postfix(UpgradeDefinition definition, bool __result)
        {
            try { if (__result && Mod.Initialised && Mod.IsHost && !Applying.Active) LevelChanged?.Invoke(definition); }
            catch (Exception e) { HookGuard.Fail("IncrementUpgradeLevel", e); }
        }
    }
}

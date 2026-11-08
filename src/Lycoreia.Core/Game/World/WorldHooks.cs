// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Economy;
using Il2CppMonomiPark.SlimeRancher.Pedia;
using Lycoreia.Game.Hooks;
using Lycoreia.Net;
using UnityEngine;

namespace Lycoreia.Game.World
{
    static class WH { public static bool On => WorldRuntime.Ready && Mod.IsNetworkActive; }

    // SetGameObject setters share native code with unrelated startup methods in this build.
    // Pending states are retried by WorldRuntime.Pump instead of detouring those setters.
    // ---- keyed models: a change notification publishes, a bound scene object applies early state
    [HarmonyPatch(typeof(WorldStatePrimarySwitch), nameof(WorldStatePrimarySwitch.SetStateForAll))]
    static class PrimarySwitchStateHook
    {
        static void Postfix(WorldStatePrimarySwitch __instance)
        {
            try { if (WH.On && !Applying.Active && __instance?._model != null) WorldRuntime.Switches.OnModelChanged(__instance._model); }
            catch (Exception e) { HookGuard.Fail("WorldStatePrimarySwitch.SetStateForAll", e); }
        }
    }
    [HarmonyPatch(typeof(WorldSwitchModel), nameof(WorldSwitchModel.NotifyParticipants))]
    static class SwitchNotifyHook { static void Postfix(WorldSwitchModel __instance) { if (WH.On) WorldRuntime.Switches.OnModelChanged(__instance); } }

    [HarmonyPatch(typeof(AccessDoorModel), nameof(AccessDoorModel.NotifyParticipants))]
    static class DoorNotifyHook { static void Postfix(AccessDoorModel __instance) { if (WH.On) WorldRuntime.Doors.OnModelChanged(__instance); } }

    [HarmonyPatch(typeof(PuzzleSlotModel), nameof(PuzzleSlotModel.NotifyParticipants))]
    static class SlotNotifyHook { static void Postfix(PuzzleSlotModel __instance) { if (WH.On) WorldRuntime.Slots.OnModelChanged(__instance); } }

    [HarmonyPatch(typeof(PlortDepositorModel), nameof(PlortDepositorModel.NotifyParticipants))]
    static class DepositorNotifyHook { static void Postfix(PlortDepositorModel __instance) { if (WH.On) WorldRuntime.Depositors.OnModelChanged(__instance); } }

    [HarmonyPatch(typeof(PrismaBarrierModel), nameof(PrismaBarrierModel.NotifyParticipants))]
    static class BarrierNotifyHook { static void Postfix(PrismaBarrierModel __instance) { if (WH.On) WorldRuntime.Barriers.OnModelChanged(__instance); } }

    /// <summary>After a fill, locks re-evaluate explicitly (a depositor or slot state can arrive before its lock exists).</summary>
    [HarmonyPatch(typeof(Il2Cpp.PuzzleSlot), nameof(Il2Cpp.PuzzleSlot.OnFilledChangedFromModel))]
    static class SlotFilledHook { static void Postfix(Il2Cpp.PuzzleSlot __instance) { try { if (WH.On && Applying.Active) __instance.InitLock(); } catch (Exception e) { HookGuard.Fail("PuzzleSlot.OnFilledChangedFromModel", e); } } }

    // ---- pedia
    [HarmonyPatch(typeof(PediaDirector), nameof(PediaDirector.Unlock), new[] { typeof(PediaEntry), typeof(bool) })]
    static class PediaUnlockHook { static void Postfix(PediaEntry entry, bool __result) { try { if (__result && WH.On) WorldRuntime.OnLocalPediaUnlock(entry); } catch (Exception e) { HookGuard.Fail("PediaDirector.Unlock", e); } } }

    // ---- time
    [HarmonyPatch(typeof(TimeDirector), nameof(TimeDirector.FastForwardTo))]
    static class FastForwardHook { static bool Prefix(double fastForwardUntil) { try { return !WH.On || WorldRuntime.OnLocalFastForward(fastForwardUntil); } catch (Exception e) { HookGuard.Fail("TimeDirector.FastForwardTo", e); return true; } } }

    // ---- currency
    // Native rewards can use PlayerModel directly, and AddCurrency(null, ...) resolves
    // the default currency internally. Observe the common actual model transition once.
    [HarmonyPatch(typeof(PlayerState), nameof(PlayerState.HandleModelCurrencyChanged))]
    static class NativeCurrencyChangedHook
    {
        static void Postfix(PlayerState __instance, ICurrency currency, int oldAmount, int newAmount)
        {
            try
            {
                if (WH.On && __instance.Pointer == SceneContext.Instance?.PlayerState?.Pointer)
                    WorldRuntime.OnLocalCurrencyChanged(currency, newAmount - oldAmount);
            }
            catch (Exception e) { HookGuard.Fail("PlayerState.HandleModelCurrencyChanged", e); }
        }
    }
    /// <summary>A guest's sale income reaches the host as a plort-sold message priced by the host, not as a currency delta.</summary>
    [HarmonyPatch(typeof(PlortEconomyDirector), nameof(PlortEconomyDirector.TrySell))]
    static class TrySellHook
    {
        static void Prefix() => WorldRuntime.SaleDepth++;
        static Exception Finalizer(Exception __exception) { if (WorldRuntime.SaleDepth > 0) WorldRuntime.SaleDepth--; return __exception; }
    }
    /// <summary>The plort market itself: native ScorePlort.Deposit calls PlayerState.AddCurrency then RegisterSold and never goes
    /// through TrySell (disassembly 2026-11-08). Without this scope a guest's market sale reached the host twice (currency delta + PlortSold).</summary>
    [HarmonyPatch(typeof(ScorePlort), nameof(ScorePlort.Deposit))]
    static class ScorePlortDepositHook
    {
        static void Prefix() => WorldRuntime.SaleDepth++;
        static Exception Finalizer(Exception __exception) { if (WorldRuntime.SaleDepth > 0) WorldRuntime.SaleDepth--; return __exception; }
    }

    // ---- gordos
    [HarmonyPatch(typeof(GordoEat), nameof(GordoEat.MaybeEat))]
    static class GordoMaybeEatHook
    {
        static void Prefix(GordoEat __instance, Collider col, out (long id, int type, int before) __state)
        {
            var actor = col != null ? col.GetComponentInParent<IdentifiableActor>() : null;
            long id = actor != null ? ActorLink.IdOf(actor.gameObject) : 0;
            int type = actor?._model != null ? ActorTypes.IdOf(actor._model.ident) : 0;
            if (type == 0 && Mod.Actors != null && Mod.Actors.Table.TryGet(id, out var food)) type = food.TypeId;
            __state = (id, type, __instance.GetEatenCount());
        }
        static void Postfix(GordoEat __instance, bool __result, (long id, int type, int before) __state)
        {
            try { if (WH.On && __result && __state.id != 0) WorldRuntime.OnLocalGordoFeed(__instance, null, __state.id, __state.type, Math.Max(1, __instance.GetEatenCount() - __state.before)); }
            catch (Exception e) { HookGuard.Fail("GordoEat.MaybeEat", e); }
        }
    }
    // Feeding animations do not decide terminal state. The host first deduplicates
    // the food and commits its canonical count, then applies completion explicitly.
    // This also prevents a duplicate native host feed from starting an early burst.
    [HarmonyPatch(typeof(GordoEat), nameof(GordoEat.ReachedTarget))]
    static class GordoReachedTargetHook
    {
        static bool Prefix(ref Il2CppSystem.Collections.IEnumerator __result)
        {
            if (!WH.On || Applying.Active) return true;
            __result = new Il2CppSystem.Collections.ArrayList().GetEnumerator();
            return false;
        }
    }
    [HarmonyPatch(typeof(GordoEat), nameof(GordoEat.ImmediateReachedTarget))]
    static class GordoImmediateTargetHook
    {
        static bool Prefix() => !Mod.IsGuest || Applying.Active;
    }
    /// <summary>Native GiveRewards runs only on the reward authority (the host), at most once per gordo (begin/complete/abort ledger).</summary>
    [HarmonyPatch(typeof(GordoRewardsBase), nameof(GordoRewardsBase.GiveRewards))]
    static class GordoRewardsHook
    {
        static bool Prefix(GordoRewardsBase __instance, out string __state)
        {
            __state = null;
            try
            {
                if (!WorldRuntime.MayGiveRewards()) return false;
                if (!WH.On || __instance == null || __instance._eat == null) return true;
                var id = __instance._eat.Id; if (string.IsNullOrEmpty(id)) return true;
                if (!WorldRuntime.GordoRewards.TryBegin(id)) return false;                                     // already paid, running or uncertain
                __state = id; return true;
            }
            catch (Exception e) { HookGuard.Fail("GordoRewardsBase.GiveRewards", e); return true; }
        }
        static Exception Finalizer(Exception __exception, string __state)
        {
            try { if (__state != null) { if (__exception == null) WorldRuntime.GordoRewards.Complete(__state); else WorldRuntime.GordoRewards.Fail(__state, null); } }
            catch { }
            return null;                                                                                        // never throw into native code
        }
    }
}

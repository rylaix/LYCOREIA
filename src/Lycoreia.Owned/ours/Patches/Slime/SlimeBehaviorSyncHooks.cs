// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Slime;
using Il2CppMonomiPark.SlimeRancher.Slime.Dervish;
using Il2CppMonomiPark.SlimeRancher.Slime.Slumber;
using Il2CppMonomiPark.SlimeRancher.Slime.Tangle;
using Lycoreia.Owned.Shared.Managers;
using UnityEngine;

namespace Lycoreia.Game.Hooks;

[HarmonyPatch(typeof(GroundVine), nameof(GroundVine.Selected))]
static class GroundVineSelectedAuthority { static bool Prefix(GroundVine __instance) => BehaviourAuthority.ShouldRun(__instance); }
[HarmonyPatch(typeof(GroundVine), nameof(GroundVine.Action))]
static class GroundVineActionAuthority { static bool Prefix(GroundVine __instance) => BehaviourAuthority.ShouldRun(__instance); }
[HarmonyPatch(typeof(GroundVine), nameof(GroundVine.Deselected))]
static class GroundVineDeselectedAuthority { static bool Prefix(GroundVine __instance) => BehaviourAuthority.ShouldRun(__instance); }
[HarmonyPatch(typeof(GroundVine), nameof(GroundVine.MaybeGrapple))]
static class GroundVineGrappleSync
{
    static bool Prefix(GroundVine __instance) => BehaviourAuthority.ShouldRun(__instance);
    static void Postfix(GroundVine __instance, GameObject target, bool __result) { if (__result) NetworkSlimeBehaviorManager.Publish(__instance, target, true); }
}
[HarmonyPatch(typeof(GroundVine), nameof(GroundVine.Release))]
static class GroundVineReleaseSync
{
    static bool Prefix(GroundVine __instance, out bool __state) { __state = __instance._target != null; return BehaviourAuthority.ShouldRun(__instance); }
    static void Postfix(GroundVine __instance, bool __state) { if (__state) NetworkSlimeBehaviorManager.Publish(__instance, null, false); }
}

[HarmonyPatch(typeof(StalkConsumable), nameof(StalkConsumable.Selected))]
static class StalkConsumableSelectedAuthority { static bool Prefix(StalkConsumable __instance) => BehaviourAuthority.ShouldRun(__instance); }
[HarmonyPatch(typeof(StalkConsumable), nameof(StalkConsumable.Action))]
static class StalkConsumableActionAuthority { static bool Prefix(StalkConsumable __instance) => BehaviourAuthority.ShouldRun(__instance); }
[HarmonyPatch(typeof(StalkConsumable), nameof(StalkConsumable.Deselected))]
static class StalkConsumableDeselectedAuthority { static bool Prefix(StalkConsumable __instance) => BehaviourAuthority.ShouldRun(__instance); }
[HarmonyPatch(typeof(StalkConsumable), nameof(StalkConsumable.SetTarget))]
static class StalkConsumableTargetSync
{
    static bool Prefix(StalkConsumable __instance, out GameObject __state) { __state = __instance._target; return BehaviourAuthority.ShouldRun(__instance); }
    static void Postfix(StalkConsumable __instance, GameObject newTarget, GameObject __state) { if (__state != newTarget) NetworkSlimeBehaviorManager.Publish(__instance, newTarget); }
}

[HarmonyPatch(typeof(InitiateYawn), nameof(InitiateYawn.Selected))]
static class InitiateYawnSelectedAuthority
{
    static bool Prefix(InitiateYawn __instance) => BehaviourAuthority.ShouldRun(__instance);
    static void Postfix(InitiateYawn __instance) => NetworkSlimeBehaviorManager.Yawn(__instance, true);
}
[HarmonyPatch(typeof(InitiateYawn), nameof(InitiateYawn.Action))]
static class InitiateYawnActionAuthority { static bool Prefix(InitiateYawn __instance) => BehaviourAuthority.ShouldRun(__instance); }
[HarmonyPatch(typeof(InitiateYawn), nameof(InitiateYawn.Deselected))]
static class InitiateYawnDeselectedAuthority
{
    static bool Prefix(InitiateYawn __instance) => BehaviourAuthority.ShouldRun(__instance);
    static void Postfix(InitiateYawn __instance) => NetworkSlimeBehaviorManager.Yawn(__instance, false);
}

[HarmonyPatch(typeof(DervishSlimeSpin), nameof(DervishSlimeSpin.Action))]
static class DervishBehaviorSync { static void Postfix(DervishSlimeSpin __instance) => NetworkSlimeBehaviorManager.Publish(__instance); }
[HarmonyPatch(typeof(DervishSlimeSpin), nameof(DervishSlimeSpin.EndAction))]
static class DervishBehaviorEndSync
{
    static bool Prefix(DervishSlimeSpin __instance) => BehaviourAuthority.ShouldRun(__instance);
    static void Postfix(DervishSlimeSpin __instance) => NetworkSlimeBehaviorManager.Publish(__instance);
}

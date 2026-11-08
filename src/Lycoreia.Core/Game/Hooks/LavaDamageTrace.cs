// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.Damage;

namespace Lycoreia.Game.Hooks
{
    [HarmonyPatch(typeof(TakeDamageInLava), nameof(TakeDamageInLava.OnLavaTriggerEnter))]
    static class LavaDamageEnterTrace
    {
        static void Postfix(TakeDamageInLava __instance)
        {
            if (!Mod.IsNetworkActive || __instance == null) return;
            try
            {
                var player = SceneContext.Instance?.Player;
                if (player == null || !__instance.transform.IsChildOf(player.transform)) return;
                var td = SceneContext.Instance.TimeDirector;
                Mod.Diagnostics?.Record("player", "lava-enter", $"sources={__instance._lavaSources?.Count ?? 0}; world-time={td?.WorldTime():0.000}; damage-at={__instance._nextDamageTime:0.000}; can-damage={__instance.CanDamage()}; time-scale={UnityEngine.Time.timeScale:0.000}");
            }
            catch (Exception e) { HookGuard.Fail("lava-enter", e); }
        }
    }
}

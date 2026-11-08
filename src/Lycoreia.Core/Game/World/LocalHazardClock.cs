// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.Damage;
using Lycoreia.World;
namespace Lycoreia.Game.World
{
    static class LocalHazardClock
    {
        public static void BeforeCorrection(double before, double after)
        {
            if (after >= before) return;
            var player = SceneContext.Instance?.Player; if (player == null) return;
            int changed = 0;
            foreach (var damager in player.GetComponentsInChildren<Damager>(true))
            {
                if (damager == null) continue;
                double deadline = HazardClockRules.RebaseDeadline(damager._nextDamageTime, before, after);
                if (deadline == damager._nextDamageTime) continue;
                damager._nextDamageTime = deadline; changed++;
            }
            // PlayerDamageable uses Unity time, so its per-source cooldowns are untouched.
            if (changed > 0 && before - after > 1)
                Mod.Diagnostics?.Record("player", "hazard-clock-rebased", $"from={before:0.000}; to={after:0.000}; local-damagers={changed}");
        }
    }
}

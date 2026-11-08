// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.World
{
    public static class MapFogRules
    {
        /// <summary>The element still draws fog (alpha) although its record says revealed and no reveal animation will clear it.</summary>
        public static bool NeedsRepair(bool shouldReveal, float alpha, bool animating) => shouldReveal && !animating && alpha > 0.5f;
        /// <summary>Fog that stays drawn even though the record is present: the guest's own map would look unexplored.</summary>
        public static bool LooksFogged(float alpha) => alpha > 0.5f;
        public static bool NeedsCanonicalRepair(bool revealed, float alpha) => revealed ? alpha > 0.01f : alpha < 0.99f;
        public static int NativeCount(bool hostSnapshotReady, int hostCount, int nativeCount)
            => hostSnapshotReady ? System.Math.Max(0, hostCount) : nativeCount;
    }
}

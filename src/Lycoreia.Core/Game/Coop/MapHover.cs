// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Game.Coop
{
    public static class MapHover
    {
        /// <summary>Index of the point nearest to the cursor within radius (pixels), -1 when none. Ties go to the lower index.</summary>
        public static int Nearest(float[] xs, float[] ys, int count, float cx, float cy, float radius)
        {
            int best = -1; float bestD = radius * radius;
            for (int i = 0; i < count; i++)
            {
                float dx = xs[i] - cx, dy = ys[i] - cy, d = dx * dx + dy * dy;
                if (d <= bestD && (best < 0 || d < bestD)) { best = i; bestD = d; }
            }
            return best;
        }

        /// <summary>Hover radius in screen pixels: about 2.5 percent of the screen height, never below 22 px.</summary>
        public static float Radius(float screenHeight) => System.Math.Max(22f, screenHeight * 0.025f);
    }
}

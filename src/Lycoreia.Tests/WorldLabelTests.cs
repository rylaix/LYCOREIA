// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
// 2.1.0-rc3 playtest fix: overlay world labels, pure maths (distance fade, size, short text, colour contrast, screen test).
using Lycoreia.Owned.Components.UI;
using Lycoreia.Text;

namespace Lycoreia.Tests
{
    static class WorldLabelTests
    {
        public static void Register()
        {
            T.Test("labels/fade is 1 near, 0 far, smooth between", () =>
            {
                T.True(WorldLabelMath.Fade(10f, 40f, 60f) == 1f, "near"); T.True(WorldLabelMath.Fade(40f, 40f, 60f) == 1f, "start");
                T.True(WorldLabelMath.Fade(60f, 40f, 60f) == 0f, "end"); T.True(WorldLabelMath.Fade(500f, 40f, 60f) == 0f, "beyond");
                float mid = WorldLabelMath.Fade(50f, 40f, 60f); T.True(mid > 0.45f && mid < 0.55f, "midpoint");
                T.True(WorldLabelMath.Fade(45f, 40f, 60f) > WorldLabelMath.Fade(55f, 40f, 60f), "monotonic");
                T.True(WorldLabelMath.Fade(float.NaN, 40f, 60f) == 0f, "nan hides"); T.True(WorldLabelMath.Fade(5f, 60f, 60f) == 1f && WorldLabelMath.Fade(61f, 60f, 60f) == 0f, "empty band");
            });
            T.Test("labels/scale is gentle", () =>
            {
                T.True(WorldLabelMath.Scale(2f) == 1.1f && WorldLabelMath.Scale(400f) == 0.75f, "ends");
                float a = WorldLabelMath.Scale(30f), b = WorldLabelMath.Scale(60f); T.True(a > b && a < 1.1f && b > 0.75f, "shrinks with distance");
            });
            T.Test("labels/short text with hysteresis", () =>
            {
                T.True(!WorldLabelMath.UseShort(30f, 25f, false, false), "no short text"); T.True(!WorldLabelMath.UseShort(30f, 0f, false, true), "no threshold");
                T.True(!WorldLabelMath.UseShort(24f, 25f, false, true), "near"); T.True(WorldLabelMath.UseShort(25f, 25f, false, true), "far");
                T.True(WorldLabelMath.UseShort(24f, 25f, true, true), "stays short just inside"); T.True(!WorldLabelMath.UseShort(20f, 25f, true, true), "back to full");
            });
            T.Test("labels/dark colours are lightened to a readable luminance", () =>
            {
                T.True(WorldLabelMath.LightenToward(1f, 1f, 1f) == 0f, "white"); T.True(WorldLabelMath.LightenToward(1f, 0.92f, 0.45f) == 0f, "note yellow");
                float t = WorldLabelMath.LightenToward(0f, 0f, 0f); T.True(System.Math.Abs(t - WorldLabelMath.MinLuminance) < 1e-4f, "black");
                float r = 0.1f, g = 0.1f, b = 0.4f, k = WorldLabelMath.LightenToward(r, g, b);
                float lum = 0.2126f * (r + (1 - r) * k) + 0.7152f * (g + (1 - g) * k) + 0.0722f * (b + (1 - b) * k); T.True(System.Math.Abs(lum - WorldLabelMath.MinLuminance) < 1e-3f, "reaches the minimum");
            });
            T.Test("labels/on screen only in front of the camera", () =>
            {
                T.True(WorldLabelMath.OnScreen(960f, 540f, 10f, 1920f, 1080f), "centre"); T.True(!WorldLabelMath.OnScreen(960f, 540f, -5f, 1920f, 1080f), "behind the camera");
                T.True(!WorldLabelMath.OnScreen(-500f, 540f, 10f, 1920f, 1080f), "far left"); T.True(WorldLabelMath.OnScreen(-20f, 540f, 10f, 1920f, 1080f), "margin");
            });
            T.Test("labels/player text cannot open a rich tag", () =>
            {
                try
                {
                    CoopText.SetLangForTests("en");
                    string n = CoopWorldText.Note("<size=900>hi", "<b>x"); T.True(n.IndexOf("<size=900>") < 0 && n.IndexOf("<b>") < 0, "tags neutralised"); T.True(n.Contains("<size=70%>"), "own markup kept");
                    T.Eq("Job: a", CoopWorldText.JobShort("a"), "short job");
                }
                finally { CoopText.SetLangForTests(null); }
            });
        }
    }
}

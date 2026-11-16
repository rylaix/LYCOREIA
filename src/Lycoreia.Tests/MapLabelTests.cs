// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Lycoreia.Game.Coop;
using Lycoreia.Text;

namespace Lycoreia.Tests
{
    static class MapLabelTests
    {
        public static void Register()
        {
            T.Test("map hover/nearest: closest within radius, else none", () =>
            {
                var xs = new[] { 100f, 140f, 400f }; var ys = new[] { 100f, 100f, 400f };
                T.Eq(0, MapHover.Nearest(xs, ys, 3, 105, 100, 30), "near first");
                T.Eq(1, MapHover.Nearest(xs, ys, 3, 130, 100, 30), "nearer to second");
                T.Eq(-1, MapHover.Nearest(xs, ys, 3, 250, 250, 30), "nothing in range");
                T.Eq(-1, MapHover.Nearest(xs, ys, 0, 100, 100, 30), "empty");
                T.Eq(2, MapHover.Nearest(xs, ys, 3, 400, 430, 30), "on the radius edge");
                T.Eq(-1, MapHover.Nearest(xs, ys, 2, 400, 400, 30), "count limits the scan");
            });
            T.Test("map hover/radius: scales with height, minimum 22", () =>
            {
                T.True(System.Math.Abs(MapHover.Radius(1080f) - 27f) < 0.01f, "1080p"); T.Eq(22f, MapHover.Radius(400f), "small");
            });
            T.Test("map text/label: open, claimed, mine; en and ru; no long dashes", () =>
            {
                try
                {
                    CoopText.SetLangForTests("en");
                    var en = MapText.Label("Feed 5 pinks", "Bo", "", false);
                    T.True(en.Contains("Co-op job") && en.Contains("Feed 5 pinks") && en.Contains("Open") && en.Contains("Pinned by Bo"), "en open: " + en);
                    T.True(MapText.Label("x", "Bo", "Ann", false).Contains("Ann is on it"), "en claimed");
                    T.True(MapText.Label("x", "", "Ann", true).Contains("You are on it") && !MapText.Label("x", "", "Ann", true).Contains("Pinned"), "en mine, no author");
                    CoopText.SetLangForTests("ru");
                    var ru = MapText.Label("Покормить", "Бо", "Аня", false);
                    T.True(ru.Contains("Задание") && ru.Contains("Аня занимается этим") && ru.Contains("Автор: Бо"), "ru: " + ru);
                    T.True(MapText.Label("x", "", "", false).Contains("Свободно"), "ru open");
                    T.True(MapText.Label("x", "", "", true).Contains("Ты этим занимаешься"), "ru mine");
                    foreach (var s in new[] { en, ru }) T.True(s.IndexOf((char)0x2014) < 0 && s.IndexOf((char)0x2013) < 0, "no long dash");
                }
                finally { CoopText.SetLangForTests(null); }
            });
        }
    }
}

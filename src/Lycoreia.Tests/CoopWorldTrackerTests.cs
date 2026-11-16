// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Lycoreia.Coop;
using Lycoreia.Game.Coop;
using Lycoreia.Text;

namespace Lycoreia.Tests
{
    static class CoopWorldTrackerTests
    {
        public static void Register()
        {
            T.Test("tracker/progress and clock", () =>
            {
                T.True(TrackerMath.Progress(5, 10) == 0.5f, "half"); T.True(TrackerMath.Progress(15, 10) == 1f, "clamped high"); T.True(TrackerMath.Progress(1, 0) == 0f, "no target");
                T.Eq("1:05", TrackerMath.Clock(65), "clock"); T.Eq("0:00", TrackerMath.Clock(-3), "negative");
            });
            T.Test("tracker/urgent in the last 20 percent only while running", () =>
            {
                T.True(!TrackerMath.Urgent(true, 0, 0, 100), "start"); T.True(!TrackerMath.Urgent(true, 79, 0, 100), "just before");
                T.True(TrackerMath.Urgent(true, 80, 0, 100), "at 20 percent left"); T.True(TrackerMath.Urgent(true, 99, 0, 100), "near the end");
                T.True(!TrackerMath.Urgent(false, 99, 0, 100), "finished run is not urgent"); T.True(!TrackerMath.Urgent(true, 5, 10, 10), "empty window");
            });
            T.Test("tracker/visible only with the native HUD", () =>
            {
                T.True(TrackerMath.Visible(true, true, false), "shown"); T.True(!TrackerMath.Visible(true, false, false), "HUD object off");
                T.True(!TrackerMath.Visible(true, true, true), "hide tag"); T.True(!TrackerMath.Visible(false, true, false), "no run");
            });
            T.Test("tracker/pulse decisions", () =>
            {
                var R = ExpeditionOutcome.Running; var S = ExpeditionOutcome.Succeeded; var F = ExpeditionOutcome.Failed;
                T.Eq(2, TrackerMath.Pulse(true, true, R, S, 3, 3), "success"); T.Eq(2, TrackerMath.Pulse(true, true, R, F, 3, 3), "fail");
                T.Eq(0, TrackerMath.Pulse(true, false, R, S, 3, 3), "never saw it running: no chime"); T.Eq(0, TrackerMath.Pulse(false, true, R, S, 3, 3), "other run");
                T.Eq(1, TrackerMath.Pulse(true, true, R, R, 3, 4), "progress"); T.Eq(0, TrackerMath.Pulse(true, true, R, R, 3, 3), "nothing");
                T.Eq(0, TrackerMath.Pulse(true, true, R, ExpeditionOutcome.Abandoned, 3, 3), "abandoned is silent");
            });
            T.Test("tracker/world strings in both languages", () =>
            {
                try
                {
                    CoopText.SetLangForTests("en"); T.Eq("2:00 left", CoopWorldText.Left("2:00"), "en left"); T.Eq("open job", CoopWorldText.OpenJob, "en open");
                    CoopText.SetLangForTests("ru"); T.Eq("осталось 2:00", CoopWorldText.Left("2:00"), "ru left"); T.True(CoopWorldText.Job("x", "y").StartsWith("Задание: x"), "ru job");
                }
                finally { CoopText.SetLangForTests(null); }
            });
        }
    }
}

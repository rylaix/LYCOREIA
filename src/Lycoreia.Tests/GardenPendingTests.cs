// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Lycoreia.World;

namespace Lycoreia.Tests
{
    static class GardenPendingTests
    {
        public static void Register()
        {
            T.Test("garden pending/decide: inactive plot defers, active plot plants, clears or is in sync", () =>
            {
                T.True(GardenPending.Decide(5, false, 0, false) == GardenStep.Defer, "inactive defers");
                T.True(GardenPending.Decide(0, false, 0, false) == GardenStep.Defer, "inactive clear defers too");
                T.True(GardenPending.Decide(5, true, 0, false) == GardenStep.Plant, "empty plot plants");
                T.True(GardenPending.Decide(5, true, 5, true) == GardenStep.InSync, "same crop in sync");
                T.True(GardenPending.Decide(5, true, 7, true) == GardenStep.ClearThenPlant, "other crop replaced");
                T.True(GardenPending.Decide(5, true, 0, true) == GardenStep.ClearThenPlant, "unknown attached object replaced");
                T.True(GardenPending.Decide(0, true, 5, true) == GardenStep.Clear, "host cleared it");
                T.True(GardenPending.Decide(0, true, 0, false) == GardenStep.InSync, "empty and wanted empty");
            });
            T.Test("garden pending/due: applied only after the plot stayed active for the settle time, restarts when it hibernates again", () =>
            {
                var p = new GardenPending(); bool active = false;
                p.Add("plot1"); p.Add("plot1"); T.Eq(1, p.Count, "no duplicate");
                T.Eq(0, p.TakeDue(0, id => active).Count, "inactive: nothing due");
                active = true;
                T.Eq(0, p.TakeDue(1, id => active).Count, "first active frame only starts the clock");
                T.Eq(0, p.TakeDue(1.2, id => active).Count, "not settled yet");
                active = false; T.Eq(0, p.TakeDue(1.4, id => active).Count, "went inactive again");
                active = true; T.Eq(0, p.TakeDue(2.0, id => active).Count, "clock restarted");
                var due = p.TakeDue(2.6, id => active); T.Eq(1, due.Count, "due after settling"); T.Eq("plot1", due[0], "the plot");
                T.Eq(0, p.Count, "taken"); T.True(!p.Has("plot1"), "gone");
            });
        }
    }
}

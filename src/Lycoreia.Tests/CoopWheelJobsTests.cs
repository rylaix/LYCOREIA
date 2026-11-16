// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System.Collections.Generic;
using System.Linq;
using Lycoreia.Coop;
using Lycoreia.Net;
using Lycoreia.Text;

namespace Lycoreia.Tests
{
    static class CoopWheelJobsTests
    {
        static readonly Vec3 Cam = new Vec3(0, 2, 0), Fwd = new Vec3(0, 0, 1);

        public static void Register()
        {
            T.Test("wheel jobs/claim, drop, done and remove are offered for what is here", () =>
            {
                var jobs = new List<JobHere>
                {
                    new JobHere { Id = 1, Text = "feed", Claimed = false, CanRemove = true },
                    new JobHere { Id = 2, Text = "sell", Claimed = true, Mine = true },
                    new JobHere { Id = 3, Text = "other", Claimed = true, Mine = false },
                };
                var o = CoopWheelRules.For("corral", jobs, 9);
                T.True(o.Count <= CoopWheelRules.MaxOptions, "at most 8");
                T.Eq(WheelAction.ClaimJob, o[0].Action); T.Eq(WheelAction.FinishJob, o[1].Action); T.Eq(WheelAction.RemoveJob, o[2].Action); T.Eq(1, o[2].JobId);
                T.Eq(WheelAction.UnclaimJob, o[3].Action); T.Eq(2, o[3].JobId); T.Eq(WheelAction.FinishJob, o[4].Action);
                T.False(o.Any(x => x.Action == WheelAction.ClaimJob && x.JobId != 1), "a taken job cannot be claimed");
                T.False(o.Any(x => x.Action == WheelAction.UnclaimJob && x.JobId == 3), "only the owner may drop");
                T.False(o.Any(x => x.Action == WheelAction.RemoveJob && x.JobId != 1), "remove only where allowed");
                var rn = o.Single(x => x.Action == WheelAction.RemoveNote); T.Eq(9, rn.JobId, "note id carried");
                T.True(o.Any(x => x.Action == WheelAction.WriteJob) && o.Any(x => x.Action == WheelAction.WriteNote), "write entries kept");
                for (int n = 1; n <= 5; n++)
                {
                    var many = Enumerable.Range(1, n).Select(i => new JobHere { Id = i, Text = "job " + i, CanRemove = true }).ToList();
                    var r = CoopWheelRules.For("spot", many, 5);
                    T.True(r.Count <= CoopWheelRules.MaxOptions, "cap with " + n + " jobs");
                    T.Eq(1, r.Count(x => x.Action == WheelAction.WriteJob)); T.Eq(1, r.Count(x => x.Action == WheelAction.RemoveNote));
                    T.True(r.Count(x => x.JobId > 0 && x.Action != WheelAction.RemoveNote) >= 3, "job actions survive the cap with " + n + " jobs");
                }
                T.Eq(-1, CoopWheelRules.For("spot", null, true).Single(x => x.Action == WheelAction.RemoveNote).JobId, "old bool overload: look up by anchor");
            });

            T.Test("wheel jobs/short text and Russian labels", () =>
            {
                T.Eq("short", CoopWheelRules.Short("short")); T.Eq(22, CoopWheelRules.Short(new string('a', 60)).Length); T.True(CoopWheelRules.Short(new string('a', 60)).EndsWith("..."));
                T.Eq("Беру: покормить", WheelText.Label("ru", "I'll do it: покормить")); T.Eq("Готово: x", WheelText.Label("ru", "Done: x"));
                T.Eq("Отказаться: x", WheelText.Label("ru", "Drop: x")); T.Eq("Убрать задание: x", WheelText.Label("ru", "Remove job: x"));
                foreach (var lang in new[] { "en", "ru" })
                {
                    var s = WheelText.Label(lang, "Remove job: x");
                    T.False(s.Contains((char)0x2014) || s.Contains((char)0x2013), "no long dash");
                }
            });

            T.Test("wheel jobs/is-here: anchor, aimed point, spot job, and the world marker in view", () =>
            {
                var aim = new Vec3(0, 0, 10); var none = new Vec3(500, 0, 500);
                T.True(CoopWheelRules.IsHere("plot:1", true, none, 1, "plot:1", 1, aim, Cam, Fwd), "same anchor");
                T.False(CoopWheelRules.IsHere("plot:2", true, none, 1, "plot:1", 1, aim, Cam, Fwd), "other anchor far away");
                T.True(CoopWheelRules.IsHere("", true, new Vec3(2, 2, 11), 1, "", 1, aim, Cam, Fwd), "spot job near the aimed point");
                T.False(CoopWheelRules.IsHere("", true, new Vec3(30, 2, 11), 1, "", 1, aim, new Vec3(0, 2, -50), new Vec3(1, 0, 0)), "far from both");
                T.True(CoopWheelRules.IsHere("plot:9", true, new Vec3(3, 2, 60), 1, "plot:1", 1, aim, Cam, Fwd), "marker 3 degrees off the view direction");
                T.False(CoopWheelRules.IsHere("plot:9", true, new Vec3(15, 2, 60), 1, "plot:1", 1, aim, Cam, Fwd), "marker 14 degrees off");
                T.False(CoopWheelRules.IsHere("plot:9", true, new Vec3(0, 2, -60), 1, "plot:1", 1, aim, Cam, Fwd), "marker behind");
                T.False(CoopWheelRules.IsHere("plot:9", true, new Vec3(0, 2, 400), 1, "plot:1", 1, aim, Cam, Fwd), "marker out of range");
                T.False(CoopWheelRules.IsHere("", true, new Vec3(0, 2, 60), 2, "", 1, aim, Cam, Fwd), "other scene group");
                T.False(CoopWheelRules.IsHere("", false, default, -1, "", 1, aim, Cam, Fwd), "no position, no anchor");
            });

            T.Test("wheel jobs/labels never overlap for 2 to 8 entries at the Russian label size", () =>
            {
                for (int n = 2; n <= 8; n++) T.False(CoopWheelLayout.LabelsOverlap(n, 264f, 330f, 64f), "labels overlap with " + n);
                T.True(CoopWheelLayout.LabelsOverlap(8, 264f, 330f, 400f), "the overlap check does detect overlaps");
            });
        }
    }
}

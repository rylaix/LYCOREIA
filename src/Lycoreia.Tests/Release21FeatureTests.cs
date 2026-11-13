// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Lycoreia.World;

namespace Lycoreia.Tests
{
    static partial class Release21Tests
    {
        static partial void RegisterFeatures()
        {
            T.Test("boost jump: only a fresh jump right after standing on a friend, once per take-off, capped", () =>
            {
                T.True(Lycoreia.Game.Avatars.BoostRules.ShouldBoost(10.1, 10.0, false, 6f, false));
                T.False(Lycoreia.Game.Avatars.BoostRules.ShouldBoost(10.1, 10.0, true, 6f, false), "still grounded");
                T.False(Lycoreia.Game.Avatars.BoostRules.ShouldBoost(10.6, 10.0, false, 6f, false), "too long after leaving the friend");
                T.False(Lycoreia.Game.Avatars.BoostRules.ShouldBoost(10.1, 10.0, false, 0.5f, false), "falling off is not a jump");
                T.False(Lycoreia.Game.Avatars.BoostRules.ShouldBoost(10.1, 10.0, false, 6f, true), "once");
                T.True(Math.Abs(Lycoreia.Game.Avatars.BoostRules.Boosted(6f) - 9.3f) < 1e-3f); T.Eq(30f, Lycoreia.Game.Avatars.BoostRules.Boosted(100f));
            });
            T.Test("co-op jobs: pin, claim (one claimer), finish, remove rules, caps and cleaning", () =>
            {
                var b = new Lycoreia.Coop.CoopBoard(); long t = 1000;
                var r = b.Apply("A", "Ann", new Lycoreia.Coop.CoopOp { Kind = Lycoreia.Coop.CoopOpKind.JobAdd, Text = "  feed <b>corral</b> 3 " }, t, 2, true);
                T.True(r.Changed); T.Eq("feed ‹b›corral‹/b› 3", b.Jobs[0].Text, "no rich text, trimmed"); int id = b.Jobs[0].Id;
                T.True(b.Apply("B", "Bo", new Lycoreia.Coop.CoopOp { Kind = Lycoreia.Coop.CoopOpKind.JobClaim, Id = id }, t, 2, false).Changed);
                T.False(b.Apply("A", "Ann", new Lycoreia.Coop.CoopOp { Kind = Lycoreia.Coop.CoopOpKind.JobClaim, Id = id }, t, 2, true).Changed, "already claimed by B");
                T.False(b.Apply("C", "Cy", new Lycoreia.Coop.CoopOp { Kind = Lycoreia.Coop.CoopOpKind.JobRemove, Id = id }, t, 2, false).Changed, "a guest cannot remove someone else's open job");
                var done = b.Apply("B", "Bo", new Lycoreia.Coop.CoopOp { Kind = Lycoreia.Coop.CoopOpKind.JobDone, Id = id }, t, 2, false);
                T.True(done.Changed && b.Jobs[0].Done); T.Eq(1L, b.Stat("B", Lycoreia.Coop.CoopStat.JobsDone)); T.True(done.Notices[0].Contains("Bo finished"));
                T.False(b.Apply("A", "Ann", new Lycoreia.Coop.CoopOp { Kind = Lycoreia.Coop.CoopOpKind.JobAdd, Text = "   " }, t, 2, true).Changed, "empty text");
                for (int i = 0; i < 40; i++) b.Apply("A", "Ann", new Lycoreia.Coop.CoopOp { Kind = Lycoreia.Coop.CoopOpKind.JobAdd, Text = "job " + i }, t, 2, true);
                T.Eq(Lycoreia.Coop.CoopBoard.MaxOpenJobs, b.Jobs.Count(j => !j.Done), "open job cap");
            });

            T.Test("co-op notes: one note per anchor (re-writing replaces it), cap, remove", () =>
            {
                var b = new Lycoreia.Coop.CoopBoard();
                b.Apply("A", "Ann", new Lycoreia.Coop.CoopOp { Kind = Lycoreia.Coop.CoopOpKind.NoteSet, Anchor = "plot:p1", Text = "no carrots" }, 1, 2, true);
                b.Apply("B", "Bo", new Lycoreia.Coop.CoopOp { Kind = Lycoreia.Coop.CoopOpKind.NoteSet, Anchor = "plot:p1", Text = "tabbies only" }, 2, 2, false);
                T.Eq(1, b.Notes.Count); T.Eq("tabbies only", b.Notes[0].Text); T.Eq("B", b.Notes[0].Author);
                b.Apply("A", "Ann", new Lycoreia.Coop.CoopOp { Kind = Lycoreia.Coop.CoopOpKind.NoteSet, Anchor = "", Text = "free note" }, 3, 2, true);
                T.Eq(2, b.Notes.Count, "unanchored notes are separate");
                T.True(b.Apply("B", "Bo", new Lycoreia.Coop.CoopOp { Kind = Lycoreia.Coop.CoopOpKind.NoteRemove, Id = b.Notes[0].Id }, 4, 2, false).Changed); T.Eq(1, b.Notes.Count);
            });

            T.Test("expeditions: one at a time, goal scales with players, reports count only the goal stat, success pays once, time-out fails", () =>
            {
                var b = new Lycoreia.Coop.CoopBoard(); long t = 10_000;
                var start = b.Apply("A", "Ann", new Lycoreia.Coop.CoopOp { Kind = Lycoreia.Coop.CoopOpKind.ExpeditionStart, Id = 5 }, t, 2, true);
                T.True(start.Changed); T.Eq(4, b.Expedition.Target, "3 + half per extra player"); T.Eq(t + 20 * 60, b.Expedition.EndsUtc);
                T.False(b.Apply("B", "Bo", new Lycoreia.Coop.CoopOp { Kind = Lycoreia.Coop.CoopOpKind.ExpeditionStart, Id = 1 }, t, 2, false).Changed, "already running");
                b.Apply("B", "Bo", new Lycoreia.Coop.CoopOp { Kind = Lycoreia.Coop.CoopOpKind.Report, Id = (int)Lycoreia.Coop.CoopStat.PlortsSold, Arg = 50 }, t + 1, 2, false);
                T.Eq(0, b.Expedition.Progress, "other stats only go to the recap"); T.Eq(50L, b.Stat("B", Lycoreia.Coop.CoopStat.PlortsSold));
                var half = b.Apply("B", "Bo", new Lycoreia.Coop.CoopOp { Kind = Lycoreia.Coop.CoopOpKind.Report, Id = (int)Lycoreia.Coop.CoopStat.PodsOpened, Arg = 2 }, t + 2, 2, false);
                T.True(half.Notices.Any(n => n.Contains("halfway")));
                var win = b.Apply("A", "Ann", new Lycoreia.Coop.CoopOp { Kind = Lycoreia.Coop.CoopOpKind.Report, Id = (int)Lycoreia.Coop.CoopStat.PodsOpened, Arg = 5 }, t + 3, 2, true);
                T.Eq(2000, win.RewardMoney); T.Eq(Lycoreia.Coop.ExpeditionOutcome.Succeeded, b.Expedition.Outcome); T.Eq(4, b.Expedition.Progress, "capped at the goal");
                T.Eq(0, b.Apply("A", "Ann", new Lycoreia.Coop.CoopOp { Kind = Lycoreia.Coop.CoopOpKind.Report, Id = (int)Lycoreia.Coop.CoopStat.PodsOpened, Arg = 5 }, t + 4, 2, true).RewardMoney, "paid once");
                b.Apply("A", "Ann", new Lycoreia.Coop.CoopOp { Kind = Lycoreia.Coop.CoopOpKind.ExpeditionStart, Id = 2 }, t + 10, 1, true);
                T.False(b.Tick(t + 10 + 11 * 60).Changed); T.True(b.Tick(t + 10 + 12 * 60).Changed); T.Eq(Lycoreia.Coop.ExpeditionOutcome.Failed, b.Expedition.Outcome);
                T.Eq(2, b.History.Count);
                T.False(b.Offers().Any(d => d.Id == 5) && b.History[0].DefId == 5 && b.History[^1].DefId == 5, "the expedition just completed is not offered again right away");
                var c = new Lycoreia.Coop.CoopBoard(); c.Apply("A", "Ann", new Lycoreia.Coop.CoopOp { Kind = Lycoreia.Coop.CoopOpKind.ExpeditionStart, Id = 5 }, 0, 1, true);
                c.Apply("A", "Ann", new Lycoreia.Coop.CoopOp { Kind = Lycoreia.Coop.CoopOpKind.Report, Id = (int)Lycoreia.Coop.CoopStat.PodsOpened, Arg = 3 }, 10, 1, true);
                T.Eq(10L, c.Expedition.EndsUtc, "ends when completed (the tracker hides)"); T.False(c.Offers().Any(d => d.Id == 5), "completed one leaves the offers");
                T.Eq(3, c.Offers().Count, "three on offer"); T.Eq((4, 2500), c.Preview(Lycoreia.Coop.Expeditions.Find(5), 1), "level 2: +50 % goal, +25 % reward");
                T.False(b.Apply("A", "Ann", new Lycoreia.Coop.CoopOp { Kind = Lycoreia.Coop.CoopOpKind.Report, Id = (int)Lycoreia.Coop.CoopStat.JobsDone, Arg = 9 }, t, 1, true).Changed, "jobs-done cannot be reported");
                T.False(b.Apply("A", "Ann", new Lycoreia.Coop.CoopOp { Kind = Lycoreia.Coop.CoopOpKind.Report, Id = 99, Arg = 9 }, t, 1, true).Changed, "unknown stat");
            });

            T.Test("co-op board: wire round trip, save file round trip (no session recap), recap line", () =>
            {
                var b = new Lycoreia.Coop.CoopBoard();
                b.Apply("A", "Ann", new Lycoreia.Coop.CoopOp { Kind = Lycoreia.Coop.CoopOpKind.JobAdd, Text = "sell plorts", HasPosition = true, Position = new Lycoreia.Net.Vec3(1, 2, 3), Scene = 7 }, 5, 2, true);
                b.Apply("A", "Ann", new Lycoreia.Coop.CoopOp { Kind = Lycoreia.Coop.CoopOpKind.NoteSet, Anchor = "gordo:g1", Text = "hungry" }, 5, 2, true);
                b.Apply("A", "Ann", new Lycoreia.Coop.CoopOp { Kind = Lycoreia.Coop.CoopOpKind.ExpeditionStart, Id = 2 }, 5, 2, true);
                b.Count("A", Lycoreia.Coop.CoopStat.MoneyEarned, 120);
                var msg = Lycoreia.Coop.CoopWire.ToMsg(b); msg.Notices.Add("hello");
                var w = new Lycoreia.Net.NetWriter(); msg.Describe(new Lycoreia.Net.Codec(w)); var bytes = w.Span.ToArray();
                var back = new Lycoreia.Net.Messages.CoopBoardMsg(); var rd = Lycoreia.Net.NetReader.Rent(bytes, 0, bytes.Length); back.Describe(new Lycoreia.Net.Codec(rd)); rd.Return();
                var g = new Lycoreia.Coop.CoopBoard(); Lycoreia.Coop.CoopWire.Apply(g, back);
                T.Eq("sell plorts", g.Jobs[0].Text); T.Eq(7, g.Jobs[0].Scene); T.Eq(3f, g.Jobs[0].Position.Z); T.Eq("hungry", g.Notes[0].Text);
                T.Eq(60, g.Expedition.Target); T.Eq(b.History.Count, g.History.Count); T.Eq(120L, g.Stat("A", Lycoreia.Coop.CoopStat.MoneyEarned)); T.Eq("hello", back.Notices[0]);
                var json = Lycoreia.Coop.CoopWire.ToJson(b); var f = new Lycoreia.Coop.CoopBoard();
                T.True(Lycoreia.Coop.CoopWire.FromJson(f, json)); T.Eq(1, f.Jobs.Count); T.Eq(1, f.Notes.Count); T.Eq(0, f.Recap.Count, "recap is not saved"); T.True(f.NextId > b.Jobs[0].Id);
                T.False(Lycoreia.Coop.CoopWire.FromJson(new Lycoreia.Coop.CoopBoard(), "{broken"), "broken file");
                T.Eq("Coop_1.json", Lycoreia.Coop.CoopWire.FileNameFor("Coop 1/..")); T.Eq("unnamed.json", Lycoreia.Coop.CoopWire.FileNameFor("../"));
                var stats = new Dictionary<Lycoreia.Coop.CoopStat, long> { [Lycoreia.Coop.CoopStat.PlortsSold] = 12, [Lycoreia.Coop.CoopStat.JobsDone] = 2 };
                T.Eq("12 plorts sold, 2 jobs done", Lycoreia.Coop.RecapText.Line(stats)); T.Eq("nothing yet", Lycoreia.Coop.RecapText.Line(new Dictionary<Lycoreia.Coop.CoopStat, long>()));
            });

            T.Test("co-op wheel: options fit the target, job actions first, at most 8; pick by direction", () =>
            {
                var corral = Lycoreia.Coop.CoopWheelRules.For("corral", new List<Lycoreia.Coop.JobHere>(), false);
                T.Eq("Job: Feed this corral", corral[0].Label); T.True(corral.Any(o => o.Action == Lycoreia.Coop.WheelAction.WriteJob)); T.True(corral.Any(o => o.Action == Lycoreia.Coop.WheelAction.WriteNote));
                T.False(corral.Any(o => o.Action == Lycoreia.Coop.WheelAction.RemoveNote), "no note here");
                var jobs = new List<Lycoreia.Coop.JobHere> { new Lycoreia.Coop.JobHere { Id = 4, Text = "feed", Claimed = false }, new Lycoreia.Coop.JobHere { Id = 5, Text = "sell", Claimed = true } };
                var busy = Lycoreia.Coop.CoopWheelRules.For("corral", jobs, true);
                T.Eq(Lycoreia.Coop.WheelAction.ClaimJob, busy[0].Action); T.Eq(4, busy[0].JobId); T.True(busy.Count <= Lycoreia.Coop.CoopWheelRules.MaxOptions);
                T.True(busy.Any(o => o.Action == Lycoreia.Coop.WheelAction.WriteJob) && busy.Any(o => o.Action == Lycoreia.Coop.WheelAction.WriteNote) && busy.Any(o => o.Action == Lycoreia.Coop.WheelAction.RemoveNote), "essentials kept");
                T.Eq("Job: Meet me here", Lycoreia.Coop.CoopWheelRules.For("unknown", null, false)[0].Label, "unknown kind = spot");
                T.Eq(-1, Lycoreia.Coop.CoopWheelRules.Pick(5, 5, 6), "dead zone"); T.Eq(0, Lycoreia.Coop.CoopWheelRules.Pick(0, 100, 6), "up");
                T.Eq(1, Lycoreia.Coop.CoopWheelRules.Pick(87, 50, 6), "60 degrees"); T.Eq(3, Lycoreia.Coop.CoopWheelRules.Pick(0, -100, 6), "down"); T.Eq(0, Lycoreia.Coop.CoopWheelRules.Pick(-10, 100, 6), "just left of up wraps to 0");
            });

            T.Test("watchdog: reports at 5, 15, 60 s and then every 5 minutes", () =>
            {
                T.False(Watchdog.ShouldReport(4, 0)); T.True(Watchdog.ShouldReport(5, 0)); T.False(Watchdog.ShouldReport(6, 5)); T.True(Watchdog.ShouldReport(15, 5));
                T.True(Watchdog.ShouldReport(60, 15)); T.False(Watchdog.ShouldReport(200, 60)); T.True(Watchdog.ShouldReport(360, 60)); T.False(Watchdog.ShouldReport(400, 360));
            });

            T.Test("silo: guest diff covers add, remove, swap; host apply respects type, max and zero", () =>
            {
                var b = new List<SiloSlot> { new SiloSlot(5, 10), default, new SiloSlot(7, 3) };
                var c = new List<SiloSlot> { new SiloSlot(5, 12), new SiloSlot(9, 1), new SiloSlot(8, 2) };
                var d = SiloLedger.Diff(b, c);
                T.Eq(4, d.Count); T.Eq(2, d[0].Delta); T.Eq(9, d[1].Type); T.Eq(-3, d[2].Delta); T.Eq(7, d[2].Type); T.Eq(2, d[3].Delta);
                T.Eq(0, SiloLedger.Diff(b, b).Count, "no change, no message");
                T.Eq(new SiloSlot(5, 12), SiloLedger.Apply(new SiloSlot(5, 10), new SiloChange { Type = 5, Delta = 2 }, 50));
                T.Eq(new SiloSlot(5, 50), SiloLedger.Apply(new SiloSlot(5, 49), new SiloChange { Type = 5, Delta = 4 }, 50), "max");
                T.Eq(new SiloSlot(5, 10), SiloLedger.Apply(new SiloSlot(5, 10), new SiloChange { Type = 6, Delta = 4 }, 50), "other type refused");
                T.True(SiloLedger.Apply(new SiloSlot(5, 2), new SiloChange { Type = 5, Delta = -9 }, 50).Empty, "never below zero; empties the slot");
                T.Eq(new SiloSlot(6, 3), SiloLedger.Apply(default, new SiloChange { Type = 6, Delta = 3 }, 50), "empty slot takes any type");
                T.True(SiloLedger.SameState(b, new List<SiloSlot>(b))); T.False(SiloLedger.SameState(b, c));
            });
        }
    }
}

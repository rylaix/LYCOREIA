// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Lycoreia.Net;

namespace Lycoreia.Coop
{
    public enum CoopOpKind : byte { JobAdd = 1, JobClaim, JobUnclaim, JobDone, JobRemove, NoteSet, NoteRemove, ExpeditionStart, ExpeditionAbandon, Report }

    /// <summary>What players do, counted on the seat where it happened (recap and expedition goals).</summary>
    public enum CoopStat : byte { PlortsSold = 1, MoneyEarned, SlimesVacuumed, ProduceVacuumed, GordoFood, PodsOpened, JobsDone }

    public sealed class CoopJob
    {
        public int Id; public string Text = "", CreatedBy = "", ClaimedBy = "", Anchor = ""; public bool Done, HasPosition; public Vec3 Position; public int Scene = -1; public long CreatedUtc, DoneUtc;
    }

    public sealed class CoopNote
    {
        public int Id; public string Anchor = "", Text = "", Author = ""; public Vec3 Position; public int Scene = -1; public long UpdatedUtc;
    }

    public sealed class ExpeditionDef
    {
        public int Id; public string Name = "", Goal = ""; public CoopStat Stat; public int Target, Minutes, Reward;
        /// <summary>The goal grows by half for every extra player.</summary>
        public int TargetFor(int players) => Target + Target * Math.Max(0, players - 1) / 2;
    }

    public sealed class ExpeditionRun
    {
        public int DefId, Target, Progress, Reward; public long StartedUtc, EndsUtc; public string StartedBy = "";
        public ExpeditionOutcome Outcome; public Dictionary<string, int> ByPlayer = new Dictionary<string, int>();
    }
    public enum ExpeditionOutcome : byte { Running, Succeeded, Failed, Abandoned }

    public sealed class ExpeditionRecord { public int DefId, Progress, Target; public ExpeditionOutcome Outcome; public long EndedUtc; }

    /// <summary>One operation from a player; the host applies it.</summary>
    public struct CoopOp
    {
        public CoopOpKind Kind; public int Id, Arg; public string Text, Anchor; public bool HasPosition; public Vec3 Position; public int Scene;
    }

    /// <summary>What an operation or a tick changed: notices for every player, money to pay out.</summary>
    public sealed class CoopResult
    {
        public bool Changed; public int RewardMoney; public readonly List<string> Notices = new List<string>();
        public static readonly CoopResult None = new CoopResult();
    }

    public static class Expeditions
    {
        public static readonly IReadOnlyList<ExpeditionDef> Catalog = new List<ExpeditionDef>
        {
            new ExpeditionDef { Id = 1, Name = "Plort Rush", Goal = "Sell plorts at the Plort Market", Stat = CoopStat.PlortsSold, Target = 60, Minutes = 15, Reward = 1500 },
            new ExpeditionDef { Id = 2, Name = "Wild Roundup", Goal = "Vacuum up slimes", Stat = CoopStat.SlimesVacuumed, Target = 40, Minutes = 12, Reward = 1200 },
            new ExpeditionDef { Id = 3, Name = "Gordo Feast", Goal = "Feed Gordo slimes", Stat = CoopStat.GordoFood, Target = 30, Minutes = 15, Reward = 1000 },
            new ExpeditionDef { Id = 4, Name = "Harvest Run", Goal = "Vacuum up fruit, veggies and meat", Stat = CoopStat.ProduceVacuumed, Target = 80, Minutes = 10, Reward = 900 },
            new ExpeditionDef { Id = 5, Name = "Treasure Trail", Goal = "Open treasure pods", Stat = CoopStat.PodsOpened, Target = 3, Minutes = 20, Reward = 2000 },
            new ExpeditionDef { Id = 6, Name = "Market Day", Goal = "Earn newbucks from sales", Stat = CoopStat.MoneyEarned, Target = 5000, Minutes = 20, Reward = 2500 },
        };
        public static ExpeditionDef Find(int id) => Catalog.FirstOrDefault(d => d.Id == id);
    }

    public sealed class CoopBoard
    {
        public const int MaxOpenJobs = 30, MaxDoneJobs = 15, MaxNotes = 64, MaxJobText = 100, MaxNoteText = 80, MaxHistory = 20, MaxReport = 100_000;
        public readonly List<CoopJob> Jobs = new List<CoopJob>();
        public readonly List<CoopNote> Notes = new List<CoopNote>();
        public readonly List<ExpeditionRecord> History = new List<ExpeditionRecord>();
        public ExpeditionRun Expedition;                                           // null when none ran this session
        /// <summary>Session recap (not saved): player -> stat -> total.</summary>
        public readonly Dictionary<string, Dictionary<CoopStat, long>> Recap = new Dictionary<string, Dictionary<CoopStat, long>>();
        public int NextId = 1;

        /// <summary>Removes rich-text markup and control characters, trims and caps the length.</summary>
        public static string Clean(string text, int max)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var chars = text.Where(c => !char.IsControl(c)).Select(c => c == '<' ? '‹' : c == '>' ? '›' : c).ToArray();
            var s = new string(chars).Trim(); return s.Length > max ? s.Substring(0, max).TrimEnd() : s;
        }

        public CoopResult Apply(string player, string playerName, CoopOp op, long nowUtc, int players, bool isHost)
        {
            var r = new CoopResult(); string who = string.IsNullOrEmpty(playerName) ? "A player" : playerName;
            switch (op.Kind)
            {
                case CoopOpKind.JobAdd:
                {
                    var text = Clean(op.Text, MaxJobText); if (text.Length == 0 || Jobs.Count(j => !j.Done) >= MaxOpenJobs) return r;
                    var job = new CoopJob { Id = NextId++, Text = text, CreatedBy = player, Anchor = Clean(op.Anchor, 128), HasPosition = op.HasPosition, Position = op.Position, Scene = op.Scene, CreatedUtc = nowUtc };
                    Jobs.Add(job); r.Changed = true; r.Notices.Add($"{who} pinned a job: {text}"); break;
                }
                case CoopOpKind.JobClaim:
                {
                    var job = Jobs.FirstOrDefault(j => j.Id == op.Id && !j.Done); if (job == null || (job.ClaimedBy.Length > 0 && job.ClaimedBy != player)) return r;
                    if (job.ClaimedBy == player) return r;
                    job.ClaimedBy = player; r.Changed = true; r.Notices.Add($"{who} is on it: {job.Text}"); break;
                }
                case CoopOpKind.JobUnclaim:
                {
                    var job = Jobs.FirstOrDefault(j => j.Id == op.Id && !j.Done); if (job == null || (job.ClaimedBy != player && !isHost) || job.ClaimedBy.Length == 0) return r;
                    job.ClaimedBy = ""; r.Changed = true; break;
                }
                case CoopOpKind.JobDone:
                {
                    var job = Jobs.FirstOrDefault(j => j.Id == op.Id && !j.Done); if (job == null) return r;
                    job.Done = true; job.DoneUtc = nowUtc; if (job.ClaimedBy.Length == 0) job.ClaimedBy = player;
                    Count(player, CoopStat.JobsDone, 1); r.Changed = true; r.Notices.Add($"{who} finished: {job.Text}");
                    var done = Jobs.Where(j => j.Done).OrderBy(j => j.DoneUtc).ToList();
                    for (int i = 0; i < done.Count - MaxDoneJobs; i++) Jobs.Remove(done[i]);
                    break;
                }
                case CoopOpKind.JobRemove:
                {
                    var job = Jobs.FirstOrDefault(j => j.Id == op.Id); if (job == null || (!isHost && job.CreatedBy != player && !job.Done)) return r;
                    Jobs.Remove(job); r.Changed = true; break;
                }
                case CoopOpKind.NoteSet:
                {
                    var text = Clean(op.Text, MaxNoteText); var anchor = Clean(op.Anchor, 128); if (text.Length == 0) return r;
                    var note = anchor.Length > 0 ? Notes.FirstOrDefault(n => n.Anchor == anchor) : Notes.FirstOrDefault(n => n.Id == op.Id);
                    if (note == null) { if (Notes.Count >= MaxNotes) return r; note = new CoopNote { Id = NextId++, Anchor = anchor }; Notes.Add(note); }
                    note.Text = text; note.Author = player; note.Position = op.Position; note.Scene = op.Scene; note.UpdatedUtc = nowUtc; r.Changed = true;
                    r.Notices.Add($"{who} left a note: {text}"); break;
                }
                case CoopOpKind.NoteRemove:
                {
                    var note = Notes.FirstOrDefault(n => n.Id == op.Id); if (note == null) return r;
                    Notes.Remove(note); r.Changed = true; break;
                }
                case CoopOpKind.ExpeditionStart:
                {
                    if (Expedition != null && Expedition.Outcome == ExpeditionOutcome.Running) return r;
                    var def = Offers().FirstOrDefault(d => d.Id == op.Id); if (def == null) return r;           // only what is on offer
                    int level = Level(def.Id);
                    Expedition = new ExpeditionRun { DefId = def.Id, Target = def.TargetFor(players) * (2 + level) / 2, Reward = def.Reward * (4 + level) / 4, StartedUtc = nowUtc, EndsUtc = nowUtc + def.Minutes * 60L, StartedBy = player };
                    r.Changed = true; r.Notices.Add($"{who} started the expedition {def.Name}: {def.Goal} ({Expedition.Target}) within {def.Minutes} minutes!"); break;
                }
                case CoopOpKind.ExpeditionAbandon:
                {
                    if (Expedition == null || Expedition.Outcome != ExpeditionOutcome.Running) return r;
                    Finish(ExpeditionOutcome.Abandoned, nowUtc); r.Changed = true; r.Notices.Add($"{who} called off the expedition."); break;
                }
                case CoopOpKind.Report:
                {
                    if (!Enum.IsDefined(typeof(CoopStat), (byte)op.Id) || op.Arg <= 0 || op.Arg > MaxReport || (CoopStat)op.Id == CoopStat.JobsDone) return r;
                    var stat = (CoopStat)op.Id; Count(player, stat, op.Arg); r.Changed = true;
                    var run = Expedition; var def = run != null ? Expeditions.Find(run.DefId) : null;
                    if (run != null && run.Outcome == ExpeditionOutcome.Running && def != null && def.Stat == stat && nowUtc < run.EndsUtc)
                    {
                        int before = run.Progress; run.Progress = Math.Min(run.Target, run.Progress + op.Arg);
                        run.ByPlayer.TryGetValue(player, out var mine); run.ByPlayer[player] = mine + op.Arg;
                        if (before * 2 < run.Target && run.Progress * 2 >= run.Target && run.Progress < run.Target) r.Notices.Add($"{def.Name}: halfway there ({run.Progress}/{run.Target})!");
                        if (run.Progress >= run.Target)
                        {
                            Finish(ExpeditionOutcome.Succeeded, nowUtc); r.RewardMoney = run.Reward;
                            r.Notices.Add($"Expedition {def.Name} complete! The ranch earns {run.Reward} newbucks.");
                        }
                    }
                    break;
                }
            }
            return r;
        }

        /// <summary>Host clock: a running expedition past its end fails.</summary>
        public CoopResult Tick(long nowUtc)
        {
            var run = Expedition; if (run == null || run.Outcome != ExpeditionOutcome.Running || nowUtc < run.EndsUtc) return CoopResult.None;
            var r = new CoopResult { Changed = true }; var def = Expeditions.Find(run.DefId);
            Finish(ExpeditionOutcome.Failed, nowUtc); r.Notices.Add($"Time is up: {def?.Name ?? "the expedition"} ended at {run.Progress}/{run.Target}.");
            return r;
        }

        public const int OfferCount = 3;
        /// <summary>How often this expedition was completed on this ranch (each completion makes the next one bigger: +50 % goal, +25 % reward).</summary>
        public int Level(int defId) => History.Count(h => h.DefId == defId && h.Outcome == ExpeditionOutcome.Succeeded);
        /// <summary>Three expeditions on offer: the least completed first, never the one just completed (it comes back later, harder).</summary>
        public List<ExpeditionDef> Offers()
        {
            int last = History.Count > 0 && History[^1].Outcome == ExpeditionOutcome.Succeeded ? History[^1].DefId : 0;
            return Expeditions.Catalog.Where(d => d.Id != last).OrderBy(d => Level(d.Id)).ThenBy(d => (d.Id * 7 + History.Count * 3) % 11).Take(OfferCount).ToList();
        }
        public (int target, int reward) Preview(ExpeditionDef d, int players) { int l = Level(d.Id); return (d.TargetFor(players) * (2 + l) / 2, d.Reward * (4 + l) / 4); }

        void Finish(ExpeditionOutcome outcome, long nowUtc)
        {
            var run = Expedition; run.Outcome = outcome; run.EndsUtc = Math.Min(run.EndsUtc, nowUtc);    // the tracker hides a few seconds after the real end
            History.Add(new ExpeditionRecord { DefId = run.DefId, Progress = run.Progress, Target = run.Target, Outcome = outcome, EndedUtc = nowUtc });
            while (History.Count > MaxHistory) History.RemoveAt(0);
        }

        public void Count(string player, CoopStat stat, long amount)
        {
            if (string.IsNullOrEmpty(player) || amount <= 0) return;
            if (!Recap.TryGetValue(player, out var m)) Recap[player] = m = new Dictionary<CoopStat, long>();
            m.TryGetValue(stat, out var v); m[stat] = v + amount;
        }

        public long Stat(string player, CoopStat stat) => Recap.TryGetValue(player, out var m) && m.TryGetValue(stat, out var v) ? v : 0;
        public long Total(CoopStat stat) => Recap.Values.Sum(m => m.TryGetValue(stat, out var v) ? v : 0);

        /// <summary>The persisted part (jobs, notes, expedition history); the recap and a running expedition belong to the session.</summary>
        public void ClearSession() { Recap.Clear(); if (Expedition != null && Expedition.Outcome == ExpeditionOutcome.Running) Expedition = null; }
        public void ClearAll() { Jobs.Clear(); Notes.Clear(); History.Clear(); Recap.Clear(); Expedition = null; NextId = 1; }
    }

    /// <summary>Recap lines for one player, in a fixed order, skipping zeros.</summary>
    public static class RecapText
    {
        static readonly (CoopStat stat, string label)[] Order =
        {
            (CoopStat.PlortsSold, "plorts sold"), (CoopStat.MoneyEarned, "newbucks earned"), (CoopStat.SlimesVacuumed, "slimes vacuumed"),
            (CoopStat.ProduceVacuumed, "food gathered"), (CoopStat.GordoFood, "Gordo bites fed"), (CoopStat.PodsOpened, "treasure pods opened"), (CoopStat.JobsDone, "jobs done"),
        };
        public static string Line(IReadOnlyDictionary<CoopStat, long> stats)
        {
            if (stats == null) return "nothing yet";
            var parts = Order.Where(o => stats.TryGetValue(o.stat, out var v) && v > 0).Select(o => $"{stats[o.stat]} {o.label}").ToList();
            return parts.Count == 0 ? "nothing yet" : string.Join(", ", parts);
        }
    }
}

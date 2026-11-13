// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Lycoreia.Net;
using Lycoreia.Net.Messages;

namespace Lycoreia.Coop
{
    public static class CoopWire
    {
        public static CoopBoardMsg ToMsg(CoopBoard b)
        {
            var m = new CoopBoardMsg
            {
                Jobs = b.Jobs.Select(j => new CoopJobWire { Id = j.Id, Scene = j.Scene, Text = j.Text, CreatedBy = j.CreatedBy, ClaimedBy = j.ClaimedBy, Anchor = j.Anchor, Done = j.Done, HasPosition = j.HasPosition, Position = j.Position }).ToList(),
                Notes = b.Notes.Select(n => new CoopNoteWire { Id = n.Id, Scene = n.Scene, Anchor = n.Anchor, Text = n.Text, Author = n.Author, Position = n.Position }).ToList(),
                History = b.History.Select(h => new CoopStatWire { Player = "", Stat = (byte)h.Outcome, Value = h.DefId }).ToList(),
                Stats = b.Recap.SelectMany(p => p.Value.Select(s => new CoopStatWire { Player = p.Key, Stat = (byte)s.Key, Value = s.Value })).ToList(),
            };
            var run = b.Expedition;
            if (run != null)
            {
                m.HasExpedition = true; m.ExpDef = run.DefId; m.ExpTarget = run.Target; m.ExpProgress = run.Progress; m.ExpReward = run.Reward; m.ExpOutcome = (byte)run.Outcome;
                m.ExpStarted = run.StartedUtc; m.ExpEnds = run.EndsUtc; m.ExpStartedBy = run.StartedBy;
                m.ExpByPlayer = run.ByPlayer.Select(kv => new CoopStatWire { Player = kv.Key, Stat = 0, Value = kv.Value }).ToList();
            }
            return m;
        }

        /// <summary>Guest: the host's board replaces the local copy.</summary>
        public static void Apply(CoopBoard b, CoopBoardMsg m)
        {
            b.Jobs.Clear(); b.Notes.Clear(); b.Recap.Clear(); b.History.Clear();
            foreach (var h in m.History) b.History.Add(new ExpeditionRecord { DefId = (int)h.Value, Outcome = (ExpeditionOutcome)h.Stat });
            foreach (var j in m.Jobs) b.Jobs.Add(new CoopJob { Id = j.Id, Scene = j.Scene, Text = j.Text ?? "", CreatedBy = j.CreatedBy ?? "", ClaimedBy = j.ClaimedBy ?? "", Anchor = j.Anchor ?? "", Done = j.Done, HasPosition = j.HasPosition, Position = j.Position });
            foreach (var n in m.Notes) b.Notes.Add(new CoopNote { Id = n.Id, Scene = n.Scene, Anchor = n.Anchor ?? "", Text = n.Text ?? "", Author = n.Author ?? "", Position = n.Position });
            foreach (var s in m.Stats) if (Enum.IsDefined(typeof(CoopStat), s.Stat)) b.Count(s.Player, (CoopStat)s.Stat, s.Value);
            if (!m.HasExpedition) { b.Expedition = null; return; }
            b.Expedition = new ExpeditionRun
            {
                DefId = m.ExpDef, Target = m.ExpTarget, Progress = m.ExpProgress, Reward = m.ExpReward, Outcome = (ExpeditionOutcome)m.ExpOutcome, StartedUtc = m.ExpStarted, EndsUtc = m.ExpEnds,
                StartedBy = m.ExpStartedBy ?? "", ByPlayer = m.ExpByPlayer.Where(p => p.Player != null).GroupBy(p => p.Player).ToDictionary(g => g.Key, g => (int)g.Sum(x => x.Value)),
            };
        }

        public static CoopOpMsg ToMsg(CoopOp op) => new CoopOpMsg { Kind = (byte)op.Kind, Id = op.Id, Arg = op.Arg, Scene = op.Scene, Text = op.Text ?? "", Anchor = op.Anchor ?? "", HasPosition = op.HasPosition, Position = op.Position };
        public static CoopOp FromMsg(CoopOpMsg m) => new CoopOp { Kind = (CoopOpKind)m.Kind, Id = m.Id, Arg = m.Arg, Scene = m.Scene, Text = m.Text, Anchor = m.Anchor, HasPosition = m.HasPosition, Position = m.Position };

        // ---------------------------------------------------------------- save file (jobs, notes, expedition history; never the session recap)
        sealed class FileJob { public int Id { get; set; } public string Text { get; set; } public string CreatedBy { get; set; } public string ClaimedBy { get; set; } public string Anchor { get; set; } public bool Done { get; set; } public bool HasPosition { get; set; } public float X { get; set; } public float Y { get; set; } public float Z { get; set; } public int Scene { get; set; } public long CreatedUtc { get; set; } public long DoneUtc { get; set; } }
        sealed class FileNote { public int Id { get; set; } public string Anchor { get; set; } public string Text { get; set; } public string Author { get; set; } public float X { get; set; } public float Y { get; set; } public float Z { get; set; } public int Scene { get; set; } public long UpdatedUtc { get; set; } }
        sealed class FileRecord { public int DefId { get; set; } public int Progress { get; set; } public int Target { get; set; } public int Outcome { get; set; } public long EndedUtc { get; set; } }
        sealed class FileBoard { public int Version { get; set; } = 1; public int NextId { get; set; } public List<FileJob> Jobs { get; set; } = new List<FileJob>(); public List<FileNote> Notes { get; set; } = new List<FileNote>(); public List<FileRecord> History { get; set; } = new List<FileRecord>(); }

        public static string ToJson(CoopBoard b)
        {
            var f = new FileBoard
            {
                NextId = b.NextId,
                Jobs = b.Jobs.Select(j => new FileJob { Id = j.Id, Text = j.Text, CreatedBy = j.CreatedBy, ClaimedBy = j.ClaimedBy, Anchor = j.Anchor, Done = j.Done, HasPosition = j.HasPosition, X = j.Position.X, Y = j.Position.Y, Z = j.Position.Z, Scene = j.Scene, CreatedUtc = j.CreatedUtc, DoneUtc = j.DoneUtc }).ToList(),
                Notes = b.Notes.Select(n => new FileNote { Id = n.Id, Anchor = n.Anchor, Text = n.Text, Author = n.Author, X = n.Position.X, Y = n.Position.Y, Z = n.Position.Z, Scene = n.Scene, UpdatedUtc = n.UpdatedUtc }).ToList(),
                History = b.History.Select(h => new FileRecord { DefId = h.DefId, Progress = h.Progress, Target = h.Target, Outcome = (int)h.Outcome, EndedUtc = h.EndedUtc }).ToList(),
            };
            return JsonSerializer.Serialize(f, new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>Loads a save file into an empty board; texts are cleaned again (the file is user-editable). False on a broken file.</summary>
        public static bool FromJson(CoopBoard b, string json)
        {
            FileBoard f; try { f = JsonSerializer.Deserialize<FileBoard>(json ?? ""); } catch { return false; }
            if (f == null) return false;
            b.ClearAll();
            foreach (var j in (f.Jobs ?? new List<FileJob>()).Take(CoopBoard.MaxOpenJobs + CoopBoard.MaxDoneJobs))
            {
                var text = CoopBoard.Clean(j.Text, CoopBoard.MaxJobText); if (text.Length == 0) continue;
                b.Jobs.Add(new CoopJob { Id = j.Id, Text = text, CreatedBy = j.CreatedBy ?? "", ClaimedBy = j.ClaimedBy ?? "", Anchor = CoopBoard.Clean(j.Anchor, 128), Done = j.Done, HasPosition = j.HasPosition, Position = new Vec3(j.X, j.Y, j.Z), Scene = j.Scene, CreatedUtc = j.CreatedUtc, DoneUtc = j.DoneUtc });
            }
            foreach (var n in (f.Notes ?? new List<FileNote>()).Take(CoopBoard.MaxNotes))
            {
                var text = CoopBoard.Clean(n.Text, CoopBoard.MaxNoteText); if (text.Length == 0) continue;
                b.Notes.Add(new CoopNote { Id = n.Id, Anchor = CoopBoard.Clean(n.Anchor, 128), Text = text, Author = n.Author ?? "", Position = new Vec3(n.X, n.Y, n.Z), Scene = n.Scene, UpdatedUtc = n.UpdatedUtc });
            }
            foreach (var h in (f.History ?? new List<FileRecord>()).TakeLast(CoopBoard.MaxHistory))
                b.History.Add(new ExpeditionRecord { DefId = h.DefId, Progress = h.Progress, Target = h.Target, Outcome = (ExpeditionOutcome)h.Outcome, EndedUtc = h.EndedUtc });
            int maxId = b.Jobs.Select(x => x.Id).Concat(b.Notes.Select(x => x.Id)).DefaultIfEmpty(0).Max();
            b.NextId = Math.Max(f.NextId, maxId + 1);
            return true;
        }

        /// <summary>File name for a save: letters, digits, dash and underscore only.</summary>
        public static string FileNameFor(string saveName)
        {
            var s = new string((saveName ?? "").Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray()).Trim('_');
            if (s.Length == 0) s = "unnamed"; if (s.Length > 80) s = s.Substring(0, 80);
            return s + ".json";
        }
    }
}

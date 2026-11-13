// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System.Collections.Generic;
using System.Linq;

namespace Lycoreia.Coop
{
    public enum WheelAction : byte { PinJob, WriteJob, Note, WriteNote, RemoveNote, ClaimJob, FinishJob, Ping }

    public sealed class WheelOption
    {
        public WheelAction Action; public string Label = "", Text = ""; public int JobId;
        public override string ToString() => Label;
    }

    /// <summary>An open job anchored where the player aims.</summary>
    public struct JobHere { public int Id; public string Text; public bool Claimed, Mine; }

    public static class CoopWheelRules
    {
        public const int MaxOptions = 8;

        static readonly Dictionary<string, (string[] jobs, string[] notes)> Presets = new Dictionary<string, (string[], string[])>
        {
            ["corral"] = (new[] { "Feed this corral", "Collect the plorts here", "Upgrade this corral" }, new[] { "Don't feed these slimes", "Keep this corral for largos" }),
            ["coop"] = (new[] { "Feed the chickens here", "Collect the chickens here" }, new[] { "Leave the chickens to breed" }),
            ["garden"] = (new[] { "Harvest this garden", "Plant something here" }, new[] { "Leave this crop alone" }),
            ["silo"] = (new[] { "Empty this silo", "Fill this silo" }, new[] { "Plorts for selling" }),
            ["pond"] = (new[] { "Check on the pond" }, new[] { "Puddle slimes only" }),
            ["incinerator"] = (new[] { "Burn the leftovers" }, new[] { "Ash for the puddles" }),
            ["plot"] = (new[] { "Build something here" }, new[] { "Reserved" }),
            ["gordo"] = (new[] { "Feed this Gordo" }, new[] { "Feeding this one: don't pop it" }),
            ["spot"] = (new[] { "Meet me here", "Explore over here" }, new[] { "Look here!" }),
        };

        /// <summary>Order: job actions on what is already here, ready-made jobs, "write your own job", ready-made note, "write your own note", remove the note.</summary>
        public static List<WheelOption> For(string kind, IReadOnlyList<JobHere> jobsHere, bool noteHere)
        {
            var list = new List<WheelOption>();
            if (!Presets.TryGetValue(kind ?? "spot", out var p)) p = Presets["spot"];
            foreach (var j in (jobsHere ?? new List<JobHere>()).Take(2))
            {
                if (!j.Claimed) list.Add(new WheelOption { Action = WheelAction.ClaimJob, JobId = j.Id, Label = "I'll do: " + j.Text });
                list.Add(new WheelOption { Action = WheelAction.FinishJob, JobId = j.Id, Label = "Done: " + j.Text });
            }
            foreach (var t in p.jobs) list.Add(new WheelOption { Action = WheelAction.PinJob, Text = t, Label = "Job: " + t });
            list.Add(new WheelOption { Action = WheelAction.WriteJob, Label = "Job: write your own..." });
            if (p.notes.Length > 0) list.Add(new WheelOption { Action = WheelAction.Note, Text = p.notes[0], Label = "Note: " + p.notes[0] });
            list.Add(new WheelOption { Action = WheelAction.WriteNote, Label = "Note: write your own..." });
            if (noteHere) list.Add(new WheelOption { Action = WheelAction.RemoveNote, Label = "Remove the note" });
            // keep the essentials when it is too long: job actions, the first ready-made job, both "write" entries
            while (list.Count > MaxOptions)
            {
                int i = list.FindLastIndex(o => o.Action == WheelAction.PinJob && list.Count(x => x.Action == WheelAction.PinJob) > 1);
                if (i < 0) i = list.FindLastIndex(o => o.Action == WheelAction.Note);
                if (i < 0) i = list.FindLastIndex(o => o.Action == WheelAction.FinishJob);
                if (i < 0) break; list.RemoveAt(i);
            }
            return list;
        }

        /// <summary>Which wedge the accumulated mouse movement points at (0 = top, clockwise); -1 inside the dead zone (cancel).</summary>
        public static int Pick(float dx, float dy, int count, float deadZone = 40f)
        {
            if (count <= 0 || dx * dx + dy * dy < deadZone * deadZone) return -1;
            double angle = System.Math.Atan2(dx, dy) * 180.0 / System.Math.PI; if (angle < 0) angle += 360;     // 0 at the top, clockwise
            double wedge = 360.0 / count; return (int)((angle + wedge / 2) / wedge) % count;
        }
    }
}

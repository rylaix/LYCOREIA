// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System.Collections.Generic;
using System.Linq;

namespace Lycoreia.Coop
{
    public enum WheelAction : byte { PinJob, WriteJob, Note, WriteNote, RemoveNote, ClaimJob, FinishJob, Ping, UnclaimJob, RemoveJob }

    public sealed class WheelOption
    {
        public WheelAction Action; public string Label = "", Text = ""; public int JobId;
        public override string ToString() => Label;
    }

    /// <summary>An open job anchored where the player aims.</summary>
    public struct JobHere { public int Id; public string Text; public bool Claimed, Mine, CanRemove; }

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

        public const int MaxJobActions = 5, ShortText = 22;

        /// <summary>Player-written text cut to fit a wheel label (ASCII dots, never the long dash).</summary>
        public static string Short(string text, int max = ShortText)
        {
            text = (text ?? "").Trim(); if (text.Length <= max) return text;
            return text.Substring(0, System.Math.Max(1, max - 3)).TrimEnd() + "...";
        }

        public static List<WheelOption> For(string kind, IReadOnlyList<JobHere> jobsHere, bool noteHere) => For(kind, jobsHere, noteHere ? -1 : 0);

        /// <summary>
        /// Order: actions for the jobs already here (claim or drop, done, remove), ready-made jobs, "write your own job", ready-made note, "write your own note",
        /// remove the note (<paramref name="noteId"/> = its id, -1 = look it up by anchor, 0 = none). At most <see cref="MaxOptions"/>: when it is too long the
        /// ready-made entries go first, the job actions and the "write" entries stay.
        /// </summary>
        public static List<WheelOption> For(string kind, IReadOnlyList<JobHere> jobsHere, int noteId)
        {
            var list = new List<(WheelOption o, int pri)>();
            if (!Presets.TryGetValue(kind ?? "spot", out var p)) p = Presets["spot"];
            int actions = 0;
            foreach (var j in jobsHere ?? new List<JobHere>())
            {
                int need = (j.Claimed && !j.Mine ? 0 : 1) + 1 + (j.CanRemove ? 1 : 0);
                if (actions > 0 && actions + need > MaxJobActions) break;
                string t = Short(j.Text);
                if (!j.Claimed) list.Add((new WheelOption { Action = WheelAction.ClaimJob, JobId = j.Id, Label = "I'll do it: " + t }, 0));
                else if (j.Mine) list.Add((new WheelOption { Action = WheelAction.UnclaimJob, JobId = j.Id, Label = "Drop: " + t }, 0));
                list.Add((new WheelOption { Action = WheelAction.FinishJob, JobId = j.Id, Label = "Done: " + t }, 0));
                if (j.CanRemove) list.Add((new WheelOption { Action = WheelAction.RemoveJob, JobId = j.Id, Label = "Remove job: " + t }, 0));
                actions += need;
            }
            for (int k = 0; k < p.jobs.Length; k++) list.Add((new WheelOption { Action = WheelAction.PinJob, Text = p.jobs[k], Label = "Job: " + p.jobs[k] }, k == 0 ? 2 : 4));
            list.Add((new WheelOption { Action = WheelAction.WriteJob, Label = "Job: write your own..." }, 1));
            if (p.notes.Length > 0) list.Add((new WheelOption { Action = WheelAction.Note, Text = p.notes[0], Label = "Note: " + p.notes[0] }, 3));
            list.Add((new WheelOption { Action = WheelAction.WriteNote, Label = "Note: write your own..." }, 1));
            if (noteId != 0) list.Add((new WheelOption { Action = WheelAction.RemoveNote, JobId = noteId, Label = "Remove the note" }, 1));
            while (list.Count > MaxOptions)
            {
                int worst = 0;
                for (int i = 1; i < list.Count; i++) if (list[i].pri >= list[worst].pri) worst = i;
                list.RemoveAt(worst);
            }
            return list.Select(x => x.o).ToList();
        }

        /// <summary>Radius (metres) around the aimed point inside which a job or note without an anchor counts as "here".</summary>
        public const float SpotRadius = 6f, MarkerAngle = 6f, MarkerRange = 160f;

        /// <summary>
        /// Is a job or note (anchor, optional world position, scene) what the player means? Yes when it sits on the aimed anchor, near the aimed point,
        /// or when its world marker is within <see cref="MarkerAngle"/> degrees of the view direction (so looking at the marker is enough).
        /// </summary>
        public static bool IsHere(string itemAnchor, bool hasPos, Lycoreia.Net.Vec3 pos, int itemScene, string anchor, int scene, Lycoreia.Net.Vec3 aim, Lycoreia.Net.Vec3 cam, Lycoreia.Net.Vec3 fwd)
        {
            if (!string.IsNullOrEmpty(anchor) && itemAnchor == anchor) return true;
            if (!hasPos || (itemScene >= 0 && scene >= 0 && itemScene != scene)) return false;
            float dx = pos.X - aim.X, dy = pos.Y - aim.Y, dz = pos.Z - aim.Z;
            if (dx * dx + dy * dy + dz * dz <= SpotRadius * SpotRadius) return true;
            float vx = pos.X - cam.X, vy = pos.Y - cam.Y, vz = pos.Z - cam.Z, dist = (float)System.Math.Sqrt(vx * vx + vy * vy + vz * vz);
            if (dist < 0.5f || dist > MarkerRange) return dist < 0.5f;
            double fl = System.Math.Sqrt(fwd.X * fwd.X + fwd.Y * fwd.Y + fwd.Z * fwd.Z); if (fl < 1e-6) return false;
            double cos = (vx * fwd.X + vy * fwd.Y + vz * fwd.Z) / (dist * fl);
            return cos >= System.Math.Cos(MarkerAngle * System.Math.PI / 180.0);
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

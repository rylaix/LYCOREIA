// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.IO;

namespace Lycoreia
{
    /// <summary>Pure decision logic for where the log files go: UserData/Lycoreia, then %LOCALAPPDATA%/Lycoreia, then the temp folder.</summary>
    public static class LogFolders
    {
        public readonly struct Line { public readonly bool Warn; public readonly string Text; public Line(bool warn, string text) { Warn = warn; Text = text; } }
        public sealed class Result { public string Folder; public readonly List<Line> Lines = new List<Line>(); }

        /// <summary>Preferred folder first, then per-user and temp fallbacks. Empty and duplicate entries are dropped.</summary>
        public static List<string> Candidates(string primary, string localAppData, string temp, string name = "Lycoreia")
        {
            var list = new List<string>();
            void Add(string p) { if (!string.IsNullOrWhiteSpace(p) && !list.Contains(p)) list.Add(p); }
            Add(primary);
            if (!string.IsNullOrWhiteSpace(localAppData)) Add(Path.Combine(localAppData, name));
            if (!string.IsNullOrWhiteSpace(temp)) Add(Path.Combine(temp, name));
            return list;
        }

        public static List<string> DefaultCandidates(string primary)
        {
            string local = ""; try { local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } catch { }
            string temp = ""; try { temp = Path.GetTempPath(); } catch { }
            return Candidates(primary, local, temp);
        }

        /// <summary>tryOpen returns null when the folder works, else the failure. Lines are the MelonLoader messages to print.</summary>
        public static Result Choose(IEnumerable<string> candidates, Func<string, Exception> tryOpen)
        {
            var r = new Result(); var failures = new List<string>();
            foreach (var c in candidates)
            {
                Exception e; try { e = tryOpen(c); } catch (Exception x) { e = x; }
                if (e == null) { r.Folder = c; break; }
                failures.Add($"'{c}' ({e.GetType().Name}: {e.Message})");
            }
            if (failures.Count > 0)
                r.Lines.Add(new Line(true, "log folder failed: " + string.Join("; ", failures) + "; " + (r.Folder != null ? $"using fallback '{r.Folder}'" : "file logging disabled")));
            r.Lines.Add(r.Folder != null ? new Line(false, "log file: " + Path.Combine(r.Folder, "latest.log")) : new Line(true, "log file: none (file logging disabled)"));
            return r;
        }
    }

    /// <summary>
    /// Which parts of the game's input a text field may hold. A text-typing owner closes the gate and every owner must release it
    /// again; the pointer (point/click/scroll) is never part of what is closed.
    /// </summary>
    public sealed class InputGateState
    {
        readonly HashSet<string> _owners = new HashSet<string>();
        public bool Blocked { get; private set; }
        public int Owners => _owners.Count;
        public enum Change { None, Closed, Opened }

        public Change Set(string owner, bool on)
        {
            if (on) _owners.Add(owner); else _owners.Remove(owner);
            return Sync();
        }
        public Change ReleaseAll() { _owners.Clear(); return Sync(); }
        Change Sync()
        {
            bool want = _owners.Count > 0;
            if (want == Blocked) return Change.None;
            Blocked = want; return want ? Change.Closed : Change.Opened;
        }

        /// <summary>Menu typing: only a field that really has keyboard focus counts; a field that merely stays selected (after Esc or a click on a button) does not.</summary>
        public static bool IsTyping(bool locked, bool selectedIsField, bool fieldFocused) => locked || (selectedIsField && fieldFocused);
        /// <summary>Whether the pointer actions have to be switched back on (they never stay off while our menu is open).</summary>
        public static bool NeedsPointerRepair(bool point, bool click) => !point || !click;
    }
}

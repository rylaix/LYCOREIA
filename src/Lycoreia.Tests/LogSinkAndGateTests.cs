// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Lycoreia.Tests
{
    static class LogSinkAndGateTests
    {
        public static void Register()
        {
            T.Test("log folders: candidates are primary, LocalAppData, temp without blanks or duplicates", () =>
            {
                var c = LogFolders.Candidates("/a/UserData/Lycoreia", "/home/u/local", "/tmp");
                T.Eq(3, c.Count); T.Eq("/a/UserData/Lycoreia", c[0]); T.Eq(Path.Combine("/home/u/local", "Lycoreia"), c[1]); T.Eq(Path.Combine("/tmp", "Lycoreia"), c[2]);
                T.Eq(1, LogFolders.Candidates("/x", "", null).Count, "blank fallbacks dropped");
                T.Eq(1, LogFolders.Candidates("/x", "/x/..", "/x").Where(p => p == "/x").Count(), "no duplicate primary");
            });

            T.Test("log folders: first folder that works is used, no warning", () =>
            {
                var r = LogFolders.Choose(new[] { "A", "B" }, f => null);
                T.Eq("A", r.Folder); T.Eq(1, r.Lines.Count); T.False(r.Lines[0].Warn); T.True(r.Lines[0].Text.StartsWith("log file: "));
            });

            T.Test("log folders: a failing primary falls back and one line names path, exception and fallback", () =>
            {
                var r = LogFolders.Choose(new[] { "A", "B", "C" }, f => f == "C" ? null : new UnauthorizedAccessException("denied " + f));
                T.Eq("C", r.Folder); T.Eq(2, r.Lines.Count); T.True(r.Lines[0].Warn);
                string w = r.Lines[0].Text;
                T.True(w.Contains("'A'") && w.Contains("UnauthorizedAccessException: denied A") && w.Contains("'B'") && w.Contains("using fallback 'C'"), w);
                T.True(r.Lines[1].Text.Contains("latest.log") && !r.Lines[1].Warn);
            });

            T.Test("log folders: when nothing works file logging is reported as disabled", () =>
            {
                var r = LogFolders.Choose(new[] { "A" }, f => throw new IOException("disk"));
                T.Eq(null, r.Folder); T.True(r.Lines[0].Text.Contains("file logging disabled") && r.Lines[0].Text.Contains("IOException: disk")); T.True(r.Lines[1].Warn);
            });

            T.Test("log folders: Log.OpenFirst falls back from an unusable folder and writes latest.log", () =>
            {
                string good = Path.Combine(AppContext.BaseDirectory, "logfb-" + Guid.NewGuid().ToString("N"));
                string blocker = Path.Combine(AppContext.BaseDirectory, "logblock-" + Guid.NewGuid().ToString("N")); File.WriteAllText(blocker, "x");   // a file where a folder is needed
                var shown = new List<string>(); Log.Console = (lv, m) => shown.Add(m);
                try
                {
                    string used = Log.OpenFirst(new[] { Path.Combine(blocker, "Lycoreia"), good });
                    Log.Info("after fallback"); Log.Close();
                    T.Eq(good, used); T.True(File.ReadAllText(Path.Combine(good, "latest.log")).Contains("after fallback"));
                    T.True(shown.Any(m => m.StartsWith("log folder failed: ") && m.Contains("using fallback")), "failure line printed");
                    T.True(shown.Any(m => m.StartsWith("log file: ") && m.EndsWith("latest.log")), "path printed");
                }
                finally { Log.Console = null; Log.Close(); File.Delete(blocker); if (Directory.Exists(good)) Directory.Delete(good, true); }
            });

            T.Test("diagnostics: an unusable folder disables the writer with a message instead of throwing", () =>
            {
                string blocker = Path.Combine(AppContext.BaseDirectory, "diagblock-" + Guid.NewGuid().ToString("N")); File.WriteAllText(blocker, "x");
                var shown = new List<string>(); Log.Console = (lv, m) => shown.Add(m);
                try
                {
                    using var d = new DiagnosticsWriter(Path.Combine(blocker, "sub")); d.Start("t");
                    T.False(d.Enabled); T.True(shown.Any(m => m.StartsWith("diagnostics could not start at ")), "reported");
                }
                finally { Log.Console = null; File.Delete(blocker); }
            });

            T.Test("input gate: holds while any owner needs the keys and releases when the last one lets go", () =>
            {
                var g = new InputGateState();
                T.Eq(InputGateState.Change.Closed, g.Set("menu", true)); T.True(g.Blocked);
                T.Eq(InputGateState.Change.None, g.Set("menu", true), "same owner twice");
                T.Eq(InputGateState.Change.None, g.Set("chat", true)); T.Eq(InputGateState.Change.None, g.Set("menu", false)); T.True(g.Blocked, "chat still holds it");
                T.Eq(InputGateState.Change.Opened, g.Set("chat", false)); T.False(g.Blocked);
                T.Eq(InputGateState.Change.None, g.Set("chat", false), "releasing a non-owner is a no-op");
            });

            T.Test("input gate: ReleaseAll (scene change, menu close, Save) never leaves it closed", () =>
            {
                var g = new InputGateState(); g.Set("menu", true); g.Set("coop-box", true);
                T.Eq(InputGateState.Change.Opened, g.ReleaseAll()); T.False(g.Blocked); T.Eq(0, g.Owners);
                T.Eq(InputGateState.Change.None, g.ReleaseAll());
            });

            T.Test("menu typing: a selected but unfocused field (after Esc or a button click) does not hold the gate", () =>
            {
                T.False(InputGateState.IsTyping(false, true, false), "selected, not focused");
                T.True(InputGateState.IsTyping(false, true, true), "focused field");
                T.True(InputGateState.IsTyping(true, false, false), "click-locked field");
                T.False(InputGateState.IsTyping(false, false, false));
            });

            T.Test("pointer actions: repair is needed whenever point or click is off", () =>
            {
                T.False(InputGateState.NeedsPointerRepair(true, true)); T.True(InputGateState.NeedsPointerRepair(false, true)); T.True(InputGateState.NeedsPointerRepair(true, false));
            });
        }
    }
}

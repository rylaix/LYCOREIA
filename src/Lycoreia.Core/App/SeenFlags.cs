// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Lycoreia
{
    public enum WhatsNewDecision { Nothing, Show, MarkSilently }

    /// <summary>Pure rule: the what's-new popup shows once per new version, never on a fresh install.</summary>
    public static class WhatsNewGate
    {
        public static WhatsNewDecision Decide(string lastSeen, string current, bool freshInstall)
        {
            if (string.IsNullOrEmpty(current)) return WhatsNewDecision.Nothing;
            if (lastSeen == current) return WhatsNewDecision.Nothing;
            if (string.IsNullOrEmpty(lastSeen) && freshInstall) return WhatsNewDecision.MarkSilently;   // nothing is new to someone who just installed
            return WhatsNewDecision.Show;
        }
    }

    public static class SeenFlags
    {
        static Dictionary<string, string> _map; static string _path; static bool _warned;

        /// <summary>Tests only: use this file (null = the mod's data folder) and forget what was loaded.</summary>
        public static void Configure(string path) { _path = path; _map = null; }

        static string FilePath => _path ?? (Mod.DataFolder != null ? Path.Combine(Mod.DataFolder, "seen.txt") : null);

        public static Dictionary<string, string> Parse(string text)
        {
            var m = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.Trim(); int eq = line.IndexOf('=');
                if (eq <= 0) continue; m[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return m;
        }

        public static string Format(IDictionary<string, string> map)
        {
            var sb = new StringBuilder(); var keys = new List<string>(map.Keys); keys.Sort(StringComparer.Ordinal);
            foreach (var k in keys) sb.Append(k).Append('=').Append(map[k]).Append('\n');
            return sb.ToString();
        }

        static Dictionary<string, string> Load()
        {
            if (_map != null) return _map;
            _map = new Dictionary<string, string>(StringComparer.Ordinal);
            try { var p = FilePath; if (p != null && File.Exists(p)) _map = Parse(File.ReadAllText(p)); }
            catch (Exception e) { Warn("read", e); }
            return _map;
        }

        /// <summary>The stored value, or null when the key was never set (or the file cannot be read).</summary>
        public static string Get(string key) { try { return Load().TryGetValue(key, out var v) ? v : null; } catch { return null; } }

        public static bool Has(string key) => Get(key) != null;

        public static void Set(string key, string value)
        {
            try
            {
                var m = Load(); m[key] = value ?? "";
                var p = FilePath; if (p == null) return;
                var dir = Path.GetDirectoryName(p); if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(p, Format(m));
            }
            catch (Exception e) { Warn("write", e); }
        }

        static void Warn(string what, Exception e) { if (_warned) return; _warned = true; try { Log.Warn("seen flags " + what + " failed: " + e.Message); } catch { } }
    }
}

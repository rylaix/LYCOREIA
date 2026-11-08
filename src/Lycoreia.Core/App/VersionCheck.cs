// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;

namespace Lycoreia
{
    public enum GameVersionVerdict { Ok, TooOld, Newer }
    public enum ModVersionVerdict { UpToDate, Outdated, Newer }

    /// <summary>Pure decisions of the startup check; message boxes and network calls live in the game layer.</summary>
    public static class VersionCheck
    {
        /// <summary>First word of the game version text, e.g. "1.3.0 (build 5)" gives "1.3.0".</summary>
        public static string FirstWord(string s) { s = (s ?? "").Trim(); int i = s.IndexOfAny(new[] { ' ', '\t', '\n' }); return i < 0 ? s : s.Substring(0, i); }

        /// <summary>Removes a pre-release suffix: "2.0.0-dev" and "1.1.0-rc.14" give the release triple.</summary>
        public static string StripPreRelease(string v) { v = (v ?? "").Trim().TrimStart('v', 'V'); int i = v.IndexOfAny(new[] { '-', '+' }); return i < 0 ? v : v.Substring(0, i); }

        public static bool TryParse(string v, out System.Version parsed)
        {
            v = StripPreRelease(v); var parts = v.Split('.'); while (parts.Length < 3) v += ".0";
            return System.Version.TryParse(v, out parsed);
        }

        public static GameVersionVerdict CheckGame(string gameVersionText, string required = BuildInfo.RequiredGameVersion)
        {
            if (!TryParse(FirstWord(gameVersionText), out System.Version have) || !TryParse(required, out var need)) return GameVersionVerdict.Ok; // unparsable: do not block
            int c = have.CompareTo(need); return c < 0 ? GameVersionVerdict.TooOld : c > 0 ? GameVersionVerdict.Newer : GameVersionVerdict.Ok;
        }

        public static ModVersionVerdict CheckMod(string installed, string latestText)
        {
            if (!TryParse(installed, out var mine) || !TryParse(latestText, out var latest)) return ModVersionVerdict.UpToDate;
            int c = mine.CompareTo(latest); return c < 0 ? ModVersionVerdict.Outdated : c > 0 ? ModVersionVerdict.Newer : ModVersionVerdict.UpToDate;
        }

        /// <summary>A dev build whose version equals the released one deserves a warning.</summary>
        public static bool DevEqualsRelease(string installed, string latestText) => TryParse(installed, out var a) && TryParse(latestText, out var b) && a.Equals(b);
    }
}

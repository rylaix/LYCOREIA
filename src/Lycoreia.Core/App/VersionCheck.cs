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

        public static bool IsPreRelease(string v) => StripPreRelease(v) != (v ?? "").Trim().TrimStart('v', 'V');

        public static bool TryParse(string v, out System.Version parsed)
        {
            v = StripPreRelease(v); int parts = v.Split('.').Length; for (; parts < 3; parts++) v += ".0";   // was a while loop that never ended for "1" or junk text
            return System.Version.TryParse(v, out parsed);
        }

        public static GameVersionVerdict CheckGame(string gameVersionText, string required = BuildInfo.RequiredGameVersion)
        {
            if (!TryParse(FirstWord(gameVersionText), out System.Version have) || !TryParse(required, out var need)) return GameVersionVerdict.Ok; // unparsable: do not block
            int c = have.CompareTo(need); return c < 0 ? GameVersionVerdict.TooOld : c > 0 ? GameVersionVerdict.Newer : GameVersionVerdict.Ok;
        }

        /// <summary>Session build tag sent in Connect: mod version plus the game version, e.g. "2.0.1 (game 1.3.0)".</summary>
        public static string BuildTag(string modVersion, string gameVersionText)
        {
            string g = FirstWord(gameVersionText);
            return string.IsNullOrEmpty(g) ? modVersion : $"{modVersion} (game {g})";
        }

        static void SplitTag(string tag, out string mod, out string game)
        {
            tag = (tag ?? "").Trim(); int i = tag.IndexOf(" (game ", StringComparison.Ordinal);
            if (i < 0) { mod = tag; game = null; return; }
            mod = tag.Substring(0, i); game = tag.Substring(i + 7).TrimEnd(')');
        }

        /// <summary>Null when host and guest may play together, else the reason shown to the guest.
        /// Both the mod and the game version must match: different game builds have different actor and plot ids.</summary>
        public static string DescribeBuildMismatch(string hostTag, string guestTag)
        {
            if (string.Equals(hostTag, guestTag, StringComparison.Ordinal)) return null;
            SplitTag(hostTag, out var hm, out var hg); SplitTag(guestTag, out var gm, out var gg);
            if (hm != gm) return $"Version mismatch: the host runs Lycoreia {hm}, you run {gm}.";
            if (hg != null && gg != null && hg != gg) return $"Game version mismatch: the host plays Slime Rancher 2 {hg}, you play {gg}. Both players need the same game version.";
            return null;    // one side did not report a game version: allow
        }

        public static ModVersionVerdict CheckMod(string installed, string latestText)
        {
            if (!TryParse(installed, out var mine) || !TryParse(latestText, out var latest)) return ModVersionVerdict.UpToDate;
            int c = mine.CompareTo(latest);
            if (c == 0) { bool preMine = IsPreRelease(installed), preLatest = IsPreRelease(latestText); if (preMine != preLatest) c = preMine ? -1 : 1; }   // 2.1.0-rc1 < 2.1.0
            return c < 0 ? ModVersionVerdict.Outdated : c > 0 ? ModVersionVerdict.Newer : ModVersionVerdict.UpToDate;
        }

        /// <summary>A dev build whose version equals the released one deserves a warning.</summary>
        public static bool DevEqualsRelease(string installed, string latestText) => TryParse(installed, out var a) && TryParse(latestText, out var b) && a.Equals(b);
    }
}

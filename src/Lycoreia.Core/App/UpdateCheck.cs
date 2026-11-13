// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Lycoreia
{
    public enum UpdateKind { Unknown, UpToDate, UpdateAvailable, AheadOfPublic }

    /// <summary>One public source and the version it offers (null when it could not be read).</summary>
    public readonly struct PublishedVersion
    {
        public readonly string Source, Version, Url;
        public PublishedVersion(string source, string version, string url) { Source = source; Version = version; Url = url; }
    }

    public sealed class UpdateStatus
    {
        public UpdateKind Kind; public string Installed, Newest, Message; public List<PublishedVersion> Offering = new List<PublishedVersion>();
    }

    /// <summary>Pure decisions; the game layer fetches the texts (no player data is sent anywhere).</summary>
    public static class UpdateCheck
    {
        public const string GitHubReleaseApi = "https://api.github.com/repos/rylaix/Lycoreia/releases/latest";
        public const string NexusGraphQl = "https://api.nexusmods.com/v2/graphql";
        public const int NexusGameId = 4823, NexusModId = 200;           // slimerancher2, mods/200 (BuildInfo.Nexus)
        public static string NexusQuery => "{\"query\":\"query { legacyMods(ids:[{gameId:" + NexusGameId + ", modId:" + NexusModId + "}]) { nodes { version } } }\"}";

        static readonly Regex TagName = new Regex("\"tag_name\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.CultureInvariant);
        static readonly Regex NexusVersion = new Regex("\"version\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.CultureInvariant);

        /// <summary>"v2.0.1" from the GitHub latest-release answer (drafts and pre-releases are never "latest"); null if absent.</summary>
        public static string ParseGitHubRelease(string json) => Clean(Match(TagName, json));
        /// <summary>The mod version from the Nexus GraphQL answer; null on errors or an empty result.</summary>
        public static string ParseNexus(string json) => json != null && json.Contains("\"errors\"") ? null : Clean(Match(NexusVersion, json));
        /// <summary>The first line of latestModVersion.txt.</summary>
        public static string ParsePlain(string text) => Clean((text ?? "").Split('\n').FirstOrDefault());

        static string Match(Regex r, string s) { if (string.IsNullOrEmpty(s)) return null; var m = r.Match(s); return m.Success ? m.Groups[1].Value : null; }
        static string Clean(string v)
        {
            v = (v ?? "").Trim().TrimStart('v', 'V').Trim();
            return v.Length > 0 && v.Length <= 32 && VersionCheck.TryParse(v, out _) ? v : null;
        }

        public static UpdateStatus Evaluate(string installed, IEnumerable<PublishedVersion> sources)
        {
            var st = new UpdateStatus { Installed = installed, Kind = UpdateKind.Unknown };
            if (!VersionCheck.TryParse(installed, out var mine)) return st;
            var known = sources.Where(s => s.Version != null && VersionCheck.TryParse(s.Version, out _)).ToList();
            if (known.Count == 0) return st;
            System.Version V(string v) { VersionCheck.TryParse(v, out var p); return p; }
            var newest = known.Select(s => V(s.Version)).Max();
            st.Newest = known.First(s => V(s.Version).Equals(newest)).Version;
            int c = mine.CompareTo(newest);
            if (c < 0)
            {
                st.Kind = UpdateKind.UpdateAvailable;
                st.Offering = known.Where(s => V(s.Version).Equals(newest)).ToList();
                st.Message = $"Lycoreia {st.Newest} is available (you have {installed}). Download it from {string.Join(" or ", st.Offering.Select(s => s.Source))}. Every player in a session needs the same version.";
            }
            else if (c > 0)
            {
                st.Kind = UpdateKind.AheadOfPublic;
                st.Message = $"You are running Lycoreia {installed}, which is newer than the public release {st.Newest}. Other players need this exact build to play with you.";
            }
            else st.Kind = UpdateKind.UpToDate;
            return st;
        }
    }
}

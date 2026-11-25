// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System.Collections.Generic;

namespace Lycoreia.Tests
{
    static class UpdateCheckTests
    {
        static PublishedVersion G(string v) => new PublishedVersion("GitHub", v, "g");
        static PublishedVersion N(string v) => new PublishedVersion("Nexus Mods", v, "n");

        public static void Register()
        {
            T.Test("update check: parses GitHub release, Nexus GraphQL and the plain version file", () =>
            {
                T.Eq("2.0.0", UpdateCheck.ParseGitHubRelease("{\"url\":\"x\",\"tag_name\": \"v2.0.0\",\"name\":\"Lycoreia 2.0.0\"}"));
                T.Eq(null, UpdateCheck.ParseGitHubRelease("{\"message\":\"API rate limit exceeded\"}"));
                T.Eq("2.0.0", UpdateCheck.ParseNexus("{\"data\":{\"legacyMods\":{\"nodes\":[{\"name\":\"LYCOREIA 2.0\",\"version\":\"2.0.0\"}]}}}"));
                T.Eq(null, UpdateCheck.ParseNexus("{\"errors\":[{\"message\":\"bad\"}],\"data\":{\"version\":\"9.9.9\"}}"), "errors win");
                T.Eq(null, UpdateCheck.ParseNexus("{\"data\":{\"legacyMods\":{\"nodes\":[]}}}"));
                T.Eq("2.0.1", UpdateCheck.ParsePlain("2.0.1\n")); T.Eq(null, UpdateCheck.ParsePlain("<html>404</html>"));
                T.True(UpdateCheck.NexusQuery.Contains("gameId:4823") && UpdateCheck.NexusQuery.Contains("modId:200"));
                T.True(VersionCheck.TryParse("2", out var two) && two.Major == 2, "short versions parse (and terminate)"); T.False(VersionCheck.TryParse("junk", out _));
            });

            T.Test("update check: newer public build, same build, ahead of public, nothing reachable", () =>
            {
                var up = UpdateCheck.Evaluate("2.0.1", new List<PublishedVersion> { G("2.0.2"), N("2.0.1") });
                T.Eq(UpdateKind.UpdateAvailable, up.Kind); T.Eq("2.0.2", up.Newest); T.Eq(1, up.Offering.Count); T.Eq("GitHub", up.Offering[0].Source);
                T.True(up.Message.Contains("2.0.2") && up.Message.Contains("GitHub") && !up.Message.Contains("Nexus"), "only the source that has it");
                var both = UpdateCheck.Evaluate("2.0.0", new List<PublishedVersion> { G("2.0.1"), N("2.0.1") });
                T.Eq(2, both.Offering.Count); T.True(both.Message.Contains("GitHub or Nexus Mods"));
                T.Eq(UpdateKind.UpToDate, UpdateCheck.Evaluate("2.0.1", new List<PublishedVersion> { G("2.0.1"), N(null) }).Kind);
                var ahead = UpdateCheck.Evaluate("2.0.1", new List<PublishedVersion> { G("2.0.0"), N("2.0.0") });
                T.Eq(UpdateKind.AheadOfPublic, ahead.Kind); T.True(ahead.Message.Contains("newer than the public release 2.0.0"));
                T.Eq(UpdateKind.Unknown, UpdateCheck.Evaluate("2.0.1", new List<PublishedVersion> { G(null), N(null) }).Kind, "offline: say nothing");
                T.Eq(UpdateKind.UpdateAvailable, UpdateCheck.Evaluate("2.0.1-dev", new List<PublishedVersion> { G("2.0.2") }).Kind, "pre-release suffix ignored");
            });

            T.Test("update check: release candidates and hotfixes are ordered (rc1 said UpToDate with rc4 public)", () =>
            {
                T.True(VersionCheck.Compare("2.1.0-rc1", "2.1.0-rc4") < 0); T.True(VersionCheck.Compare("2.1.0-rc4", "2.1.0-rc4.1") < 0);
                T.True(VersionCheck.Compare("2.1.0-rc9", "2.1.0-rc10") < 0, "numbers, not text"); T.True(VersionCheck.Compare("2.1.0-rc4.1", "2.1.0") < 0);
                T.True(VersionCheck.Compare("2.1.0", "2.1.0-rc4.1") > 0); T.Eq(0, VersionCheck.Compare("v2.1.0-rc4", "2.1.0-rc4")); T.True(VersionCheck.Compare("2.1.0", "2.0.9") > 0);
                T.Eq(ModVersionVerdict.Outdated, VersionCheck.CheckMod("2.1.0-rc1", "2.1.0-rc4"));
                T.Eq(ModVersionVerdict.Outdated, VersionCheck.CheckMod("2.1.0-rc4", "2.1.0-rc4.1"));
                T.Eq(ModVersionVerdict.UpToDate, VersionCheck.CheckMod("2.1.0-rc4.1", "2.1.0-rc4.1"));
                var up = UpdateCheck.Evaluate("2.1.0-rc1", new List<PublishedVersion> { G("2.1.0-rc4.1"), N("2.1.0-rc4") });
                T.Eq(UpdateKind.UpdateAvailable, up.Kind); T.Eq("2.1.0-rc4.1", up.Newest); T.Eq(1, up.Offering.Count); T.Eq("GitHub", up.Offering[0].Source);
                T.Eq(UpdateKind.AheadOfPublic, UpdateCheck.Evaluate("2.1.0-rc4.1", new List<PublishedVersion> { G("2.1.0-rc4") }).Kind);
            });
        }
    }
}

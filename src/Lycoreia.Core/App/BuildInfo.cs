// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia
{
    /// <summary>Identity and links; the version comes from the one constant in Version.cs.</summary>
    public static class BuildInfo
    {
        public const string Id = "com.rylaix.lycoreia", Name = "Lycoreia", Description = "Host-authoritative multiplayer co-op for Slime Rancher 2";
        public const string Author = "Rylaix (core developer, github.com/rylaix)";
        public const string Version = ModVersion.Value;
        public const string RequiredGameVersion = "1.3.0", MinimumGameVersion = "1.3.0";
        public const string Discord = "https://discord.gg/EvD7WhSwqh", SourceCode = "https://github.com/rylaix/Lycoreia", Nexus = "https://www.nexusmods.com/slimerancher2/mods/200";
        public const string Releases = "https://github.com/rylaix/Lycoreia/releases";
        public const string LatestVersionUrl = "https://raw.githubusercontent.com/rylaix/Lycoreia/refs/heads/main/latestModVersion.txt";
        public const string DataFolder = "Lycoreia";
        public const string DiscordAppId = "1565692817748140102";
#if LYC_TEST
        public const string Flavour = "test";
#elif DEEPLOG
        public const string Flavour = "deeplog";
#else
        public const string Flavour = "release";
#endif
        public const bool Diagnostics =
#if DEEPLOG
            true;
#else
            false;
#endif
    }
}

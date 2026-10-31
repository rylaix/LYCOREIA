#pragma warning disable RCS1110 // Declare type inside namespace
// ReSharper disable once CheckNamespace

internal static class BuildInfo
{
    internal const string ID = "com.rylaix.lycoreia";
    internal const string Name = "Lycoreia";
    internal const string Description = "Host-authoritative multiplayer co-op for Slime Rancher 2";
    internal const string Author = "Rylaix (core developer, github.com/rylaix)";
    internal static readonly string[] CoAuthors = null;
    // Upstream (Ranching Together) authors are credited in NOTICE.md and AssemblyCopyright
    internal static readonly string[] Contributors = new[] { "Kum1te (core QA, github.com/kum1te)" };
    // MelonVersion is shown by ML on startup
    // Version is shown by Starlight
    // Release builds are stamped by tools/set-version.sh (GitHub Action "Build release")
    internal const string MelonVersion = "1.0.1";
    internal const string Version = "1.0.1"; // Auto-Dev_Do_not_remove
    internal const string Discord = "https://discord.gg/EvD7WhSwqh";
    internal const string SourceCode = "https://github.com/rylaix/Lycoreia";
    internal const string Nexus = "https://www.nexusmods.com/slimerancher2/mods/200";
    internal const string Releases = "https://github.com/rylaix/Lycoreia/releases";
    internal const string LatestVersionUrl = "https://raw.githubusercontent.com/rylaix/Lycoreia/refs/heads/main/latestModVersion.txt";
    internal const string DiscordAppID = "1565692817748140102";
    internal const string OriginalProject = "https://github.com/pyeight/SlimeRancher2Multiplayer";
    internal const bool UsePrism = false;
    internal const string MinimumStarlightVersion = Starlight.BuildInfo.CodeVersion; // e.g "3.6.3", the min required SR2 version. No beta or alpha versions
    internal const string MinimumGameVersion = "1.3.0"; // e.g 1.1.0 or something similar (optional)
    internal const string ExactGameVersion = "1.3.0"; // e.g 1.1.0 or something similar (optional)
}

using System.Reflection;
using MelonLoader;

[assembly: AssemblyTitle(BuildInfo.Name)]
[assembly: AssemblyDescription(BuildInfo.Description)]
[assembly: AssemblyCompany(BuildInfo.Author)]
[assembly: AssemblyProduct(BuildInfo.Name)]
[assembly: AssemblyCopyright("Copyright (C) 2026 Rylaix and Lycoreia contributors; portions (C) Ranching Together contributors. GPLv3")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyVersion(BuildInfo.MelonVersion)]
[assembly: AssemblyFileVersion(BuildInfo.MelonVersion)]


[assembly: MelonOptionalDependencies("DiscordRPC", "SharpOpenNat")]

[assembly: VerifyLoaderVersion(0, 7, 3, true)]
[assembly: MelonColor(255, 77, 149, 203)]

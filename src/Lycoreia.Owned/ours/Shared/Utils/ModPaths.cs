// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using MelonLoader.Utils;

namespace Lycoreia.Owned.Shared.Utils;

/// <summary>Lycoreia's own data folder: UserData/Lycoreia (inventories, skins, logs, save backups).</summary>
internal static class ModPaths
{
    private static readonly object Gate = new();
    private static string? dataDirectory;

    internal static string DataDirectory
    {
        get
        {
            if (dataDirectory != null)
                return dataDirectory;

            lock (Gate)
            {
                if (dataDirectory != null)
                    return dataDirectory;

                var target = Path.Combine(MelonEnvironment.UserDataDirectory, BuildInfo.Name);
                Directory.CreateDirectory(target);
                dataDirectory = target;
                return target;
            }
        }
    }
}

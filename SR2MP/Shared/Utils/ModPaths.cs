using MelonLoader;
using MelonLoader.Utils;

namespace SR2MP.Shared.Utils;

/// <summary>
/// Lycoreia's own UserData folder, separate from Ranching Together's "UserData/SR2MP" so switching
/// between the two mods never mixes inventories, skins or backups. The first run copies an
/// existing SR2MP folder once, so players upgrading from earlier builds keep their data.
/// </summary>
internal static class ModPaths
{
    private const string LegacyFolderName = "SR2MP";
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
                var legacy = Path.Combine(MelonEnvironment.UserDataDirectory, LegacyFolderName);
                if (!Directory.Exists(target) && Directory.Exists(legacy))
                {
                    try
                    {
                        CopyDirectory(legacy, target);
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning($"Could not migrate {legacy} to {target}: {ex.Message}");
                    }
                }

                Directory.CreateDirectory(target);
                dataDirectory = target;
                return target;
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            // Old logs and diagnostics are not migrated; they belong to the old install.
            if (name.StartsWith("latest.log", StringComparison.Ordinal) ||
                name.StartsWith("sensitive.log", StringComparison.Ordinal) ||
                name.StartsWith("diagnostics.jsonl", StringComparison.Ordinal))
                continue;
            File.Copy(file, Path.Combine(destination, name), overwrite: false);
        }

        foreach (var directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}

using System.Security.Cryptography;
#if SR2MP_TEST
using System.Text;
#endif

namespace SR2MP.Shared.Utils;

internal static class PlayerIdGenerator
{
    private static string? cachedPlayerId;

    public static string GenerateSessionPlayerId()
    {
        if (cachedPlayerId != null)
            return cachedPlayerId;

        try
        {
            // A session identity must be unique even when two Proton prefixes on
            // the same Linux account are used for a local host/client test. The ID
            // is cached for this process and is not a save/profile identifier.
            var hashBytes = RandomNumberGenerator.GetBytes(16);
#if SR2MP_TEST
            var playerIdOverride = Environment.GetEnvironmentVariable("SR2MP_PLAYER_ID_OVERRIDE");
            if (!string.IsNullOrWhiteSpace(playerIdOverride))
                hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(playerIdOverride + Environment.UserName));
#endif

            var hash = BitConverter.ToString(hashBytes)
                .Replace("-", string.Empty)[..9]
                .ToUpperInvariant();

            cachedPlayerId = $"PLAYER_{hash}";

            SrLogger.LogMessage($"Generated session player ID: {cachedPlayerId}");
            return cachedPlayerId;
        }
        catch (Exception ex)
        {
            SrLogger.LogError($"Failed to generate persistent player ID: {ex}");
            return null!;
        }
    }

    public static ushort GetPlayerIDNumber(string id)
    {
        ushort number = 12345;
        foreach (var c in id[7..])
            number = (ushort)((number << 5) + number + c);
        return number;
    }

    // public static bool IsValidPlayerId(string playerId)
    // {
    //     if (string.IsNullOrWhiteSpace(playerId))
    //         return false;

    //     if (!playerId.StartsWith("PLAYER_"))
    //         return false;

    //     return playerId.Length == 16;
    // }
}

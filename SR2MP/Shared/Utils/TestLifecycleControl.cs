#if SR2MP_TEST
using System.IO;
using System.Text.Json;

namespace SR2MP.Shared.Utils;

/// <summary>
/// Handles a small, local-only command file used by the disposable-client runner.
/// The file content is exactly one of the recognized lifecycle/content tokens; it is
/// never treated as a command line, path, or script. Snapshot data is written beside
/// the configured control file as a private test artifact.
/// </summary>
internal static class TestLifecycleControl
{
    private const string ControlFileName = "control.txt";
    private const int MaximumTokenFileBytes = 32;
    private const float DisconnectReconnectPauseSeconds = 5f;

    internal static float SuppressAutoConnectUntil { get; private set; }

    /// <returns>True when quit was requested and the rest of this pump should stop.</returns>
    internal static bool Pump()
    {
        var configuredPath = Environment.GetEnvironmentVariable("SR2MP_TEST_CONTROL_PATH");
        if (string.IsNullOrWhiteSpace(configuredPath))
            return false;

        string controlPath;
        try
        {
            if (!Path.IsPathFullyQualified(configuredPath) ||
                !string.Equals(Path.GetFileName(configuredPath), ControlFileName, StringComparison.Ordinal))
            {
                SrLogger.LogWarning(
                    "[SR2MP_TEST] lifecycle-control ignored: control path must be an absolute control.txt path");
                return false;
            }

            controlPath = Path.GetFullPath(configuredPath);
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning(
                $"[SR2MP_TEST] lifecycle-control ignored invalid control path ({ex.GetType().Name})");
            return false;
        }

        if (!File.Exists(controlPath))
            return false;

        string token;
        try
        {
            if (new FileInfo(controlPath).Length > MaximumTokenFileBytes)
            {
                File.Delete(controlPath);
                SrLogger.LogWarning("[SR2MP_TEST] lifecycle-control rejected oversized token file");
                return false;
            }

            token = File.ReadAllText(controlPath).Trim();
            // Consume before executing so a retry or exception cannot replay an action.
            File.Delete(controlPath);
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning(
                $"[SR2MP_TEST] lifecycle-control could not consume token file ({ex.GetType().Name})");
            return false;
        }

        switch (token)
        {
            case "quit":
                SrLogger.LogMessage("[SR2MP_TEST] lifecycle-control command=quit requested");
                if (Main.Client.IsConnected || Main.Client.IsConnecting)
                    Main.Client.Disconnect();
                if (Main.Server.IsRunning)
                    Main.Server.Close();
                UnityEngine.Application.Quit();
                return true;

            case "disconnect":
                SuppressAutoConnectForFiveSeconds();
                SrLogger.LogMessage(
                    $"[SR2MP_TEST] lifecycle-control command=disconnect begin " +
                    $"suppressAutoConnectUntil={SuppressAutoConnectUntil:F2}");
                Main.Client.Disconnect();
                SrLogger.LogMessage("[SR2MP_TEST] lifecycle-control command=disconnect complete");
                return false;

            case "resync":
                SrLogger.LogMessage(
                    $"[SR2MP_TEST] lifecycle-control command=resync requested connected={Main.Client.IsConnected}");
                Main.Server.ReSyncManager.RequestResync();
                return false;

            case "garden-fixture":
                TestGardenSmoke.PrepareHostFixture();
                return false;

            case "garden-plant":
                TestGardenSmoke.ApplyGuestPlant();
                return false;

            case "garden-growth-step":
                TestGardenGrowth.AdvanceLocallyOwnedGardenOneCycle();
                return false;

            case "refinery-step":
                TestRefinerySmoke.ApplyGuestIncrement();
                return false;

            case "snapshot":
                WriteSnapshot(controlPath + ".snapshot.json");
                return false;

            default:
                SrLogger.LogWarning("[SR2MP_TEST] lifecycle-control rejected unknown token");
                return false;
        }
    }

    private static void WriteSnapshot(string snapshotPath)
    {
        try
        {
            var ammoTotals = CaptureGuestAmmoTotals();
            var snapshot = new
            {
                capturedAtUtc = DateTimeOffset.UtcNow,
                connected = Main.Client.IsConnected,
                joined = Main.Client.IsJoined,
                serverRunning = Main.Server.IsRunning,
                clientCount = Main.Server.GetClientCount,
                actorIds = ActorManager.Actors.Keys.OrderBy(actorId => actorId).ToArray(),
                remotePlayerObjectCount = PlayerObjects.Values.Count(player => player),
                guestAmmoTotalsBySlotIdName = ammoTotals,
                refinery = TestRefinerySmoke.ReadState(),
                garden = TestGardenSmoke.ReadState(),
                gardenGrowth = TestGardenGrowth.ReadState(),
                gardenFixture = TestGardenSmoke.ReadFixtureState()
            };

            File.WriteAllText(snapshotPath, JsonSerializer.Serialize(snapshot,
                new JsonSerializerOptions { WriteIndented = true }));
            SrLogger.LogMessage(
                $"[SR2MP_TEST] lifecycle-control command=snapshot wrote file=" +
                $"{Path.GetFileName(snapshotPath)} actorCount={snapshot.actorIds.Length} " +
                $"remotePlayerObjectCount={snapshot.remotePlayerObjectCount} " +
                $"ammoReady={ammoTotals != null}");
        }
        catch (Exception ex)
        {
            SrLogger.LogError(
                $"[SR2MP_TEST] lifecycle-control command=snapshot failed ({ex.GetType().Name})");
        }
    }

    private static Dictionary<string, int>? CaptureGuestAmmoTotals()
    {
        try
        {
            var slots = SceneContext.Instance?.PlayerState?.Ammo?.Slots;
            if (!Main.Client.IsConnected || slots == null)
                return null;

            var totals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var slot in slots)
            {
                if (slot == null || slot.Id == null || string.IsNullOrWhiteSpace(slot.Id.name))
                    continue;

                var name = slot.Id.name;
                totals.TryGetValue(name, out var existing);
                totals[name] = checked(existing + slot.Count);
            }

            return totals;
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning(
                $"[SR2MP_TEST] lifecycle-control snapshot ammo unavailable ({ex.GetType().Name})");
            return null;
        }
    }

    internal static void SuppressAutoConnectForFiveSeconds()
        => SuppressAutoConnectUntil = Math.Max(
            SuppressAutoConnectUntil,
            UnityEngine.Time.unscaledTime + DisconnectReconnectPauseSeconds);
}
#endif

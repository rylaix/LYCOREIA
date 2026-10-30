using System.Security.Cryptography;
using System.Text;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Labyrinth;
using SR2MP.Packets.World;

namespace SR2MP.Shared.Managers;

internal static class NetworkPrismaDisruptionManager
{
    private static PrismaDisruptionStatePacket? pendingSnapshot;
    private static PrismaDisruptionStatePacket? pendingHostBroadcast;
    private static string? pendingHostFingerprint;
    private static string? lastSentFingerprint;
    private static int nextPendingPumpFrame;
    private static int nextHostBroadcastFrame;
    private static bool initialized;
    private const int HostBroadcastIntervalFrames = 12;

    internal static void Initialize()
    {
        if (initialized || Main.Server == null || Main.Client == null)
            return;

        initialized = true;
        Main.Server.OnServerStarted += ClearHostFingerprint;
        Main.Client.OnConnected += _ => ClearPendingSnapshot();
        Main.Client.OnDisconnected += ClearPendingSnapshot;
    }

    internal static PrismaDisruptionStatePacket? CreateSnapshot()
    {
        var model = GetModel();
        if (model == null || model._areaDatas == null)
            return null;

        if (!TryCapture(model, out var packet, out _))
            return null;

        return packet;
    }

    internal static void Broadcast(PrismaDisruptionModel model)
    {
        if (HandlingPacket || !Main.Server.IsRunning || model == null || model._areaDatas == null)
            return;

        if (!TryCapture(model, out var packet, out var fingerprint))
            return;

        if (fingerprint == lastSentFingerprint)
        {
            pendingHostBroadcast = null;
            pendingHostFingerprint = null;
            return;
        }

        pendingHostBroadcast = packet;
        pendingHostFingerprint = fingerprint;
        if (Time.frameCount >= nextHostBroadcastFrame)
            FlushHostBroadcast();
    }

    internal static void ApplySnapshot(PrismaDisruptionStatePacket packet)
    {
        if (!Validate(packet))
        {
            SrLogger.LogWarning("Rejected invalid Prisma disruption state packet");
            return;
        }

        pendingSnapshot = packet;
        nextPendingPumpFrame = 0;
        TryApplyPendingSnapshot();
    }

    internal static void Pump()
    {
        if (pendingSnapshot != null && Time.frameCount >= nextPendingPumpFrame)
        {
            nextPendingPumpFrame = Time.frameCount + 30;
            TryApplyPendingSnapshot();
        }

        if (pendingHostBroadcast != null && Time.frameCount >= nextHostBroadcastFrame)
            FlushHostBroadcast();
    }

    private static void TryApplyPendingSnapshot()
    {
        var packet = pendingSnapshot;
        if (packet == null)
            return;

        var model = GetModel();
        if (model == null)
            return;

        var definitions = new Dictionary<string, DisruptionAreaDefinition>(StringComparer.Ordinal);
        foreach (var definition in Resources.FindObjectsOfTypeAll<DisruptionAreaDefinition>())
        {
            if (definition && !string.IsNullOrEmpty(definition!.ReferenceId))
                definitions[definition!.ReferenceId] = definition;
        }

        // Resolve and construct the entire replacement dictionary before touching
        // the live model. A partially resolved snapshot would erase valid areas.
        var areaData = new CppCollections.Dictionary<DisruptionAreaDefinition, PrismaDisruptionModel.DisruptionAreaData>();
        foreach (var area in packet.Areas)
        {
            if (!definitions.TryGetValue(area.ReferenceId, out var definition))
                return;

            var forecast = new CppCollections.List<DisruptionPattern>();
            foreach (var patternState in area.Forecast)
            {
                var entries = new CppCollections.List<DisruptionPatternEntry>();
                foreach (var entry in patternState.Entries)
                    entries.Add(new DisruptionPatternEntry(entry.StartTime, (DisruptionLevel)entry.Level));

                forecast.Add(new DisruptionPattern(entries, patternState.EndTime));
            }

            areaData.Add(definition, new PrismaDisruptionModel.DisruptionAreaData
            {
                Definition = definition,
                Forecast = forecast,
                LastCompletionTime = area.LastCompletionTime,
                ForecastUnlocked = area.ForecastUnlocked,
                NextChangeTime = area.NextChangeTime
            });
        }

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            model._areaDatas = areaData;
            model._forecastedTime = packet.ForecastedTime;
            model._areaRefreshNeeded = packet.AreaRefreshNeeded;
            model.NotifyParticipants();
            pendingSnapshot = null;
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"Could not apply Prisma disruption state: {ex.Message}");
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }
    }

    private static bool TryCapture(PrismaDisruptionModel model, out PrismaDisruptionStatePacket packet, out string fingerprint)
    {
        packet = new PrismaDisruptionStatePacket
        {
            ForecastedTime = model._forecastedTime,
            AreaRefreshNeeded = model._areaRefreshNeeded
        };

        foreach (var pair in model._areaDatas)
        {
            var definition = pair.key;
            var data = pair.value;
            if (!definition || data == null || string.IsNullOrEmpty(definition!.ReferenceId) ||
                data.Forecast == null)
            {
                fingerprint = string.Empty;
                SrLogger.LogWarning("Could not capture Prisma disruption state: area data has no stable definition reference");
                return false;
            }

            var area = new PrismaDisruptionStatePacket.Area
            {
                ReferenceId = definition.ReferenceId,
                LastCompletionTime = data.LastCompletionTime,
                ForecastUnlocked = data.ForecastUnlocked,
                NextChangeTime = data.NextChangeTime
            };

            foreach (var pattern in data.Forecast)
            {
                if (pattern == null || pattern.Entries == null)
                {
                    fingerprint = string.Empty;
                    return false;
                }

                var patternState = new PrismaDisruptionStatePacket.Pattern { EndTime = pattern.EndTime };
                foreach (var entry in pattern.Entries)
                {
                    if (entry == null)
                    {
                        fingerprint = string.Empty;
                        return false;
                    }

                    patternState.Entries.Add(new PrismaDisruptionStatePacket.Entry
                    {
                        StartTime = entry.StartTime,
                        Level = (int)entry.Level
                    });
                }

                area.Forecast.Add(patternState);
            }

            packet.Areas.Add(area);
        }

        packet.Areas.Sort((left, right) => StringComparer.Ordinal.Compare(left.ReferenceId, right.ReferenceId));
        fingerprint = GetFingerprint(packet);
        return true;
    }

    private static bool Validate(PrismaDisruptionStatePacket packet)
    {
        if (packet.Areas == null || double.IsNaN(packet.ForecastedTime))
            return false;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var area in packet.Areas)
        {
            if (area == null || string.IsNullOrWhiteSpace(area.ReferenceId) || !seen.Add(area.ReferenceId) ||
                double.IsNaN(area.LastCompletionTime) || double.IsNaN(area.NextChangeTime) || area.Forecast == null)
                return false;

            foreach (var pattern in area.Forecast)
            {
                if (pattern == null || double.IsNaN(pattern.EndTime) || pattern.Entries == null)
                    return false;

                foreach (var entry in pattern.Entries)
                {
                    if (entry == null || double.IsNaN(entry.StartTime) ||
                        !Enum.IsDefined(typeof(DisruptionLevel), entry.Level))
                        return false;
                }
            }
        }

        return true;
    }

    private static string GetFingerprint(PrismaDisruptionStatePacket packet)
    {
        var builder = new StringBuilder();
        AppendDouble(builder, packet.ForecastedTime);
        builder.Append(packet.AreaRefreshNeeded ? '1' : '0').Append('|');

        foreach (var area in packet.Areas)
        {
            builder.Append(area.ReferenceId.Length).Append(':').Append(area.ReferenceId).Append('|');
            AppendDouble(builder, area.LastCompletionTime);
            builder.Append(area.ForecastUnlocked ? '1' : '0').Append('|');
            AppendDouble(builder, area.NextChangeTime);
            foreach (var pattern in area.Forecast)
            {
                AppendDouble(builder, pattern.EndTime);
                foreach (var entry in pattern.Entries)
                {
                    AppendDouble(builder, entry.StartTime);
                    builder.Append(entry.Level).Append('|');
                }

                builder.Append(';');
            }

            builder.Append('#');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendDouble(StringBuilder builder, double value)
        => builder.Append(BitConverter.DoubleToInt64Bits(value)).Append('|');

    private static PrismaDisruptionModel? GetModel()
    {
        var context = SceneContext.Instance;
        if (!context || context!.GameModel == null)
            return null;

        return context!.GameModel.GetPrismaDisruptionModel();
    }

    private static void FlushHostBroadcast()
    {
        var packet = pendingHostBroadcast;
        var fingerprint = pendingHostFingerprint;
        if (packet == null || fingerprint == null || !Main.Server.IsRunning)
            return;

        pendingHostBroadcast = null;
        pendingHostFingerprint = null;
        lastSentFingerprint = fingerprint;
        nextHostBroadcastFrame = Time.frameCount + HostBroadcastIntervalFrames;
        Main.SendToAllOrServer(packet);
    }

    private static void ClearHostFingerprint()
    {
        lastSentFingerprint = null;
        pendingHostBroadcast = null;
        pendingHostFingerprint = null;
        nextHostBroadcastFrame = 0;
    }

    private static void ClearPendingSnapshot()
    {
        pendingSnapshot = null;
        nextPendingPumpFrame = 0;
    }
}

using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Weather;
using Il2CppMonomiPark.SlimeRancher.World;
using Il2CppMonomiPark.SlimeRancher.UI.Map;
using SR2MP.Packets.World;
using SR2MP.Server.Managers;

namespace SR2MP.Client.Managers;

internal static class NetworkWeatherManager
{
    public static WeatherRegistry Registry => SceneContext.Instance.WeatherRegistry;

    public static WeatherDirector Director
    {
        get
        {
            if (!director)
            {
                director = Resources.FindObjectsOfTypeAll<WeatherDirector>().FirstOrDefault()!;
            }

            return director;
        }
    }

    public static LightningStrike Lightning
    {
        get
        {
            if (!lightning)
            {
                lightning = Resources.FindObjectsOfTypeAll<LightningStrike>().First(x => x.BlastPower < 2749f);
            }

            return lightning;
        }
    }

    private static LightningStrike lightning;
    private static WeatherDirector director;

    public static readonly Dictionary<int, WeatherStateDefinition> WeatherStates = new();
    private static readonly object PendingPacketLock = new();
    private static readonly object ApplyPacketLock = new();
    private static WeatherPacket? pendingPacket;
    private static bool pendingImmediate;
    private static long pendingSession;
    private static long sessionGeneration;
    private static long latestReceivedRevision;
    private static long lastAppliedRevision;
    private static bool lifecycleSubscribed;
    private static DateTime nextPendingLog;

    static NetworkWeatherManager()
    {
#if SR2MP_TEST
        var policyPassed = WeatherSyncPolicy.VerifyRules();
        var revisionCodecPassed = WeatherPacket.VerifyRevisionSerializationRoundTrip();
        var forecastCodecPassed = WeatherForecast.VerifyPayloadSerializationRoundTrip();
        var timeCodecPassed = WorldTimePacket.VerifySerializationRoundTrip();
        SrLogger.LogMessage(
            $"[SR2MP_TEST] weather-sync-policy={(policyPassed ? "PASS" : "FAIL")} " +
            $"weather-revision-codec={(revisionCodecPassed ? "PASS" : "FAIL")} " +
            $"weather-forecast-codec={(forecastCodecPassed ? "PASS" : "FAIL")} " +
            $"world-time-codec={(timeCodecPassed ? "PASS" : "FAIL")}");
#endif
    }

    internal static void SubscribeToClientLifecycle()
    {
        if (lifecycleSubscribed)
            return;

        lifecycleSubscribed = true;
        Main.Client.OnConnected += _ => ResetPending();
        Main.Client.OnDisconnected += ResetPending;
    }

    private static void ResetPending()
    {
        lock (PendingPacketLock)
        {
            pendingPacket = null;
            pendingImmediate = false;
            pendingSession = 0;
            latestReceivedRevision = 0;
            lastAppliedRevision = 0;
            sessionGeneration++;
        }
    }

    private static void ClearPending()
    {
        lock (PendingPacketLock)
        {
            pendingPacket = null;
            pendingImmediate = false;
            pendingSession = 0;
        }
    }

    private static void Initialize()
    {
        var refer = GameContext.Instance.AutoSaveDirector._saveReferenceTranslation;
        foreach (var state in refer._weatherStateTranslation.RawLookupDictionary)
        {
            WeatherStates.Add(refer.GetPersistenceId(state.value), state.value.TryCast<WeatherStateDefinition>()!);
        }
    }

    public static void CheckInitialized()
    {
        if (WeatherStates.Count == 0)
            Initialize();
    }

    public static int GetPersistentID(WeatherStateDefinition state)
        => GameContext.Instance.AutoSaveDirector._saveReferenceTranslation
            .GetPersistenceId(state.Cast<IWeatherState>());

    internal static void Apply(WeatherPacket packet, bool immediate)
    {
        long generation;
        lock (PendingPacketLock)
        {
            if (!Main.Client.IsConnected
                || !WeatherSyncPolicy.IsNewRevision(packet.Revision, latestReceivedRevision))
                return;

            latestReceivedRevision = packet.Revision;
            generation = sessionGeneration;
        }

        if (!IsReadyForApply())
        {
            QueuePending(packet, immediate, generation);
            return;
        }

        ApplyAccepted(packet, immediate, generation);
    }

    internal static void Pump()
    {
        if (!Main.Client.IsConnected)
        {
            ClearPending();
            return;
        }

        if (!IsReadyForApply())
            return;

        WeatherPacket? packet;
        bool immediate;
        long generation;
        lock (PendingPacketLock)
        {
            packet = pendingPacket;
            immediate = pendingImmediate;
            generation = pendingSession;
            pendingPacket = null;
            pendingImmediate = false;
        }

        if (packet == null || !IsSessionCurrent(generation))
            return;

        ApplyAccepted(packet, immediate, generation);
    }

    private static bool IsSessionCurrent(long generation)
    {
        lock (PendingPacketLock)
            return Main.Client.IsConnected && sessionGeneration == generation;
    }

    private static bool IsRevisionCurrent(long revision, long generation)
    {
        lock (PendingPacketLock)
            return Main.Client.IsConnected
                   && sessionGeneration == generation
                   && WeatherSyncPolicy.CanApplyRevision(revision, latestReceivedRevision, lastAppliedRevision);
    }

    private static void ApplyAccepted(WeatherPacket packet, bool immediate, long generation)
    {
        lock (ApplyPacketLock)
        {
            if (!IsRevisionCurrent(packet.Revision, generation) || !IsSessionCurrent(generation))
                return;

            try
            {
                ApplyReady(packet, immediate);

                lock (PendingPacketLock)
                {
                    if (sessionGeneration == generation && packet.Revision > lastAppliedRevision)
                        lastAppliedRevision = packet.Revision;
                }
            }
            catch (Exception ex)
            {
                QueuePending(packet, immediate, generation);
                LogPendingFailure(ex);
            }
        }
    }

    private static bool IsReadyForApply()
    {
        var systemContext = SystemContext.Instance;
        var sceneContext = SceneContext.Instance;
        if (!systemContext || !sceneContext || sceneContext.player == null)
            return false;

        var sceneLoader = systemContext.SceneLoader;
        if (!sceneLoader || sceneLoader.IsSceneLoadInProgress || !sceneLoader.IsCurrentSceneGroupGameplay())
            return false;

        var registry = sceneContext.WeatherRegistry;
        return registry && registry._zones != null && registry._zones.Count > 0 && Director;
    }

    private static void QueuePending(WeatherPacket packet, bool immediate, long generation)
    {
        lock (PendingPacketLock)
        {
            var currentPendingRevision = pendingPacket?.Revision ?? -1;
            if (!Main.Client.IsConnected
                || generation != sessionGeneration
                || packet.Revision != latestReceivedRevision
                || !WeatherSyncPolicy.ShouldReplacePending(
                    packet.Revision,
                    currentPendingRevision,
                    lastAppliedRevision))
                return;

            var preserveImmediate = pendingPacket != null && pendingImmediate;
            pendingPacket = packet;
            pendingImmediate = immediate || preserveImmediate;
            pendingSession = generation;
        }

        if (DateTime.UtcNow < nextPendingLog)
            return;

        nextPendingLog = DateTime.UtcNow.AddSeconds(5);
        SrLogger.LogDebug("NetworkWeatherManager.Apply: world weather systems are still loading; queued latest snapshot");
    }

    private static void LogPendingFailure(Exception exception)
    {
        if (DateTime.UtcNow < nextPendingLog)
            return;

        nextPendingLog = DateTime.UtcNow.AddSeconds(5);
        SrLogger.LogWarning($"NetworkWeatherManager.Apply: snapshot deferred after an incomplete weather model: {exception.Message}");
    }

    private static void ApplyReady(WeatherPacket packet, bool immediate)
    {
        if (packet.Zones == null)
            return;

        WeatherUpdateHelper.EnsureLookupInitialized();
        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;

        try
        {
            var registry = Registry;
            var localDirector = Director;

            var zoneKeys = new List<ZoneDefinition>();
            foreach (var zone in registry._zones)
                zoneKeys.Add(zone.Key);

            foreach (var zoneKey in zoneKeys)
            {
                if (zoneKey == null || string.IsNullOrEmpty(zoneKey.name))
                    continue;

                if (!packet.Zones.TryGetValue(zoneKey.name, out var data))
                    continue;

                if (!registry._zones.TryGetValue(zoneKey, out var zone)
                    || zone == null
                    || zone.Forecast == null
                    || zone.Parameters == null
                    || data == null
                    || data.WeatherForecasts == null)
                    continue;

                var forecastCopy = new List<WeatherModel.ForecastEntry>();
                foreach (var forecast in zone.Forecast)
                    forecastCopy.Add(forecast);

                foreach (var forecast in forecastCopy)
                {
                    if (forecast.State == null || !WeatherSyncPolicy.ShouldRunForecast(forecast.Started))
                        continue;

                    var patternInstance = forecast.Pattern == null
                        ? null
                        : registry.GetWeatherPatternInstance(zoneKey, forecast.Pattern);

                    if (patternInstance == null)
                    {
                        localDirector.StopState(
                            forecast.State.Cast<IWeatherState>(),
                            zone.Parameters
                        );
                    }
                    else
                    {
                        registry.StopPatternState(
                            zoneKey,
                            patternInstance,
                            forecast.State
                        );
                    }
                }

                zone.Forecast.Clear();
                zone.Parameters.WindDirection = data.WindSpeed;

                foreach (var forecast in data.WeatherForecasts)
                {
                    if (forecast == null || forecast.State == null)
                        continue;

                    var pattern = WeatherUpdateHelper.GetPatternForZoneAndState(zoneKey, forecast.State.name);

                    zone.Forecast.Add(new WeatherModel.ForecastEntry
                    {
                        State = forecast.State.Cast<IWeatherState>(),
                        Pattern = pattern,
                        Started = forecast.WeatherStarted,
                        StartTime = forecast.StartTime,
                        EndTime = forecast.EndTime
                    });
                }
            }

            if (localDirector.Zone == null
                || !registry._zones.TryGetValue(localDirector.Zone, out var activeZone)
                || activeZone == null
                || activeZone.Forecast == null
                || activeZone.Parameters == null)
                return;

            var activeCopy = new List<WeatherModel.ForecastEntry>();
            foreach (var activeForecast in activeZone.Forecast)
                activeCopy.Add(activeForecast);

            foreach (var forecast in activeCopy)
            {
                if (forecast.State == null || !WeatherSyncPolicy.ShouldRunForecast(forecast.Started))
                    continue;

                var patternInstance = forecast.Pattern == null
                    ? null
                    : registry.GetWeatherPatternInstance(localDirector.Zone, forecast.Pattern);

                if (patternInstance == null)
                {
                    localDirector.RunState(forecast.State.Cast<IWeatherState>(), activeZone.Parameters, immediate);
                }
                else
                {
                    registry.RunPatternState(
                        localDirector.Zone,
                        patternInstance,
                        forecast.State,
                        immediate
                    );
                }
            }

            if (ActiveMapUI != null)
            {
                var zoomedOutUI = ActiveMapUI._zoomedOutUI;
                if (zoomedOutUI != null && zoomedOutUI._zoneMarkerUIs != null)
                {
                    foreach (var markerUI in zoomedOutUI._zoneMarkerUIs)
                    {
                        var zoneMarkerUI = markerUI.TryCast<ZoneMarkerUI>();
                        if (zoneMarkerUI != null)
                        {
                            zoneMarkerUI.SetUpWeather();
                        }
                    }
                }
            }
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }
    }
}

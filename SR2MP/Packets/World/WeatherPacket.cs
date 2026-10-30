using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Weather;
using SR2MP.Client.Managers;
using SR2MP.Packets.Utils;
using SR2MP.Server.Managers;

namespace SR2MP.Packets.World;

internal sealed class WeatherPacket : IPacket
{
    public Dictionary<string, WeatherZoneData> Zones;
    public long Revision;

    public PacketType Type { get; private init; }
    public PacketReliability Reliability => PacketReliability.Reliable;
    public NetworkChannel Channel => NetworkChannel.Weather;

    public void Serialise(PacketWriter writer)
    {
        writer.WritePackedLong(Revision);
        writer.WriteDictionary(Zones, PacketWriterDels.String, (w, zone) =>
        {
            w.WriteList(zone.WeatherForecasts, (fw, forecast) =>
            {
                var id = NetworkWeatherManager.GetPersistentID(forecast.State);
                if (id == 0)
                    SrLogger.LogWarning($"WeatherForecast: GetPersistentID returned 0 for state {forecast.State?.name}");
                fw.WritePackedInt(id);
                fw.WriteBool(forecast.WeatherStarted);
                fw.WriteDouble(forecast.StartTime);
                fw.WriteDouble(forecast.EndTime);
            });
            w.WriteVector3(zone.WindSpeed);
        });
    }

    public void Deserialise(PacketReader reader)
    {
        Revision = reader.ReadPackedLong();
        Zones = reader.ReadDictionary(PacketReaderDels.String, PacketReaderDels.NetObject<WeatherZoneData>.Reader)!;
    }

    public static WeatherPacket CreateFromModel(WeatherModel model, PacketType type)
    {
        var packet = new WeatherPacket
        {
            Type = type,
            Revision = WeatherUpdateHelper.NextRevision(),
            Zones = new Dictionary<string, WeatherZoneData>()
        };

        foreach (var zone in model._zoneDatas)
        {
            if (zone.Key == null || string.IsNullOrEmpty(zone.Key.name))
                continue;

            var zoneData = new WeatherZoneData
            {
                WeatherForecasts = new List<WeatherForecast>(),
                WindSpeed = zone.Value.Parameters.WindDirection
            };
            
            foreach (var forecast in zone.Value.Forecast)
            {
                zoneData.WeatherForecasts.Add(new WeatherForecast
                {
                    State = forecast.State.Cast<WeatherStateDefinition>(),
                    WeatherStarted = forecast.Started,
                    StartTime = forecast.StartTime,
                    EndTime = forecast.EndTime
                });
            }

            packet.Zones[zone.Key.name] = zoneData;
        }

        return packet;
    }

#if SR2MP_TEST
    internal static bool VerifyRevisionSerializationRoundTrip()
    {
        var expected = new WeatherPacket
        {
            Type = PacketType.WeatherUpdate,
            Revision = 12345,
            Zones = new Dictionary<string, WeatherZoneData>()
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        var actual = new WeatherPacket();
        actual.Deserialise(reader);

        return reader.BytesRemaining == 0 && actual.Revision == expected.Revision && actual.Zones.Count == 0;
    }
#endif
}

internal sealed class WeatherZoneData : INetObject
{
    public List<WeatherForecast> WeatherForecasts;
    public Vector3 WindSpeed;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteList(WeatherForecasts, PacketWriterDels.NetObject<WeatherForecast>.Writer);
        writer.WriteVector3(WindSpeed);
    }

    public void Deserialise(PacketReader reader)
    {
        WeatherForecasts = reader.ReadList(PacketReaderDels.NetObject<WeatherForecast>.Reader)!;
        WindSpeed = reader.ReadVector3();
    }
}

internal sealed class WeatherForecast : INetObject
{
    public WeatherStateDefinition State;
    public bool WeatherStarted;
    public double StartTime;
    public double EndTime;

    public void Serialise(PacketWriter writer)
    {
        WritePayload(
            writer,
            NetworkWeatherManager.GetPersistentID(State),
            WeatherStarted,
            StartTime,
            EndTime);
    }

    public void Deserialise(PacketReader reader)
    {
        ReadPayload(reader, out var persistentId, out var weatherStarted, out var startTime, out var endTime);
        NetworkWeatherManager.CheckInitialized();
        State = NetworkWeatherManager.WeatherStates[persistentId];
        WeatherStarted = weatherStarted;
        StartTime = startTime;
        EndTime = endTime;
    }

    internal static void WritePayload(
        PacketWriter writer,
        int persistentId,
        bool weatherStarted,
        double startTime,
        double endTime)
    {
        writer.WritePackedInt(persistentId);
        writer.WriteBool(weatherStarted);
        writer.WriteDouble(startTime);
        writer.WriteDouble(endTime);
    }

    internal static void ReadPayload(
        PacketReader reader,
        out int persistentId,
        out bool weatherStarted,
        out double startTime,
        out double endTime)
    {
        persistentId = reader.ReadPackedInt();
        weatherStarted = reader.ReadBool();
        startTime = reader.ReadDouble();
        endTime = reader.ReadDouble();
    }

#if SR2MP_TEST
    internal static bool VerifyPayloadSerializationRoundTrip()
    {
        using var writer = PacketWriter.Borrow();
        WritePayload(writer, 481, weatherStarted: false, startTime: 17.25, endTime: 48.5);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        ReadPayload(reader, out var id, out var started, out var start, out var end);
        return reader.BytesRemaining == 0
               && id == 481
               && !started
               && start == 17.25
               && end == 48.5;
    }
#endif
}

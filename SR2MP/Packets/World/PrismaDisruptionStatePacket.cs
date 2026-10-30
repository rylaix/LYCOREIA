using Il2CppMonomiPark.SlimeRancher.Labyrinth;
using SR2MP.Packets.Utils;

namespace SR2MP.Packets.World;

/// <summary>
/// Complete replicated state for the Labyrinth disruption model.
/// </summary>
internal sealed class PrismaDisruptionStatePacket : IPacket
{
    internal sealed class Area : INetObject
    {
        public string ReferenceId = string.Empty;
        public double LastCompletionTime;
        public bool ForecastUnlocked;
        public double NextChangeTime;
        public List<Pattern> Forecast = new();

        public void Serialise(PacketWriter writer)
        {
            writer.WriteString(ReferenceId);
            writer.WriteDouble(LastCompletionTime);
            writer.WriteBool(ForecastUnlocked);
            writer.WriteDouble(NextChangeTime);
            writer.WriteList(Forecast, PacketWriterDels.NetObject<Pattern>.Writer);
        }

        public void Deserialise(PacketReader reader)
        {
            ReferenceId = reader.ReadPooledString() ?? string.Empty;
            LastCompletionTime = reader.ReadDouble();
            ForecastUnlocked = reader.ReadBool();
            NextChangeTime = reader.ReadDouble();
            Forecast = reader.ReadList(PacketReaderDels.NetObject<Pattern>.Reader) ?? new List<Pattern>();
        }
    }

    internal sealed class Pattern : INetObject
    {
        public double EndTime;
        public List<Entry> Entries = new();

        public void Serialise(PacketWriter writer)
        {
            writer.WriteDouble(EndTime);
            writer.WriteList(Entries, PacketWriterDels.NetObject<Entry>.Writer);
        }

        public void Deserialise(PacketReader reader)
        {
            EndTime = reader.ReadDouble();
            Entries = reader.ReadList(PacketReaderDels.NetObject<Entry>.Reader) ?? new List<Entry>();
        }
    }

    internal sealed class Entry : INetObject
    {
        public double StartTime;
        public int Level;

        public void Serialise(PacketWriter writer)
        {
            writer.WriteDouble(StartTime);
            writer.WriteInt(Level);
        }

        public void Deserialise(PacketReader reader)
        {
            StartTime = reader.ReadDouble();
            Level = reader.ReadInt();
        }
    }

    public double ForecastedTime;
    public bool AreaRefreshNeeded;
    public List<Area> Areas = new();

    public PacketType Type => PacketType.PrismaDisruptionState;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.WorldState;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteDouble(ForecastedTime);
        writer.WriteBool(AreaRefreshNeeded);
        writer.WriteList(Areas, PacketWriterDels.NetObject<Area>.Writer);
    }

    public void Deserialise(PacketReader reader)
    {
        ForecastedTime = reader.ReadDouble();
        AreaRefreshNeeded = reader.ReadBool();
        Areas = reader.ReadList(PacketReaderDels.NetObject<Area>.Reader) ?? new List<Area>();
    }
}

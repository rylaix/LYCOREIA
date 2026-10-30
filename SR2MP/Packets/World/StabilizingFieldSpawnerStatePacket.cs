using SR2MP.Packets.Utils;

namespace SR2MP.Packets.World;

/// <summary>
/// Replicated state for one or more Labyrinth stabilizing-field spawners.
/// </summary>
internal sealed class StabilizingFieldSpawnerStatePacket : IPacket
{
    internal sealed class Spawner : INetObject
    {
        public string Id = string.Empty;
        public double NextTriggerTime;
        public double DespawnAtWorldTime;
        public bool HasSpawned;

        public void Serialise(PacketWriter writer)
        {
            writer.WriteString(Id);
            writer.WriteDouble(NextTriggerTime);
            writer.WriteDouble(DespawnAtWorldTime);
            writer.WriteBool(HasSpawned);
        }

        public void Deserialise(PacketReader reader)
        {
            Id = reader.ReadPooledString() ?? string.Empty;
            NextTriggerTime = reader.ReadDouble();
            DespawnAtWorldTime = reader.ReadDouble();
            HasSpawned = reader.ReadBool();
        }
    }

    public List<Spawner> Spawners = new();
    public bool IsSnapshot;

    public PacketType Type => PacketType.StabilizingFieldSpawnerState;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.WorldState;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteBool(IsSnapshot);
        writer.WriteList(Spawners, PacketWriterDels.NetObject<Spawner>.Writer);
    }

    public void Deserialise(PacketReader reader)
    {
        IsSnapshot = reader.ReadBool();
        Spawners = reader.ReadList(PacketReaderDels.NetObject<Spawner>.Reader) ?? new List<Spawner>();
    }
}

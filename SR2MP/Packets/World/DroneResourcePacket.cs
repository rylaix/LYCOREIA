using SR2MP.Packets.Utils;

namespace SR2MP.Packets.World;

internal sealed class DroneResourcePacket : IPacket
{
    internal sealed class DroneResourceEntry : INetObject
    {
        public bool Valid;
        public string Id = string.Empty;
        public Vector3 Location;
        public Vector3 OffsetLocation;
        public List<int> TypeIds = new();

        public void Serialise(PacketWriter writer)
        {
            writer.WriteBool(Valid);

            if (!Valid)
                return;

            writer.WriteString(Id);
            writer.WriteVector3(Location);
            writer.WriteVector3(OffsetLocation);
            writer.WritePackedInt(TypeIds.Count);

            foreach (var typeId in TypeIds)
                writer.WritePackedInt(typeId);
        }

        public void Deserialise(PacketReader reader)
        {
            Valid = reader.ReadBool();

            if (!Valid)
                return;

            Id = reader.ReadPooledString()!;
            Location = reader.ReadVector3();
            OffsetLocation = reader.ReadVector3();

            var count = reader.ReadPackedInt();
            TypeIds = new List<int>(count);

            for (var i = 0; i < count; i++)
                TypeIds.Add(reader.ReadPackedInt());
        }
    }
    
    public int Scene;
    public long StationId;
    public int Index;
    public DroneResourceEntry Entry = new();

    public PacketType Type => PacketType.DroneResourceUpdate;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.WorldState;

    public void Serialise(PacketWriter writer)
    {
        writer.WritePackedInt(Scene);
        writer.WritePackedLong(StationId);
        writer.WritePackedInt(Index);
        Entry.Serialise(writer);
    }

    public void Deserialise(PacketReader reader)
    {
        Scene = reader.ReadPackedInt();
        StationId = reader.ReadPackedLong();
        Index = reader.ReadPackedInt();
        Entry.Deserialise(reader);
    }

#if SR2MP_TEST
    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new DroneResourcePacket
        {
            Scene = 12,
            StationId = 0x123456,
            Index = 3,
            Entry = new DroneResourceEntry
            {
                Valid = true,
                Id = "resource-3",
                Location = new Vector3(1f, 2f, 3f),
                OffsetLocation = new Vector3(-1f, 0.5f, 4f),
                TypeIds = new List<int> { 7, 29 }
            }
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        using var reader = PacketReader.Borrow(writer.ToSpan().ToArray());
        var actual = new DroneResourcePacket();
        actual.Deserialise(reader);

        return reader.BytesRemaining == 0 && actual.Scene == expected.Scene &&
               actual.StationId == expected.StationId &&
               actual.Index == expected.Index &&
               actual.Entry.Valid == expected.Entry.Valid && actual.Entry.Id == expected.Entry.Id &&
               actual.Entry.Location == expected.Entry.Location &&
               actual.Entry.OffsetLocation == expected.Entry.OffsetLocation &&
               actual.Entry.TypeIds.SequenceEqual(expected.Entry.TypeIds);
    }
#endif
}

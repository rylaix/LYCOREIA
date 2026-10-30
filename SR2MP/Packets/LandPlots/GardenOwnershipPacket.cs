using SR2MP.Packets.Utils;

namespace SR2MP.Packets.LandPlots;

internal sealed class GardenOwnershipPacket : IPacket
{
    internal struct Entry : INetObject
    {
        public string GardenId;
        public string OwnerId;
        public bool HasFinalState;
        public GardenUpdatePacket.Entry FinalState;

        public readonly void Serialise(PacketWriter writer)
        {
            writer.WriteString(GardenId);
            writer.WriteString(OwnerId);
            writer.WriteBool(HasFinalState);
            if (HasFinalState)
                FinalState.Serialise(writer);
        }

        public void Deserialise(PacketReader reader)
        {
            GardenId = reader.ReadPooledString()!;
            OwnerId = reader.ReadPooledString() ?? string.Empty;
            HasFinalState = reader.ReadBool();
            if (HasFinalState)
                FinalState.Deserialise(reader);
        }
    }

    public List<Entry> Entries;

    public PacketType Type => PacketType.GardenOwnership;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.Landplots;

    public void Serialise(PacketWriter writer)
        => writer.WriteList(Entries, PacketWriterDels.NetObject<Entry>.Writer);

    public void Deserialise(PacketReader reader)
        => Entries = reader.ReadList(PacketReaderDels.NetObject<Entry>.Reader)!;

#if SR2MP_TEST
    internal static bool VerifyFinalStateSerializationRoundTrip()
    {
        var expected = new GardenOwnershipPacket
        {
            Entries = new List<Entry>
            {
                new()
                {
                    GardenId = "plot-a_0",
                    OwnerId = string.Empty,
                    HasFinalState = true,
                    FinalState = new GardenUpdatePacket.Entry
                    {
                        GardenId = "plot-a_0",
                        NextSpawnTime = 12345.5,
                        StoredWater = 0.75f,
                        NextSpawnRipens = true,
                        Revision = 19
                    }
                },
                new()
                {
                    GardenId = "plot-b_0",
                    OwnerId = "PLAYER_OWNER_123",
                    HasFinalState = false
                }
            }
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        using var reader = PacketReader.Borrow(writer.ToSpan().ToArray());
        var actual = new GardenOwnershipPacket();
        actual.Deserialise(reader);

        return reader.BytesRemaining == 0 && actual.Entries.Count == 2 &&
               actual.Entries[0].GardenId == expected.Entries[0].GardenId &&
               actual.Entries[0].OwnerId == expected.Entries[0].OwnerId &&
               actual.Entries[0].HasFinalState &&
               actual.Entries[0].FinalState.GardenId == expected.Entries[0].FinalState.GardenId &&
               actual.Entries[0].FinalState.NextSpawnTime == expected.Entries[0].FinalState.NextSpawnTime &&
               actual.Entries[0].FinalState.StoredWater == expected.Entries[0].FinalState.StoredWater &&
               actual.Entries[0].FinalState.NextSpawnRipens == expected.Entries[0].FinalState.NextSpawnRipens &&
               actual.Entries[0].FinalState.Revision == expected.Entries[0].FinalState.Revision &&
               actual.Entries[1].GardenId == expected.Entries[1].GardenId &&
               actual.Entries[1].OwnerId == expected.Entries[1].OwnerId &&
               !actual.Entries[1].HasFinalState;
    }
#endif
}

using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Loading;

internal sealed class InitialEventsRaisedPacket : IPacket
{
    internal sealed class Entry : INetObject
    {
        public string EventKey;
        public string DataKey;
        public int Count;

        public void Serialise(PacketWriter writer)
        {
            writer.WriteString(EventKey);
            writer.WriteString(DataKey);
            writer.WritePackedInt(Count);
        }

        public void Deserialise(PacketReader reader)
        {
            EventKey = reader.ReadPooledString()!;
            DataKey = reader.ReadPooledString()!;
            Count = reader.ReadPackedInt();
        }
    }

    public List<Entry> Entries;

    public PacketType Type => PacketType.InitialEventsRaised;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.WorldState;

    public void Serialise(PacketWriter writer) => writer.WriteList(Entries, PacketWriterDels.NetObject<Entry>.Writer);

    public void Deserialise(PacketReader reader) => Entries = reader.ReadList(PacketReaderDels.NetObject<Entry>.Reader)!;

#if SR2MP_TEST
    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new InitialEventsRaisedPacket
        {
            Entries = new List<Entry>
            {
                new() { EventKey = "Gift", DataKey = "gift-test", Count = 2 }
            }
        };
        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        var actual = new InitialEventsRaisedPacket();
        actual.Deserialise(reader);
        return reader.BytesRemaining == 0
               && actual.Entries.Count == expected.Entries.Count
               && actual.Entries[0].EventKey == expected.Entries[0].EventKey
               && actual.Entries[0].DataKey == expected.Entries[0].DataKey
               && actual.Entries[0].Count == expected.Entries[0].Count;
    }
#endif
}

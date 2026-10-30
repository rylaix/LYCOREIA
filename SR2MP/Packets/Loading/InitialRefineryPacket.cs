using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Loading;

internal sealed class InitialRefineryPacket : IPacket
{
    public Dictionary<int, int> Items;
    public long Revision;

    public PacketType Type => PacketType.InitialRefinery;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.Ammo;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteLong(Revision);
        writer.WriteDictionary(Items, PacketWriterDels.PackedInt, PacketWriterDels.Int);
    }

    public void Deserialise(PacketReader reader)
    {
        Revision = reader.ReadLong();
        Items = reader.ReadDictionary(PacketReaderDels.PackedInt, PacketReaderDels.Int)!;
    }

#if SR2MP_TEST
    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new InitialRefineryPacket
        {
            Revision = 31,
            Items = new Dictionary<int, int> { [23] = 70000, [24] = 0 }
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        using var reader = PacketReader.Borrow(writer.ToSpan().ToArray());
        var actual = new InitialRefineryPacket();
        actual.Deserialise(reader);
        return reader.BytesRemaining == 0 && actual.Revision == expected.Revision &&
               actual.Items.Count == expected.Items.Count &&
               expected.Items.All(entry => actual.Items.TryGetValue(entry.Key, out var count) && count == entry.Value);
    }
#endif
}

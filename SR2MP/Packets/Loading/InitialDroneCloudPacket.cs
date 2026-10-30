using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Loading;

internal sealed class InitialDroneCloudPacket : IPacket
{
    public Dictionary<int, int> Amounts = new();

    public PacketType Type => PacketType.InitialDroneCloud;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.WorldState;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteDictionary(Amounts, PacketWriterDels.PackedInt, PacketWriterDels.PackedInt);
    }

    public void Deserialise(PacketReader reader)
    {
        Amounts = reader.ReadDictionary(PacketReaderDels.PackedInt, PacketReaderDels.PackedInt)!;
    }

    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new InitialDroneCloudPacket
        {
            Amounts = new Dictionary<int, int> { [3] = 0, [0x1234] = 17 }
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        var actual = new InitialDroneCloudPacket();
        actual.Deserialise(reader);

        return reader.BytesRemaining == 0 && actual.Amounts.Count == expected.Amounts.Count &&
               expected.Amounts.All(entry =>
                   actual.Amounts.TryGetValue(entry.Key, out var amount) && amount == entry.Value);
    }
}

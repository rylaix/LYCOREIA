using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Loading;

internal sealed class InitialComponentsPacket : IPacket
{
    public Dictionary<string, byte> Items;

    public PacketType Type => PacketType.InitialComponents;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.Ammo;

    public void Serialise(PacketWriter writer) => writer.WriteDictionary(Items, PacketWriterDels.String, PacketWriterDels.Byte);

    public void Deserialise(PacketReader reader) => Items = reader.ReadDictionary(PacketReaderDels.String, PacketReaderDels.Byte)!;

    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new InitialComponentsPacket
        {
            Items = new Dictionary<string, byte> { ["drone-core"] = 7, ["warp-cell"] = 0 }
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        var actual = new InitialComponentsPacket();
        actual.Deserialise(reader);

        return reader.BytesRemaining == 0 && actual.Items.Count == expected.Items.Count &&
               expected.Items.All(entry =>
                   actual.Items.TryGetValue(entry.Key, out var count) && count == entry.Value);
    }
}

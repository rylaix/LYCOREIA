using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Loading;

internal sealed class InitialMapPacket : IPacket
{
    public List<string?> UnlockedNodes;

    public PacketType Type => PacketType.InitialMapEntries;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.WorldState;

    // todo: Add navigation marker data later.

    public void Serialise(PacketWriter writer)
    {
        writer.WriteList(UnlockedNodes, PacketWriterDels.String);
    }

    public void Deserialise(PacketReader reader)
    {
        UnlockedNodes = reader.ReadList(PacketReaderDels.String)!;
    }

#if SR2MP_TEST
    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new InitialMapPacket
        {
            UnlockedNodes = new List<string?> { "map-node-test", "second-map-node-test" }
        };
        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        var actual = new InitialMapPacket();
        actual.Deserialise(reader);
        return reader.BytesRemaining == 0
               && actual.UnlockedNodes.Count == expected.UnlockedNodes.Count
               && actual.UnlockedNodes[0] == expected.UnlockedNodes[0]
               && actual.UnlockedNodes[1] == expected.UnlockedNodes[1];
    }
#endif
}

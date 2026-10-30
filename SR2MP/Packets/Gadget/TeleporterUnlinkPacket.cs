using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Gadget;

internal sealed class TeleporterUnlinkPacket : IPacket
{
    public string SourceNodeId = string.Empty;
    public string DestinationNodeId = string.Empty;

    public PacketType Type => PacketType.TeleporterUnlink;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.WorldState;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteString(SourceNodeId);
        writer.WriteString(DestinationNodeId);
    }

    public void Deserialise(PacketReader reader)
    {
        SourceNodeId = reader.ReadPooledString()!;
        DestinationNodeId = reader.ReadPooledString()!;
    }

#if SR2MP_TEST
    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new TeleporterUnlinkPacket
        {
            SourceNodeId = "source-node",
            DestinationNodeId = "destination-node"
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        using var reader = PacketReader.Borrow(writer.ToSpan().ToArray());
        var actual = new TeleporterUnlinkPacket();
        actual.Deserialise(reader);

        return reader.BytesRemaining == 0 && actual.SourceNodeId == expected.SourceNodeId &&
               actual.DestinationNodeId == expected.DestinationNodeId &&
               actual.Reliability == PacketReliability.ReliableOrdered &&
               actual.Channel == NetworkChannel.WorldState;
    }
#endif
}

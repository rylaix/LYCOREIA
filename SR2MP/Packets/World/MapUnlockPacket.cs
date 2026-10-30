using SR2MP.Packets.Utils;

namespace SR2MP.Packets.World;

internal sealed class MapUnlockPacket : IPacket
{
    public string NodeID;

    public PacketType Type => PacketType.MapUnlock;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.WorldState;

    public void Serialise(PacketWriter writer) => writer.WriteString(NodeID);

    public void Deserialise(PacketReader reader) => NodeID = reader.ReadPooledString()!;

#if SR2MP_TEST
    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new MapUnlockPacket { NodeID = "map-node-test" };
        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        var actual = new MapUnlockPacket();
        actual.Deserialise(reader);
        return reader.BytesRemaining == 0 && actual.NodeID == expected.NodeID;
    }
#endif
}

using SR2MP.Packets.Utils;

namespace SR2MP.Packets.World;

internal sealed class EventRaisedPacket : IPacket
{
    public string EventKey;
    public string DataKey;
    public int Count;

    public PacketType Type => PacketType.EventRaised;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.WorldState;

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

#if SR2MP_TEST
    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new EventRaisedPacket
        {
            EventKey = "endGameRewardGranted",
            DataKey = "reward-test",
            Count = 3
        };
        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        var actual = new EventRaisedPacket();
        actual.Deserialise(reader);
        return reader.BytesRemaining == 0
               && actual.EventKey == expected.EventKey
               && actual.DataKey == expected.DataKey
               && actual.Count == expected.Count;
    }
#endif
}

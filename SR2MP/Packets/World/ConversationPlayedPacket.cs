using SR2MP.Packets.Utils;

namespace SR2MP.Packets.World;

internal sealed class ConversationPlayedPacket : IPacket
{
    public string ConversationName;

    public PacketType Type => PacketType.ConversationPlayed;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.WorldState;

    public void Serialise(PacketWriter writer) => writer.WriteString(ConversationName);

    public void Deserialise(PacketReader reader) => ConversationName = reader.ReadPooledString()!;

#if SR2MP_TEST
    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new ConversationPlayedPacket { ConversationName = "test-conversation" };
        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        var actual = new ConversationPlayedPacket();
        actual.Deserialise(reader);
        return reader.BytesRemaining == 0 && actual.ConversationName == expected.ConversationName;
    }
#endif
}

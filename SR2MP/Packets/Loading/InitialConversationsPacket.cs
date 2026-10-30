using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Loading;

internal sealed class InitialConversationsPacket : IPacket
{
    public List<string?> ConversationNames;

    public PacketType Type => PacketType.InitialConversations;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.WorldState;

    public void Serialise(PacketWriter writer) => writer.WriteList(ConversationNames, PacketWriterDels.String);

    public void Deserialise(PacketReader reader) => ConversationNames = reader.ReadList(PacketReaderDels.String)!;

#if SR2MP_TEST
    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new InitialConversationsPacket
        {
            ConversationNames = new List<string?> { "test-conversation", "second-test-conversation" }
        };
        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        var actual = new InitialConversationsPacket();
        actual.Deserialise(reader);
        return reader.BytesRemaining == 0
               && actual.ConversationNames.Count == expected.ConversationNames.Count
               && actual.ConversationNames[0] == expected.ConversationNames[0]
               && actual.ConversationNames[1] == expected.ConversationNames[1];
    }
#endif
}

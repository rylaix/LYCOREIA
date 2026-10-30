using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Shop;

internal sealed class ShopPurchaseResultPacket : IPacket
{
    public Guid RequestId;
    public bool Accepted;

    public PacketType Type => PacketType.ShopPurchaseResult;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.Economy;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteGuid(RequestId);
        writer.WriteBool(Accepted);
    }

    public void Deserialise(PacketReader reader)
    {
        RequestId = reader.ReadGuid();
        Accepted = reader.ReadBool();
    }

    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new ShopPurchaseResultPacket
        {
            RequestId = Guid.Parse("87e9c629-f7d8-4741-aafd-5269a568fa06"),
            Accepted = true
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        var actual = new ShopPurchaseResultPacket();
        actual.Deserialise(reader);
        return reader.BytesRemaining == 0 && actual.RequestId == expected.RequestId && actual.Accepted;
    }
}

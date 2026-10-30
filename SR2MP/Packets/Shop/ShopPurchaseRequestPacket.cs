using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Shop;

internal sealed class ShopPurchaseRequestPacket : IPacket
{
    public Guid RequestId;
    public string ShopAssetTag = string.Empty;
    public string ItemAssetGuid = string.Empty;
    public int Count;

    public PacketType Type => PacketType.ShopPurchaseRequest;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.Economy;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteGuid(RequestId);
        writer.WriteString(ShopAssetTag);
        writer.WriteString(ItemAssetGuid);
        writer.WritePackedInt(Count);
    }

    public void Deserialise(PacketReader reader)
    {
        RequestId = reader.ReadGuid();
        ShopAssetTag = reader.ReadPooledString() ?? string.Empty;
        ItemAssetGuid = reader.ReadPooledString() ?? string.Empty;
        Count = reader.ReadPackedInt();
    }

    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new ShopPurchaseRequestPacket
        {
            RequestId = Guid.Parse("dce4652e-66ca-4c1e-84e5-e14706786f90"),
            ShopAssetTag = "shop-tag",
            ItemAssetGuid = "shop-item-guid",
            Count = 7
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        var actual = new ShopPurchaseRequestPacket();
        actual.Deserialise(reader);
        return reader.BytesRemaining == 0 && actual.RequestId == expected.RequestId &&
               actual.ShopAssetTag == expected.ShopAssetTag &&
               actual.ItemAssetGuid == expected.ItemAssetGuid && actual.Count == expected.Count;
    }
}

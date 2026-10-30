using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Economy;

/// <summary>Guest -> host: a plort sale to apply to the authoritative market saturation.</summary>
internal sealed class PlortSoldPacket : IPacket
{
    public int ItemId;
    public int Count;
    public int Price;

    public PacketType Type => PacketType.PlortSold;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.Economy;

    public void Serialise(PacketWriter writer)
    {
        writer.WritePackedInt(ItemId);
        writer.WritePackedInt(Count);
        writer.WritePackedInt(Price);
    }

    public void Deserialise(PacketReader reader)
    {
        ItemId = reader.ReadPackedInt();
        Count = reader.ReadPackedInt();
        Price = reader.ReadPackedInt();
    }
}

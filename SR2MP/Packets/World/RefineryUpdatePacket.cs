using SR2MP.Packets.Utils;

namespace SR2MP.Packets.World;

internal sealed class RefineryUpdatePacket : IPacket
{
    public Guid RequestId;
    public int Delta;
    public int ItemCount;
    public int ItemID;
    public long Revision;

    public PacketType Type => PacketType.RefineryUpdate;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.Ammo;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteGuid(RequestId);
        writer.WritePackedInt(Delta);
        writer.WriteInt(ItemCount);
        writer.WritePackedInt(ItemID);
        writer.WriteLong(Revision);
    }

    public void Deserialise(PacketReader reader)
    {
        RequestId = reader.ReadGuid();
        Delta = reader.ReadPackedInt();
        ItemCount = reader.ReadInt();
        ItemID = reader.ReadPackedInt();
        Revision = reader.ReadLong();
    }

#if SR2MP_TEST
    internal static bool VerifySerializationRoundTrip()
    {
        var request = new RefineryUpdatePacket
        {
            RequestId = Guid.Parse("e3c18b5a-7a9d-42e5-a354-17b3512feea0"),
            Delta = -7,
            ItemID = 23,
            Revision = 0
        };
        var canonical = new RefineryUpdatePacket
        {
            RequestId = request.RequestId,
            ItemCount = 70000,
            ItemID = 23,
            Revision = 9
        };

        using var writer = PacketWriter.Borrow();
        request.Serialise(writer);
        canonical.Serialise(writer);
        using var reader = PacketReader.Borrow(writer.ToSpan().ToArray());
        var actualRequest = new RefineryUpdatePacket();
        actualRequest.Deserialise(reader);
        var actualCanonical = new RefineryUpdatePacket();
        actualCanonical.Deserialise(reader);

        return reader.BytesRemaining == 0 && actualRequest.RequestId == request.RequestId &&
               actualRequest.Delta == request.Delta && actualRequest.ItemID == request.ItemID &&
               actualRequest.ItemCount == request.ItemCount && actualRequest.Revision == 0 &&
               actualCanonical.RequestId == canonical.RequestId && actualCanonical.Delta == canonical.Delta &&
               actualCanonical.ItemCount == canonical.ItemCount && actualCanonical.ItemID == canonical.ItemID &&
               actualCanonical.Revision == canonical.Revision;
    }
#endif
}

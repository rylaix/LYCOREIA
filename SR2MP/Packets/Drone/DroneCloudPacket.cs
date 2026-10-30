using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Drone;

internal struct DroneCloudPacket : IPacket
{
    public int TypeId;
    public int Amount;

    public readonly PacketType Type => PacketType.DroneCloud;
    public readonly PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public readonly NetworkChannel Channel => NetworkChannel.WorldState;

    public readonly void Serialise(PacketWriter writer)
    {
        writer.WritePackedInt(TypeId);
        writer.WritePackedInt(Amount);
    }

    public void Deserialise(PacketReader reader)
    {
        TypeId = reader.ReadPackedInt();
        Amount = reader.ReadPackedInt();
    }

    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new DroneCloudPacket { TypeId = 0x1234, Amount = 17 };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        var actual = new DroneCloudPacket();
        actual.Deserialise(reader);

        return reader.BytesRemaining == 0 && actual.TypeId == expected.TypeId &&
               actual.Amount == expected.Amount;
    }
}

using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Drone;

internal sealed class DroneOwnershipPacket : IPacket
{
    public long StationId;
    public string ClaimerID = string.Empty;
    public string PreviousOwnerID = string.Empty;

    public PacketType Type => PacketType.DroneOwnership;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.WorldState;

    public void Serialise(PacketWriter writer)
    {
        writer.WritePackedLong(StationId);
        writer.WriteString(ClaimerID);
        writer.WriteString(PreviousOwnerID);
    }

    public void Deserialise(PacketReader reader)
    {
        StationId = reader.ReadPackedLong();
        ClaimerID = reader.ReadPooledString()!;
        PreviousOwnerID = reader.ReadPooledString()!;
    }

#if SR2MP_TEST
    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new DroneOwnershipPacket
        {
            StationId = 0x123456,
            ClaimerID = "player-b",
            PreviousOwnerID = "player-a"
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        using var reader = PacketReader.Borrow(writer.ToSpan().ToArray());
        var actual = new DroneOwnershipPacket();
        actual.Deserialise(reader);

        return reader.BytesRemaining == 0 && actual.StationId == expected.StationId &&
               actual.ClaimerID == expected.ClaimerID && actual.PreviousOwnerID == expected.PreviousOwnerID;
    }
#endif
}

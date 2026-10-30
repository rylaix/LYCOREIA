using SR2MP.Packets.Utils;

namespace SR2MP.Packets.GordoSlime;

internal sealed class GordoSlimeFeedPacket : IPacket
{
    public string ID;
    public string RequestId;
    public long FoodActorId;
    public int NewFoodCount;

    // Used as a consistency check against the host's canonical Gordo model.
    public int RequiredFoodCount;
    public int GordoSlimeType;

    public PacketType Type => PacketType.GordoSlimeFeed;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.WorldState;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteString(ID);
        writer.WriteString(RequestId);
        writer.WritePackedLong(FoodActorId);
        writer.WritePackedInt(NewFoodCount);
        writer.WritePackedInt(RequiredFoodCount);
        writer.WritePackedInt(GordoSlimeType);
    }

    public void Deserialise(PacketReader reader)
    {
        ID = reader.ReadPooledString()!;
        RequestId = reader.ReadPooledString()!;
        FoodActorId = reader.ReadPackedLong();
        NewFoodCount = reader.ReadPackedInt();
        RequiredFoodCount = reader.ReadPackedInt();
        GordoSlimeType = reader.ReadPackedInt();
    }

#if SR2MP_TEST
    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new GordoSlimeFeedPacket
        {
            ID = "gordo-test",
            RequestId = "request-1",
            FoodActorId = -0x123456,
            NewFoodCount = 2,
            RequiredFoodCount = 4,
            GordoSlimeType = 1234
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        using var reader = PacketReader.Borrow(writer.ToSpan().ToArray());
        var actual = new GordoSlimeFeedPacket();
        actual.Deserialise(reader);

        return reader.BytesRemaining == 0 && actual.ID == expected.ID &&
               actual.RequestId == expected.RequestId && actual.FoodActorId == expected.FoodActorId &&
               actual.NewFoodCount == expected.NewFoodCount &&
               actual.RequiredFoodCount == expected.RequiredFoodCount &&
               actual.GordoSlimeType == expected.GordoSlimeType;
    }
#endif
}

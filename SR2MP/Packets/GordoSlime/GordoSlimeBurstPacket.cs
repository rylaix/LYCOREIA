using SR2MP.Packets.Utils;

namespace SR2MP.Packets.GordoSlime;

internal sealed class GordoSlimeBurstPacket : IPacket
{
    public string ID;
    public int EatenCount;
    public int RequiredFoodCount;
    public int GordoSlimeType;
    public string RewardAuthorityId;

    public PacketType Type => PacketType.GordoSlimeBurst;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.WorldState;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteString(ID);
        writer.WritePackedInt(EatenCount);
        writer.WritePackedInt(RequiredFoodCount);
        writer.WritePackedInt(GordoSlimeType);
        writer.WriteString(RewardAuthorityId);
    }

    public void Deserialise(PacketReader reader)
    {
        ID = reader.ReadPooledString()!;
        EatenCount = reader.ReadPackedInt();
        RequiredFoodCount = reader.ReadPackedInt();
        GordoSlimeType = reader.ReadPackedInt();
        RewardAuthorityId = reader.ReadPooledString()!;
    }

#if SR2MP_TEST
    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new GordoSlimeBurstPacket
        {
            ID = "gordo-test",
            EatenCount = 42,
            RequiredFoodCount = 40,
            GordoSlimeType = 1234,
            RewardAuthorityId = "player-authority"
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        var actual = new GordoSlimeBurstPacket();
        actual.Deserialise(reader);

        return reader.BytesRemaining == 0
               && actual.ID == expected.ID
               && actual.EatenCount == expected.EatenCount
               && actual.RequiredFoodCount == expected.RequiredFoodCount
               && actual.GordoSlimeType == expected.GordoSlimeType
               && actual.RewardAuthorityId == expected.RewardAuthorityId;
    }
#endif
}

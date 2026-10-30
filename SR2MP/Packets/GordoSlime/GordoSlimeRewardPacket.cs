using SR2MP.Packets.Utils;

namespace SR2MP.Packets.GordoSlime;

internal sealed class GordoSlimeRewardPacket : IPacket, INetObject
{
    public string GordoSlimeId;
    public int PrefabIndex;
    public int RewardOrdinal;
    public Vector3 Position;
    public Quaternion Rotation;

    public PacketType Type => PacketType.GordoSlimeReward;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.WorldState;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteString(GordoSlimeId);
        writer.WritePackedInt(PrefabIndex);
        writer.WritePackedInt(RewardOrdinal);
        writer.WriteVector3(Position);
        writer.WriteQuaternion(Rotation);
    }

    public void Deserialise(PacketReader reader)
    {
        GordoSlimeId = reader.ReadPooledString()!;
        PrefabIndex = reader.ReadPackedInt();
        RewardOrdinal = reader.ReadPackedInt();
        Position = reader.ReadVector3();
        Rotation = reader.ReadQuaternion();
    }

#if SR2MP_TEST
    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new GordoSlimeRewardPacket
        {
            GordoSlimeId = "gordo-test",
            PrefabIndex = 12,
            RewardOrdinal = 2,
            Position = new Vector3(1.25f, -4.5f, 8.75f),
            Rotation = Quaternion.Euler(10f, 20f, 30f)
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        var actual = new GordoSlimeRewardPacket();
        actual.Deserialise(reader);

        return reader.BytesRemaining == 0
               && actual.GordoSlimeId == expected.GordoSlimeId
               && actual.PrefabIndex == expected.PrefabIndex
               && actual.RewardOrdinal == expected.RewardOrdinal
               && actual.Position == expected.Position
               && actual.Rotation == expected.Rotation;
    }
#endif
}

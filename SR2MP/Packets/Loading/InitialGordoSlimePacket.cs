using SR2MP.Packets.Utils;
using SR2MP.Packets.GordoSlime;

namespace SR2MP.Packets.Loading;

internal sealed class InitialGordoSlimesPacket : IPacket
{
    internal sealed class GordoSlime : INetObject
    {
        public string Id;
        public int EatenCount;
        public int RequiredEatCount;
        public int GordoSlimeType;
        public bool WasSeen;
        public bool Popped;
        public List<GordoSlimeRewardPacket> ActiveRewards = new();

        public void Serialise(PacketWriter writer)
        {
            writer.WriteString(Id);
            writer.WritePackedInt(EatenCount);
            writer.WritePackedInt(RequiredEatCount);
            writer.WritePackedInt(GordoSlimeType);
            writer.WritePackedBool(WasSeen);
            writer.WritePackedBool(Popped);
            writer.WriteList(ActiveRewards, PacketWriterDels.NetObject<GordoSlimeRewardPacket>.Writer);
        }

        public void Deserialise(PacketReader reader)
        {
            Id = reader.ReadPooledString()!;
            EatenCount = reader.ReadPackedInt();
            RequiredEatCount = reader.ReadPackedInt();
            GordoSlimeType = reader.ReadPackedInt();
            WasSeen = reader.ReadPackedBool();
            Popped = reader.ReadPackedBool();
            ActiveRewards = reader.ReadList(PacketReaderDels.NetObject<GordoSlimeRewardPacket>.Reader)
                            ?? new List<GordoSlimeRewardPacket>();
        }
    }

    public List<GordoSlime> GordoSlimes;

    public PacketType Type => PacketType.InitialGordoSlimes;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.WorldState;

    public void Serialise(PacketWriter writer) => writer.WriteList(GordoSlimes, PacketWriterDels.NetObject<GordoSlime>.Writer);

    public void Deserialise(PacketReader reader) => GordoSlimes = reader.ReadList(PacketReaderDels.NetObject<GordoSlime>.Reader)!;

#if SR2MP_TEST
    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new InitialGordoSlimesPacket
        {
            GordoSlimes = new List<GordoSlime>
            {
                new()
                {
                    Id = "gordo-late-join",
                    EatenCount = 40,
                    RequiredEatCount = 40,
                    GordoSlimeType = 123,
                    WasSeen = false,
                    Popped = true,
                    ActiveRewards = new List<GordoSlimeRewardPacket>
                    {
                        new()
                        {
                            GordoSlimeId = "gordo-late-join",
                            PrefabIndex = 2,
                            RewardOrdinal = 1,
                            Position = new Vector3(3f, 4f, 5f),
                            Rotation = Quaternion.Euler(0f, 90f, 0f)
                        }
                    }
                }
            }
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        var actual = new InitialGordoSlimesPacket();
        actual.Deserialise(reader);
        if (reader.BytesRemaining != 0 || actual.GordoSlimes.Count != 1)
            return false;

        var expectedGordo = expected.GordoSlimes[0];
        var actualGordo = actual.GordoSlimes[0];
        if (actualGordo.Id != expectedGordo.Id || actualGordo.EatenCount != expectedGordo.EatenCount ||
            actualGordo.RequiredEatCount != expectedGordo.RequiredEatCount ||
            actualGordo.GordoSlimeType != expectedGordo.GordoSlimeType ||
            actualGordo.WasSeen != expectedGordo.WasSeen || actualGordo.Popped != expectedGordo.Popped ||
            actualGordo.ActiveRewards.Count != expectedGordo.ActiveRewards.Count)
            return false;

        var expectedReward = expectedGordo.ActiveRewards[0];
        var actualReward = actualGordo.ActiveRewards[0];
        return actualReward.GordoSlimeId == expectedReward.GordoSlimeId &&
               actualReward.PrefabIndex == expectedReward.PrefabIndex &&
               actualReward.RewardOrdinal == expectedReward.RewardOrdinal &&
               actualReward.Position == expectedReward.Position &&
               actualReward.Rotation == expectedReward.Rotation;
    }
#endif
}

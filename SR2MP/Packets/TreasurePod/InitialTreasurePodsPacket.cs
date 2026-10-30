using SR2MP.Packets.Utils;

namespace SR2MP.Packets.TreasurePod;

internal sealed class InitialTreasurePodsPacket : IPacket
{
    public Dictionary<int, Il2Cpp.TreasurePod.State> TreasurePods;
    public Dictionary<int, TreasurePodPacket.RewardSnapshot> RewardsByPod = new();

    public PacketType Type => PacketType.InitialTreasurePods;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.WorldState;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteDictionary(
            TreasurePods,
            PacketWriterDels.PackedInt,
            PacketWriterDels.PackedEnum<Il2Cpp.TreasurePod.State>.Writer
        );

        writer.WriteDictionary(
            RewardsByPod,
            PacketWriterDels.PackedInt,
            PacketWriterDels.NetObject<TreasurePodPacket.RewardSnapshot>.Writer
        );
    }

    public void Deserialise(PacketReader reader)
    {
        TreasurePods = reader.ReadDictionary(
            PacketReaderDels.PackedInt,
            PacketReaderDels.PackedEnum<Il2Cpp.TreasurePod.State>.Reader
        )!;

        RewardsByPod = reader.ReadDictionary(
            PacketReaderDels.PackedInt,
            PacketReaderDels.NetObject<TreasurePodPacket.RewardSnapshot>.Reader
        ) ?? new Dictionary<int, TreasurePodPacket.RewardSnapshot>();
    }
}

using SR2MP.Packets.Utils;

namespace SR2MP.Packets.TreasurePod;

internal sealed class TreasurePodPacket : IPacket
{
    public int ID;
    public uint RequestId;
    public bool IsRejectedResponse;
    /// <summary>Host to guest: activate this unloaded-on-host pod locally. Guest to host: activation done, rewards attached.</summary>
    public bool ClientActivated;
    public RewardSnapshot Rewards = new();

    public PacketType Type => PacketType.TreasurePod;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.WorldState;

    public void Serialise(PacketWriter writer)
    {
        writer.WritePackedInt(ID);
        writer.WritePackedUInt(RequestId);
        writer.WriteBool(IsRejectedResponse);
        writer.WriteNetObject(Rewards);
        writer.WriteBool(ClientActivated);
    }

    public void Deserialise(PacketReader reader)
    {
        ID = reader.ReadPackedInt();
        RequestId = reader.ReadPackedUInt();
        IsRejectedResponse = reader.ReadBool();
        Rewards = reader.ReadNetObject<RewardSnapshot>();
        ClientActivated = reader.ReadBool();
    }

    internal sealed class RewardSnapshot : INetObject
    {
        public List<string> BlueprintReferenceIds = new();
        public List<string> UpgradeComponentReferenceIds = new();
        public List<AppearanceReward> AppearanceRewards = new();

        public RewardSnapshot() { }

        public void Serialise(PacketWriter writer)
        {
            writer.WriteList(BlueprintReferenceIds, PacketWriterDels.String);
            writer.WriteList(UpgradeComponentReferenceIds, PacketWriterDels.String);
            writer.WriteList(AppearanceRewards, PacketWriterDels.NetObject<AppearanceReward>.Writer);
        }

        public void Deserialise(PacketReader reader)
        {
            BlueprintReferenceIds = reader.ReadList(PacketReaderDels.String) ?? new List<string>();
            UpgradeComponentReferenceIds = reader.ReadList(PacketReaderDels.String) ?? new List<string>();
            AppearanceRewards = reader.ReadList(PacketReaderDels.NetObject<AppearanceReward>.Reader)
                                ?? new List<AppearanceReward>();
        }

        internal bool HasSupportedRewards
            => BlueprintReferenceIds.Count > 0 || UpgradeComponentReferenceIds.Count > 0 ||
               AppearanceRewards.Count > 0;

        internal sealed class AppearanceReward : INetObject
        {
            public string SlimeDefinitionReferenceId = string.Empty;
            public Il2Cpp.SlimeAppearance.AppearanceSaveSet SaveSet;

            public AppearanceReward() { }

            public void Serialise(PacketWriter writer)
            {
                writer.WriteString(SlimeDefinitionReferenceId);
                writer.WritePackedEnum(SaveSet);
            }

            public void Deserialise(PacketReader reader)
            {
                SlimeDefinitionReferenceId = reader.ReadPooledString() ?? string.Empty;
                SaveSet = reader.ReadPackedEnum<Il2Cpp.SlimeAppearance.AppearanceSaveSet>();
            }
        }
    }
}

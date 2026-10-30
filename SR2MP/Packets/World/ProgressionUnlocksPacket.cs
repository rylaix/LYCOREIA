using SR2MP.Packets.Utils;

namespace SR2MP.Packets.World;

/// <summary>
/// Unlock-only progression (gadget blueprints, slime appearances). Receivers apply the union and
/// never lock anything, so join snapshots and live additions use the same packet.
/// </summary>
internal sealed class ProgressionUnlocksPacket : IPacket
{
    public List<int> BlueprintIds = new();
    public List<AppearanceUnlock> Appearances = new();
    public List<string> OwnedPalettes = new();

    public PacketType Type => PacketType.ProgressionUnlocks;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.WorldState;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteList(BlueprintIds, PacketWriterDels.PackedInt);
        writer.WriteList(Appearances, PacketWriterDels.NetObject<AppearanceUnlock>.Writer);
        writer.WriteList(OwnedPalettes, PacketWriterDels.String);
    }

    public void Deserialise(PacketReader reader)
    {
        BlueprintIds = reader.ReadList(PacketReaderDels.PackedInt) ?? new List<int>();
        Appearances = reader.ReadList(PacketReaderDels.NetObject<AppearanceUnlock>.Reader) ?? new List<AppearanceUnlock>();
        OwnedPalettes = (reader.ReadList(PacketReaderDels.String) ?? new List<string?>()).Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).ToList();
    }

    internal sealed class AppearanceUnlock : INetObject
    {
        public int SlimeId;
        public Il2Cpp.SlimeAppearance.AppearanceSaveSet SaveSet;

        public AppearanceUnlock() { }

        public void Serialise(PacketWriter writer)
        {
            writer.WritePackedInt(SlimeId);
            writer.WritePackedEnum(SaveSet);
        }

        public void Deserialise(PacketReader reader)
        {
            SlimeId = reader.ReadPackedInt();
            SaveSet = reader.ReadPackedEnum<Il2Cpp.SlimeAppearance.AppearanceSaveSet>();
        }
    }
}

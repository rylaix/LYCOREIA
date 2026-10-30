using SR2MP.Packets.Utils;

namespace SR2MP.Packets.World;

internal sealed class SanctuaryThemeOwnershipPacket : IPacket
{
    public string ThemeReferenceId;
    public bool IsOwned;

    public PacketType Type => PacketType.SanctuaryThemeOwnership;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.WorldState;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteString(ThemeReferenceId);
        writer.WriteBool(IsOwned);
    }

    public void Deserialise(PacketReader reader)
    {
        ThemeReferenceId = reader.ReadPooledString()!;
        IsOwned = reader.ReadBool();
    }
}

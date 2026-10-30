using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Player;

/// <summary>One player's vacpack skin. The host stamps PlayerId from the sender and relays it.</summary>
internal sealed class PlayerVacPalettePacket : IPacket
{
    public string PlayerId = string.Empty;
    public string PaletteReferenceId = string.Empty;

    public PacketType Type => PacketType.PlayerVacPalette;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.WorldState;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteString(PlayerId);
        writer.WriteString(PaletteReferenceId);
    }

    public void Deserialise(PacketReader reader)
    {
        PlayerId = reader.ReadString() ?? string.Empty;
        PaletteReferenceId = reader.ReadString() ?? string.Empty;
    }
}

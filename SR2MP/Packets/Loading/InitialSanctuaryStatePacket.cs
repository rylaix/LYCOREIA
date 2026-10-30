using SR2MP.Packets.Utils;
using SR2MP.Packets.World;

namespace SR2MP.Packets.Loading;

internal sealed class InitialSanctuaryStatePacket : IPacket
{
    public List<SanctuaryRoomState> Rooms = new();
    public List<string?> OwnedThemeReferenceIds = new();

    public PacketType Type => PacketType.InitialSanctuaryState;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.WorldState;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteList(Rooms, PacketWriterDels.NetObject<SanctuaryRoomState>.Writer);
        writer.WriteList(OwnedThemeReferenceIds, PacketWriterDels.String);
    }

    public void Deserialise(PacketReader reader)
    {
        Rooms = reader.ReadList(PacketReaderDels.NetObject<SanctuaryRoomState>.Reader)!;
        OwnedThemeReferenceIds = reader.ReadList(PacketReaderDels.String)!;
    }
}

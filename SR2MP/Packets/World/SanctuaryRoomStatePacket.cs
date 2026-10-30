using SR2MP.Packets.Utils;

namespace SR2MP.Packets.World;

internal sealed class SanctuaryRoomStatePacket : IPacket
{
    public SanctuaryRoomState State = new();

    public PacketType Type => PacketType.SanctuaryRoomState;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.WorldState;

    public void Serialise(PacketWriter writer) => writer.WriteNetObject(State);

    public void Deserialise(PacketReader reader) => State = reader.ReadNetObject<SanctuaryRoomState>();
}

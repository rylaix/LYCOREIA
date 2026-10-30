using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Slime.Tangle;

internal sealed class TangleVineGrapplePacket : IPacket
{
    public ActorId TangleActorId;
    public ActorId TargetActorId;
    public bool Active;
    // Clients send zero; the host assigns the authoritative revision before relay.
    public long Revision;

    public PacketType Type => PacketType.TangleVineGrapple;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.ActorCritical;

    public void Serialise(PacketWriter writer)
    {
        writer.WritePackedLong(TangleActorId.Value);
        writer.WritePackedLong(TargetActorId.Value);
        writer.WriteBool(Active);
        writer.WritePackedLong(Revision);
    }

    public void Deserialise(PacketReader reader)
    {
        TangleActorId = new ActorId(reader.ReadPackedLong());
        TargetActorId = new ActorId(reader.ReadPackedLong());
        Active = reader.ReadBool();
        Revision = reader.ReadPackedLong();
    }
}

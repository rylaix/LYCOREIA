using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Slime.Stalk;

internal sealed class StalkConsumableTargetPacket : IPacket
{
    public ActorId SlimeActorId;
    // ActorId zero clears the current target.
    public ActorId TargetActorId;
    // Clients send zero; the host assigns the authoritative revision before relay.
    public long Revision;

    public PacketType Type => PacketType.StalkConsumableTarget;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.ActorCritical;

    public void Serialise(PacketWriter writer)
    {
        writer.WritePackedLong(SlimeActorId.Value);
        writer.WritePackedLong(TargetActorId.Value);
        writer.WritePackedLong(Revision);
    }

    public void Deserialise(PacketReader reader)
    {
        SlimeActorId = new ActorId(reader.ReadPackedLong());
        TargetActorId = new ActorId(reader.ReadPackedLong());
        Revision = reader.ReadPackedLong();
    }
}

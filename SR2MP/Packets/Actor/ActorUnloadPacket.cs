using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Actor;

// Not sure what to call it, 'unload' as in when the actor leaves render distance.
internal sealed class ActorUnloadPacket : IActorWorldRevisionPacket
{
    public ActorId ActorId;
    public string SenderId = string.Empty;
    public long WorldRevision { get; set; }
    public bool HasWorldRevision { get; set; }

    public PacketType Type => PacketType.ActorUnload;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.ActorCritical;

    public void Serialise(PacketWriter writer)
    {
        writer.WritePackedLong(ActorId.Value);
        writer.WriteString(SenderId);
        if (HasWorldRevision)
            writer.WriteLong(WorldRevision);
    }

    public void Deserialise(PacketReader reader)
    {
        ActorId = new ActorId(reader.ReadPackedLong());
        SenderId = reader.ReadPooledString() ?? string.Empty;
        if (reader.BytesRemaining >= sizeof(long))
        {
            WorldRevision = reader.ReadLong();
            HasWorldRevision = true;
        }
    }
}

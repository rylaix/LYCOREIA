using System.Runtime.InteropServices;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Packets.Utils;
using Unity.Mathematics;

namespace SR2MP.Packets.Actor;

[StructLayout(LayoutKind.Auto)]
internal struct ActorStatePacket : IActorWorldRevisionPacket
{
    public ActorId ActorId;
    public long WorldRevision { get; set; }
    public bool HasWorldRevision { get; set; }

    public float4 Emotions;
    public bool Sleeping;

    public double ResourceProgress;
    public ResourceCycle.State ResourceState;
    public float ResourceScale;

    public bool Invulnerable;
    public float InvulnerablePeriod;

    public ActorUpdateType UpdateType;

    public readonly PacketType Type => PacketType.ActorState;
    public readonly PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public readonly NetworkChannel Channel => NetworkChannel.ActorCritical;

    public readonly void Serialise(PacketWriter writer)
    {
        writer.WriteLong(ActorId.Value);
        writer.WriteEnum(UpdateType);

        switch (UpdateType)
        {
            case ActorUpdateType.Slime:
                writer.WriteFloat4(Emotions);
                writer.WriteBool(Sleeping);
                break;

            case ActorUpdateType.Resource:
                writer.WriteDouble(ResourceProgress);
                writer.WritePackedEnum(ResourceState);
                writer.WriteFloat(ResourceScale);
                break;

            case ActorUpdateType.Plort:
                writer.WriteBool(Invulnerable);
                writer.WriteFloat(InvulnerablePeriod);
                break;
        }

        if (HasWorldRevision)
            writer.WriteLong(WorldRevision);
    }

    public void Deserialise(PacketReader reader)
    {
        ActorId    = new ActorId(reader.ReadLong());
        UpdateType = reader.ReadEnum<ActorUpdateType>();

        switch (UpdateType)
        {
            case ActorUpdateType.Slime:
                Emotions = reader.ReadFloat4();
                Sleeping = reader.ReadBool();
                break;

            case ActorUpdateType.Resource:
                ResourceProgress = reader.ReadDouble();
                ResourceState    = reader.ReadPackedEnum<ResourceCycle.State>();
                ResourceScale    = reader.ReadFloat();
                break;

            case ActorUpdateType.Plort:
                Invulnerable       = reader.ReadBool();
                InvulnerablePeriod = reader.ReadFloat();
                break;
        }

        if (reader.BytesRemaining >= sizeof(long))
        {
            WorldRevision = reader.ReadLong();
            HasWorldRevision = true;
        }
    }
}

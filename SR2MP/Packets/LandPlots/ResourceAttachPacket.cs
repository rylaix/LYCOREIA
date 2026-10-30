using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Packets.Actor;
using SR2MP.Packets.Utils;

namespace SR2MP.Packets.LandPlots;

internal sealed class ResourceAttachPacket : IActorWorldRevisionPacket
{
    public ActorId ActorId;
    public long WorldRevision { get; set; }
    public bool HasWorldRevision { get; set; }
    public string PlotID;
    public int Joint;
    public Vector3 SpawnerID;

    public SpawnResourceModel Model;

    public PacketType Type => PacketType.ResourceAttach;
    public PacketReliability Reliability => PacketReliability.Reliable;
    public NetworkChannel Channel => NetworkChannel.Landplots;

    public void Serialise(PacketWriter writer)
    {
        writer.WritePackedLong(ActorId.Value);
        writer.WriteString(PlotID);
        writer.WriteInt(Joint);
        writer.WriteVector3(SpawnerID);

        writer.WriteDouble(Model.nextSpawnTime);
        writer.WriteFloat(Model.storedWater);
        writer.WritePackedBool(Model.nextSpawnRipens);
        writer.WritePackedBool(Model.wasPreviouslyPlanted);
        if (HasWorldRevision)
            writer.WriteLong(WorldRevision);
    }

    public void Deserialise(PacketReader reader)
    {
        ActorId = new ActorId(reader.ReadPackedLong());
        PlotID = reader.ReadPooledString()!;
        Joint = reader.ReadInt();
        SpawnerID = reader.ReadVector3();

        Model = new SpawnResourceModel
        {
            nextSpawnTime = reader.ReadDouble(),
            storedWater = reader.ReadFloat(),
            nextSpawnRipens = reader.ReadPackedBool(),
            wasPreviouslyPlanted = reader.ReadPackedBool()
        };

        if (reader.BytesRemaining >= sizeof(long))
        {
            WorldRevision = reader.ReadLong();
            HasWorldRevision = true;
        }
    }
}

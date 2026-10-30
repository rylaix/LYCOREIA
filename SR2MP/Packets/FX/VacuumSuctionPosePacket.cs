using SR2MP.Packets.Utils;

namespace SR2MP.Packets.FX;

/// <summary>Short lived nozzle pose updates while a peer is actively vacuuming.</summary>
internal sealed class VacuumSuctionPosePacket : IPacket
{
    internal string PlayerId = string.Empty;
    internal uint Sequence;
    internal Vector3 Position;
    internal Quaternion Rotation;

    public PacketType Type => PacketType.VacuumSuctionPose;
    public PacketReliability Reliability => PacketReliability.Unreliable;
    public NetworkChannel Channel => NetworkChannel.PlayerUpdate;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteStringWithoutSize(PlayerId);
        writer.WriteUInt(Sequence);
        writer.WriteVector3(Position);
        writer.WriteQuaternion(Rotation);
    }

    public void Deserialise(PacketReader reader)
    {
        PlayerId = reader.ReadPooledStringOfSize(16)!;
        Sequence = reader.ReadUInt();
        Position = reader.ReadVector3();
        Rotation = reader.ReadQuaternion();
    }
}

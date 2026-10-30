using System.Runtime.InteropServices;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Actor;

[StructLayout(LayoutKind.Auto)]
internal struct ActorUpdatePacket : IActorWorldRevisionPacket
{
    public ActorId ActorId;
    public long WorldRevision { get; set; }
    public bool HasWorldRevision { get; set; }

    /// <summary>
    /// Ownership epoch the sender held for this actor (see OwnershipEpochPolicy). Receivers
    /// drop updates whose epoch is older than the epoch they have stored.
    /// </summary>
    public uint Epoch;
    public bool HasEpoch;

    private const byte FlagWorldRevision = 1;
    private const byte FlagEpoch = 2;

    public Quaternion Rotation;
    public Vector3 Position;
    public Vector3 Velocity;

    public readonly PacketType Type => PacketType.ActorUpdate;
    public readonly PacketReliability Reliability => PacketReliability.Ordered;
    public readonly NetworkChannel Channel => NetworkChannel.ActorUpdate;

    public readonly void Serialise(PacketWriter writer)
    {
        writer.WriteLong(ActorId.Value);
        writer.WriteVector3(Position);
        writer.WriteQuaternion(Rotation);
        writer.WriteVector3(Velocity);
        // Presence flags make the two optional trailing fields unambiguous.
        var flags = (byte)((HasWorldRevision ? FlagWorldRevision : 0) | (HasEpoch ? FlagEpoch : 0));
        writer.WriteByte(flags);
        if (HasWorldRevision)
            writer.WriteLong(WorldRevision);
        if (HasEpoch)
            writer.WriteUInt(Epoch);
    }

    public void Deserialise(PacketReader reader)
    {
        ActorId  = new ActorId(reader.ReadLong());
        Position = reader.ReadVector3();
        Rotation = reader.ReadQuaternion();
        Velocity = reader.ReadVector3();
        if (reader.BytesRemaining < 1)
            return;

        var flags = reader.ReadByte();
        if ((flags & FlagWorldRevision) != 0 && reader.BytesRemaining >= sizeof(long))
        {
            WorldRevision = reader.ReadLong();
            HasWorldRevision = true;
        }
        if ((flags & FlagEpoch) != 0 && reader.BytesRemaining >= sizeof(uint))
        {
            Epoch = reader.ReadUInt();
            HasEpoch = true;
        }
    }

#if SR2MP_TEST
    internal static bool VerifySerializationRoundTrip()
    {
        foreach (var (withRevision, withEpoch) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            var expected = new ActorUpdatePacket
            {
                ActorId = new ActorId(4242),
                Position = new Vector3(1f, 2f, 3f),
                Rotation = Quaternion.identity,
                Velocity = new Vector3(4f, 5f, 6f),
                HasWorldRevision = withRevision,
                WorldRevision = withRevision ? 777L : 0L,
                HasEpoch = withEpoch,
                Epoch = withEpoch ? 9u : 0u
            };
            using var writer = PacketWriter.Borrow();
            expected.Serialise(writer);
            using var reader = PacketReader.Borrow(writer.ToSpan().ToArray());
            var actual = new ActorUpdatePacket();
            actual.Deserialise(reader);
            if (reader.BytesRemaining != 0 || actual.ActorId.Value != 4242 ||
                actual.Position != expected.Position || actual.Velocity != expected.Velocity ||
                actual.HasWorldRevision != withRevision || actual.WorldRevision != expected.WorldRevision ||
                actual.HasEpoch != withEpoch || actual.Epoch != expected.Epoch)
                return false;
        }
        return true;
    }
#endif
}

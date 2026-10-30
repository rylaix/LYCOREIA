using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Packets.Utils;
using Il2CppMonomiPark.SlimeRancher.Slime.Dervish;
using UnityEngine;

namespace SR2MP.Packets.Slime.Dervish;

internal sealed class DervishCyclonePacket : IPacket
{
    public ActorId ActorId;
    public bool Active;
    public byte Size;
    public Vector3 FloatDir;
    // Clients submit zero; the server assigns the authoritative per-actor revision.
    public long Revision;

    internal bool IsValid => ActorId.Value > 0 && Revision >= 0 &&
                             float.IsFinite(FloatDir.x) && float.IsFinite(FloatDir.y) &&
                             float.IsFinite(FloatDir.z) &&
                             (!Active || Enum.IsDefined(typeof(DervishSlimeSpin.CycloneSize), (int)Size));

    public PacketType Type => PacketType.DervishCyclone;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.ActorCritical;

    public void Serialise(PacketWriter writer)
    {
        if (!IsValid)
            throw new InvalidDataException("Invalid Dervish cyclone state.");

        writer.WritePackedLong(ActorId.Value);
        writer.WritePackedBool(Active);
        writer.WriteByte(Size);
        writer.WriteVector3(FloatDir);
        writer.WritePackedLong(Revision);
    }

    public void Deserialise(PacketReader reader)
    {
        ActorId = new ActorId(reader.ReadPackedLong());
        Active = reader.ReadPackedBool();
        Size = reader.ReadByte();
        FloatDir = reader.ReadVector3();
        Revision = reader.ReadPackedLong();

        if (!IsValid)
            throw new InvalidDataException("Invalid Dervish cyclone state.");
    }

#if SR2MP_TEST
    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new DervishCyclonePacket
        {
            ActorId = new ActorId(0x345),
            Active = true,
            Size = (byte)(int)DervishSlimeSpin.CycloneSize.MEDIUM,
            FloatDir = new Vector3(1.25f, -2.5f, 3.75f),
            Revision = 8
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();
        using var reader = PacketReader.Borrow(bytes);
        var actual = new DervishCyclonePacket();
        actual.Deserialise(reader);

        return reader.BytesRemaining == 0 && actual.IsValid &&
               actual.ActorId.Value == expected.ActorId.Value && actual.Active == expected.Active &&
               actual.Size == expected.Size && actual.FloatDir == expected.FloatDir &&
               actual.Revision == expected.Revision &&
               !new DervishCyclonePacket { ActorId = new ActorId(1), Active = true, Size = byte.MaxValue }.IsValid &&
               !new DervishCyclonePacket
               {
                   ActorId = new ActorId(1), Active = true,
                   Size = (byte)(int)DervishSlimeSpin.CycloneSize.SMALL,
                   FloatDir = new Vector3(float.NaN, 0f, 0f)
               }.IsValid;
    }
#endif
}

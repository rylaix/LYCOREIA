#if SR2MP_TEST
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Actor;

internal struct TestCaptureStatusRequestPacket : IPacket
{
    internal Guid ProbeId;
    internal int QueryId;
    internal long ActorId;

    public readonly PacketType Type => PacketType.TestCaptureStatusRequest;
    public readonly PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public readonly NetworkChannel Channel => NetworkChannel.Important;

    public readonly void Serialise(PacketWriter writer)
    {
        writer.WriteGuid(ProbeId);
        writer.WriteInt(QueryId);
        writer.WriteLong(ActorId);
    }

    public void Deserialise(PacketReader reader)
    {
        ProbeId = reader.ReadGuid();
        QueryId = reader.ReadInt();
        ActorId = reader.ReadLong();
    }

    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new TestCaptureStatusRequestPacket
        {
            ProbeId = Guid.Parse("17c642a1-4623-4fb0-9f5e-423cc5de75f1"),
            QueryId = 2,
            ActorId = 9876
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        using var reader = PacketReader.Borrow(writer.ToSpan().ToArray());
        var actual = new TestCaptureStatusRequestPacket();
        actual.Deserialise(reader);
        return reader.BytesRemaining == 0 && actual.ProbeId == expected.ProbeId &&
               actual.QueryId == expected.QueryId && actual.ActorId == expected.ActorId;
    }
}

internal struct TestCaptureStatusResultPacket : IPacket
{
    internal Guid ProbeId;
    internal int QueryId;
    internal long ActorId;
    internal bool HostActorPresent;
    internal bool HostModelPresent;

    public readonly PacketType Type => PacketType.TestCaptureStatusResult;
    public readonly PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public readonly NetworkChannel Channel => NetworkChannel.Important;

    public readonly void Serialise(PacketWriter writer)
    {
        writer.WriteGuid(ProbeId);
        writer.WriteInt(QueryId);
        writer.WriteLong(ActorId);
        writer.WriteBool(HostActorPresent);
        writer.WriteBool(HostModelPresent);
    }

    public void Deserialise(PacketReader reader)
    {
        ProbeId = reader.ReadGuid();
        QueryId = reader.ReadInt();
        ActorId = reader.ReadLong();
        HostActorPresent = reader.ReadBool();
        HostModelPresent = reader.ReadBool();
    }

    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new TestCaptureStatusResultPacket
        {
            ProbeId = Guid.Parse("34cb7ea3-d3a6-41e7-b7d4-9ef169c581df"),
            QueryId = 3,
            ActorId = 54321,
            HostActorPresent = false,
            HostModelPresent = false
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        using var reader = PacketReader.Borrow(writer.ToSpan().ToArray());
        var actual = new TestCaptureStatusResultPacket();
        actual.Deserialise(reader);
        return reader.BytesRemaining == 0 && actual.ProbeId == expected.ProbeId &&
               actual.QueryId == expected.QueryId && actual.ActorId == expected.ActorId &&
               actual.HostActorPresent == expected.HostActorPresent &&
               actual.HostModelPresent == expected.HostModelPresent;
    }
}
#endif

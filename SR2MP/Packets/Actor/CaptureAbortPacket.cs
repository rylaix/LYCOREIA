using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Actor;

/// <summary>
/// Releases a host-side capture reservation when an approved native action
/// definitively did not start or its outcome became uncertain.
/// </summary>
internal struct CaptureAbortPacket : IPacket
{
    public ActorId ActorId;
    public Guid CaptureRequestId;

    public PacketType Type => PacketType.CaptureAbort;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.ActorCritical;

    public void Serialise(PacketWriter writer)
    {
        writer.WritePackedLong(ActorId.Value);
        writer.WriteGuid(CaptureRequestId);
    }

    public void Deserialise(PacketReader reader)
    {
        ActorId = new ActorId(reader.ReadPackedLong());
        CaptureRequestId = reader.ReadGuid();
    }

#if SR2MP_TEST
    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new CaptureAbortPacket
        {
            ActorId = new ActorId(0x7fff1234),
            CaptureRequestId = Guid.Parse("dce4652e-66ca-4c1e-84e5-e14706786f90")
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        using var reader = PacketReader.Borrow(writer.ToSpan().ToArray());
        var actual = new CaptureAbortPacket();
        actual.Deserialise(reader);
        return reader.BytesRemaining == 0 &&
               actual.ActorId.Value == expected.ActorId.Value &&
               actual.CaptureRequestId == expected.CaptureRequestId;
    }
#endif
}

#if SR2MP_TEST
using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Actor;

internal struct TestCaptureTargetPacket : IPacket
{
    internal long ActorId;

    public readonly PacketType Type => PacketType.TestCaptureTarget;
    public readonly PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public readonly NetworkChannel Channel => NetworkChannel.Important;

    public readonly void Serialise(PacketWriter writer) => writer.WriteLong(ActorId);

    public void Deserialise(PacketReader reader) => ActorId = reader.ReadLong();

    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new TestCaptureTargetPacket { ActorId = 0xC9 };
        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        using var reader = PacketReader.Borrow(writer.ToSpan().ToArray());
        var actual = new TestCaptureTargetPacket();
        actual.Deserialise(reader);
        return reader.BytesRemaining == 0 && actual.ActorId == expected.ActorId;
    }
}
#endif

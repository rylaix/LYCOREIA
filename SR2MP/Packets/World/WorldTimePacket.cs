using SR2MP.Packets.Utils;

namespace SR2MP.Packets.World;

internal struct WorldTimePacket : IPacket
{
    private static long nextRevision;

    public double Time;
    public long Revision;

    public readonly PacketType Type => PacketType.WorldTime;
    public readonly PacketReliability Reliability => PacketReliability.Unreliable;
    public readonly NetworkChannel Channel => NetworkChannel.Weather;

    internal static long NextRevision() => Interlocked.Increment(ref nextRevision);

    public readonly void Serialise(PacketWriter writer)
    {
        writer.WritePackedLong(Revision);
        writer.WriteDouble(Time);
    }

    public void Deserialise(PacketReader reader)
    {
        Revision = reader.ReadPackedLong();
        Time = reader.ReadDouble();
    }

#if SR2MP_TEST
    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new WorldTimePacket { Revision = 9876, Time = 1234.5 };
        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        var actual = new WorldTimePacket();
        actual.Deserialise(reader);

        return reader.BytesRemaining == 0
               && actual.Revision == expected.Revision
               && actual.Time == expected.Time;
    }
#endif
}

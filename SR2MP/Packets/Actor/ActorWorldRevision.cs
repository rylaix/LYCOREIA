using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Actor;

/// <summary>
/// A host-stamped actor mutation. The revision is assigned at the host's send
/// boundary, giving InitialActors a watermark across otherwise independent UDP
/// packet-type/channel order streams.
/// </summary>
internal interface IActorWorldRevisionPacket : IPacket
{
    long WorldRevision { get; set; }
    bool HasWorldRevision { get; set; }
}

internal static class ActorWorldRevision
{
    private static long currentRevision;

    internal static long Current => Interlocked.Read(ref currentRevision);

    internal static T Stamp<T>(T packet) where T : IPacket
    {
        object boxed = packet!;
        if (boxed is not IActorWorldRevisionPacket revisioned)
            return packet;

        revisioned.WorldRevision = Interlocked.Increment(ref currentRevision);
        revisioned.HasWorldRevision = true;
        return (T)boxed;
    }

#if SR2MP_TEST
    internal static bool VerifyHostStampIsMonotonic()
    {
        var previous = Current;
        var first = Stamp(new ActorDestroyPacket { ActorId = default });
        var second = Stamp(new ActorDestroyPacket { ActorId = default });
        return first.HasWorldRevision && second.HasWorldRevision &&
               first.WorldRevision > previous && second.WorldRevision > first.WorldRevision;
    }
#endif
}

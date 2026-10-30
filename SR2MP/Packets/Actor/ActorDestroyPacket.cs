using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Actor;

internal struct ActorDestroyPacket : IActorWorldRevisionPacket
{
    private const string NoOwner = "SR2MP_NO_OWNER__";

    public ActorId ActorId;
    public string? OwnerId;
    public bool HasOwnerId;
    public long WorldRevision { get; set; }
    public bool HasWorldRevision { get; set; }

    public readonly PacketType Type => PacketType.ActorDestroy;
    public readonly PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public readonly NetworkChannel Channel => NetworkChannel.ActorCritical;

    public readonly void Serialise(PacketWriter writer)
    {
        writer.WritePackedLong(ActorId.Value);
        writer.WriteStringWithoutSize(string.IsNullOrEmpty(OwnerId) ? NoOwner : OwnerId);
        if (HasWorldRevision)
            writer.WriteLong(WorldRevision);
    }

    public void Deserialise(PacketReader reader)
    {
        ActorId = new ActorId(reader.ReadPackedLong());
        if (reader.BytesRemaining < 16)
            return;

        OwnerId = reader.ReadPooledStringOfSize(16)!;
        HasOwnerId = OwnerId != NoOwner;
        if (!HasOwnerId)
            OwnerId = string.Empty;

        if (reader.BytesRemaining >= sizeof(long))
        {
            WorldRevision = reader.ReadLong();
            HasWorldRevision = true;
        }
    }

#if SR2MP_TEST
    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new ActorDestroyPacket
        {
            ActorId = new ActorId(54321),
            OwnerId = "PLAYER_GUEST_123",
            WorldRevision = 77,
            HasWorldRevision = true
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        using var reader = PacketReader.Borrow(writer.ToSpan().ToArray());
        var actual = new ActorDestroyPacket();
        actual.Deserialise(reader);
        if (reader.BytesRemaining != 0 || actual.ActorId.Value != expected.ActorId.Value ||
            !actual.HasOwnerId || actual.OwnerId != expected.OwnerId ||
            !actual.HasWorldRevision || actual.WorldRevision != expected.WorldRevision)
            return false;

        // Old ActorDestroy packets contained only the actor id.
        using var legacyWriter = PacketWriter.Borrow();
        legacyWriter.WritePackedLong(expected.ActorId.Value);
        using var legacyReader = PacketReader.Borrow(legacyWriter.ToSpan().ToArray());
        var legacy = new ActorDestroyPacket();
        legacy.Deserialise(legacyReader);
        return legacyReader.BytesRemaining == 0 && legacy.ActorId.Value == expected.ActorId.Value &&
               !legacy.HasOwnerId && string.IsNullOrEmpty(legacy.OwnerId);
    }
#endif
}

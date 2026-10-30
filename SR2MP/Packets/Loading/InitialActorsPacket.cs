using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Loading;

internal partial class InitialActorsPacket : IPacket
{
    private static readonly ReadDel<ActorBase> ReadFunction = reader =>
    {
        var actorTypeEnum = reader.ReadEnum<ActorType>();
        var actorType = actorTypes![actorTypeEnum];
        var actor = (ActorBase)Activator.CreateInstance(actorType)!;

        actor.Deserialise(reader);

        return actor;
    };

    public double WorldTime;
    public long StartingActorID;
    public List<ActorBase> Actors;
    public bool HasOwnershipSnapshot;
    /// <summary>Owner plus ownership epoch of one actor in the join snapshot.</summary>
    internal readonly record struct OwnershipEntry(string OwnerId, uint Epoch);

    private static readonly WriteDel<OwnershipEntry> WriteOwnershipEntry = (writer, entry) =>
    {
        writer.WriteString(entry.OwnerId);
        writer.WritePackedUInt(entry.Epoch);
    };

    private static readonly ReadDel<OwnershipEntry> ReadOwnershipEntry = reader =>
        new OwnershipEntry(reader.ReadString() ?? string.Empty, reader.ReadPackedUInt());

    public Dictionary<long, OwnershipEntry> Ownership = new();
    public bool HasWorldRevision;
    public long WorldRevision;
    // Matches the ConnectionApprove that initiated this snapshot. Zero denotes
    // an older host that does not carry sync generations.
    public long SyncId;

    public PacketType Type => PacketType.InitialActors;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.ActorCritical;

    public void Serialise(PacketWriter writer)
    {
        writer.WritePackedLong(StartingActorID);
        writer.WriteDouble(WorldTime);
        writer.WriteList(Actors, PacketWriterDels.NetObject<ActorBase>.Writer);
        writer.WritePackedBool(HasOwnershipSnapshot);
        if (HasOwnershipSnapshot)
            writer.WriteDictionary(Ownership, PacketWriterDels.PackedLong, WriteOwnershipEntry);
        writer.WritePackedBool(HasWorldRevision);
        if (HasWorldRevision)
            writer.WriteLong(WorldRevision);
        if (SyncId > 0)
            writer.WriteLong(SyncId);
    }

    public void Deserialise(PacketReader reader)
    {
        StartingActorID = reader.ReadPackedLong();
        WorldTime = reader.ReadDouble();
        Actors = reader.ReadList(ReadFunction)!;

        // The trailing ownership map was added by the coop-anything fork. Older hosts
        // end the packet after Actors; mark their owner data as unknown so this client
        // will not claim actors with an empty compare-and-swap value.
        if (reader.BytesRemaining > 0)
        {
            HasOwnershipSnapshot = reader.ReadPackedBool();
            if (HasOwnershipSnapshot)
                Ownership = reader.ReadDictionary(PacketReaderDels.PackedLong, ReadOwnershipEntry)!;
        }

        // Host actor revisions form a barrier across the independent live packet
        // type/channel sequences. Older hosts simply omit this trailing marker.
        if (reader.BytesRemaining > 0)
        {
            HasWorldRevision = reader.ReadPackedBool();
            if (HasWorldRevision && reader.BytesRemaining >= sizeof(long))
                WorldRevision = reader.ReadLong();
            else
                HasWorldRevision = false;
        }

        // Optional trailing generation lets the actor barrier correlate packets
        // even when Important and ActorCritical channels deliver out of order.
        if (reader.BytesRemaining >= sizeof(long))
            SyncId = reader.ReadLong();
    }

#if SR2MP_TEST
    internal static bool VerifyWorldRevisionRoundTrip()
    {
        var expected = new InitialActorsPacket
        {
            StartingActorID = 1234,
            WorldTime = 56.5,
            Actors = new List<ActorBase>(),
            HasOwnershipSnapshot = true,
            Ownership = new Dictionary<long, OwnershipEntry> { [12] = new OwnershipEntry("PLAYER_HOST_12", 7) },
            HasWorldRevision = true,
            WorldRevision = 9876,
            SyncId = 42
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        using var reader = PacketReader.Borrow(writer.ToSpan().ToArray());
        var actual = new InitialActorsPacket();
        actual.Deserialise(reader);
        if (reader.BytesRemaining != 0 || actual.StartingActorID != expected.StartingActorID ||
            actual.WorldTime != expected.WorldTime || !actual.HasOwnershipSnapshot ||
            !actual.Ownership.TryGetValue(12, out var owner) || owner.OwnerId != "PLAYER_HOST_12" || owner.Epoch != 7 ||
            !actual.HasWorldRevision || actual.WorldRevision != expected.WorldRevision ||
            actual.SyncId != expected.SyncId)
            return false;

        // Older hosts omit the optional generation after the revision watermark.
        expected.SyncId = 0;
        using var legacyWriter = PacketWriter.Borrow();
        expected.Serialise(legacyWriter);
        using var legacyReader = PacketReader.Borrow(legacyWriter.ToSpan().ToArray());
        var legacy = new InitialActorsPacket();
        legacy.Deserialise(legacyReader);
        return legacyReader.BytesRemaining == 0 && legacy.SyncId == 0 && legacy.HasWorldRevision &&
               legacy.WorldRevision == expected.WorldRevision;
    }
#endif
}

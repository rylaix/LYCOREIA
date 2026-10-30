using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Actor;

internal sealed class ActorTransferPacket : IActorWorldRevisionPacket
{
    private const string NoOwner = "SR2MP_NO_OWNER__";

    public ActorId ActorId;
    public string OwnerId;

    /// <summary>
    /// The owner observed by the sender before this transfer. The optional trailing
    /// field lets the server reject delayed transfer requests from a former owner.
    /// Legacy packets omit the field and are handled with stricter server-side rules.
    /// </summary>
    public string PreviousOwnerId = string.Empty;
    public bool HasPreviousOwnerId;

    /// <summary>
    /// A preflight request and host approval for a pending vacuum capture.
    /// CaptureCommit marks the requester's report that the approved native action
    /// finished; only the host's committed response transfers ownership.
    /// </summary>
    public bool CaptureIntent;
    public bool CaptureApproved;
    public Guid CaptureRequestId;
    /// <summary>
    /// Second phase: the authenticated requester reports that the approved
    /// native capture operation completed. Ownership changes only after this.
    /// </summary>
    public bool CaptureCommit;
    public long WorldRevision { get; set; }
    public bool HasWorldRevision { get; set; }
    /// <summary>
    /// True when the approved native operation consumed the actor. The host then
    /// removes that exact actor as part of committing the transaction.
    /// </summary>
    public bool CaptureConsumesActor;

    /// <summary>
    /// Ownership epoch. Host-stamped on every ownership change it applies or repairs; on
    /// client requests it is the epoch the client believes current (host logs only).
    /// HasEpoch false = unversioned packet (legacy or cached copy), applied without ordering.
    /// </summary>
    public uint Epoch;
    public bool HasEpoch;

    /// <summary>
    /// Client to host: a claim of interest, never a transfer. The host arbiter decides; nothing is
    /// replied to an interest that loses. InterestDistance is the sender's distance to the actor.
    /// Encoded in bit 1 of the epoch flag byte with a trailing float, so older readers ignore it.
    /// </summary>
    public bool OwnershipInterest;
    public float InterestDistance;

    public PacketType Type => PacketType.ActorTransfer;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.ActorCritical;

    public void Serialise(PacketWriter writer)
    {
        writer.WritePackedLong(ActorId.Value);
        writer.WriteStringWithoutSize(string.IsNullOrEmpty(OwnerId) ? NoOwner : OwnerId);
        writer.WriteStringWithoutSize(string.IsNullOrEmpty(PreviousOwnerId) ? NoOwner : PreviousOwnerId);
        writer.WriteBool(CaptureIntent);
        writer.WriteBool(CaptureApproved);
        writer.WriteGuid(CaptureRequestId);
        writer.WriteBool(CaptureCommit);
        if (HasWorldRevision)
            writer.WriteLong(WorldRevision);
        writer.WriteBool(CaptureConsumesActor);
        // Explicit presence flag keeps the epoch separate from the optional revision above.
        writer.WriteByte((byte)((HasEpoch ? 1 : 0) | (OwnershipInterest ? 2 : 0)));
        if (HasEpoch)
            writer.WriteUInt(Epoch);
        if (OwnershipInterest)
            writer.WriteFloat(InterestDistance);
    }

    public void Deserialise(PacketReader reader)
    {
        ActorId = new ActorId(reader.ReadPackedLong());
        // Older builds encoded an empty no-owner value as zero bytes. Accept
        // that legacy terminal packet before reading the fixed-width fields
        // used by current builds, whose explicit sentinel keeps later fields
        // aligned.
        if (reader.BytesRemaining < 16)
        {
            OwnerId = string.Empty;
            return;
        }

        OwnerId = reader.ReadPooledStringOfSize(16)!;
        if (OwnerId == NoOwner)
            OwnerId = string.Empty;

        // ActorTransfer packets predating this field end immediately after OwnerId.
        // PacketReader/handler dispatch does not require the packet reader to be exhausted.
        if (reader.BytesRemaining >= 16)
        {
            PreviousOwnerId = reader.ReadPooledStringOfSize(16)!;
            HasPreviousOwnerId = true;

            if (PreviousOwnerId == NoOwner)
                PreviousOwnerId = string.Empty;
        }

        // Capture fields are an optional extension so legacy ActorTransfer
        // packets still decode. Only the server can set CaptureApproved.
        if (reader.BytesRemaining >= 1)
            CaptureIntent = reader.ReadBool();
        if (reader.BytesRemaining >= 1)
            CaptureApproved = reader.ReadBool();
        if (reader.BytesRemaining >= 16)
            CaptureRequestId = reader.ReadGuid();
        if (reader.BytesRemaining >= 1)
            CaptureCommit = reader.ReadBool();
        if (reader.BytesRemaining >= sizeof(long))
        {
            WorldRevision = reader.ReadLong();
            HasWorldRevision = true;
        }
        if (reader.BytesRemaining >= 1)
            CaptureConsumesActor = reader.ReadBool();
        if (reader.BytesRemaining >= 1)
        {
            var epochFlags = reader.ReadByte();
            if ((epochFlags & 1) != 0 && reader.BytesRemaining >= sizeof(uint))
            {
                Epoch = reader.ReadUInt();
                HasEpoch = true;
            }
            if ((epochFlags & 2) != 0 && reader.BytesRemaining >= sizeof(float))
            {
                OwnershipInterest = true;
                InterestDistance = reader.ReadFloat();
            }
        }
    }

#if SR2MP_TEST
    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new ActorTransferPacket
        {
            ActorId = new ActorId(12345),
            OwnerId = "PLAYER_NEW_12345",
            PreviousOwnerId = "PLAYER_OLD_12345",
            HasPreviousOwnerId = true,
            CaptureIntent = true,
            CaptureApproved = true,
            CaptureRequestId = Guid.Parse("dce4652e-66ca-4c1e-84e5-e14706786f90"),
            CaptureCommit = true,
            CaptureConsumesActor = true
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        var actual = new ActorTransferPacket();
        actual.Deserialise(reader);
        if (reader.BytesRemaining != 0 || actual.ActorId.Value != expected.ActorId.Value ||
            actual.OwnerId != expected.OwnerId || actual.PreviousOwnerId != expected.PreviousOwnerId ||
            !actual.HasPreviousOwnerId || !actual.CaptureIntent || !actual.CaptureApproved ||
            actual.CaptureRequestId != expected.CaptureRequestId || !actual.CaptureCommit ||
            !actual.CaptureConsumesActor)
            return false;

        // Packets with the ownership CAS but without capture metadata remain readable.
        using var legacyWriter = PacketWriter.Borrow();
        legacyWriter.WritePackedLong(expected.ActorId.Value);
        legacyWriter.WriteStringWithoutSize(expected.OwnerId);
        legacyWriter.WriteStringWithoutSize(expected.PreviousOwnerId);
        using var legacyReader = PacketReader.Borrow(legacyWriter.ToSpan().ToArray());
        var legacyWithCas = new ActorTransferPacket();
        legacyWithCas.Deserialise(legacyReader);
        if (legacyReader.BytesRemaining != 0 || !legacyWithCas.HasPreviousOwnerId ||
            !string.Equals(legacyWithCas.PreviousOwnerId, expected.PreviousOwnerId, StringComparison.Ordinal) ||
            legacyWithCas.CaptureIntent || legacyWithCas.CaptureApproved ||
            legacyWithCas.CaptureRequestId != Guid.Empty || legacyWithCas.CaptureCommit)
            return false;

        using var preparedWriter = PacketWriter.Borrow();
        preparedWriter.WritePackedLong(expected.ActorId.Value);
        preparedWriter.WriteStringWithoutSize(expected.OwnerId);
        preparedWriter.WriteStringWithoutSize(expected.PreviousOwnerId);
        preparedWriter.WriteBool(true);
        preparedWriter.WriteBool(true);
        preparedWriter.WriteGuid(expected.CaptureRequestId);
        using var preparedReader = PacketReader.Borrow(preparedWriter.ToSpan().ToArray());
        var prepared = new ActorTransferPacket();
        prepared.Deserialise(preparedReader);
        if (preparedReader.BytesRemaining != 0 || !prepared.CaptureIntent || !prepared.CaptureApproved ||
            prepared.CaptureCommit || prepared.CaptureRequestId != expected.CaptureRequestId ||
            prepared.CaptureConsumesActor)
            return false;

        // Revisioned packets from builds predating CaptureConsumesActor still
        // decode their revision and default this optional trailing flag to false.
        using var revisionedLegacyWriter = PacketWriter.Borrow();
        revisionedLegacyWriter.WritePackedLong(expected.ActorId.Value);
        revisionedLegacyWriter.WriteStringWithoutSize(expected.OwnerId);
        revisionedLegacyWriter.WriteStringWithoutSize(expected.PreviousOwnerId);
        revisionedLegacyWriter.WriteBool(true);
        revisionedLegacyWriter.WriteBool(true);
        revisionedLegacyWriter.WriteGuid(expected.CaptureRequestId);
        revisionedLegacyWriter.WriteBool(true);
        revisionedLegacyWriter.WriteLong(987654321L);
        using var revisionedLegacyReader = PacketReader.Borrow(revisionedLegacyWriter.ToSpan().ToArray());
        var revisionedLegacy = new ActorTransferPacket();
        revisionedLegacy.Deserialise(revisionedLegacyReader);
        if (revisionedLegacyReader.BytesRemaining != 0 || !revisionedLegacy.HasWorldRevision ||
            revisionedLegacy.WorldRevision != 987654321L || revisionedLegacy.CaptureConsumesActor)
            return false;

        var revisionedConsume = new ActorTransferPacket
        {
            ActorId = expected.ActorId,
            OwnerId = expected.OwnerId,
            PreviousOwnerId = expected.PreviousOwnerId,
            HasPreviousOwnerId = true,
            CaptureIntent = true,
            CaptureApproved = true,
            CaptureRequestId = expected.CaptureRequestId,
            CaptureCommit = true,
            WorldRevision = 987654321L,
            HasWorldRevision = true,
            CaptureConsumesActor = true
        };
        using var revisionedConsumeWriter = PacketWriter.Borrow();
        revisionedConsume.Serialise(revisionedConsumeWriter);
        using var revisionedConsumeReader = PacketReader.Borrow(revisionedConsumeWriter.ToSpan().ToArray());
        var revisionedConsumeActual = new ActorTransferPacket();
        revisionedConsumeActual.Deserialise(revisionedConsumeReader);
        if (revisionedConsumeReader.BytesRemaining != 0 || !revisionedConsumeActual.HasWorldRevision ||
            revisionedConsumeActual.WorldRevision != revisionedConsume.WorldRevision ||
            !revisionedConsumeActual.CaptureConsumesActor)
            return false;

        // A rejected preflight can have no known owner. Both fixed-width owner
        // fields must use the existing sentinel so the request id and flags keep
        // their offsets and old peers still parse the packet.
        var rejectedPrepare = new ActorTransferPacket
        {
            ActorId = expected.ActorId,
            OwnerId = string.Empty,
            PreviousOwnerId = string.Empty,
            HasPreviousOwnerId = true,
            CaptureIntent = true,
            CaptureApproved = false,
            CaptureRequestId = expected.CaptureRequestId,
            CaptureCommit = false
        };
        using var rejectedPrepareWriter = PacketWriter.Borrow();
        rejectedPrepare.Serialise(rejectedPrepareWriter);
        var rejectedPrepareBytes = rejectedPrepareWriter.ToSpan().ToArray();
        using var rejectedPrepareReader = PacketReader.Borrow(rejectedPrepareBytes);
        var rejectedPrepareRoundTrip = new ActorTransferPacket();
        rejectedPrepareRoundTrip.Deserialise(rejectedPrepareReader);
        if (rejectedPrepareReader.BytesRemaining != 0 ||
            rejectedPrepareBytes.Length != bytes.Length ||
            rejectedPrepareRoundTrip.OwnerId != string.Empty ||
            rejectedPrepareRoundTrip.PreviousOwnerId != string.Empty ||
            !rejectedPrepareRoundTrip.HasPreviousOwnerId ||
            !rejectedPrepareRoundTrip.CaptureIntent || rejectedPrepareRoundTrip.CaptureApproved ||
            rejectedPrepareRoundTrip.CaptureRequestId != expected.CaptureRequestId ||
            rejectedPrepareRoundTrip.CaptureCommit || rejectedPrepareRoundTrip.CaptureConsumesActor)
            return false;

        var rejectedCommit = new ActorTransferPacket
        {
            ActorId = expected.ActorId,
            OwnerId = string.Empty,
            PreviousOwnerId = string.Empty,
            HasPreviousOwnerId = true,
            CaptureIntent = true,
            CaptureApproved = false,
            CaptureRequestId = expected.CaptureRequestId,
            CaptureCommit = true
        };
        using var rejectedCommitWriter = PacketWriter.Borrow();
        rejectedCommit.Serialise(rejectedCommitWriter);
        var rejectedCommitBytes = rejectedCommitWriter.ToSpan().ToArray();
        using var rejectedCommitReader = PacketReader.Borrow(rejectedCommitBytes);
        var rejectedCommitRoundTrip = new ActorTransferPacket();
        rejectedCommitRoundTrip.Deserialise(rejectedCommitReader);
        if (rejectedCommitReader.BytesRemaining != 0 ||
            rejectedCommitBytes.Length != bytes.Length ||
            rejectedCommitRoundTrip.OwnerId != string.Empty ||
            rejectedCommitRoundTrip.PreviousOwnerId != string.Empty ||
            !rejectedCommitRoundTrip.HasPreviousOwnerId ||
            !rejectedCommitRoundTrip.CaptureIntent || rejectedCommitRoundTrip.CaptureApproved ||
            rejectedCommitRoundTrip.CaptureRequestId != expected.CaptureRequestId ||
            !rejectedCommitRoundTrip.CaptureCommit || rejectedCommitRoundTrip.CaptureConsumesActor)
            return false;

        // Epoch round trip, with and without the optional world revision.
        foreach (var withRevision in new[] { false, true })
        {
            var epoched = new ActorTransferPacket
            {
                ActorId = expected.ActorId,
                OwnerId = expected.OwnerId,
                PreviousOwnerId = expected.PreviousOwnerId,
                HasPreviousOwnerId = true,
                CaptureCommit = true,
                CaptureConsumesActor = true,
                HasWorldRevision = withRevision,
                WorldRevision = withRevision ? 55L : 0L,
                HasEpoch = true,
                Epoch = 4000000001u
            };
            using var epochWriter = PacketWriter.Borrow();
            epoched.Serialise(epochWriter);
            using var epochReader = PacketReader.Borrow(epochWriter.ToSpan().ToArray());
            var epochActual = new ActorTransferPacket();
            epochActual.Deserialise(epochReader);
            if (epochReader.BytesRemaining != 0 || !epochActual.HasEpoch || epochActual.Epoch != epoched.Epoch ||
                epochActual.HasWorldRevision != withRevision || epochActual.WorldRevision != epoched.WorldRevision ||
                !epochActual.CaptureConsumesActor || epochActual.OwnerId != epoched.OwnerId)
                return false;
        }

        // The pre-extension packet had only actor id and owner id.
        using var originalLegacyWriter = PacketWriter.Borrow();
        originalLegacyWriter.WritePackedLong(expected.ActorId.Value);
        originalLegacyWriter.WriteStringWithoutSize(expected.OwnerId);
        using var originalLegacyReader = PacketReader.Borrow(originalLegacyWriter.ToSpan().ToArray());
        var originalLegacy = new ActorTransferPacket();
        originalLegacy.Deserialise(originalLegacyReader);
        if (originalLegacyReader.BytesRemaining != 0 || originalLegacy.HasPreviousOwnerId ||
            originalLegacy.CaptureIntent || originalLegacy.CaptureApproved ||
            originalLegacy.CaptureRequestId != Guid.Empty || originalLegacy.CaptureCommit ||
            originalLegacy.CaptureConsumesActor)
            return false;

        // Before the sentinel was added, an empty OwnerId serialized to no bytes.
        using var emptyLegacyWriter = PacketWriter.Borrow();
        emptyLegacyWriter.WritePackedLong(expected.ActorId.Value);
        using var emptyLegacyReader = PacketReader.Borrow(emptyLegacyWriter.ToSpan().ToArray());
        var legacyNoOwner = new ActorTransferPacket();
        legacyNoOwner.Deserialise(emptyLegacyReader);
        return emptyLegacyReader.BytesRemaining == 0 && legacyNoOwner.OwnerId == string.Empty &&
               !legacyNoOwner.HasPreviousOwnerId && !legacyNoOwner.CaptureIntent &&
               !legacyNoOwner.CaptureApproved && legacyNoOwner.CaptureRequestId == Guid.Empty &&
               !legacyNoOwner.CaptureCommit && !legacyNoOwner.CaptureConsumesActor;
    }
#endif
}

using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Slime.Sloomber;

internal sealed class SloomberYawnPacket : IPacket
{
    public ActorId ActorId;
    public bool Active;
    // Host-only cancellation when the owner disappears before sending the normal end event.
    public bool ForceClear;
    // Clients submit zero; the server assigns the authoritative per-actor revision.
    public long Revision;

    internal bool IsValid => ActorId.Value > 0 && Revision >= 0 && (!ForceClear || !Active);

    public PacketType Type => PacketType.SloomberYawn;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.ActorCritical;

    public void Serialise(PacketWriter writer)
    {
        if (!IsValid)
            throw new InvalidDataException("Invalid Sloomber yawn state.");

        writer.WritePackedLong(ActorId.Value);
        writer.WriteBool(Active);
        writer.WriteBool(ForceClear);
        writer.WritePackedLong(Revision);
    }

    public void Deserialise(PacketReader reader)
    {
        ActorId = new ActorId(reader.ReadPackedLong());
        Active = reader.ReadBool();
        ForceClear = reader.ReadBool();
        Revision = reader.ReadPackedLong();

        if (!IsValid)
            throw new InvalidDataException("Invalid Sloomber yawn state.");
    }

#if SR2MP_TEST
    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new SloomberYawnPacket
        {
            ActorId = new ActorId(0x456),
            Active = true,
            Revision = 9
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();
        using var reader = PacketReader.Borrow(bytes);
        var actual = new SloomberYawnPacket();
        actual.Deserialise(reader);

        return reader.BytesRemaining == 0 && actual.IsValid && actual.ActorId.Value == expected.ActorId.Value &&
               actual.Active == expected.Active && actual.ForceClear == expected.ForceClear &&
               actual.Revision == expected.Revision &&
               !new SloomberYawnPacket { ActorId = new ActorId(1), Active = true, ForceClear = true }.IsValid;
    }
#endif
}

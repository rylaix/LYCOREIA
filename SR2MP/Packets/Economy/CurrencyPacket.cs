using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Economy;

internal struct CurrencyPacket : IPacket
{
    public CurrencyMessageKind Kind;
    public byte CurrencyType;
    public Guid RequestId;
    public ulong Revision;
    public int NewAmount;
    public int Delta;
    public int BeforeAmount;
    public int AfterAmount;
    public int RequestedAmount;
    public bool ShowUINotification;
    public int SourceIdent;

    public readonly PacketType Type => PacketType.CurrencyAdjust;
    public readonly PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public readonly NetworkChannel Channel => NetworkChannel.Economy;

    public readonly void Serialise(PacketWriter writer)
    {
        writer.WriteByte((byte)Kind);
        writer.WriteByte(CurrencyType);
        writer.WriteGuid(RequestId);
        writer.WriteULong(Revision);
        writer.WritePackedInt(NewAmount);
        writer.WritePackedInt(Delta);
        writer.WritePackedInt(BeforeAmount);
        writer.WritePackedInt(AfterAmount);
        writer.WritePackedInt(RequestedAmount);
        writer.WriteBool(ShowUINotification);
        writer.WritePackedInt(SourceIdent);
    }

    public void Deserialise(PacketReader reader)
    {
        Kind = (CurrencyMessageKind)reader.ReadByte();
        CurrencyType = reader.ReadByte();
        RequestId = reader.ReadGuid();
        Revision = reader.ReadULong();
        NewAmount = reader.ReadPackedInt();
        Delta = reader.ReadPackedInt();
        BeforeAmount = reader.ReadPackedInt();
        AfterAmount = reader.ReadPackedInt();
        RequestedAmount = reader.ReadPackedInt();
        ShowUINotification = reader.ReadBool();
        SourceIdent = reader.ReadPackedInt();
    }

    internal static bool VerifySerializationRoundTrip()
        => VerifyRoundTrip(new CurrencyPacket
           {
               Kind = CurrencyMessageKind.Request,
               CurrencyType = 2,
               RequestId = Guid.Parse("c738b92b-0991-4fb2-bff3-2e267d3f88f5"),
               Revision = 9,
               Delta = -25,
               BeforeAmount = 100,
               AfterAmount = 75,
               RequestedAmount = 25,
               ShowUINotification = true,
               SourceIdent = 33
           })
           && VerifyRoundTrip(new CurrencyPacket
           {
               Kind = CurrencyMessageKind.State,
               CurrencyType = 2,
               RequestId = Guid.Parse("3f9ca991-339f-44e9-b5ec-a0cd968f4600"),
               Revision = 10,
               NewAmount = 75,
               ShowUINotification = false,
               SourceIdent = -1
           });

    private static bool VerifyRoundTrip(CurrencyPacket expected)
    {
        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        var actual = new CurrencyPacket();
        actual.Deserialise(reader);

        return reader.BytesRemaining == 0
               && actual.Kind == expected.Kind
               && actual.CurrencyType == expected.CurrencyType
               && actual.RequestId == expected.RequestId
               && actual.Revision == expected.Revision
               && actual.NewAmount == expected.NewAmount
               && actual.Delta == expected.Delta
               && actual.BeforeAmount == expected.BeforeAmount
               && actual.AfterAmount == expected.AfterAmount
               && actual.RequestedAmount == expected.RequestedAmount
               && actual.ShowUINotification == expected.ShowUINotification
               && actual.SourceIdent == expected.SourceIdent;
    }
}

internal enum CurrencyMessageKind : byte
{
    Request = 0,
    State = 1
}

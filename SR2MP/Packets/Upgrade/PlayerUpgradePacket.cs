using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Upgrade;

internal struct PlayerUpgradePacket : IPacket
{
    public byte UpgradeID;
    public PlayerUpgradeMessageKind Kind;
    public sbyte Level;
    public ulong Revision;
    public Guid RequestId;

    public readonly PacketType Type => PacketType.PlayerUpgrade;
    public readonly PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public readonly NetworkChannel Channel => NetworkChannel.WorldState;

    public readonly void Serialise(PacketWriter writer)
    {
        writer.WriteByte(UpgradeID);
        writer.WriteByte((byte)Kind);
        writer.WriteSByte(Level);
        writer.WriteULong(Revision);
        writer.WriteGuid(RequestId);
    }

    public void Deserialise(PacketReader reader)
    {
        UpgradeID = reader.ReadByte();
        Kind = (PlayerUpgradeMessageKind)reader.ReadByte();
        Level = reader.ReadSByte();
        Revision = reader.ReadULong();
        RequestId = reader.ReadGuid();
    }

    internal static bool VerifySerializationRoundTrip()
        => VerifyRoundTrip(new PlayerUpgradePacket
           {
               UpgradeID = 23,
               Kind = PlayerUpgradeMessageKind.State,
               Level = 7,
               Revision = 0xFEDCBA9876543210UL,
               RequestId = Guid.Parse("b9e70ec0-3340-411c-81a7-5ee700a0dd77")
           })
           && VerifyRoundTrip(new PlayerUpgradePacket
           {
               UpgradeID = 255,
               Kind = PlayerUpgradeMessageKind.Rejected,
               RequestId = Guid.Parse("22547ab6-3f97-4b78-9d7e-873cbe68c055")
           });

    private static bool VerifyRoundTrip(PlayerUpgradePacket expected)
    {
        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        var actual = new PlayerUpgradePacket();
        actual.Deserialise(reader);

        return reader.BytesRemaining == 0
               && actual.UpgradeID == expected.UpgradeID
               && actual.Kind == expected.Kind
               && actual.Level == expected.Level
               && actual.Revision == expected.Revision
               && actual.RequestId == expected.RequestId;
    }
}

internal enum PlayerUpgradeMessageKind : byte
{
    Request = 0,
    State = 1,
    Rejected = 2
}

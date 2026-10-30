using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Loading;

internal sealed class InitialUpgradesPacket : IPacket
{
    public Dictionary<byte, sbyte> Upgrades = new();
    public Dictionary<byte, ulong> Revisions = new();

    public PacketType Type => PacketType.InitialPlayerUpgrades;
    public PacketReliability Reliability => PacketReliability.Reliable;
    public NetworkChannel Channel => NetworkChannel.WorldState;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteDictionary(Upgrades, PacketWriterDels.Byte, PacketWriterDels.SByte);
        writer.WriteDictionary(Revisions, PacketWriterDels.Byte, PacketWriterDels.ULong);
    }

    public void Deserialise(PacketReader reader)
    {
        Upgrades = reader.ReadDictionary(PacketReaderDels.Byte, PacketReaderDels.SByte)
                   ?? new Dictionary<byte, sbyte>();
        Revisions = reader.ReadDictionary(PacketReaderDels.Byte, PacketReaderDels.ULong)
                    ?? new Dictionary<byte, ulong>();
    }

    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new InitialUpgradesPacket
        {
            Upgrades = new Dictionary<byte, sbyte> { [2] = 1, [9] = 4 },
            Revisions = new Dictionary<byte, ulong> { [2] = 0, [9] = 0xFEDCBA9876543210UL }
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        var actual = new InitialUpgradesPacket();
        actual.Deserialise(reader);

        return reader.BytesRemaining == 0
               && actual.Upgrades.Count == expected.Upgrades.Count
               && expected.Upgrades.All(entry =>
                   actual.Upgrades.TryGetValue(entry.Key, out var value) && value == entry.Value)
               && actual.Revisions.Count == expected.Revisions.Count
               && expected.Revisions.All(entry =>
                   actual.Revisions.TryGetValue(entry.Key, out var value) && value == entry.Value);
    }
}

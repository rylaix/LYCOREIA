// todo
// what is this even supposed to be?
// It's implemented now!

using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Upgrade;

internal sealed class UpgradeComponentPacket : IPacket
{
    public string ComponentId;
    public byte Count;

    public PacketType Type => PacketType.ComponentAdd;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.Ammo;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteString(ComponentId);
        writer.WriteByte(Count);
    }

    public void Deserialise(PacketReader reader)
    {
        ComponentId = reader.ReadPooledString()!;
        Count = reader.ReadByte();
    }

    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new UpgradeComponentPacket { ComponentId = "drone-core", Count = 7 };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        var actual = new UpgradeComponentPacket();
        actual.Deserialise(reader);

        return reader.BytesRemaining == 0 && actual.ComponentId == expected.ComponentId &&
               actual.Count == expected.Count;
    }
}

using SR2MP.Packets.Utils;

namespace SR2MP.Packets.GordoSlime;

internal sealed class GordoSlimeSeenPacket : IPacket
{
    public string ID;
    public int EatenCount;
    public int RequiredFoodCount;
    public int GordoSlimeType;

    public PacketType Type => PacketType.GordoSlimeSeen;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.WorldState;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteString(ID);
        writer.WritePackedInt(EatenCount);
        writer.WritePackedInt(RequiredFoodCount);
        writer.WritePackedInt(GordoSlimeType);
    }

    public void Deserialise(PacketReader reader)
    {
        ID = reader.ReadPooledString()!;
        EatenCount = reader.ReadPackedInt();
        RequiredFoodCount = reader.ReadPackedInt();
        GordoSlimeType = reader.ReadPackedInt();
    }
}

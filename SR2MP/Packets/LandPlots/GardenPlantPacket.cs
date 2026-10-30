using SR2MP.Packets.Utils;

namespace SR2MP.Packets.LandPlots;

internal sealed class GardenPlantPacket : IPacket
{
    public string ID;
    public string GardenId;
    public int ActorType;
    public long Revision;

    public PacketType Type => PacketType.GardenPlant;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.Landplots;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteString(ID);
        writer.WriteString(GardenId);
        writer.WritePackedInt(ActorType);
        writer.WriteLong(Revision);
    }

    public void Deserialise(PacketReader reader)
    {
        ID = reader.ReadPooledString()!;
        GardenId = reader.ReadPooledString()!;
        ActorType = reader.ReadPackedInt();
        Revision = reader.ReadLong();
    }

#if SR2MP_TEST
    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new GardenPlantPacket
        {
            ID = "plot-a",
            GardenId = "plot-a_0",
            ActorType = 42,
            Revision = 23
        };

        var emptyCropCorrection = new GardenPlantPacket
        {
            ID = "plot-b",
            GardenId = "plot-b_0",
            ActorType = 9,
            Revision = 24
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        emptyCropCorrection.Serialise(writer);
        using var reader = PacketReader.Borrow(writer.ToSpan().ToArray());
        var actual = new GardenPlantPacket();
        actual.Deserialise(reader);
        var actualCorrection = new GardenPlantPacket();
        actualCorrection.Deserialise(reader);

        return reader.BytesRemaining == 0 && actual.ID == expected.ID &&
               actual.GardenId == expected.GardenId && actual.ActorType == expected.ActorType &&
               actual.Revision == expected.Revision && actualCorrection.ID == emptyCropCorrection.ID &&
               actualCorrection.GardenId == emptyCropCorrection.GardenId &&
               actualCorrection.ActorType == emptyCropCorrection.ActorType &&
               actualCorrection.Revision == emptyCropCorrection.Revision;
    }
#endif
}

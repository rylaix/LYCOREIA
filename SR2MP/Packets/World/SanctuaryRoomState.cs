using SR2MP.Packets.Utils;

namespace SR2MP.Packets.World;

internal sealed class SanctuaryRoomState : INetObject
{
    internal sealed class PreplacedGadget : INetObject
    {
        public string Id;
        public long ActorId;

        public void Serialise(PacketWriter writer)
        {
            writer.WriteString(Id);
            writer.WriteLong(ActorId);
        }

        public void Deserialise(PacketReader reader)
        {
            Id = reader.ReadPooledString()!;
            ActorId = reader.ReadLong();
        }
    }

    public string Id;
    public string? ThemeReferenceId;
    public List<PreplacedGadget> PreplacedGadgets = new();

    public void Serialise(PacketWriter writer)
    {
        writer.WriteString(Id);
        writer.WriteString(ThemeReferenceId);
        writer.WriteList(PreplacedGadgets, PacketWriterDels.NetObject<PreplacedGadget>.Writer);
    }

    public void Deserialise(PacketReader reader)
    {
        Id = reader.ReadPooledString()!;
        ThemeReferenceId = reader.ReadPooledString();
        PreplacedGadgets = reader.ReadList(PacketReaderDels.NetObject<PreplacedGadget>.Reader)!;
    }
}

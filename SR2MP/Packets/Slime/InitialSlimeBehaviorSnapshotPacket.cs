using SR2MP.Packets.Utils;
using Il2CppMonomiPark.SlimeRancher.Slime.Dervish;

namespace SR2MP.Packets.Slime;

internal sealed class InitialSlimeBehaviorSnapshotPacket : IPacket
{
    internal enum BehaviorKind : byte
    {
        StalkTarget = 1,
        TangleGrapple = 2,
        DervishCyclone = 3,
        SloomberYawn = 4
    }

    internal sealed class Entry : INetObject
    {
        public BehaviorKind Kind;
        public long ActorId;
        public long TargetActorId;
        public long Revision;
        public bool Active;
        public byte Size;
        public Vector3 FloatDir;

        public bool IsValid => Kind switch
        {
            BehaviorKind.StalkTarget => ActorId > 0 && TargetActorId >= 0 && Revision > 0 &&
                                        Active == (TargetActorId != 0),
            BehaviorKind.TangleGrapple => ActorId > 0 && TargetActorId >= 0 && Revision > 0 &&
                                           (Active ? TargetActorId > 0 : TargetActorId == 0),
            BehaviorKind.DervishCyclone => ActorId > 0 && TargetActorId == 0 && Revision > 0 &&
                                           float.IsFinite(FloatDir.x) && float.IsFinite(FloatDir.y) &&
                                           float.IsFinite(FloatDir.z) &&
                                           (!Active || Enum.IsDefined(typeof(DervishSlimeSpin.CycloneSize), (int)Size)),
            BehaviorKind.SloomberYawn => ActorId > 0 && TargetActorId == 0 && Revision > 0,
            _ => false
        };

        public void Serialise(PacketWriter writer)
        {
            if (!IsValid)
                throw new InvalidDataException("Invalid slime behavior snapshot entry.");

            writer.WriteEnum(Kind);
            writer.WritePackedLong(ActorId);
            writer.WritePackedLong(TargetActorId);
            writer.WritePackedLong(Revision);
            writer.WriteBool(Active);
            if (Kind == BehaviorKind.DervishCyclone)
            {
                writer.WriteByte(Size);
                writer.WriteVector3(FloatDir);
            }
        }

        public void Deserialise(PacketReader reader)
        {
            Kind = reader.ReadEnum<BehaviorKind>();
            ActorId = reader.ReadPackedLong();
            TargetActorId = reader.ReadPackedLong();
            Revision = reader.ReadPackedLong();
            Active = reader.ReadBool();
            if (Kind == BehaviorKind.DervishCyclone)
            {
                Size = reader.ReadByte();
                FloatDir = reader.ReadVector3();
            }

            if (!IsValid)
                throw new InvalidDataException("Invalid slime behavior snapshot entry.");
        }
    }

    internal const int MaxEntries = 4096;
    private const int MinimumEntryBytes = 5;

    public List<Entry> Entries = new();

    public PacketType Type => PacketType.InitialSlimeBehaviorSnapshot;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.ActorCritical;

    public void Serialise(PacketWriter writer)
    {
        if (Entries == null || Entries.Count > MaxEntries || !HasUniqueValidEntries(Entries))
            throw new InvalidDataException("Invalid or oversized slime behavior snapshot.");

        writer.WriteUShort((ushort)Entries.Count);
        foreach (var entry in Entries)
            entry.Serialise(writer);
    }

    public void Deserialise(PacketReader reader)
    {
        var count = reader.ReadUShort();
        if (count > MaxEntries || (long)count * MinimumEntryBytes > reader.BytesRemaining)
            throw new InvalidDataException($"Invalid slime behavior snapshot entry count: {count}.");

        var entries = new List<Entry>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = new Entry();
            entry.Deserialise(reader);
            entries.Add(entry);
        }

        if (!HasUniqueValidEntries(entries))
            throw new InvalidDataException("Duplicate slime behavior state in snapshot.");

        Entries = entries;
    }

    internal static bool HasUniqueValidEntries(IReadOnlyCollection<Entry>? entries)
    {
        if (entries == null || entries.Count > MaxEntries)
            return false;

        var keys = new HashSet<(BehaviorKind Kind, long ActorId)>();
        foreach (var entry in entries)
        {
            if (entry == null || !entry.IsValid || !keys.Add((entry.Kind, entry.ActorId)))
                return false;
        }

        return true;
    }

    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new InitialSlimeBehaviorSnapshotPacket
        {
            Entries = new List<Entry>
            {
                new()
                {
                    Kind = BehaviorKind.StalkTarget,
                    ActorId = 0x120,
                    TargetActorId = 0x230,
                    Revision = 4,
                    Active = true
                },
                new()
                {
                    Kind = BehaviorKind.StalkTarget,
                    ActorId = 0x121,
                    TargetActorId = 0,
                    Revision = 2,
                    Active = false
                },
                new()
                {
                    Kind = BehaviorKind.TangleGrapple,
                    ActorId = 0x130,
                    TargetActorId = 0x231,
                    Revision = 7,
                    Active = true
                },
                new()
                {
                    Kind = BehaviorKind.TangleGrapple,
                    ActorId = 0x131,
                    TargetActorId = 0,
                    Revision = 1,
                    Active = false
                },
                new()
                {
                    Kind = BehaviorKind.DervishCyclone,
                    ActorId = 0x140,
                    TargetActorId = 0,
                    Revision = 3,
                    Active = true,
                    Size = (byte)(int)DervishSlimeSpin.CycloneSize.LARGE,
                    FloatDir = new Vector3(-1f, 0.5f, 2f)
                },
                new()
                {
                    Kind = BehaviorKind.SloomberYawn,
                    ActorId = 0x150,
                    TargetActorId = 0,
                    Revision = 1,
                    Active = true
                },
                new()
                {
                    Kind = BehaviorKind.SloomberYawn,
                    ActorId = 0x151,
                    TargetActorId = 0,
                    Revision = 2,
                    Active = false
                }
            }
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        var actual = new InitialSlimeBehaviorSnapshotPacket();
        actual.Deserialise(reader);

        return reader.BytesRemaining == 0 && actual.Entries.Count == expected.Entries.Count &&
               actual.Entries.Zip(expected.Entries).All(pair =>
                   pair.First.Kind == pair.Second.Kind &&
                   pair.First.ActorId == pair.Second.ActorId &&
                   pair.First.TargetActorId == pair.Second.TargetActorId &&
                   pair.First.Revision == pair.Second.Revision &&
                   pair.First.Active == pair.Second.Active && pair.First.Size == pair.Second.Size &&
                   pair.First.FloatDir == pair.Second.FloatDir);
    }
}

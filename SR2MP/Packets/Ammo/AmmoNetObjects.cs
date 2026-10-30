using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Player;
using Il2CppMonomiPark.UnitPropertySystem;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Packets.Ammo;

internal sealed class NetworkAmmo : INetObject
{
    public Dictionary<int, NetworkAmmoSlot> AmmoSlots = new();

    public void Serialise(PacketWriter writer)
    {
        writer.WriteDictionary(AmmoSlots, PacketWriterDels.PackedInt, PacketWriterDels.NetObject<NetworkAmmoSlot>.Writer);
    }

    public void Deserialise(PacketReader reader)
    {
        AmmoSlots = reader.ReadDictionary(PacketReaderDels.PackedInt, PacketReaderDels.NetObject<NetworkAmmoSlot>.Reader)!;
    }

    public AmmoSlotManager ToGameAmmo()
    {
        var definitions = AmmoSlots.Values.ToList()
            .ConvertAll(input => NetworkAmmoManager.GetSlotDefinition(input.SlotDefinition)).ToArray();
        var ammo = new AmmoSlotManager(definitions);
        ammo.InitModel(new AmmoModel(null));
        ammo.SetModel(new AmmoModel(null));

        ammo._ammoModel.Slots = new Il2CppReferenceArray<AmmoSlot>(
            Array.ConvertAll(AmmoSlots.Values.ToArray(), input => input.ToGameAmmoSlot()));

        return ammo;
    }
}

internal struct NetworkAmmoSlot : INetObject
{
    public int Identifiable;
    public int Count;
    public int MaxCount;
    public ushort SlotDefinition;
    public bool Radiant;

    public readonly void Serialise(PacketWriter writer)
    {
        writer.WritePackedInt(Identifiable);
        writer.WritePackedInt(Count);
        writer.WritePackedInt(MaxCount);
        writer.WriteUShort(SlotDefinition);
        writer.WriteBool(Radiant);
    }

    public void Deserialise(PacketReader reader)
    {
        Identifiable = reader.ReadPackedInt();
        Count = reader.ReadPackedInt();
        MaxCount = reader.ReadPackedInt();
        SlotDefinition = reader.ReadUShort();
        Radiant = reader.ReadBool();
    }

    public readonly AmmoSlot ToGameAmmoSlot() => new()
    {
        _count = Count,
        _id = ActorManager.ActorTypes[Identifiable],
        Radiant = this.Radiant,
        // _isUnlockedValue = new NullableFloatProperty(1),
        _maxCountValue = new NullableFloatProperty((float)MaxCount),
    };

#if SR2MP_TEST
    internal static bool VerifyRadiantSerializationRoundTrip()
    {
        foreach (var radiant in new[] { false, true })
        {
            var expected = new NetworkAmmoSlot
            {
                Identifiable = 0x1357,
                Count = 3,
                MaxCount = 5,
                SlotDefinition = 0x6B4,
                Radiant = radiant
            };

            using var writer = PacketWriter.Borrow();
            expected.Serialise(writer);
            var bytes = writer.ToSpan().ToArray();

            using var reader = PacketReader.Borrow(bytes);
            var actual = new NetworkAmmoSlot();
            actual.Deserialise(reader);

            if (actual.Identifiable != expected.Identifiable ||
                actual.Count != expected.Count ||
                actual.MaxCount != expected.MaxCount ||
                actual.SlotDefinition != expected.SlotDefinition ||
                actual.Radiant != expected.Radiant ||
                reader.BytesRemaining != 0)
                return false;
        }

        return true;
    }
#endif
}

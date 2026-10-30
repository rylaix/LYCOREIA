using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Ammo;

/// <summary>
/// A host-authored complete storage state used to reconcile a sender after every
/// accepted silo mutation and after any mutation the host refuses or applies partially.
/// </summary>
internal sealed class AmmoStorageSnapshotPacket : IPacket
{
    public string? ID;
    public NetworkAmmo Ammo = new();

    public PacketType Type => PacketType.AmmoStorageSnapshot;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.Ammo;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteString(ID);
        writer.WriteNetObject(Ammo);
    }

    public void Deserialise(PacketReader reader)
    {
        ID = reader.ReadPooledString();
        Ammo = reader.ReadNetObject<NetworkAmmo>();
    }

    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new AmmoStorageSnapshotPacket
        {
            ID = "plot-silo-test",
            Ammo = new NetworkAmmo
            {
                AmmoSlots = new Dictionary<int, NetworkAmmoSlot>
                {
                    [0] = new() { Identifiable = 41, Count = 3, MaxCount = 100, SlotDefinition = 12, Radiant = true },
                    [1] = new() { Identifiable = -1, Count = 0, MaxCount = 100, SlotDefinition = 13, Radiant = false }
                }
            }
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        using var reader = PacketReader.Borrow(writer.ToSpan().ToArray());
        var actual = new AmmoStorageSnapshotPacket();
        actual.Deserialise(reader);

        return reader.BytesRemaining == 0 && actual.ID == expected.ID &&
               actual.Ammo.AmmoSlots.Count == expected.Ammo.AmmoSlots.Count &&
               actual.Ammo.AmmoSlots.All(pair => expected.Ammo.AmmoSlots.TryGetValue(pair.Key, out var slot) &&
                   pair.Value.Identifiable == slot.Identifiable && pair.Value.Count == slot.Count &&
                   pair.Value.MaxCount == slot.MaxCount && pair.Value.SlotDefinition == slot.SlotDefinition &&
                   pair.Value.Radiant == slot.Radiant);
    }
}

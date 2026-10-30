using System.Net;
using Il2CppMonomiPark.SlimeRancher.Player;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Ammo;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Ammo;

[PacketHandler((byte)PacketType.AmmoAddToSlot)]
internal sealed class AmmoAddToSlotHandler : BasePacketHandler<AmmoAddToSlotPacket>
{
    protected override bool Handle(AmmoAddToSlotPacket packet, IPEndPoint? endpoint)
    {
        var ammo = NetworkAmmoManager.GetAmmo(packet.ID);

        if (ammo == null) return false;

        if (packet.Count <= 0 || !ActorManager.ActorTypes.TryGetValue(packet.Identifiable, out var ident) || ident == null)
        {
            SrLogger.LogWarning($"AmmoAddToSlot: invalid count or unknown identifiable type {packet.Identifiable}");
            NetworkAmmoManager.SendCanonicalSnapshot(endpoint, packet.ID, ammo);
            return false;
        }

        if (packet.SlotIndex < 0 || ammo.Slots == null || packet.SlotIndex >= ammo.Slots.Count)
        {
            SrLogger.LogWarning($"AmmoAddToSlot: slot {packet.SlotIndex} out of range for '{packet.ID}' ({ammo.Slots?.Count ?? 0} slots)");
            NetworkAmmoManager.SendCanonicalSnapshot(endpoint, packet.ID, ammo);
            return false;
        }

        var slot = ammo.Slots[packet.SlotIndex];
        if (slot == null)
        {
            NetworkAmmoManager.SendCanonicalSnapshot(endpoint, packet.ID, ammo);
            return false;
        }

        var before = slot.Count;

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            var metadata = new AmmoSlot.AmmoMetadata(ident) { Radiant = packet.Radiant };
            ammo.MaybeAddToSpecificSlot(metadata, packet.SlotIndex, packet.Count, false);
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        var addedCount = slot.Count - before;
        if (addedCount <= 0)
        {
            NetworkAmmoManager.SendCanonicalSnapshot(endpoint, packet.ID, ammo);
            return false;
        }

        Logger.LogDiagnostic("inventory", "ammo-added-to-slot",
            $"container={packet.ID}; item={packet.Identifiable}; slot={packet.SlotIndex}; requested={packet.Count}; " +
            $"before={before}; after={slot.Count}; radiant={packet.Radiant}");

        var snapshotBroadcast = AmmoMutationSnapshotSync.BroadcastCanonicalSnapshot(packet.ID, ammo);
        if (!snapshotBroadcast)
        {
            NetworkAmmoManager.SendCanonicalSnapshot(endpoint, packet.ID, ammo);
            packet.Count = addedCount;
        }

        return AmmoMutationSnapshotPolicy.ShouldRelayRelativeDelta(
            mutationAccepted: true, snapshotBroadcast: snapshotBroadcast);
    }
}

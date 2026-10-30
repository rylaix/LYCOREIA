using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Ammo;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Ammo;

[PacketHandler((byte)PacketType.AmmoDecrement)]
internal sealed class AmmoDecrementHandler : BasePacketHandler<AmmoDecrementPacket>
{
    protected override bool Handle(AmmoDecrementPacket packet, IPEndPoint? endpoint)
    {
        var ammo = NetworkAmmoManager.GetAmmo(packet.ID);

        if (ammo == null)
            return false;

        if (packet.Count <= 0 ||
            !AmmoMutationSnapshotPolicy.IsValidSlotIndex(ammo.Slots != null, packet.SlotIndex, ammo.Slots?.Count ?? 0))
        {
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
            ammo.Decrement(packet.SlotIndex, packet.Count);
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        var decrementedCount = before - slot.Count;
        if (decrementedCount <= 0)
        {
            NetworkAmmoManager.SendCanonicalSnapshot(endpoint, packet.ID, ammo);
            return false;
        }

        Logger.LogDiagnostic("inventory", "ammo-decremented",
            $"container={packet.ID}; slot={packet.SlotIndex}; requested={packet.Count}; " +
            $"before={before}; after={slot.Count}; actual={decrementedCount}");

        // Broadcast complete host state after the serialized commit. If the
        // snapshot send fails, fall back to the bounded actual delta and repair
        // the sender directly.
        var snapshotBroadcast = AmmoMutationSnapshotSync.BroadcastCanonicalSnapshot(packet.ID, ammo);
        if (!snapshotBroadcast)
        {
            NetworkAmmoManager.SendCanonicalSnapshot(endpoint, packet.ID, ammo);
            packet.Count = decrementedCount;
        }

#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_AMMO") == "1")
            SrLogger.LogMessage($"[SR2MP_TEST] received AmmoDecrement id={packet.ID} slot={packet.SlotIndex} count={packet.Count} before={before} after={slot?.Count ?? -1}");
#endif

        return AmmoMutationSnapshotPolicy.ShouldRelayRelativeDelta(
            mutationAccepted: true, snapshotBroadcast: snapshotBroadcast);
    }
}

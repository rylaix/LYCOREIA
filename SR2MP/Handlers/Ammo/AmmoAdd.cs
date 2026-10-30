using System.Net;
using Il2CppMonomiPark.SlimeRancher.Player;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Ammo;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Ammo;

[PacketHandler((byte)PacketType.AmmoAdd)]
internal sealed class AmmoAddHandler : BasePacketHandler<AmmoAddPacket>
{
    protected override bool Handle(AmmoAddPacket packet, IPEndPoint? endpoint)
    {
        var ammo = NetworkAmmoManager.GetAmmo(packet.ID);

        if (ammo == null) return false;

        if (packet.Count <= 0 || !ActorManager.ActorTypes.TryGetValue(packet.Identifiable, out var ident) || ident == null)
        {
            SrLogger.LogWarning($"AmmoAdd: invalid count or unknown identifiable type {packet.Identifiable}");
            NetworkAmmoManager.SendCanonicalSnapshot(endpoint, packet.ID, ammo);
            return false;
        }
        
        var slotIndex = ammo.GetNextSlot(ident);
        if (slotIndex == -1)
        {
            NetworkAmmoManager.SendCanonicalSnapshot(endpoint, packet.ID, ammo);
            return false;
        }

        var slot = ammo.Slots[slotIndex];
        var before = slot?.Count ?? 0;

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            var metadata = new AmmoSlot.AmmoMetadata(ident) { Radiant = packet.Radiant };
            ammo.MaybeAddToSpecificSlot(metadata, slotIndex, packet.Count, false);
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        var addedCount = (slot?.Count ?? before) - before;
        if (addedCount <= 0)
        {
            NetworkAmmoManager.SendCanonicalSnapshot(endpoint, packet.ID, ammo);
            return false;
        }

        Logger.LogDiagnostic("inventory", "ammo-added",
            $"container={packet.ID}; item={packet.Identifiable}; slot={slotIndex}; requested={packet.Count}; " +
            $"before={before}; after={slot?.Count ?? before}; radiant={packet.Radiant}");

        // The host serializes this request with all others. Broadcast the complete
        // post-commit state so every peer converges even if their prior counts differ.
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

using Il2CppMonomiPark.SlimeRancher.Player;
using SR2MP.Packets.Ammo;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Ammo;

/// <summary>Snapshot-first convergence for host-serialized silo mutations.</summary>
internal static class AmmoMutationSnapshotSync
{
    internal static bool BroadcastCanonicalSnapshot(string? storageId, AmmoSlotManager ammo)
    {
        if (!Main.Server.IsRunning || string.IsNullOrWhiteSpace(storageId) || ammo == null)
            return false;

        try
        {
            Main.Server.SendToAll(new AmmoStorageSnapshotPacket
            {
                ID = storageId,
                Ammo = NetworkAmmoManager.CreateCanonicalSnapshot(ammo)
            });
            return true;
        }
        catch (Exception exception)
        {
            SrLogger.LogError($"Could not broadcast canonical silo state '{storageId}': {exception}");
            return false;
        }
    }
}

/// <summary>Pure routing rules for committed and refused silo mutations.</summary>
internal static class AmmoMutationSnapshotPolicy
{
    internal static bool ShouldRelayRelativeDelta(bool mutationAccepted, bool snapshotBroadcast)
        => mutationAccepted && !snapshotBroadcast;

    internal static bool IsValidSlotIndex(bool slotsAvailable, int slotIndex, int slotCount)
        => slotsAvailable && slotIndex >= 0 && slotIndex < slotCount;

    internal static bool VerifyRules()
        => !ShouldRelayRelativeDelta(mutationAccepted: false, snapshotBroadcast: false)
           && !ShouldRelayRelativeDelta(mutationAccepted: false, snapshotBroadcast: true)
           && !ShouldRelayRelativeDelta(mutationAccepted: true, snapshotBroadcast: true)
           && ShouldRelayRelativeDelta(mutationAccepted: true, snapshotBroadcast: false)
           && IsValidSlotIndex(slotsAvailable: true, slotIndex: 0, slotCount: 1)
           && IsValidSlotIndex(slotsAvailable: true, slotIndex: 1, slotCount: 2)
           && !IsValidSlotIndex(slotsAvailable: false, slotIndex: 0, slotCount: 1)
           && !IsValidSlotIndex(slotsAvailable: true, slotIndex: -1, slotCount: 1)
           && !IsValidSlotIndex(slotsAvailable: true, slotIndex: 1, slotCount: 1);
}

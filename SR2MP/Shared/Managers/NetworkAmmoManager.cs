using System.Collections;
using System.Net;
using Il2CppMonomiPark.SlimeRancher.Caretaker;
using Il2CppMonomiPark.SlimeRancher.Player;
using SR2MP.Packets.Ammo;
using SR2MP.Shared.Utils;
// ReSharper disable InconsistentNaming

namespace SR2MP.Shared.Managers;

internal static class NetworkAmmoManager
{
    public static void Initialize()
    {
        ClearAmmoCache();
        slotDefinitions.Clear();

        // Right now, I don't know where the definitions are actually stored,
        // but it isn't in the normal places like LookupDirector or SaveReferenceTranslation.
        //
        // However, I can see that only the definitions for plots or gadgets are loaded on the main menu,
        // so they are probably just stored where they are used only.
        // -PinkTarr
        foreach (var def in Resources.FindObjectsOfTypeAll<AmmoSlotDefinition>())
            slotDefinitions[def.name.Hash16()] = def;
    }

    public static int GetNextSlot(this AmmoSlotManager ammo, IdentifiableType id)
    {
        for (var i = 0; i < ammo._ammoModel.Slots.Count; i++)
        {
            var isSlotEmptyOrSameType = ammo.Slots[i]!._count == 0 || ammo.Slots[i]!._id == id;

            var isSlotFull = ammo.Slots[i]!.Count >= ammo.Slots[i]!.MaxCount;

            if (isSlotEmptyOrSameType && isSlotFull) break;

            if (isSlotEmptyOrSameType)
                return i;
        }

        return -1;
    }
    
    public static void ApplySlotData(this AmmoSlotManager ammo, NetworkAmmo networkAmmo)
    {
        foreach (var (slotIndex, networkSlot) in networkAmmo.AmmoSlots)
        {
            var ammoSlot = ammo.Slots[slotIndex];
            if (ammoSlot != null)
            {
                ammoSlot.Count = networkSlot.Count;
                ammoSlot.MaxCount = networkSlot.MaxCount;
            }

            var ammoModelSlot = ammo._ammoModel?.Slots[slotIndex];
            if (ammoModelSlot != null)
            {
                ammoModelSlot.Count = networkSlot.Count;
                ammoModelSlot.MaxCount = networkSlot.MaxCount;
            }
        }
    }

    internal static NetworkAmmo CreateCanonicalSnapshot(AmmoSlotManager ammo)
    {
        var slots = new Dictionary<int, NetworkAmmoSlot>();
        if (ammo?.Slots == null)
            return new NetworkAmmo { AmmoSlots = slots };

        for (var i = 0; i < ammo.Slots.Count; i++)
        {
            var slot = ammo.Slots[i];
            if (slot?.Definition == null)
                continue;

            slots[i] = new NetworkAmmoSlot
            {
                Count = slot.Count,
                MaxCount = slot.MaxCount,
                Identifiable = slot._id == null ? -1 : NetworkActorManager.GetPersistentID(slot._id),
                SlotDefinition = GetId(slot.Definition),
                Radiant = slot.Radiant
            };
        }

        return new NetworkAmmo { AmmoSlots = slots };
    }

    internal static bool TryApplyCanonicalSnapshot(AmmoSlotManager ammo, NetworkAmmo snapshot, out string error)
    {
        error = string.Empty;
        if (ammo?.Slots == null || ammo._ammoModel?.Slots == null || snapshot?.AmmoSlots == null)
        {
            error = "storage or snapshot model is unavailable";
            return false;
        }

        if (!AmmoCanonicalSnapshotPolicy.HasMatchingSlotCounts(
                ammo.Slots.Count, ammo._ammoModel.Slots.Count))
        {
            error = "storage runtime/model slot counts do not match";
            return false;
        }

        var expectedSlotCount = 0;
        for (var i = 0; i < ammo.Slots.Count; i++)
        {
            var slot = ammo.Slots[i];
            if (slot?.Definition == null)
                continue;

            expectedSlotCount++;
            if (!snapshot.AmmoSlots.TryGetValue(i, out var networkSlot))
            {
                error = $"snapshot is missing slot {i}";
                return false;
            }

            if (!AmmoCanonicalSnapshotPolicy.IsValidSlot(networkSlot))
            {
                error = $"snapshot slot {i} has invalid count/type data";
                return false;
            }

            if (networkSlot.SlotDefinition != GetId(slot.Definition))
            {
                error = $"snapshot slot {i} definition does not match the loaded storage";
                return false;
            }

            IdentifiableType? identifiable = null;
            if (networkSlot.Identifiable != -1 &&
                !ActorManager.ActorTypes.TryGetValue(networkSlot.Identifiable, out identifiable))
            {
                error = $"snapshot slot {i} references unknown identifiable {networkSlot.Identifiable}";
                return false;
            }
        }

        if (snapshot.AmmoSlots.Count != expectedSlotCount)
        {
            error = $"snapshot has {snapshot.AmmoSlots.Count} slots; loaded storage has {expectedSlotCount}";
            return false;
        }

        for (var i = 0; i < ammo.Slots.Count; i++)
        {
            var runtimeSlot = ammo.Slots[i];
            if (runtimeSlot?.Definition == null || !snapshot.AmmoSlots.TryGetValue(i, out var networkSlot))
                continue;

            IdentifiableType? identifiable = null;
            if (networkSlot.Identifiable != -1)
                ActorManager.ActorTypes.TryGetValue(networkSlot.Identifiable, out identifiable);

            ApplyCanonicalSlot(runtimeSlot, networkSlot, identifiable);

            var modelSlot = ammo._ammoModel.Slots[i];
            if (modelSlot != null && modelSlot.Pointer != runtimeSlot.Pointer)
                ApplyCanonicalSlot(modelSlot, networkSlot, identifiable);
        }

        return true;
    }

    private static void ApplyCanonicalSlot(AmmoSlot slot, NetworkAmmoSlot state, IdentifiableType? identifiable)
    {
        slot._id = identifiable;
        slot.Count = state.Count;
        slot._count = state.Count;
        slot.MaxCount = state.MaxCount;
        slot.Radiant = state.Radiant;
    }

    internal static void SendCanonicalSnapshot(IPEndPoint? endpoint, string? storageId, AmmoSlotManager ammo)
    {
        if (endpoint == null || !Main.Server.IsRunning || string.IsNullOrWhiteSpace(storageId))
            return;

        try
        {
            Main.Server.SendToClient(new AmmoStorageSnapshotPacket
            {
                ID = storageId,
                Ammo = CreateCanonicalSnapshot(ammo)
            }, endpoint);
        }
        catch (Exception exception)
        {
            SrLogger.LogError($"Could not send canonical silo state '{storageId}' to {endpoint}: {exception}");
        }
    }

    internal static bool VerifyCanonicalSnapshotRules()
        => AmmoCanonicalSnapshotPolicy.VerifyRules() && AmmoStorageSnapshotPacket.VerifySerializationRoundTrip();
    
    private static readonly Dictionary<ushort, AmmoSlotDefinition> slotDefinitions = new();
    private static readonly Dictionary<IntPtr, string> ammoToID = new();
    private static readonly Dictionary<string, AmmoSlotManager> IDToAmmo = new();
    private static readonly Dictionary<IntPtr, (AmmoSlotManager ammo, int index)> slotToAmmo = new();

    // TODO: Persistent guest inventory needs a PlayerId-owned save/network model. Do not register
    // PlayerState.Ammo here as shared world ammo; this registry is for silo/gadget storage.
    private static readonly Dictionary<long, AmmoSlotManager> warpDepotAmmoByActorId = new();

    public static string? GetPlotID(this AmmoSlotManager ammo) => ammoToID.GetValueOrDefault(ammo.Pointer);

    public static string? GetPlotID(this AmmoSlot slot)
        => slotToAmmo.TryGetValue(slot.Pointer, out var ammoTuple) ? ammoTuple.ammo.GetPlotID() : null;

    public static int? GetSlotIndex(this AmmoSlot slot)
    {
        if (slotToAmmo.TryGetValue(slot.Pointer, out var ammoTuple))
            return ammoTuple.index;

        return null;
    }

    // public static AmmoSlotManager? GetAmmo(this AmmoSlot slot)
    //     => slotToAmmo.TryGetValue(slot.Pointer, out var ammoTuple) ? ammoTuple.ammo : null;

    public static AmmoSlotManager? GetAmmo(string? id) => IDToAmmo!.GetValueOrDefault(id);

    private static void ClearAmmoCache()
    {
        ammoToID.Clear();
        IDToAmmo.Clear();
        slotToAmmo.Clear();
        warpDepotAmmoByActorId.Clear();
    }

    private static void RegisterAmmoPointer(this AmmoSlotManager ammo, string id)
    {
        ammoToID[ammo.Pointer] = id;
        IDToAmmo[id] = ammo;

        for (var i = 0; i < ammo.Slots.Count; i++)
        {
            var slot = ammo.Slots[i];
            slotToAmmo[slot!.Pointer] = (ammo, i);
        }
    }

    public static void RegisterAmmoPointer(this SiloStorage siloStorage)
    {
        StartCoroutine(RegisterAmmoPointerCoroutine(siloStorage));
    }

    private static IEnumerator RegisterAmmoPointerCoroutine(SiloStorage siloStorage)
    {
        yield return new WaitFrames(3);
        
        if (siloStorage == null) yield break;

        // needs to include inactive ones, don't question why
        var plot = siloStorage.GetComponentInParent<LandPlotLocation>(true);
        var gadget = siloStorage.GetComponentInParent<Gadget>(true);
        var sprinkle = siloStorage.GetComponentInParent<SprinkleCanister>(true);

        if (plot == null && gadget == null && sprinkle == null)
        {
            SrLogger.LogWarning($"SiloStorage has no known parent type: {siloStorage.name}");
            yield break;
        }

        var ammo = siloStorage.GetRelevantAmmo();
        if (ammo == null)
        {
            SrLogger.LogWarning($"SiloStorage has no ammo to register yet: {siloStorage.name}");
            yield break;
        }

        if (plot != null)
        {
            ammo.RegisterAmmoPointer($"{plot._id}_{siloStorage.AmmoSetReference.name}");
            yield break;
        }

        if (gadget != null)
        {
            var actorId = gadget.GetActorId().Value;
            
            if (siloStorage.TryCast<LinkedSiloStorage>() != null)
            {
                RegisterWarpDepotAmmo(ammo, actorId);
                yield break;
            }

            if (siloStorage.AmmoSetReference == null)
            {
                SrLogger.LogWarning($"SiloStorage has no AmmoSetReference yet: {siloStorage.name}");
                yield break;
            }

            ammo.RegisterAmmoPointer($"gadget{actorId}_{siloStorage.AmmoSetReference.name}");
            yield break;
        }

        if (sprinkle != null)
        {
            ammo.RegisterAmmoPointer($"{sprinkle!.GetComponent<IdHandler>().Id}_{siloStorage.AmmoSetReference.name}");
        }
    }

    private static void RegisterWarpDepotAmmo(AmmoSlotManager ammo, long actorId)
    {
        warpDepotAmmoByActorId[actorId] = ammo;

        var ammoId = NetworkGadgetManager.TryGetLinkedGadgetId(actorId, out var partnerId)
            ? Math.Min(actorId, partnerId)
            : actorId;

        ammo.RegisterAmmoPointer($"gadget{ammoId}_warpdepot");
    }
    
    internal static void OnGadgetLinkResolved(long gadgetId, long partnerId)
    {
        var ammoId = Math.Min(gadgetId, partnerId);
        ReKeyWarpDepotAmmo(gadgetId, ammoId);
        ReKeyWarpDepotAmmo(partnerId, ammoId);
    }

    private static void ReKeyWarpDepotAmmo(long actorId, long ammoId)
    {
        if (!warpDepotAmmoByActorId.TryGetValue(actorId, out var ammo) || ammo == null)
            return;

        var newId = $"gadget{ammoId}_warpdepot";

        if (ammoToID.TryGetValue(ammo.Pointer, out var currentId))
        {
            if (currentId == newId) return;
            IDToAmmo.Remove(currentId);
        }

        ammo.RegisterAmmoPointer(newId);
    }

    public static AmmoSlotDefinition GetSlotDefinition(ushort id) => slotDefinitions[id];

    public static ushort GetId(AmmoSlotDefinition def)
    {
        if (def.name == null)
        {
            SrLogger.LogError("GetId called with a null definition name.");
            return 0;
        }

        var hash = def.name.Hash16();
        slotDefinitions.TryAdd(hash, def);
        return hash;
    }
}

/// <summary>Pure validation for absolute ammo-storage snapshots.</summary>
internal static class AmmoCanonicalSnapshotPolicy
{
    internal static bool HasMatchingSlotCounts(int runtimeSlotCount, int modelSlotCount)
        => runtimeSlotCount >= 0 && runtimeSlotCount == modelSlotCount;

    internal static bool IsValidSlot(NetworkAmmoSlot slot)
        => slot.Count >= 0 && slot.MaxCount >= 0 && slot.Count <= slot.MaxCount &&
           slot.Identifiable >= -1 && (slot.Identifiable != -1 || slot.Count == 0);

    internal static bool VerifyRules()
        => HasMatchingSlotCounts(runtimeSlotCount: 4, modelSlotCount: 4) &&
           !HasMatchingSlotCounts(runtimeSlotCount: 4, modelSlotCount: 5) &&
           !HasMatchingSlotCounts(runtimeSlotCount: 5, modelSlotCount: 4) &&
           !HasMatchingSlotCounts(runtimeSlotCount: -1, modelSlotCount: -1) &&
           IsValidSlot(new NetworkAmmoSlot { Identifiable = 21, Count = 3, MaxCount = 10 }) &&
           IsValidSlot(new NetworkAmmoSlot { Identifiable = -1, Count = 0, MaxCount = 10 }) &&
           !IsValidSlot(new NetworkAmmoSlot { Identifiable = -1, Count = 1, MaxCount = 10 }) &&
           !IsValidSlot(new NetworkAmmoSlot { Identifiable = 21, Count = 11, MaxCount = 10 }) &&
           !IsValidSlot(new NetworkAmmoSlot { Identifiable = -2, Count = 0, MaxCount = 10 });
}

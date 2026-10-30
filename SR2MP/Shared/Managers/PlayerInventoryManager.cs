using System.Collections;
using System.Text;
using System.Text.Json;
using Il2CppMonomiPark.SlimeRancher.Player;
using MelonLoader.Utils;
using SR2MP.Shared.Utils;
using Unity.Mathematics;

namespace SR2MP.Shared.Managers;

/// <summary>
/// Keeps the local guest's vacuum inventory in Melon UserData. It never registers
/// PlayerState.Ammo with shared world ammo and never sends this data to the host.
/// </summary>
internal static class PlayerInventoryManager
{
    private const int MaximumSnapshotBytes = 64 * 1024;
    private const string ProfileFileName = "install-profile-id.txt";
    private const string InventoryFileName = "inventory.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static string? profileId;
    private static bool initialized;
    private static bool guestSession;
    private static bool snapshotWasMissing;
    private static bool playerUpgradeSnapshotApplied;
    private static int unityThreadId;
    private static int sessionGeneration;
    private static RestoreState restoreState;
    private static PlayerInventorySnapshot? pendingSnapshot;

    private enum RestoreState
    {
        Inactive,
        WaitingForAmmo,
        MissingSnapshot,
        Restored,
        InvalidSnapshot,
        Disabled
    }

    internal static void Initialize()
    {
        if (initialized)
            return;

        initialized = true;
        unityThreadId = Environment.CurrentManagedThreadId;
        profileId = LoadOrCreateProfileId();
        if (profileId == null)
            SrLogger.LogError("Player inventory persistence is disabled because the install profile id could not be loaded safely.");
    }

    internal static void OnClientConnected()
    {
        Initialize();

        guestSession = true;
        snapshotWasMissing = false;
        pendingSnapshot = null;
        sessionGeneration++;
        restoreState = profileId == null ? RestoreState.Disabled : RestoreState.WaitingForAmmo;

        if (profileId == null)
            return;

        var path = GetInventoryPath();
        if (!File.Exists(path))
        {
            snapshotWasMissing = true;
            SrLogger.LogMessage("No local guest inventory snapshot exists yet; the vanilla inventory will be left untouched.");
            return;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaximumSnapshotBytes)
            {
                RejectSnapshot("snapshot file size is invalid");
                return;
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            if (!PlayerInventorySnapshotRules.TryDeserialize(json, out var snapshot))
            {
                RejectSnapshot("snapshot JSON is invalid");
                return;
            }

            if (!PlayerInventorySnapshotRules.Validate(snapshot, profileId, out var reason))
            {
                RejectSnapshot(reason);
                return;
            }

            pendingSnapshot = snapshot;
            SrLogger.LogMessage("A valid local guest inventory snapshot is waiting for the player ammo model.");
        }
        catch (Exception ex)
        {
            RejectSnapshot($"could not read snapshot ({ex.GetType().Name})");
        }
    }

    /// <summary>
    /// Marks the host's player-upgrade snapshot as applied. Ammo slot count and
    /// definitions can change with those upgrades, so restore must wait for it.
    /// This flag is intentionally not reset by OnClientConnected: the snapshot can
    /// arrive before ConnectionApprove because those packets use different channels.
    /// </summary>
    internal static void NotifyPlayerUpgradeSnapshotApplied()
        => playerUpgradeSnapshotApplied = true;

    internal static IEnumerator RestoreWhenPlayerReady()
    {
        var generation = sessionGeneration;
        while (guestSession && generation == sessionGeneration && Main.Client.IsJoined)
        {
            var ammoReady = TryGetReadyAmmo(out var ammo);
            if (!PlayerInventoryRestorePolicy.CanRestore(ammoReady, playerUpgradeSnapshotApplied))
            {
                yield return new WaitForSeconds(0.25f);
                continue;
            }

            if (restoreState == RestoreState.Disabled || restoreState == RestoreState.InvalidSnapshot)
                yield break;

            if (snapshotWasMissing)
            {
                restoreState = RestoreState.MissingSnapshot;
                SrLogger.LogMessage("Player ammo model is ready; no snapshot was present, so vanilla inventory remains unchanged.");
                yield break;
            }

            if (pendingSnapshot == null)
            {
                RejectSnapshot("snapshot was not available after the file was read");
                yield break;
            }

            if (!TryPrepareSnapshot(ammo!, pendingSnapshot, out var prepared, out var reason))
            {
                RejectSnapshot(reason);
                yield break;
            }

            if (!TryCaptureRuntimeState(ammo!, out var rollback, out reason))
            {
                RejectSnapshot($"could not capture a rollback state ({reason})");
                yield break;
            }

            var previousHandlingPacket = HandlingPacket;
            try
            {
                HandlingPacket = true;
                ApplyPreparedSnapshot(ammo!, prepared, pendingSnapshot.SelectedSlotIndex);
                restoreState = RestoreState.Restored;
                pendingSnapshot = null;
                SrLogger.LogMessage($"Restored local guest inventory ({prepared.Length} slots).");
            }
            catch (Exception ex)
            {
                try
                {
                    ApplyRuntimeState(ammo!, rollback);
                    SrLogger.LogWarning("Guest inventory restore failed; the previous in-memory ammo state was restored.");
                }
                catch (Exception rollbackException)
                {
                    SrLogger.LogError($"Guest inventory restore and rollback both failed: {rollbackException}");
                }

                RejectSnapshot($"could not apply snapshot ({ex.GetType().Name})");
            }
            finally
            {
                HandlingPacket = previousHandlingPacket;
            }

            yield break;
        }
    }

    /// <summary>Called by the FileStorageProvider.StoreGameData prefix.</summary>
    internal static void SaveBeforeVanillaGameWrite()
    {
        if (!guestSession || !Main.Client.IsJoined || !CanPersistCurrentSession())
            return;

        SaveCurrentInventory("before local save");
    }

    internal static void OnClientDisconnected()
    {
        if (!guestSession)
        {
            playerUpgradeSnapshotApplied = false;
            return;
        }

        if (Environment.CurrentManagedThreadId == unityThreadId)
        {
            FinishGuestSession(sessionGeneration);
            return;
        }

        var dispatcher = MainThreadDispatcher.Instance;
        if (dispatcher)
        {
            var generation = sessionGeneration;
            dispatcher.Enqueue(() => FinishGuestSession(generation));
            return;
        }

        SrLogger.LogWarning("Could not save guest inventory on disconnect because the Unity main-thread dispatcher was unavailable.");
        guestSession = false;
        pendingSnapshot = null;
        restoreState = RestoreState.Inactive;
        playerUpgradeSnapshotApplied = false;
    }

    private static void FinishGuestSession(int expectedGeneration)
    {
        if (expectedGeneration != sessionGeneration)
            return;

        if (guestSession && CanPersistCurrentSession())
            SaveCurrentInventory("disconnect");
        else if (guestSession)
            SrLogger.LogMessage("Guest inventory was not overwritten on disconnect because it was not safely restored or validated.");

        guestSession = false;
        snapshotWasMissing = false;
        pendingSnapshot = null;
        restoreState = RestoreState.Inactive;
        playerUpgradeSnapshotApplied = false;
    }

    private static bool CanPersistCurrentSession()
        => profileId != null && restoreState is RestoreState.MissingSnapshot or RestoreState.Restored;

    private static void SaveCurrentInventory(string reason)
    {
        if (profileId == null || !TryGetReadyAmmo(out var ammo))
            return;

        if (!TryCaptureSnapshot(ammo!, profileId, out var snapshot, out var captureReason))
        {
            SrLogger.LogWarning($"Skipped local guest inventory save ({reason}): {captureReason}.");
            return;
        }

        if (!PlayerInventorySnapshotRules.Validate(snapshot, profileId, out var validationReason))
        {
            SrLogger.LogWarning($"Skipped local guest inventory save ({reason}): {validationReason}.");
            return;
        }

        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
            if (bytes.Length <= 0 || bytes.Length > MaximumSnapshotBytes)
            {
                SrLogger.LogWarning($"Skipped local guest inventory save ({reason}): serialized snapshot is too large.");
                return;
            }

            AtomicWrite(GetInventoryPath(), bytes, overwrite: true);
            SrLogger.LogMessage($"Saved local guest inventory ({reason}; {snapshot.Slots.Count} slots).");
        }
        catch (Exception ex)
        {
            SrLogger.LogError($"Could not save local guest inventory ({reason}): {ex}");
        }
    }

    private static bool TryGetReadyAmmo(out AmmoSlotManager? ammo)
    {
        ammo = null;
        try
        {
            var scene = SceneContext.Instance;
            var playerState = scene ? scene.PlayerState : null;
            var playerModel = playerState?._model;
            var candidate = playerState?.Ammo;
            var model = candidate?._ammoModel;
            var modelAmmo = playerModel?.ammoModel;
            if (playerState == null || playerModel == null || candidate == null || model == null || modelAmmo == null ||
                candidate.Slots == null || model.Slots == null || modelAmmo.Slots == null)
                return false;

            var count = candidate.Slots.Count;
            if (count is < 1 or > PlayerInventorySnapshotRules.MaximumSlots ||
                model.Slots.Count != count || modelAmmo.Slots.Count != count)
                return false;

            for (var index = 0; index < count; index++)
            {
                var slot = candidate.Slots[index];
                var modelSlot = model.Slots[index];
                var playerModelSlot = modelAmmo.Slots[index];
                if (slot == null || modelSlot == null || playerModelSlot == null ||
                    slot.Definition == null || modelSlot.Definition == null || playerModelSlot.Definition == null)
                    return false;
            }

            ammo = candidate;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryCaptureSnapshot(
        AmmoSlotManager ammo,
        string currentProfileId,
        out PlayerInventorySnapshot snapshot,
        out string reason)
    {
        snapshot = new PlayerInventorySnapshot();
        reason = string.Empty;

        try
        {
            var count = ammo.Slots.Count;
            if (count is < 1 or > PlayerInventorySnapshotRules.MaximumSlots ||
                ammo._ammoModel?.Slots == null || ammo._ammoModel.Slots.Count != count)
                return Fail("ammo slots are not ready", out reason);

            var selectedIndex = FindSelectedIndex(ammo);
            if (selectedIndex < -1)
                return Fail("selected ammo slot is not part of the live slot array", out reason);

            snapshot = new PlayerInventorySnapshot
            {
                SchemaVersion = PlayerInventorySnapshotRules.CurrentSchemaVersion,
                ProfileId = currentProfileId,
                SelectedSlotIndex = selectedIndex,
                CapturedAtUtcUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Slots = new List<PlayerInventorySlotSnapshot>(count)
            };

            for (var index = 0; index < count; index++)
            {
                var slot = ammo.Slots[index];
                if (slot == null || slot.Definition == null || string.IsNullOrWhiteSpace(slot.Definition.name))
                    return Fail($"ammo slot {index} has no definition", out reason);

                var itemReferenceId = string.Empty;
                if (slot.Count > 0)
                {
                    var item = slot.Id;
                    if (item == null || string.IsNullOrWhiteSpace(item.ReferenceId))
                        return Fail($"occupied ammo slot {index} has no resolvable item reference", out reason);
                    itemReferenceId = item.ReferenceId;
                }

                var appearance = slot.Appearance;
                if (!Enum.IsDefined(typeof(SlimeAppearance.AppearanceSaveSet), appearance))
                    return Fail($"ammo slot {index} has an unknown appearance value", out reason);

                var emotions = slot.Emotions;
                snapshot.Slots.Add(new PlayerInventorySlotSnapshot
                {
                    SlotIndex = index,
                    SlotDefinition = slot.Definition.name,
                    ItemReferenceId = itemReferenceId,
                    Count = slot.Count,
                    MaxCount = slot.MaxCount,
                    Radiant = slot.Radiant,
                    EmotionX = emotions.x,
                    EmotionY = emotions.y,
                    EmotionZ = emotions.z,
                    EmotionW = emotions.w,
                    Appearance = (int)appearance
                });
            }

            return PlayerInventorySnapshotRules.Validate(snapshot, currentProfileId, out reason);
        }
        catch (Exception ex)
        {
            return Fail($"could not read ammo model ({ex.GetType().Name})", out reason);
        }
    }

    private static bool TryPrepareSnapshot(
        AmmoSlotManager ammo,
        PlayerInventorySnapshot snapshot,
        out PreparedSlot[] prepared,
        out string reason)
    {
        prepared = Array.Empty<PreparedSlot>();
        reason = string.Empty;
        if (!PlayerInventorySnapshotRules.Validate(snapshot, profileId, out reason))
            return false;

        try
        {
            var count = ammo.Slots.Count;
            if (count != snapshot.Slots.Count || ammo._ammoModel?.Slots == null || ammo._ammoModel.Slots.Count != count)
                return Fail("saved slot count does not match the ready player ammo model", out reason);

            prepared = new PreparedSlot[count];
            foreach (var savedSlot in snapshot.Slots)
            {
                var index = savedSlot.SlotIndex;
                var liveSlot = ammo.Slots[index];
                var modelSlot = ammo._ammoModel.Slots[index];
                if (liveSlot == null || modelSlot == null || liveSlot.Definition == null || modelSlot.Definition == null)
                    return Fail($"live ammo slot {index} is not initialized", out reason);
                if (!string.Equals(liveSlot.Definition.name, savedSlot.SlotDefinition, StringComparison.Ordinal) ||
                    !string.Equals(modelSlot.Definition.name, savedSlot.SlotDefinition, StringComparison.Ordinal))
                    return Fail($"slot {index} definition does not match the saved inventory", out reason);
                if (!PlayerInventoryRestorePolicy.CanRestoreCount(
                        savedSlot.Count, liveSlot.MaxCount, modelSlot.MaxCount))
                    return Fail(
                        $"saved slot {index} count {savedSlot.Count} exceeds the current host-upgrade capacity " +
                        $"(live={liveSlot.MaxCount}, model={modelSlot.MaxCount}); preserving the sidecar without applying it",
                        out reason);

                var appearance = (SlimeAppearance.AppearanceSaveSet)savedSlot.Appearance;
                if (!Enum.IsDefined(typeof(SlimeAppearance.AppearanceSaveSet), appearance))
                    return Fail($"slot {index} appearance value is not defined in this game build", out reason);

                IdentifiableType? item = null;
                if (savedSlot.Count > 0)
                {
                    item = ResolveItemReference(savedSlot.ItemReferenceId);
                    if (item == null)
                        return Fail($"item reference '{savedSlot.ItemReferenceId}' is unavailable in this game build", out reason);
                    if (!liveSlot.Definition.IsAllowed(item) || !modelSlot.Definition.IsAllowed(item))
                        return Fail($"item reference is not allowed in ammo slot {index}", out reason);
                }

                prepared[index] = new PreparedSlot(savedSlot, item);
            }

            if (prepared.Any(slot => slot == null))
                return Fail("saved inventory is missing one or more slot entries", out reason);

            return true;
        }
        catch (Exception ex)
        {
            return Fail($"could not validate live ammo model ({ex.GetType().Name})", out reason);
        }
    }

    private static IdentifiableType? ResolveItemReference(string referenceId)
    {
        IdentifiableType? resolved = null;
        foreach (var candidate in ActorManager.ActorTypes.Values)
        {
            if (candidate == null || !string.Equals(candidate.ReferenceId, referenceId, StringComparison.Ordinal))
                continue;

            if (resolved != null && resolved.Pointer != candidate.Pointer)
                return null;
            resolved = candidate;
        }

        return resolved;
    }

    private static void ApplyPreparedSnapshot(AmmoSlotManager ammo, PreparedSlot[] prepared, int selectedSlotIndex)
    {
        for (var index = 0; index < prepared.Length; index++)
        {
            var liveSlot = ammo.Slots[index]!;
            var modelSlot = ammo._ammoModel!.Slots[index]!;
            var preparedSlot = prepared[index];
            ApplySlot(liveSlot, preparedSlot.Snapshot, preparedSlot.Item);
            if (modelSlot.Pointer != liveSlot.Pointer)
                ApplySlot(modelSlot, preparedSlot.Snapshot, preparedSlot.Item);
        }

        // Restore -1 as well as ordinary slot indices. The validated snapshot may
        // represent no selected slot, and RestoreSelectedSlot verifies the result
        // using the same checked path used by rollback.
        RestoreSelectedSlot(ammo, selectedSlotIndex);
    }

    private static void ApplySlot(AmmoSlot slot, PlayerInventorySlotSnapshot data, IdentifiableType? item)
    {
        if (item == null)
        {
            slot.Clear();
            slot.Emotions = new float4(data.EmotionX, data.EmotionY, data.EmotionZ, data.EmotionW);
        }
        else
        {
            var emotions = new float4(data.EmotionX, data.EmotionY, data.EmotionZ, data.EmotionW);
            slot.SetFromMetadata(new AmmoSlot.AmmoMetadata(item, emotions, data.Radiant));
            slot.Emotions = emotions;
        }

        slot.Count = data.Count;
        slot.Radiant = data.Radiant;
        slot.Appearance = (SlimeAppearance.AppearanceSaveSet)data.Appearance;
    }

    private static bool TryCaptureRuntimeState(AmmoSlotManager ammo, out AmmoRuntimeState state, out string reason)
    {
        state = new AmmoRuntimeState(Array.Empty<SlotRuntimeState>(), Array.Empty<SlotRuntimeState>(), -1);
        reason = string.Empty;
        try
        {
            var count = ammo.Slots.Count;
            if (count is < 1 or > PlayerInventorySnapshotRules.MaximumSlots || ammo._ammoModel?.Slots == null ||
                ammo._ammoModel.Slots.Count != count)
                return Fail("ammo arrays are not ready", out reason);

            var live = new SlotRuntimeState[count];
            var model = new SlotRuntimeState[count];
            for (var index = 0; index < count; index++)
            {
                var liveSlot = ammo.Slots[index];
                var modelSlot = ammo._ammoModel.Slots[index];
                if (liveSlot == null || modelSlot == null)
                    return Fail($"ammo slot {index} is null", out reason);
                live[index] = CaptureSlot(liveSlot);
                model[index] = CaptureSlot(modelSlot);
            }

            var selected = FindSelectedIndex(ammo);
            if (selected < -1)
                return Fail("selected ammo slot is outside the slot array", out reason);

            state = new AmmoRuntimeState(live, model, selected);
            return true;
        }
        catch (Exception ex)
        {
            return Fail($"could not capture live ammo state ({ex.GetType().Name})", out reason);
        }
    }

    private static SlotRuntimeState CaptureSlot(AmmoSlot slot)
        => new(slot.Id, slot.Count, slot.MaxCount, slot.Emotions, slot.Radiant, slot.Appearance);

    private static void ApplyRuntimeState(AmmoSlotManager ammo, AmmoRuntimeState state)
    {
        for (var index = 0; index < state.LiveSlots.Length; index++)
        {
            ApplyRuntimeSlot(ammo.Slots[index]!, state.LiveSlots[index]);
            var modelSlot = ammo._ammoModel!.Slots[index]!;
            if (modelSlot.Pointer != ammo.Slots[index]!.Pointer)
                ApplyRuntimeSlot(modelSlot, state.ModelSlots[index]);
        }

        RestoreSelectedSlot(ammo, state.SelectedIndex);
    }

    private static void ApplyRuntimeSlot(AmmoSlot slot, SlotRuntimeState state)
    {
        slot.Clear();
        if (state.Item != null)
            slot.SetFromMetadata(new AmmoSlot.AmmoMetadata(state.Item, state.Emotions, state.Radiant));
        slot.Emotions = state.Emotions;
        slot.MaxCount = state.MaxCount;
        slot.Count = state.Count;
        slot.Radiant = state.Radiant;
        slot.Appearance = state.Appearance;
    }

    private static int FindSelectedIndex(AmmoSlotManager ammo)
    {
        var selected = ammo.SelectedSlot;
        if (selected == null)
            return -1;

        for (var index = 0; index < ammo.Slots.Count; index++)
        {
            var slot = ammo.Slots[index];
            if (slot != null && slot.Pointer == selected.Pointer)
                return index;
        }

        return -2;
    }

    private static void RestoreSelectedSlot(AmmoSlotManager ammo, int selectedIndex)
    {
        if (selectedIndex < -1)
            throw new InvalidOperationException($"rollback selected slot {selectedIndex} is invalid");

        if (PlayerInventoryRestorePolicy.IsSelectedIndexRestored(selectedIndex, FindSelectedIndex(ammo)))
            return;

        // The IL2CPP API exposes only SetSelectedSlot(int); -1 is attempted for
        // the captured no-selection state and verified from the resulting model.
        // A false return is not failure when the requested state is already live.
        ammo.SetSelectedSlot(selectedIndex);
        var actualSelectedIndex = FindSelectedIndex(ammo);
        if (!PlayerInventoryRestorePolicy.IsSelectedIndexRestored(selectedIndex, actualSelectedIndex))
            throw new InvalidOperationException(
                $"rollback could not restore selected slot {selectedIndex} (actual={actualSelectedIndex})");
    }

    private static string? LoadOrCreateProfileId()
    {
        var profilePath = GetProfilePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
            if (File.Exists(profilePath))
            {
                var existing = File.ReadAllText(profilePath, Encoding.UTF8).Trim();
                if (Guid.TryParseExact(existing, "N", out var parsed))
                    return parsed.ToString("N");

                SrLogger.LogError("Install profile id file is invalid; it was preserved and no replacement id was generated.");
                return null;
            }

            var generated = Guid.NewGuid().ToString("N");
            try
            {
                AtomicWrite(profilePath, Encoding.UTF8.GetBytes(generated), overwrite: false);
                return generated;
            }
            catch (IOException) when (File.Exists(profilePath))
            {
                var raced = File.ReadAllText(profilePath, Encoding.UTF8).Trim();
                return Guid.TryParseExact(raced, "N", out var parsed) ? parsed.ToString("N") : null;
            }
        }
        catch (Exception ex)
        {
            SrLogger.LogError($"Could not load or create install profile id: {ex}");
            return null;
        }
    }

    private static string GetProfilePath()
        => Path.Combine(ModPaths.DataDirectory, "PlayerInventory", ProfileFileName);

    private static string GetInventoryPath()
        => Path.Combine(ModPaths.DataDirectory, "PlayerInventory", profileId!, InventoryFileName);

    private static void AtomicWrite(string path, byte[] bytes, bool overwrite)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            if (overwrite)
                File.Move(tempPath, path, overwrite: true);
            else
                File.Move(tempPath, path);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static void RejectSnapshot(string reason)
    {
        restoreState = RestoreState.InvalidSnapshot;
        pendingSnapshot = null;
        SrLogger.LogWarning($"Local guest inventory snapshot was rejected; live ammo remains unchanged ({reason}).");
    }

    private static bool Fail(string message, out string reason)
    {
        reason = message;
        return false;
    }

    private sealed class PreparedSlot
    {
        internal readonly PlayerInventorySlotSnapshot Snapshot;
        internal readonly IdentifiableType? Item;

        internal PreparedSlot(PlayerInventorySlotSnapshot snapshot, IdentifiableType? item)
        {
            Snapshot = snapshot;
            Item = item;
        }
    }

    private readonly struct SlotRuntimeState
    {
        internal readonly IdentifiableType? Item;
        internal readonly int Count;
        internal readonly int MaxCount;
        internal readonly float4 Emotions;
        internal readonly bool Radiant;
        internal readonly SlimeAppearance.AppearanceSaveSet Appearance;

        internal SlotRuntimeState(
            IdentifiableType? item,
            int count,
            int maxCount,
            float4 emotions,
            bool radiant,
            SlimeAppearance.AppearanceSaveSet appearance)
        {
            Item = item;
            Count = count;
            MaxCount = maxCount;
            Emotions = emotions;
            Radiant = radiant;
            Appearance = appearance;
        }
    }

    private readonly struct AmmoRuntimeState
    {
        internal readonly SlotRuntimeState[] LiveSlots;
        internal readonly SlotRuntimeState[] ModelSlots;
        internal readonly int SelectedIndex;

        internal AmmoRuntimeState(SlotRuntimeState[] liveSlots, SlotRuntimeState[] modelSlots, int selectedIndex)
        {
            LiveSlots = liveSlots;
            ModelSlots = modelSlots;
            SelectedIndex = selectedIndex;
        }
    }
}

using System.Text.Json;

namespace SR2MP.Shared.Managers;

/// <summary>
/// Local-only inventory state for one installation. This DTO is deliberately separate
/// from NetworkAmmo, which represents shared world storage.
/// </summary>
internal sealed class PlayerInventorySnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public string ProfileId { get; set; } = string.Empty;
    public int SelectedSlotIndex { get; set; } = -1;
    public long CapturedAtUtcUnixMs { get; set; }
    public List<PlayerInventorySlotSnapshot> Slots { get; set; } = new();
}

internal sealed class PlayerInventorySlotSnapshot
{
    public int SlotIndex { get; set; }
    public string SlotDefinition { get; set; } = string.Empty;
    public string ItemReferenceId { get; set; } = string.Empty;
    public int Count { get; set; }
    public int MaxCount { get; set; }
    public bool Radiant { get; set; }
    public float EmotionX { get; set; }
    public float EmotionY { get; set; }
    public float EmotionZ { get; set; }
    public float EmotionW { get; set; }
    public int Appearance { get; set; }
}

/// <summary>Pure bounds and JSON checks; it does not access Unity or IL2CPP state.</summary>
internal static class PlayerInventorySnapshotRules
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumSlots = 16;
    internal const int MaximumItemCount = 100_000;
    private const int MaximumDefinitionNameLength = 128;
    private const int MaximumReferenceIdLength = 256;
    private const float MaximumEmotionMagnitude = 100f;

    internal static bool Validate(
        PlayerInventorySnapshot? snapshot,
        string? expectedProfileId,
        out string reason)
    {
        reason = string.Empty;

        if (snapshot == null)
            return Fail("snapshot is null", out reason);
        if (snapshot.SchemaVersion != CurrentSchemaVersion)
            return Fail("unsupported schema version", out reason);
        if (!IsProfileId(snapshot.ProfileId) || !IsProfileId(expectedProfileId))
            return Fail("invalid profile id", out reason);
        if (!string.Equals(snapshot.ProfileId, expectedProfileId, StringComparison.OrdinalIgnoreCase))
            return Fail("profile id mismatch", out reason);
        if (snapshot.Slots == null || snapshot.Slots.Count is < 1 or > MaximumSlots)
            return Fail("slot count is outside the supported range", out reason);
        if (snapshot.SelectedSlotIndex < -1 || snapshot.SelectedSlotIndex >= snapshot.Slots.Count)
            return Fail("selected slot index is outside the slot array", out reason);

        var seen = new bool[snapshot.Slots.Count];
        foreach (var slot in snapshot.Slots)
        {
            if (slot == null)
                return Fail("slot entry is null", out reason);
            if (slot.SlotIndex < 0 || slot.SlotIndex >= seen.Length || seen[slot.SlotIndex])
                return Fail("slot indices are invalid or duplicated", out reason);
            seen[slot.SlotIndex] = true;

            if (string.IsNullOrWhiteSpace(slot.SlotDefinition) ||
                slot.SlotDefinition.Length > MaximumDefinitionNameLength)
                return Fail("slot definition name is invalid", out reason);
            if (slot.Count < 0 || slot.Count > MaximumItemCount ||
                slot.MaxCount < 0 || slot.MaxCount > MaximumItemCount ||
                slot.Count > slot.MaxCount)
                return Fail("slot count or maximum is outside the supported range", out reason);
            if (slot.Count > 0 && string.IsNullOrWhiteSpace(slot.ItemReferenceId))
                return Fail("occupied slot has no item reference id", out reason);
            if (slot.ItemReferenceId == null || slot.ItemReferenceId.Length > MaximumReferenceIdLength)
                return Fail("item reference id is invalid", out reason);
            if (slot.Appearance < 0 || slot.Appearance > ushort.MaxValue)
                return Fail("appearance enum value is outside the supported range", out reason);
            if (!IsBoundedEmotion(slot.EmotionX) || !IsBoundedEmotion(slot.EmotionY) ||
                !IsBoundedEmotion(slot.EmotionZ) || !IsBoundedEmotion(slot.EmotionW))
                return Fail("emotion value is not finite or is outside the supported range", out reason);
        }

        if (seen.Any(value => !value))
            return Fail("slot indices are not contiguous", out reason);

        return true;
    }

    internal static bool TryDeserialize(string json, out PlayerInventorySnapshot? snapshot)
    {
        snapshot = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            snapshot = JsonSerializer.Deserialize<PlayerInventorySnapshot>(json);
            return snapshot != null;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

#if SR2MP_TEST
    internal static bool VerifySerializationAndBounds()
    {
        var profileId = "31ac0ac109d84a25b3edbc831a4e0c87";
        var snapshot = new PlayerInventorySnapshot
        {
            ProfileId = profileId,
            SelectedSlotIndex = 0,
            CapturedAtUtcUnixMs = 1_800_000_000_000,
            Slots = new List<PlayerInventorySlotSnapshot>
            {
                new()
                {
                    SlotIndex = 0,
                    SlotDefinition = "Main",
                    ItemReferenceId = "slime_pink",
                    Count = 2,
                    MaxCount = 20,
                    Radiant = true,
                    EmotionX = 0.1f,
                    EmotionY = 0.2f,
                    EmotionZ = -0.3f,
                    EmotionW = 1f,
                    Appearance = 1
                },
                new()
                {
                    SlotIndex = 1,
                    SlotDefinition = "Resources",
                    ItemReferenceId = string.Empty,
                    Count = 0,
                    MaxCount = 50
                }
            }
        };

        if (!Validate(snapshot, profileId, out _))
            return false;

        var json = JsonSerializer.Serialize(snapshot);
        if (!TryDeserialize(json, out var roundTrip) || !Validate(roundTrip, profileId, out _))
            return false;
        if (roundTrip!.Slots.Count != 2 || roundTrip.Slots[0].ItemReferenceId != "slime_pink" ||
            roundTrip.Slots[0].Count != 2 || roundTrip.Slots[0].EmotionZ != -0.3f ||
            roundTrip.SelectedSlotIndex != 0)
            return false;

        var invalidCount = Clone(snapshot);
        invalidCount.Slots[0].Count = invalidCount.Slots[0].MaxCount + 1;
        if (Validate(invalidCount, profileId, out _))
            return false;

        var invalidProfile = Clone(snapshot);
        if (Validate(invalidProfile, "b8e9dd172eb74832a6d7f26be67d82fd", out _))
            return false;

        var duplicateIndex = Clone(snapshot);
        duplicateIndex.Slots[1].SlotIndex = 0;
        if (Validate(duplicateIndex, profileId, out _))
            return false;

        var invalidEmotion = Clone(snapshot);
        invalidEmotion.Slots[0].EmotionX = float.NaN;
        if (Validate(invalidEmotion, profileId, out _))
            return false;

        var invalidSchema = Clone(snapshot);
        invalidSchema.SchemaVersion++;
        return !Validate(invalidSchema, profileId, out _)
               && PlayerInventoryRestorePolicy.VerifyRules()
               && PlayerInventoryRestorePolicy.VerifySelectedSlotRules()
               && PlayerInventoryRestorePolicy.VerifyCapacityRules();
    }

    private static PlayerInventorySnapshot Clone(PlayerInventorySnapshot source)
        => JsonSerializer.Deserialize<PlayerInventorySnapshot>(JsonSerializer.Serialize(source))!;
#endif

    private static bool IsProfileId(string? value)
        => value != null && Guid.TryParseExact(value, "N", out _);

    private static bool IsBoundedEmotion(float value)
        => float.IsFinite(value) && Math.Abs(value) <= MaximumEmotionMagnitude;

    private static bool Fail(string message, out string reason)
    {
        reason = message;
        return false;
    }
}

/// <summary>Join ordering rule for restoring private inventory after shared upgrades.</summary>
internal static class PlayerInventoryRestorePolicy
{
    internal static bool CanRestore(bool playerAmmoReady, bool hostUpgradeSnapshotApplied)
        => playerAmmoReady && hostUpgradeSnapshotApplied;

    internal static bool IsRequestedSlotSelected(int requestedIndex, int actualSelectedIndex)
        => requestedIndex >= 0 && actualSelectedIndex == requestedIndex;

    internal static bool IsSelectedIndexRestored(int requestedIndex, int actualSelectedIndex)
        => requestedIndex >= -1 && actualSelectedIndex == requestedIndex;

    internal static bool CanRestoreCount(int savedCount, int liveSlotMaxCount, int modelSlotMaxCount)
        => savedCount >= 0 && liveSlotMaxCount >= 0 && modelSlotMaxCount >= 0 &&
           savedCount <= liveSlotMaxCount && savedCount <= modelSlotMaxCount;

    internal static bool VerifyRules()
        => !CanRestore(playerAmmoReady: false, hostUpgradeSnapshotApplied: false)
           && !CanRestore(playerAmmoReady: true, hostUpgradeSnapshotApplied: false)
           && !CanRestore(playerAmmoReady: false, hostUpgradeSnapshotApplied: true)
           && CanRestore(playerAmmoReady: true, hostUpgradeSnapshotApplied: true);

    internal static bool VerifySelectedSlotRules()
    {
        const int requestedIndex = 0;
        var selectedBefore = requestedIndex;
        var setterReturned = false; // SetSelectedSlot may return false when no change was needed.
        var selectedAfter = requestedIndex;
        var falseResultForAlreadySelectedSlotIsAccepted =
            !setterReturned && selectedBefore == requestedIndex &&
            IsRequestedSlotSelected(requestedIndex, selectedAfter);

        return falseResultForAlreadySelectedSlotIsAccepted
               && IsRequestedSlotSelected(requestedIndex, requestedIndex)
               && !IsRequestedSlotSelected(requestedIndex, requestedIndex + 1)
               && !IsRequestedSlotSelected(requestedIndex, -1)
               && !IsRequestedSlotSelected(-1, -1);
    }

    internal static bool VerifyCapacityRules()
        => CanRestoreCount(savedCount: 0, liveSlotMaxCount: 0, modelSlotMaxCount: 0)
           && CanRestoreCount(savedCount: 8, liveSlotMaxCount: 12, modelSlotMaxCount: 10)
           && CanRestoreCount(savedCount: 4, liveSlotMaxCount: 8, modelSlotMaxCount: 8)
           && !CanRestoreCount(savedCount: 11, liveSlotMaxCount: 10, modelSlotMaxCount: 12)
           && !CanRestoreCount(savedCount: 11, liveSlotMaxCount: 12, modelSlotMaxCount: 10)
           && !CanRestoreCount(savedCount: -1, liveSlotMaxCount: 10, modelSlotMaxCount: 10)
           && !CanRestoreCount(savedCount: 1, liveSlotMaxCount: -1, modelSlotMaxCount: 10)
           && IsSelectedIndexRestored(requestedIndex: -1, actualSelectedIndex: -1)
           && !IsSelectedIndexRestored(requestedIndex: -1, actualSelectedIndex: 0)
           && IsSelectedIndexRestored(requestedIndex: 0, actualSelectedIndex: 0)
           && !IsSelectedIndexRestored(requestedIndex: -2, actualSelectedIndex: -2);
}

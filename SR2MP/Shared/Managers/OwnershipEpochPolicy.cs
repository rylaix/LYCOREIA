namespace SR2MP.Shared.Managers;

internal enum OwnershipEpochDecision
{
    /// <summary>Apply the ownership packet and store its epoch.</summary>
    Apply,
    /// <summary>Older than the stored epoch: drop.</summary>
    Stale,
    /// <summary>Same non-zero epoch but a different owner: keep the stored owner and log.</summary>
    Conflict
}

/// <summary>
/// Pure rules for per-actor ownership epochs. Only the host issues epochs.
/// Epoch 0 means "never granted by the host": actors created by spawn packets start at 0 on both
/// peers (spawn never carries an epoch), and the first host-applied ownership change makes it 1.
/// uint wrap-around is not handled (4 billion changes of one actor); Next saturates instead.
/// </summary>
internal static class OwnershipEpochPolicy
{
    internal static OwnershipEpochDecision DecideTransfer(
        uint storedEpoch, string? storedOwner, bool hasIncomingEpoch, uint incomingEpoch, string? incomingOwner)
    {
        if (!hasIncomingEpoch || incomingEpoch > storedEpoch)
            return OwnershipEpochDecision.Apply;
        if (incomingEpoch < storedEpoch)
            return OwnershipEpochDecision.Stale;
        // Equal epoch. At 0 neither side has ordering information, so behave as before.
        if (incomingEpoch == 0 || string.Equals(storedOwner ?? string.Empty, incomingOwner ?? string.Empty,
                StringComparison.Ordinal))
            return OwnershipEpochDecision.Apply;
        return OwnershipEpochDecision.Conflict;
    }

    /// <summary>Owner-sourced updates: an older epoch is a past owner still talking.</summary>
    internal static bool ShouldAcceptUpdate(bool hasEpoch, uint incomingEpoch, uint storedEpoch)
        => !hasEpoch || incomingEpoch >= storedEpoch;

    /// <summary>Host: bump on an owner change, and on the first grant of an epoch-0 actor.</summary>
    internal static bool ShouldBump(string? previousOwner, string? newOwner, uint storedEpoch)
        => storedEpoch == 0 ||
           !string.Equals(previousOwner ?? string.Empty, newOwner ?? string.Empty, StringComparison.Ordinal);

    internal static uint Next(uint epoch) => epoch == uint.MaxValue ? epoch : epoch + 1;

#if SR2MP_TEST
    internal static bool VerifyRules()
    {
        const string a = "A", b = "B";
        return DecideTransfer(5, a, true, 6, b) == OwnershipEpochDecision.Apply &&
               DecideTransfer(5, a, true, 4, b) == OwnershipEpochDecision.Stale &&
               DecideTransfer(5, a, true, 5, a) == OwnershipEpochDecision.Apply &&
               DecideTransfer(5, a, true, 5, b) == OwnershipEpochDecision.Conflict &&
               DecideTransfer(0, a, true, 0, b) == OwnershipEpochDecision.Apply &&
               DecideTransfer(9, a, false, 0, b) == OwnershipEpochDecision.Apply &&
               ShouldAcceptUpdate(true, 5, 5) && ShouldAcceptUpdate(true, 6, 5) &&
               !ShouldAcceptUpdate(true, 4, 5) && ShouldAcceptUpdate(false, 0, 5) &&
               ShouldBump(a, b, 3) && !ShouldBump(a, a, 3) && ShouldBump(a, a, 0) &&
               Next(1) == 2 && Next(uint.MaxValue) == uint.MaxValue;
    }
#endif
}

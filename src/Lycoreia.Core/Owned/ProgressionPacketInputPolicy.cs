// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Owned;

/// <summary>
/// Rejects malformed progression packet fields before they reach game models.
/// These checks validate wire shape only; they do not establish purchase or interaction authority.
/// </summary>
public static class ProgressionPacketInputPolicy
{
    internal static bool HasIdentifier(string identifier)
        => !string.IsNullOrWhiteSpace(identifier);

    internal static bool IsDefinedEnum<TEnum>(TEnum value) where TEnum : struct, Enum
        => Enum.IsDefined(typeof(TEnum), value);

    internal static bool IsNonNegativeDepositorAmount(int amount)
        => amount >= 0;

#if LYC_TEST
    private enum FixtureState
    {
        Closed,
        Open
    }

    internal static bool VerifyRules()
        => !HasIdentifier(null)
           && !HasIdentifier(string.Empty)
           && !HasIdentifier(" \t ")
           && HasIdentifier("known-progression-id")
           && IsDefinedEnum(FixtureState.Closed)
           && IsDefinedEnum(FixtureState.Open)
           && !IsDefinedEnum((FixtureState)2)
           && IsNonNegativeDepositorAmount(0)
           && IsNonNegativeDepositorAmount(1)
           && !IsNonNegativeDepositorAmount(-1)
           && !IsNonNegativeDepositorAmount(int.MinValue);
#endif
}

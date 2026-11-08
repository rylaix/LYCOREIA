// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Owned;

/// <summary>Idempotence rule for applying a map-node unlock to MapDirector.</summary>
public static class MapUnlockPolicy
{
    internal static bool ShouldNotifyMapDirector(bool alreadyUnlocked) => !alreadyUnlocked;

    internal static bool VerifyRules()
        => ShouldNotifyMapDirector(alreadyUnlocked: false)
           && !ShouldNotifyMapDirector(alreadyUnlocked: true);
}

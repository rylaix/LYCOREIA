namespace SR2MP.Handlers.Map;

/// <summary>Idempotence rule for applying a map-node unlock to MapDirector.</summary>
internal static class MapUnlockPolicy
{
    internal static bool ShouldNotifyMapDirector(bool alreadyUnlocked) => !alreadyUnlocked;

    internal static bool VerifyRules()
        => ShouldNotifyMapDirector(alreadyUnlocked: false)
           && !ShouldNotifyMapDirector(alreadyUnlocked: true);
}

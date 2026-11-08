// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Owned;

public static class ActorDestroyPolicy
{
    internal static bool IsLocalOnlyRottenResourceCleanup(
        bool sessionActive, bool isNetworkedActor, bool locallyOwned, bool isRottenResource)
        => sessionActive && isNetworkedActor && !locallyOwned && isRottenResource;

    internal static bool ShouldSuppress(
        bool sessionActive, bool isNetworkedActor, bool locallyOwned, bool isRottenResource = false)
        => sessionActive && isNetworkedActor && !locallyOwned && !isRottenResource;

    /// <summary>
    /// Timed transforms (chick to hen, egg to chick) run only on the owner: a replica's destroy is suppressed, so its transform would
    /// spawn a fresh actor every frame (after a death time skip the elapsed delay passes at once for every replica).
    /// </summary>
    internal static bool ShouldSkipTimedTransform(bool sessionActive, bool isNetworkedActor, bool locallyOwned)
        => sessionActive && isNetworkedActor && !locallyOwned;

#if LYC_TEST
    internal static bool VerifyRules()
        => !ShouldSuppress(sessionActive: false, isNetworkedActor: true, locallyOwned: false)
           && !ShouldSuppress(sessionActive: true, isNetworkedActor: false, locallyOwned: false)
           && !ShouldSuppress(sessionActive: true, isNetworkedActor: true, locallyOwned: true)
           && ShouldSuppress(sessionActive: true, isNetworkedActor: true, locallyOwned: false)
           && !ShouldSuppress(sessionActive: true, isNetworkedActor: true, locallyOwned: false,
               isRottenResource: true)
           && IsLocalOnlyRottenResourceCleanup(sessionActive: true, isNetworkedActor: true,
               locallyOwned: false, isRottenResource: true)
           && !IsLocalOnlyRottenResourceCleanup(sessionActive: true, isNetworkedActor: true,
               locallyOwned: false, isRottenResource: false)
           && !IsLocalOnlyRottenResourceCleanup(sessionActive: true, isNetworkedActor: true,
               locallyOwned: true, isRottenResource: true)
           && ShouldSkipTimedTransform(true, true, false) && !ShouldSkipTimedTransform(true, true, true)
           && !ShouldSkipTimedTransform(false, true, false) && !ShouldSkipTimedTransform(true, false, false);
#endif
}

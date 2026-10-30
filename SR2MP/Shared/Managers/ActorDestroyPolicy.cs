namespace SR2MP.Shared.Managers;

internal static class ActorDestroyPolicy
{
    internal static bool IsLocalOnlyRottenResourceCleanup(
        bool sessionActive, bool isNetworkedActor, bool locallyOwned, bool isRottenResource)
        => sessionActive && isNetworkedActor && !locallyOwned && isRottenResource;

    internal static bool ShouldSuppress(
        bool sessionActive, bool isNetworkedActor, bool locallyOwned, bool isRottenResource = false)
        => sessionActive && isNetworkedActor && !locallyOwned && !isRottenResource;

#if SR2MP_TEST
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
               locallyOwned: true, isRottenResource: true);
#endif
}

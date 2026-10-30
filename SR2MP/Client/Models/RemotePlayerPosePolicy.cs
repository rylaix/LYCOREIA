namespace SR2MP.Client.Models;

/// <summary>Controls when a remote avatar has a safe world pose to display.</summary>
internal static class RemotePlayerPosePolicy
{
    internal static bool ShouldRenderRemoteBody(bool hasReceivedState)
        => hasReceivedState;

#if SR2MP_TEST
    internal static bool VerifyRules()
        => !ShouldRenderRemoteBody(hasReceivedState: false) &&
           ShouldRenderRemoteBody(hasReceivedState: true);
#endif
}

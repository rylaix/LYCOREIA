// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Owned;

/// <summary>Controls when a remote avatar has a safe world pose to display.</summary>
public static class RemotePlayerPosePolicy
{
    internal static bool ShouldRenderRemoteBody(bool hasReceivedState)
        => hasReceivedState;

#if LYC_TEST
    internal static bool VerifyRules()
        => !ShouldRenderRemoteBody(hasReceivedState: false) &&
           ShouldRenderRemoteBody(hasReceivedState: true);
#endif
}

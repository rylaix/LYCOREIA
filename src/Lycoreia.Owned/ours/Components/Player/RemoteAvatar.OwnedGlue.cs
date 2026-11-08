// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Game.Avatars;

public sealed partial class RemoteAvatar
{
    partial void OwnedSetup()
    {
        if (!hasAnimationController) return;
        SetupFingers();
    }

    partial void OwnedTick(float dt)
    {
        if (!hasAnimationController) return;
        UpdateKickDynamics();
        var warmup = UpdateIdleTimers();
        var release = WarmupRelease(warmup);
        var curl = LeftFingerCurl(warmup, release);
        ApplyLeftFingers(curl * AvatarPose.ForeGripCurlShare, 0f, curl * AvatarPose.ForeGripIndexShare); // fore-grip: softer wrap, index along the barrel
        AnimateKick();
    }

    partial void OwnedDestroy() { kickRig = null; }
}

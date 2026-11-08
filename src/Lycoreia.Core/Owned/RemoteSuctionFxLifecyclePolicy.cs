// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Owned;

/// <summary>Pure retry state for a remote suction visual; it never represents gameplay suction.</summary>
public sealed class RemoteSuctionFxLifecyclePolicy
{
    internal const float RetryIntervalSeconds = 0.25f;
    internal bool DesiredActive { get; private set; }
    private float nextStartAttemptAt;

    internal void SetDesired(bool active)
    {
        DesiredActive = active;
        if (!active)
            nextStartAttemptAt = 0f;
    }

    internal void MarkStartAttempt(float now)
        => nextStartAttemptAt = now + RetryIntervalSeconds;

    internal bool TryBeginStartAttempt(float now, bool hasVisual)
    {
        if (!DesiredActive || hasVisual || now < nextStartAttemptAt)
            return false;
        MarkStartAttempt(now);
        return true;
    }
}

namespace SR2MP.Components.FX;

/// <summary>Pure retry state for a remote suction visual; it never represents gameplay suction.</summary>
internal sealed class RemoteSuctionFxLifecyclePolicy
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

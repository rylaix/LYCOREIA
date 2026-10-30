namespace SR2MP.Handlers.Events;

/// <summary>Whether an accepted event request needs an authoritative count echo.</summary>
internal static class EventCanonicalReconciliationPolicy
{
    internal static bool ShouldEchoCanonicalCount(bool accepted, int requestedCount, int canonicalCount)
        => accepted && canonicalCount > 0 && requestedCount != canonicalCount;

    internal static bool VerifyRules()
        => ShouldEchoCanonicalCount(accepted: true, requestedCount: 3, canonicalCount: 5)
           && ShouldEchoCanonicalCount(accepted: true, requestedCount: 5, canonicalCount: 3)
           && !ShouldEchoCanonicalCount(accepted: true, requestedCount: 5, canonicalCount: 5)
           && !ShouldEchoCanonicalCount(accepted: true, requestedCount: 0, canonicalCount: 0)
           && !ShouldEchoCanonicalCount(accepted: false, requestedCount: 3, canonicalCount: 5);
}

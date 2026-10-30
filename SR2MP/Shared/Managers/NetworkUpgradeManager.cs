using SR2MP.Packets.Upgrade;

namespace SR2MP.Shared.Managers;

/// <summary>
/// Maintains per-upgrade authoritative revisions and client ordering state.
/// A snapshot for one upgrade must not make a live update for a different upgrade stale.
/// </summary>
internal static class NetworkUpgradeManager
{
    private const long PendingRequestTimeoutMilliseconds = 30_000;
    private static readonly UpgradeRevisionTracker hostRevisions = new();
    private static readonly UpgradeClientRevisionState clientState = new();
    internal static bool ExecutingHostPurchase { get; set; }

    internal static void ResetHostSession()
    {
        hostRevisions.Clear();
        ExecutingHostPurchase = false;
        NetworkUpgradePurchaseManager.ResetHostSession();
    }

    internal static void ResetClientSession() => clientState.Clear();

    internal static void NoteClientRequest(byte upgradeId, Guid requestId)
        => clientState.NoteRequest(upgradeId, requestId, Environment.TickCount64);

    internal static void ForgetClientRequest(Guid requestId) => clientState.ForgetRequest(requestId);

    internal static bool ForgetClientRequest(byte upgradeId, Guid requestId)
        => clientState.ForgetRequest(upgradeId, requestId);

    internal static bool TryAcceptHostRejection(byte upgradeId, Guid requestId)
        => clientState.TryAcceptHostRejection(upgradeId, requestId);

    internal static void PumpPendingRequests()
        => clientState.ExpirePendingRequests(Environment.TickCount64, PendingRequestTimeoutMilliseconds);

    internal static ulong GetHostRevision(byte upgradeId) => hostRevisions.GetCurrent(upgradeId);

    internal static Dictionary<byte, ulong> CreateHostRevisionSnapshot(IEnumerable<byte> upgradeIds)
    {
        var revisions = new Dictionary<byte, ulong>();
        foreach (var upgradeId in upgradeIds)
            revisions[upgradeId] = hostRevisions.GetCurrent(upgradeId);

        return revisions;
    }

    /// <summary>
    /// Records a snapshot revision even when a purchase request is still in flight, but delays
    /// applying the value until the matching host state arrives.
    /// </summary>
    internal static bool TryApplySnapshot(byte upgradeId, ulong revision)
    {
        return clientState.TryApplySnapshot(upgradeId, revision);
    }

    /// <summary>
    /// Accepts only newer host state. A matching request ID also accepts an equal revision
    /// once when an earlier uncorrelated live state already carried the same host revision.
    /// </summary>
    internal static bool TryAcceptHostState(byte upgradeId, ulong revision, Guid requestId)
    {
        return clientState.TryAcceptHostState(upgradeId, revision, requestId);
    }

    internal static void BroadcastHostUpgrade(byte upgradeId, sbyte level, Guid requestId = default)
    {
        if (!Main.Server.IsRunning)
            return;

        var packet = new PlayerUpgradePacket
        {
            UpgradeID = upgradeId,
            Kind = PlayerUpgradeMessageKind.State,
            Level = level,
            Revision = hostRevisions.NextRevision(upgradeId),
            RequestId = requestId
        };

        Main.Server.SendToAll(packet);
    }

    internal static bool VerifyRevisionOrdering()
    {
        var host = new UpgradeRevisionTracker();
        var client = new UpgradeClientRevisionState();

        return host.NextRevision(4) == 1
               && host.NextRevision(4) == 2
               && host.NextRevision(9) == 1
               && client.TryAcceptHostState(4, 2, Guid.Empty)
               && !client.TryAcceptHostState(4, 1, Guid.Empty)
               && !client.TryApplySnapshot(4, 1)
               && client.TryApplySnapshot(4, 2)
               && !client.TryAcceptHostState(4, 2, Guid.Empty)
               && client.TryAcceptHostState(4, 3, Guid.Empty)
               && client.TryApplySnapshot(9, 0)
               && client.TryAcceptHostState(9, 1, Guid.Empty)
               && client.GetCurrent(4) == 3
               && client.GetCurrent(9) == 1
               && VerifiesCorrelatedRequestState()
               && VerifiesPendingRequestCleanup()
               && UpgradePurchaseRequestLedger.VerifyRules();
    }

    private static bool VerifiesCorrelatedRequestState()
    {
        var client = new UpgradeClientRevisionState();
        var requestId = Guid.Parse("1e8b2d12-bbbd-4a74-95f2-72eecf970967");
        client.NoteRequest(12, requestId, requestedAtMilliseconds: 0);

        // A snapshot can advance the revision floor while the request remains pending.
        return !client.TryApplySnapshot(12, 1)
               && !client.TryAcceptHostState(12, 1, Guid.Empty)
               && !client.TryApplySnapshot(12, 1)
               && client.TryAcceptHostState(12, 2, requestId)
               && !client.TryApplySnapshot(12, 1)
               && client.TryAcceptHostState(12, 3, Guid.Empty)
               && !client.TryAcceptHostState(12, 2, Guid.Empty)
               && client.TryApplySnapshot(12, 3);
    }

    private static bool VerifiesPendingRequestCleanup()
    {
        var client = new UpgradeClientRevisionState();
        var unknownUpgradeRequest = Guid.Parse("2a89b653-4bf0-47b2-bbc6-c27e6e1d6b97");
        var staleRequest = Guid.Parse("cfeb2758-fec7-4a28-a0a2-98e417a4c55f");
        var recentRequest = Guid.Parse("1b4526b7-1292-4fdb-a930-2bf70bfc25aa");

        client.NoteRequest(17, unknownUpgradeRequest, 100);
        if (client.TryAcceptHostRejection(18, unknownUpgradeRequest) ||
            !client.TryAcceptHostRejection(17, unknownUpgradeRequest) ||
            client.TryAcceptHostRejection(17, unknownUpgradeRequest))
            return false;

        client.NoteRequest(17, staleRequest, 200);
        client.NoteRequest(18, recentRequest, 29_900);
        var expired = client.ExpirePendingRequests(30_200, PendingRequestTimeoutMilliseconds);

        if (expired != 1 || client.TryAcceptHostRejection(17, staleRequest) ||
            !client.TryAcceptHostRejection(18, recentRequest))
            return false;

        var bounded = new UpgradeClientRevisionState();
        var firstRequest = Guid.Empty;
        var lastRequest = Guid.Empty;
        for (var index = 0; index <= 1024; index++)
        {
            var requestId = Guid.Parse($"00000000-0000-0000-0000-{index + 1:x12}");
            if (index == 0)
                firstRequest = requestId;
            lastRequest = requestId;
            bounded.NoteRequest(19, requestId, index);
        }

        return !bounded.TryAcceptHostRejection(19, firstRequest) &&
               bounded.TryAcceptHostRejection(19, lastRequest);
    }
}

/// <summary>
/// Pure client-side ordering state, including the window where a purchase intent is
/// waiting for its correlated host state packet.
/// </summary>
internal sealed class UpgradeClientRevisionState
{
    private const int MaxPendingRequests = 1024;
    private readonly UpgradeRevisionTracker revisions = new();
    private readonly Dictionary<Guid, LinkedListNode<PendingRequest>> pendingRequests = new();
    private readonly LinkedList<PendingRequest> pendingRequestOrder = new();

    internal void NoteRequest(byte upgradeId, Guid requestId, long requestedAtMilliseconds)
    {
        if (requestId == Guid.Empty)
            return;

        ForgetRequest(requestId);
        while (pendingRequests.Count >= MaxPendingRequests && pendingRequestOrder.First != null)
            RemovePendingRequest(pendingRequestOrder.First);

        var node = pendingRequestOrder.AddLast(new PendingRequest(upgradeId, requestId, requestedAtMilliseconds));
        pendingRequests[requestId] = node;
    }

    internal void ForgetRequest(Guid requestId)
    {
        if (pendingRequests.TryGetValue(requestId, out var node))
            RemovePendingRequest(node);
    }

    internal bool ForgetRequest(byte upgradeId, Guid requestId)
    {
        if (!TryGetPendingRequest(upgradeId, requestId, out var node))
            return false;

        RemovePendingRequest(node);
        return true;
    }

    internal bool TryAcceptHostRejection(byte upgradeId, Guid requestId)
        => ForgetRequest(upgradeId, requestId);

    internal int ExpirePendingRequests(long nowMilliseconds, long timeoutMilliseconds)
    {
        if (timeoutMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));

        var expired = 0;
        while (pendingRequestOrder.First is { } first &&
               nowMilliseconds - first.Value.RequestedAtMilliseconds >= timeoutMilliseconds)
        {
            RemovePendingRequest(first);
            expired++;
        }

        return expired;
    }

    internal bool TryApplySnapshot(byte upgradeId, ulong revision)
    {
        if (!revisions.TryAcceptSnapshot(upgradeId, revision))
            return false;

        return !pendingRequests.Values.Any(node => node.Value.UpgradeId == upgradeId);
    }

    internal bool TryAcceptHostState(byte upgradeId, ulong revision, Guid requestId)
    {
        var matchedRequest = false;
        if (requestId != Guid.Empty && pendingRequests.TryGetValue(requestId, out var requestedUpgrade))
        {
            if (requestedUpgrade.Value.UpgradeId != upgradeId)
                return false;

            RemovePendingRequest(requestedUpgrade);
            matchedRequest = true;
        }

        if (revisions.TryAcceptLive(upgradeId, revision))
            return true;

        return matchedRequest && revisions.IsCurrent(upgradeId, revision);
    }

    internal ulong GetCurrent(byte upgradeId) => revisions.GetCurrent(upgradeId);

    internal void Clear()
    {
        revisions.Clear();
        pendingRequests.Clear();
        pendingRequestOrder.Clear();
    }

    private bool TryGetPendingRequest(byte upgradeId, Guid requestId,
        out LinkedListNode<PendingRequest> node)
    {
        if (requestId != Guid.Empty && pendingRequests.TryGetValue(requestId, out node!) &&
            node.Value.UpgradeId == upgradeId)
            return true;

        node = null!;
        return false;
    }

    private void RemovePendingRequest(LinkedListNode<PendingRequest> node)
    {
        pendingRequests.Remove(node.Value.RequestId);
        pendingRequestOrder.Remove(node);
    }

    private readonly record struct PendingRequest(byte UpgradeId, Guid RequestId, long RequestedAtMilliseconds);
}

/// <summary>Pure revision ordering state, independent of Unity/IL2CPP.</summary>
internal sealed class UpgradeRevisionTracker
{
    private readonly Dictionary<byte, ulong> revisions = new();

    internal ulong GetCurrent(byte upgradeId)
        => revisions.TryGetValue(upgradeId, out var revision) ? revision : 0;

    internal ulong NextRevision(byte upgradeId)
    {
        var current = GetCurrent(upgradeId);
        if (current == ulong.MaxValue)
            throw new InvalidOperationException($"Upgrade revision exhausted for id {upgradeId}.");

        var next = current + 1;
        revisions[upgradeId] = next;
        return next;
    }

    internal bool TryAcceptLive(byte upgradeId, ulong revision)
    {
        if (revisions.TryGetValue(upgradeId, out var current) && revision <= current)
            return false;

        revisions[upgradeId] = revision;
        return true;
    }

    internal bool TryAcceptSnapshot(byte upgradeId, ulong revision)
    {
        if (revisions.TryGetValue(upgradeId, out var current) && revision < current)
            return false;

        revisions[upgradeId] = revision;
        return true;
    }

    internal bool IsCurrent(byte upgradeId, ulong revision)
        => revisions.TryGetValue(upgradeId, out var current) && current == revision;

    internal void Clear() => revisions.Clear();
}

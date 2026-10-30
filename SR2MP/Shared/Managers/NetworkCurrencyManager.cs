using SR2MP.Packets.Economy;

namespace SR2MP.Shared.Managers;

/// <summary>
/// Tracks ordered host currency states and client delta requests. Delta requests keep
/// honest peers from overwriting a shared balance with an old absolute snapshot.
/// Structural checks cannot prove a guest earned positive currency: the packet has no
/// host-observed sale or inventory evidence, so this remains an explicit trust boundary.
/// </summary>
internal static class NetworkCurrencyManager
{
    private const int MaxRememberedRequests = 8192;
    private static readonly CurrencyRevisionState hostRevisions = new();
    private static readonly CurrencyRevisionState clientRevisions = new();
    private static readonly CurrencyRequestLedger requestLedger = new(MaxRememberedRequests);

    internal static ulong GetHostRevision(byte currencyType) => hostRevisions.GetCurrent(currencyType);

    internal static Dictionary<byte, ulong> CreateHostRevisionSnapshot(IEnumerable<byte> currencyTypes)
    {
        var revisions = new Dictionary<byte, ulong>();
        foreach (var currencyType in currencyTypes)
            revisions[currencyType] = hostRevisions.GetCurrent(currencyType);

        return revisions;
    }

    internal static bool ApplyClientSnapshot(byte currencyType, ulong revision)
        => clientRevisions.TryApplySnapshot(currencyType, revision);

    internal static bool TryAcceptClientState(byte currencyType, ulong revision, Guid requestId)
        => clientRevisions.TryAcceptState(currencyType, revision, requestId);

    internal static void ResetClientSession() => clientRevisions.Clear();

    internal static bool SendClientDelta(CurrencyPacket packet)
    {
        if (packet.Kind != CurrencyMessageKind.Request || packet.RequestId != Guid.Empty ||
            !Main.Client.IsConnected || Main.Server.IsRunning)
            return false;

        packet.RequestId = Guid.NewGuid();
        packet.Revision = clientRevisions.GetCurrent(packet.CurrencyType);
        clientRevisions.NoteRequest(packet.CurrencyType, packet.RequestId);

        try
        {
            Main.Client.SendPacket(packet);
            return true;
        }
        catch (Exception exception)
        {
            clientRevisions.ForgetRequest(packet.RequestId);
            SrLogger.LogError($"Could not send currency delta request {packet.RequestId}: {exception}");
            return false;
        }
    }

    internal static CurrencyPacket CreateHostState(byte currencyType, int amount, bool showUiNotification,
        int sourceIdent, Guid requestId = default, bool incrementRevision = false)
    {
        var revision = incrementRevision
            ? hostRevisions.Next(currencyType)
            : hostRevisions.GetCurrent(currencyType);

        return new CurrencyPacket
        {
            Kind = CurrencyMessageKind.State,
            CurrencyType = currencyType,
            RequestId = requestId,
            Revision = revision,
            NewAmount = amount,
            ShowUINotification = showUiNotification,
            SourceIdent = sourceIdent
        };
    }

    internal static CurrencyRequestDisposition BeginHostRequest(string endpoint, CurrencyPacket packet)
        => requestLedger.TryBegin(endpoint, packet);

    internal static bool VerifyProtocolRules()
        => CurrencyRequestLedger.VerifyRules()
           && CurrencyRevisionState.VerifyRules()
           && CurrencyDeltaRequestPolicy.VerifyRules()
           && CurrencyPacket.VerifySerializationRoundTrip();
}

/// <summary>
/// Checks facts derivable from the current delta packet only. It intentionally does
/// not call a positive delta legitimate: a peer can forge a self-consistent request.
/// </summary>
internal static class CurrencyDeltaRequestPolicy
{
    internal static bool IsStructurallyValid(CurrencyPacket packet, out string rejection)
    {
        if (packet.RequestId == Guid.Empty)
        {
            rejection = "empty request id";
            return false;
        }

        if (packet.CurrencyType == 0 || packet.BeforeAmount < 0 || packet.AfterAmount < 0 ||
            packet.RequestedAmount < 0 || packet.SourceIdent < -1)
        {
            rejection = "invalid currency id, amount, or source sentinel";
            return false;
        }

        var actualDelta = (long)packet.AfterAmount - packet.BeforeAmount;
        if (actualDelta != packet.Delta)
        {
            rejection = "delta does not match observed before/after amounts";
            return false;
        }

        if (packet.Delta > 0 && (packet.RequestedAmount == 0 || packet.Delta > packet.RequestedAmount))
        {
            rejection = "positive delta exceeds the native AddCurrency amount";
            return false;
        }

        if (packet.Delta < 0 && (long)-packet.Delta > packet.RequestedAmount)
        {
            rejection = "negative delta exceeds the native SpendCurrency amount";
            return false;
        }

        rejection = string.Empty;
        return true;
    }

    internal static bool VerifyRules()
    {
        var consistentIncome = new CurrencyPacket
        {
            Kind = CurrencyMessageKind.Request,
            CurrencyType = 1,
            RequestId = Guid.Parse("fd47cf7b-fd58-4d4f-8a4c-1df8996b7e20"),
            BeforeAmount = 50,
            AfterAmount = 150,
            Delta = 100,
            RequestedAmount = 100,
            SourceIdent = -1
        };

        return IsStructurallyValid(consistentIncome, out _) &&
               !IsStructurallyValid(consistentIncome with { RequestedAmount = 99 }, out _) &&
               !IsStructurallyValid(consistentIncome with { AfterAmount = 151 }, out _) &&
               !IsStructurallyValid(consistentIncome with { SourceIdent = -2 }, out _);
    }
}

internal enum CurrencyRequestDisposition
{
    Invalid,
    New,
    Duplicate,
    ConflictingDuplicate
}

/// <summary>Pure per-endpoint request deduplication with a bounded FIFO cache.</summary>
internal sealed class CurrencyRequestLedger
{
    private readonly int capacity;
    private readonly Dictionary<(string Endpoint, Guid RequestId), CurrencyRequestFingerprint> requests = new();
    private readonly Queue<(string Endpoint, Guid RequestId)> order = new();

    internal CurrencyRequestLedger(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        this.capacity = capacity;
    }

    internal CurrencyRequestDisposition TryBegin(string endpoint, CurrencyPacket packet)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || packet.Kind != CurrencyMessageKind.Request ||
            packet.RequestId == Guid.Empty)
            return CurrencyRequestDisposition.Invalid;

        var key = (endpoint, packet.RequestId);
        var fingerprint = CurrencyRequestFingerprint.From(packet);
        if (requests.TryGetValue(key, out var previous))
            return previous == fingerprint
                ? CurrencyRequestDisposition.Duplicate
                : CurrencyRequestDisposition.ConflictingDuplicate;

        requests.Add(key, fingerprint);
        order.Enqueue(key);
        while (order.Count > capacity)
            requests.Remove(order.Dequeue());

        return CurrencyRequestDisposition.New;
    }

    internal void Clear()
    {
        requests.Clear();
        order.Clear();
    }

    internal static bool VerifyRules()
    {
        var ledger = new CurrencyRequestLedger(2);
        var requestId = Guid.Parse("79b0180e-ea57-412f-aa35-dfef19f821c5");
        var packet = new CurrencyPacket
        {
            Kind = CurrencyMessageKind.Request,
            CurrencyType = 1,
            RequestId = requestId,
            Delta = 10,
            BeforeAmount = 5,
            AfterAmount = 15,
            RequestedAmount = 10,
            SourceIdent = -1
        };

        if (ledger.TryBegin("127.0.0.1:1001", packet) != CurrencyRequestDisposition.New ||
            ledger.TryBegin("127.0.0.1:1001", packet) != CurrencyRequestDisposition.Duplicate ||
            ledger.TryBegin("127.0.0.1:1002", packet) != CurrencyRequestDisposition.New)
            return false;

        var conflicting = packet;
        conflicting.Delta++;
        if (ledger.TryBegin("127.0.0.1:1001", conflicting) != CurrencyRequestDisposition.ConflictingDuplicate)
            return false;

        return ledger.TryBegin(string.Empty, packet) == CurrencyRequestDisposition.Invalid &&
               ledger.TryBegin("127.0.0.1:1001", new CurrencyPacket
               {
                   Kind = CurrencyMessageKind.Request,
                   CurrencyType = 1,
                   RequestId = Guid.Empty
               }) == CurrencyRequestDisposition.Invalid;
    }

    private readonly record struct CurrencyRequestFingerprint(
        byte CurrencyType,
        int Delta,
        int BeforeAmount,
        int AfterAmount,
        int RequestedAmount,
        bool ShowUINotification,
        int SourceIdent)
    {
        internal static CurrencyRequestFingerprint From(CurrencyPacket packet)
            => new(packet.CurrencyType, packet.Delta, packet.BeforeAmount, packet.AfterAmount,
                packet.RequestedAmount, packet.ShowUINotification, packet.SourceIdent);
    }
}

/// <summary>Pure per-currency revision ordering for host output and client input.</summary>
internal sealed class CurrencyRevisionState
{
    private const int MaxPendingRequests = 1024;
    private readonly Dictionary<byte, ulong> revisions = new();
    private readonly Dictionary<Guid, byte> pendingRequests = new();
    private readonly Queue<Guid> pendingRequestOrder = new();

    internal ulong GetCurrent(byte currencyType)
        => revisions.TryGetValue(currencyType, out var revision) ? revision : 0;

    internal ulong Next(byte currencyType)
    {
        var current = GetCurrent(currencyType);
        if (current == ulong.MaxValue)
            throw new InvalidOperationException($"Currency revision exhausted for type {currencyType}.");

        var next = current + 1;
        revisions[currencyType] = next;
        return next;
    }

    internal void NoteRequest(byte currencyType, Guid requestId)
    {
        if (requestId != Guid.Empty)
        {
            if (!pendingRequests.ContainsKey(requestId))
                pendingRequestOrder.Enqueue(requestId);
            pendingRequests[requestId] = currencyType;

            while (pendingRequestOrder.Count > MaxPendingRequests)
                pendingRequests.Remove(pendingRequestOrder.Dequeue());
        }
    }

    internal void ForgetRequest(Guid requestId) => pendingRequests.Remove(requestId);

    internal bool TryApplySnapshot(byte currencyType, ulong revision)
    {
        if (revision < GetCurrent(currencyType))
            return false;

        revisions[currencyType] = revision;
        return true;
    }

    internal bool TryAcceptState(byte currencyType, ulong revision, Guid requestId)
    {
        var matchingRequest = false;
        if (requestId != Guid.Empty && pendingRequests.TryGetValue(requestId, out var requestedCurrency))
        {
            if (requestedCurrency != currencyType)
                return false;

            matchingRequest = true;
            pendingRequests.Remove(requestId);
        }

        if (revision > GetCurrent(currencyType))
        {
            revisions[currencyType] = revision;
            return true;
        }

        return matchingRequest && revision == GetCurrent(currencyType);
    }

    internal void Clear()
    {
        revisions.Clear();
        pendingRequests.Clear();
        pendingRequestOrder.Clear();
    }

    internal static bool VerifyRules()
    {
        var state = new CurrencyRevisionState();
        var requestId = Guid.Parse("c204f725-f317-4e53-930d-3f1901acb79b");
        state.NoteRequest(2, requestId);

        return state.Next(1) == 1
               && state.Next(1) == 2
               && state.Next(2) == 1
               && state.TryApplySnapshot(2, 1)
               && !state.TryApplySnapshot(2, 0)
               && !state.TryAcceptState(2, 1, Guid.Empty)
               && state.TryAcceptState(2, 1, requestId)
               && !state.TryAcceptState(2, 1, Guid.Empty)
               && state.TryAcceptState(1, 3, Guid.Empty)
               && !state.TryAcceptState(1, 2, Guid.Empty)
               && state.GetCurrent(1) == 3
               && state.GetCurrent(2) == 1;
    }
}

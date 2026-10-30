using System.Net;
using Il2CppMonomiPark.SlimeRancher.Economy;
using Il2CppMonomiPark.SlimeRancher.UI.Framework.Data;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Economy;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Currency;

[PacketHandler((byte)PacketType.CurrencyAdjust)]
internal sealed class CurrencyHandler : BasePacketHandler<CurrencyPacket>
{
    protected override bool Handle(CurrencyPacket packet, IPEndPoint? endpoint)
    {
        if (!TryFindCurrency(packet.CurrencyType, out var currency))
            return false;

        if (IsServerSide)
        {
            if (packet.Kind != CurrencyMessageKind.Request || endpoint == null ||
                !Main.Server.ClientManager.TryGetClient(endpoint, out var sender) || sender == null)
                return false;

            HandleHostRequest(packet, endpoint, sender.PlayerId, currency);
            return false;
        }

        if (packet.Kind != CurrencyMessageKind.State || packet.NewAmount < 0 ||
            !NetworkCurrencyManager.TryAcceptClientState(
                packet.CurrencyType, packet.Revision, packet.RequestId))
            return false;

        ApplyState(packet, currency);
        return false;
    }

    private void HandleHostRequest(CurrencyPacket packet, IPEndPoint endpoint, string playerId, ICurrency currency)
    {
        var playerState = SceneContext.Instance?.PlayerState;
        if (!playerState)
            return;

        var currentAmount = playerState.GetCurrency(currency);
        if (currentAmount < 0)
            return;

        var disposition = NetworkCurrencyManager.BeginHostRequest(endpoint.ToString(), packet);
        if (disposition != CurrencyRequestDisposition.New)
        {
            if (disposition != CurrencyRequestDisposition.Invalid)
                SendCanonicalState(endpoint, packet, currentAmount, showUiNotification: false,
                    sourceIdent: -1, incrementRevision: false);
            return;
        }

        if (!IsValidDeltaRequest(packet, out var rejection))
        {
            SrLogger.LogWarning(
                $"Rejected currency request={packet.RequestId} endpoint={endpoint}: {rejection}.");
            SendCanonicalState(endpoint, packet, currentAmount, showUiNotification: false,
                sourceIdent: -1, incrementRevision: false);
            return;
        }

        var delta = packet.Delta;
        var requestedBalance = (long)currentAmount + delta;
        if (delta < 0 && -(long)delta > currentAmount || requestedBalance is < 0 or > int.MaxValue)
        {
            SrLogger.LogMessage(
                $"Reconciled unaffordable currency spend request={packet.RequestId} endpoint={endpoint} " +
                $"currency={packet.CurrencyType} delta={delta} hostBalance={currentAmount}.");
            SendCanonicalState(endpoint, packet, currentAmount, showUiNotification: false,
                sourceIdent: -1, incrementRevision: false);
            return;
        }

        if (packet.RequestedAmount > 0 && delta < 0 && packet.BeforeAmount < packet.RequestedAmount)
        {
            SrLogger.LogMessage(
                $"Reconciled guest-unaffordable currency spend request={packet.RequestId} endpoint={endpoint} " +
                $"currency={packet.CurrencyType} requested={packet.RequestedAmount} " +
                $"guestBefore={packet.BeforeAmount}.");
            SendCanonicalState(endpoint, packet, currentAmount, showUiNotification: false,
                sourceIdent: -1, incrementRevision: false);
            return;
        }

        var source = ResolveSource(packet.SourceIdent);
        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            if (delta < 0)
                playerState.SpendCurrency(currency, (int)-(long)delta, source!);
            else if (delta > 0)
                playerState.AddCurrency(currency, delta, packet.ShowUINotification, source!);
        }
        catch (Exception exception)
        {
            SrLogger.LogError(
                $"Currency delta request={packet.RequestId} endpoint={endpoint} threw: {exception}");
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        var authoritativeAmount = playerState.GetCurrency(currency);
        var changed = authoritativeAmount != currentAmount;
        Logger.LogDiagnostic("economy", "currency-request-resolved",
            $"requestId={packet.RequestId}; player={playerId}; currency={packet.CurrencyType}; delta={delta}; " +
            $"before={currentAmount}; after={authoritativeAmount}; changed={changed}; requestedAmount={packet.RequestedAmount}");
        var acknowledgement = NetworkCurrencyManager.CreateHostState(
            packet.CurrencyType, authoritativeAmount, packet.ShowUINotification, packet.SourceIdent,
            packet.RequestId, incrementRevision: changed);

        Main.Server.SendToClient(acknowledgement, endpoint);
        if (!changed)
            return;

        acknowledgement.RequestId = Guid.Empty;
        Main.Server.SendToAllExcept(acknowledgement, endpoint);
    }

    private static bool IsValidDeltaRequest(CurrencyPacket packet, out string rejection)
    {
        if (!CurrencyDeltaRequestPolicy.IsStructurallyValid(packet, out rejection))
            return false;

        // This structural gate rejects impossible amount combinations. A positive
        // request whose fields are mutually consistent is still not proof of a sale;
        // the current player-inventory and market hooks do not give the host evidence
        // to validate guest-owned inventory without changing normal guest economics.
        return true;
    }

    private static bool TryFindCurrency(byte currencyType, out ICurrency currency)
    {
        currency = null!;
        if (currencyType == 0)
            return false;

        var entries = GameContext.Instance?.LookupDirector?._currencyList?._currencies;
        if (entries == null)
            return false;

        foreach (var entry in entries)
        {
            if (entry == null)
                continue;

            var candidate = entry.Cast<ICurrency>();
            if (candidate.PersistenceId != currencyType)
                continue;

            currency = candidate;
            return true;
        }

        return false;
    }

    private static IUIDisplayData? ResolveSource(int sourceIdent)
        => sourceIdent != -1 && ActorManager.ActorTypes.TryGetValue(sourceIdent, out var sourceType)
            ? sourceType.TryCast<IUIDisplayData>()
            : null;

    private static void SendCanonicalState(IPEndPoint endpoint, CurrencyPacket request, int amount,
        bool showUiNotification, int sourceIdent, bool incrementRevision)
    {
        var state = NetworkCurrencyManager.CreateHostState(
            request.CurrencyType, amount, showUiNotification, sourceIdent, request.RequestId, incrementRevision);
        Main.Server.SendToClient(state, endpoint);
    }

    private static void ApplyState(CurrencyPacket packet, ICurrency currency)
    {
        var scene = SceneContext.Instance;
        if (!scene || scene.PlayerState == null)
            return;

        var currentAmount = scene.PlayerState.GetCurrency(currency);
        if (currentAmount < 0)
            return;

        var difference = (long)packet.NewAmount - currentAmount;
        if (difference == 0)
        {
            Logger.LogDiagnostic("economy", "currency-state-unchanged",
                $"requestId={packet.RequestId}; currency={packet.CurrencyType}; amount={currentAmount}; revision={packet.Revision}");
            return;
        }

        var source = ResolveSource(packet.SourceIdent);
        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            if (difference < 0)
                scene.PlayerState.SpendCurrency(currency, (int)-difference, source!);
            else
                scene.PlayerState.AddCurrency(currency, (int)difference, packet.ShowUINotification, source!);

            // Native SpendCurrency can refuse a debit when the guest's local cache is behind.
            // The host packet is canonical, so finish by setting the exact model balance.
            if (scene.PlayerState.GetCurrency(currency) != packet.NewAmount)
                scene.PlayerState._model.SetCurrency(currency, packet.NewAmount);
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        Logger.LogDiagnostic("economy", "currency-state-applied",
            $"requestId={packet.RequestId}; currency={packet.CurrencyType}; before={currentAmount}; " +
            $"after={scene.PlayerState.GetCurrency(currency)}; revision={packet.Revision}; source={packet.SourceIdent}");
    }
}

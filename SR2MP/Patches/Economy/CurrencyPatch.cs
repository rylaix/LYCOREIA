using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Economy;
using Il2CppMonomiPark.SlimeRancher.UI.Framework.Data;
using SR2MP.Packets.Economy;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Economy;

[HarmonyPatch(typeof(PlayerState))]
internal static class CurrencyPatch
{
    // Prevent Add/SpendCurrency model callbacks from sending a second notification.
    private static bool handlingCurrencyCall;

    [HarmonyPrefix, HarmonyPatch(nameof(PlayerState.AddCurrency))]
    public static void AddCurrencyPrefix(PlayerState __instance, ICurrency currencyDefinition, int adjust,
        bool showUiNotification, IUIDisplayData sourceOfChange, out CurrencyPatchState __state)
        => __state = BeginCurrencyCall(__instance, currencyDefinition, adjust, showUiNotification, sourceOfChange);

    [HarmonyPostfix, HarmonyPatch(nameof(PlayerState.AddCurrency))]
    public static void AddCurrency(PlayerState __instance, ICurrency currencyDefinition, int adjust,
        bool showUiNotification, IUIDisplayData sourceOfChange, CurrencyPatchState __state)
        => PublishObservedChange(__instance, __state.Observation);

    [HarmonyFinalizer, HarmonyPatch(nameof(PlayerState.AddCurrency))]
    public static Exception? AddCurrencyFinalizer(Exception? __exception, CurrencyPatchState __state)
        => RestoreCurrencyCall(__exception, __state);

    [HarmonyPrefix, HarmonyPatch(nameof(PlayerState.AddCurrencyDisplayDelta))]
    public static void AddCurrencyDisplayDeltaPrefix(PlayerState __instance, ICurrency currencyDefinition,
        int adjust, bool showUiNotification, IUIDisplayData sourceOfChange, out CurrencyPatchState __state)
        => __state = BeginCurrencyCall(__instance, currencyDefinition, adjust, showUiNotification, sourceOfChange);

    [HarmonyPostfix, HarmonyPatch(nameof(PlayerState.AddCurrencyDisplayDelta))]
    public static void AddCurrencyDisplayDelta(PlayerState __instance, ICurrency currencyDefinition, int adjust,
        bool showUiNotification, IUIDisplayData sourceOfChange, CurrencyPatchState __state)
        => PublishObservedChange(__instance, __state.Observation);

    [HarmonyFinalizer, HarmonyPatch(nameof(PlayerState.AddCurrencyDisplayDelta))]
    public static Exception? AddCurrencyDisplayDeltaFinalizer(Exception? __exception, CurrencyPatchState __state)
        => RestoreCurrencyCall(__exception, __state);

    [HarmonyPrefix, HarmonyPatch(nameof(PlayerState.SpendCurrency))]
    public static void SpendCurrencyPrefix(PlayerState __instance, ICurrency currency, int adjust,
        IUIDisplayData sourceOfChange, out CurrencyPatchState __state)
        => __state = BeginCurrencyCall(__instance, currency, adjust, showUiNotification: true, sourceOfChange);

    [HarmonyPostfix, HarmonyPatch(nameof(PlayerState.SpendCurrency))]
    public static void SpendCurrency(PlayerState __instance, ICurrency currency, int adjust,
        IUIDisplayData sourceOfChange, CurrencyPatchState __state)
        => PublishObservedChange(__instance, __state.Observation);

    [HarmonyFinalizer, HarmonyPatch(nameof(PlayerState.SpendCurrency))]
    public static Exception? SpendCurrencyFinalizer(Exception? __exception, CurrencyPatchState __state)
        => RestoreCurrencyCall(__exception, __state);

    [HarmonyPostfix, HarmonyPatch(nameof(PlayerState.HandleModelCurrencyChanged))]
    public static void HandleModelCurrencyChanged(ICurrency currency, int oldAmount, int newAmount)
    {
        if (HandlingPacket || handlingCurrencyCall || oldAmount == newAmount ||
            (!Main.Server.IsRunning && !Main.Client.IsConnected))
            return;

        if (currency.PersistenceId is < byte.MinValue or > byte.MaxValue)
            return;

        PublishObservedChange(new CurrencyObservation(
            (byte)currency.PersistenceId,
            currency,
            oldAmount,
            newAmount,
            0,
            true,
            -1));
    }

    private static CurrencyPatchState BeginCurrencyCall(PlayerState playerState, ICurrency currency,
        int requestedAmount, bool showUiNotification, IUIDisplayData? sourceOfChange)
    {
        var wasHandlingCurrencyCall = handlingCurrencyCall;
        handlingCurrencyCall = true;

        CurrencyObservation? observation = null;
        if (!wasHandlingCurrencyCall && !HandlingPacket &&
            (Main.Server.IsRunning || Main.Client.IsConnected) && currency != null &&
            currency.PersistenceId is >= byte.MinValue and <= byte.MaxValue)
        {
            observation = new CurrencyObservation(
                (byte)currency.PersistenceId,
                currency,
                playerState.GetCurrency(currency),
                0,
                NormalizeRequestedAmount(requestedAmount),
                showUiNotification,
                GetSourceIdent(sourceOfChange));
        }

        return new CurrencyPatchState(wasHandlingCurrencyCall, observation);
    }

    private static void PublishObservedChange(PlayerState playerState, CurrencyObservation? observation)
    {
        if (observation == null || HandlingPacket)
            return;

        var afterAmount = playerState.GetCurrency(observation.Currency);
        if (afterAmount < 0)
            return;

        PublishObservedChange(observation with { AfterAmount = afterAmount });
    }

    private static void PublishObservedChange(CurrencyObservation observation)
    {
        var delta = (long)observation.AfterAmount - observation.BeforeAmount;
        if (delta is < int.MinValue or > int.MaxValue)
        {
            SrLogger.LogWarning(
                $"Skipped currency change outside signed packet range: type={observation.CurrencyType}, " +
                $"before={observation.BeforeAmount}, after={observation.AfterAmount}.");
            return;
        }

        var packet = new CurrencyPacket
        {
            Kind = CurrencyMessageKind.Request,
            CurrencyType = observation.CurrencyType,
            Delta = (int)delta,
            BeforeAmount = observation.BeforeAmount,
            AfterAmount = observation.AfterAmount,
            RequestedAmount = observation.RequestedAmount,
            ShowUINotification = observation.ShowUINotification,
            SourceIdent = observation.SourceIdent
        };

        if (Main.Server.IsRunning)
        {
            if (delta == 0)
                return;

            var state = NetworkCurrencyManager.CreateHostState(
                observation.CurrencyType,
                observation.AfterAmount,
                observation.ShowUINotification,
                observation.SourceIdent,
                incrementRevision: true);
            Main.Server.SendToAll(state);
            return;
        }

        if (!Main.Client.IsConnected || (delta == 0 && observation.RequestedAmount == 0))
            return;

        NetworkCurrencyManager.SendClientDelta(packet);
    }

    private static Exception? RestoreCurrencyCall(Exception? exception, CurrencyPatchState state)
    {
        handlingCurrencyCall = state.PreviousHandlingState;
        return exception;
    }

    private static int NormalizeRequestedAmount(int amount)
        => amount == int.MinValue ? int.MaxValue : Math.Abs(amount);

    private static int GetSourceIdent(IUIDisplayData? sourceOfChange)
    {
        var identType = sourceOfChange?.TryCast<IdentifiableType>();
        return identType != null ? NetworkActorManager.GetPersistentID(identType) : -1;
    }

    internal sealed record CurrencyPatchState(bool PreviousHandlingState, CurrencyObservation? Observation);

    internal sealed record CurrencyObservation(
        byte CurrencyType,
        ICurrency Currency,
        int BeforeAmount,
        int AfterAmount,
        int RequestedAmount,
        bool ShowUINotification,
        int SourceIdent);
}

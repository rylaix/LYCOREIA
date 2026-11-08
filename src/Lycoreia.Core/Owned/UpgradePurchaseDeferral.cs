// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Owned;

/// <summary>
/// Decides when the host may run a guest's upgrade purchase through the native Fabricator transaction.
/// The transaction spends currency (CurrencyChanged -> FabricatorUI.HandleCurrencyChanged, coin popup) and levels the
/// upgrade while the host's own Fabricator UI is alive; running it under an open UI crashed the host, so a request waits
/// until the UI is closed, and is refused after a bounded wait (the guest's own request timeout is 30 s).
/// </summary>
public static class UpgradePurchaseDeferral
{
    public enum Verdict { RunNow, Wait, Refuse }

    public const long MaxWaitMilliseconds = 20_000;

    public static Verdict Decide(bool hostFabricatorUiOpen, long queuedAtMilliseconds, long nowMilliseconds)
    {
        if (!hostFabricatorUiOpen) return Verdict.RunNow;
        return nowMilliseconds - queuedAtMilliseconds >= MaxWaitMilliseconds ? Verdict.Refuse : Verdict.Wait;
    }
}

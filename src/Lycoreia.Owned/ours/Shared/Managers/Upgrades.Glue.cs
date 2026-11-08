// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Lycoreia.Net;
using Lycoreia.Net.Messages;

namespace Lycoreia.Owned.Shared.Managers;

internal static class UpgradesGlue
{
    internal static void Register()
    {
        var sync = Lycoreia.Game.World.WorldRuntime.Upgrades; if (sync == null) return;
        sync.PurchaseHandler = (c, m) =>
        {
            // a host-run native transaction: its currency and component hooks must publish (lift the applying flag)
            NetworkUpgradePurchaseManager.Enqueue(m, c.Sender);   // run from the pump, with the applying flag lifted there
            return true;
        };
        sync.SuppressHostLocal = () => NetworkUpgradeManager.ExecutingHostPurchase;
        sync.AcceptState = m => m.UpgradeId is >= byte.MinValue and <= byte.MaxValue && NetworkUpgradeManager.TryAcceptHostState((byte)m.UpgradeId, m.Revision, m.RequestId);
        sync.AcceptSnapshot = (id, rev) => id is >= byte.MinValue and <= byte.MaxValue && NetworkUpgradeManager.TryApplySnapshot((byte)id, rev);
        sync.OnRejected = m =>
        {
            if (m.UpgradeId is >= byte.MinValue and <= byte.MaxValue && NetworkUpgradeManager.TryAcceptHostRejection((byte)m.UpgradeId, m.RequestId))
                OwnedLog.LogWarning($"Host rejected the upgrade purchase {m.RequestId} (upgrade {m.UpgradeId}).");
        };
        Mod.Session.ServerStarted += NetworkUpgradeManager.ResetHostSession;
        Mod.Session.ClientJoined += NetworkUpgradeManager.ResetClientSession;
        Mod.Session.ClientDisconnected += _ => NetworkUpgradeManager.ResetClientSession();
    }

    internal static void Pump()
    {
        if (ModFacade.Server.IsRunning) NetworkUpgradePurchaseManager.Pump();
        else if (ModFacade.Client.IsConnected) NetworkUpgradeManager.PumpPendingRequests();
    }
}

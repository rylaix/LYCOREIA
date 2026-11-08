// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Il2CppMonomiPark.SlimeRancher.Event;
using Il2CppMonomiPark.SlimeRancher.RecipePinning;
using Il2CppMonomiPark.SlimeRancher.UI.Fabricator;
using PlayerUpgradePacket = Lycoreia.Net.Messages.PlayerUpgrade;
using PlayerUpgradeMessageKind = Lycoreia.Net.Messages.PlayerUpgradeKind;
using Lycoreia.Owned;
using UnityEngine;

namespace Lycoreia.Owned.Shared.Managers;

/// <summary>
/// Runs player-upgrade purchases through the game's IFabricatableItem transaction
/// on the host. The host uses a configured Fabricator category to supply items,
/// recipes and the upgrade event producer; it never constructs a synthetic item.
/// </summary>
internal static class NetworkUpgradePurchaseManager
{
    private const int MaxRememberedRequests = 4096;
    private static readonly UpgradePurchaseRequestLedger Decisions = new(MaxRememberedRequests);

    // A guest's request is run from the pump, never inside the network callback (the host may be mid-frame in a menu).
    private static readonly Queue<(PlayerUpgradePacket Request, Lycoreia.Net.Peer Endpoint, long QueuedAt)> Pending = new();
    private const int MaxPending = 64;

    internal static void ResetHostSession() { Decisions.Clear(); Pending.Clear(); }

    internal static void Enqueue(PlayerUpgradePacket request, Lycoreia.Net.Peer? endpoint)
    {
        if (endpoint == null || Pending.Count >= MaxPending) return;
        Pending.Enqueue((request, endpoint, Environment.TickCount64));
    }

    internal static bool RequestClientPurchase(PlayerUpgradeFabricatableItem item, int count)
    {
        if (HandlingPacket || !ModFacade.Client.IsConnected || item == null)
            return NotSent("not connected or no item");
        if (count != 1)
            return NotSent($"count={count}");

        var definition = item.UpgradeDefinition;
        if (definition == null)
            return NotSent("no upgrade definition");
        if (definition._uniqueId is < byte.MinValue or > byte.MaxValue)
            return NotSent($"upgrade id {definition._uniqueId} does not fit the packet");

        // Same level source as the host (UpgradeModel.GetUpgradeLevel). An upgrade never bought can be -1.
        var model = SceneContext.Instance?.PlayerState?._model?.upgradeModel;
        var currentLevel = model != null ? model.GetUpgradeLevel(definition) : item.CurrentUpgradeLevel;
        if (currentLevel is < -1 or > sbyte.MaxValue)
            return NotSent($"level {currentLevel} out of range");

        OwnedLog.LogMessage($"Upgrade purchase: asking host for '{definition.name}' (id={definition._uniqueId}, level={currentLevel}).");
        var packet = new PlayerUpgradePacket
        {
            UpgradeId = (byte)definition._uniqueId,
            Kind = PlayerUpgradeMessageKind.Request,
            Level = (sbyte)currentLevel,
            RequestId = Guid.NewGuid()
        };

        NetworkUpgradeManager.NoteClientRequest((byte)packet.UpgradeId, packet.RequestId);
        try
        {
            ModFacade.Client.SendPacket(packet);
            return true;
        }
        catch (Exception exception)
        {
            NetworkUpgradeManager.ForgetClientRequest(packet.RequestId);
            OwnedLog.LogError($"Could not send upgrade purchase request {packet.RequestId}: {exception}");
            return false;
        }
    }

    private static bool NotSent(string reason)
    {
        OwnedLog.LogWarning($"Upgrade purchase not sent: {reason}.");
        if (Components.UI.MultiplayerUI.Instance)
            Components.UI.MultiplayerUI.Instance.ShowLocalSystemMessage($"Upgrade purchase failed: {reason}.");
        return false;
    }

    internal static void HandleHostRequest(PlayerUpgradePacket request, Lycoreia.Net.Peer? endpoint)
    {
        if (endpoint == null || !ModFacade.Server.IsRunning || !endpoint.Registered || string.IsNullOrEmpty(endpoint.PlayerId))
            return;
        if (request.UpgradeId is < byte.MinValue or > byte.MaxValue || request.Level is < sbyte.MinValue or > sbyte.MaxValue)
            return;

        var playerId = endpoint.PlayerId;
        var packet = (UpgradeID: (byte)request.UpgradeId, Level: (sbyte)request.Level, RequestId: request.RequestId);
        if (!UpgradePurchaseRequestLedger.IsValidRequest(playerId, packet.RequestId, packet.Level))
            return;

        var model = SceneContext.Instance?.PlayerState?._model?.upgradeModel;
        var upgrade = model?.upgradeDefinitions?.items?._items?.FirstOrDefault(
            definition => definition != null && definition._uniqueId == packet.UpgradeID);
        // Upgrades are shared: the host's own UpgradeModel is the single authority for the level.
        var currentLevel = upgrade == null || model == null ? -1 : model.GetUpgradeLevel(upgrade);
        var previousRequest = Decisions.TryGetDecision(
            playerId!, packet.RequestId, packet.UpgradeID, packet.Level, out var previousDecision);
        if (previousRequest == UpgradePurchaseRequestLookup.Duplicate)
        {
            OwnedLog.LogMessage(
                $"Replayed upgrade purchase result request={packet.RequestId} player={playerId} " +
                $"accepted={previousDecision} currentLevel={currentLevel}.");
            if (upgrade == null || model == null)
                SendRejection(endpoint, packet.UpgradeID, packet.RequestId);
            else
                SendAuthoritativeState(packet.UpgradeID, currentLevel, packet.RequestId);
            return;
        }

        if (previousRequest == UpgradePurchaseRequestLookup.ConflictingDuplicate)
        {
            OwnedLog.LogWarning(
                $"Rejected conflicting upgrade purchase request reuse request={packet.RequestId} " +
                $"player={playerId} upgrade={packet.UpgradeID} level={packet.Level}.");
            SendRejection(endpoint, packet.UpgradeID, packet.RequestId);
            return;
        }

        if (upgrade == null || model == null)
        {
            Decisions.Remember(playerId!, packet.RequestId, packet.UpgradeID, packet.Level, accepted: false);
            OwnedLog.LogWarning(
                $"Rejected upgrade purchase request {packet.RequestId} from {playerId}: " +
                $"unknown upgrade id {packet.UpgradeID} or upgrade model is not ready.");
            SendRejection(endpoint, packet.UpgradeID, packet.RequestId);
            return;
        }

        var accepted = false;
        var rejection = "none";
        var expectedLevel = (int)packet.Level;
        if (currentLevel != expectedLevel)
        {
            rejection = $"stale-level expected={expectedLevel} host={currentLevel}";
        }
        else if (!TryFindNativeItem(packet.UpgradeID, out var item, out var itemSource))
        {
            rejection = "native Fabricator item/provider is not loaded";
        }
        else
        {
            try
            {
                var fabricatableItem = item!.TryCast<IFabricatableItem>();
                if (fabricatableItem == null)
                    throw new InvalidOperationException("Player upgrade item does not implement IFabricatableItem.");

                var preflight = fabricatableItem.CheckCanFabricate(1);
                if (preflight != FabricationBlockedReason.NONE)
                {
                    rejection = $"preflight={preflight}";
                }
                else
                {
                    Crumb($"before FabricateAndSpendCost upgrade={packet.UpgradeID} level={currentLevel}");
                    var wasExecutingHostPurchase = NetworkUpgradeManager.ExecutingHostPurchase;
                    NetworkUpgradeManager.ExecutingHostPurchase = true;
                    FabricationBlockedReason result;
                    try
                    {
                        // One native transaction on the host model: cost, level-up and events (currency is spent once, here).
                        // The level-changed hook is deferred so one correlated revision goes to everyone below.
                        result = fabricatableItem.FabricateAndSpendCost(1);
                    }
                    finally
                    {
                        NetworkUpgradeManager.ExecutingHostPurchase = wasExecutingHostPurchase;
                    }

                    Crumb($"after FabricateAndSpendCost result={result}");
                    currentLevel = model.GetUpgradeLevel(upgrade);
                    accepted = result == FabricationBlockedReason.NONE && currentLevel == expectedLevel + 1;
                    if (!accepted)
                        rejection = $"transaction={result} hostLevel={currentLevel}";
                    else
                        OwnedLog.LogMessage(
                            $"Committed native upgrade request={packet.RequestId} player={playerId} " +
                            $"upgrade={packet.UpgradeID} level={currentLevel} source={itemSource}.");
                }
            }
            catch (Exception exception)
            {
                currentLevel = model.GetUpgradeLevel(upgrade);
                rejection = $"native transaction threw {exception.GetType().Name}";
                OwnedLog.LogError(
                    $"Upgrade purchase request={packet.RequestId} player={playerId} failed: {exception}");
            }
        }

        Decisions.Remember(playerId!, packet.RequestId, packet.UpgradeID, packet.Level, accepted);
        if (!accepted)
        {
            OwnedLog.LogWarning(
                $"Rejected upgrade purchase request={packet.RequestId} player={playerId} " +
                $"upgrade={packet.UpgradeID}: {rejection}.");
            SendRejection(endpoint, packet.UpgradeID, packet.RequestId);
            if (Components.UI.MultiplayerUI.Instance)
                Components.UI.MultiplayerUI.Instance.ShowLocalSystemMessage(
                    $"A guest's upgrade purchase failed ({upgrade.name}): {rejection}.");
        }

        // Accepted or not, publish the host's canonical level to everyone (a rejected stale buyer resyncs from it).
        currentLevel = model.GetUpgradeLevel(upgrade);
        SendAuthoritativeState(packet.UpgradeID, currentLevel, packet.RequestId);
        Crumb($"after broadcast upgrade={packet.UpgradeID} level={currentLevel}");
    }

    private static void SendRejection(Lycoreia.Net.Peer endpoint, byte upgradeId, Guid requestId)
    {
        try
        {
            ModFacade.Server.SendToClient(new PlayerUpgradePacket
            {
                UpgradeId = upgradeId,
                Kind = PlayerUpgradeMessageKind.Rejected,
                RequestId = requestId
            }, endpoint);
        }
        catch (Exception exception)
        {
            OwnedLog.LogError($"Could not send upgrade rejection request={requestId}: {exception}");
        }
    }

    private static void SendAuthoritativeState(byte upgradeId, int level, Guid requestId)
    {
        if (level is < sbyte.MinValue or > sbyte.MaxValue)
        {
            OwnedLog.LogError($"Cannot sync upgrade {upgradeId}: level {level} is outside packet range.");
            return;
        }

        NetworkUpgradeManager.BroadcastHostUpgrade(upgradeId, (sbyte)level, requestId);
    }

    private static bool TryFindNativeItem(byte upgradeId,
        out PlayerUpgradeFabricatableItem? item, out string source)
    {
        try
        {
            return TryFindNativeItemCore(upgradeId, out item, out source);
        }
        catch (Exception exception)
        {
            item = null;
            source = $"native provider lookup threw {exception.GetType().Name}";
            OwnedLog.LogWarning($"Could not resolve native upgrade item {upgradeId}: {exception}");
            return false;
        }
    }

    // The station's config/category assets are only in memory while some menu has them loaded (owner 2026-11-01:
    // a guest's first purchase failed until the host opened the pause menu). Remember what was found, and if
    // nothing is loaded build the native item from the always-available recipe factory + definition + event.
    private static UpgradeEventProducer? cachedEventProducer;

    private static float nextWarmUpAt;

    /// <summary>Host: every 20 s until cached, so a guest's first purchase works without opening a menu.</summary>
    internal static void Pump()
    {
        if (ModFacade.Server.IsRunning && Pending.Count > 0)
        {
            var (request, endpoint, queuedAt) = Pending.Peek();
            var verdict = DeferEnabled
                ? UpgradePurchaseDeferral.Decide(HostFabricatorUiOpen(), queuedAt, Environment.TickCount64)
                : UpgradePurchaseDeferral.Verdict.RunNow;
            if (verdict != UpgradePurchaseDeferral.Verdict.Wait)
            {
                Pending.Dequeue();
                try
                {
                    if (verdict == UpgradePurchaseDeferral.Verdict.Refuse)
                        SendRejection(endpoint, (byte)request.UpgradeId, request.RequestId);
                    else using (Lycoreia.Net.Applying.Suspend()) HandleHostRequest(request, endpoint);
                }
                catch (Exception exception) { OwnedLog.LogError($"Upgrade purchase handler failed: {exception}"); }
            }
        }
        if (cachedEventProducer != null || !ModFacade.Server.IsRunning)
            return;
        var now = UnityEngine.Time.unscaledTime;
        if (now < nextWarmUpAt)
            return;
        nextWarmUpAt = now + 20f;
        WarmUp();
        if (cachedEventProducer != null)
            OwnedLog.LogMessage("Upgrade station data cached for guest purchases.");
    }

    // Optional safety net, deeplog builds only and off unless LYCOREIA_DEFER_UPGRADES=1: wait while the host's Fabricator UI is open.
    private static readonly bool DeferEnabled =
#if DEEPLOG
        Environment.GetEnvironmentVariable("LYCOREIA_DEFER_UPGRADES") == "1";
#else
        false;
#endif

    // Deeplog-only breadcrumbs, appended and flushed to disk at once so a native crash leaves the last step reached.
    [System.Diagnostics.Conditional("DEEPLOG")]
    private static void Crumb(string step)
    {
        try { System.IO.File.AppendAllText("UserData/Lycoreia/upgrade-crumbs.log", $"{DateTime.UtcNow:HH:mm:ss.fff} {step}\n"); } catch { }
    }

    private static bool HostFabricatorUiOpen()
    {
        try
        {
            foreach (var ui in Resources.FindObjectsOfTypeAll<FabricatorUI>())
                if (ui && ui.gameObject.activeInHierarchy) return true;
        }
        catch (Exception exception) { OwnedLog.LogDebug($"Fabricator UI probe failed: {exception.Message}"); }
        return false;
    }

    internal static void WarmUp()
    {
        if (cachedEventProducer != null)
            return;
        try
        {
            foreach (var category in Resources.FindObjectsOfTypeAll<PlayerUpgradeFabricateCategory>())
                if (category && category._upgradePurchaseEvent != null) { cachedEventProducer = category._upgradePurchaseEvent; return; }
            foreach (var producer in Resources.FindObjectsOfTypeAll<UpgradeEventProducer>())
                if (producer) { cachedEventProducer = producer; return; }
        }
        catch (Exception exception)
        {
            OwnedLog.LogDebug($"Upgrade warm-up failed: {exception.Message}");
        }
    }

    private static bool TryBuildNativeItem(byte upgradeId, out PlayerUpgradeFabricatableItem? item, out string source)
    {
        item = null;
        source = "no event producer / recipe factory to build the native item";
        WarmUp();
        var model = SceneContext.Instance?.PlayerState?._model?.upgradeModel;
        var definition = model?.upgradeDefinitions?.items?._items?.FirstOrDefault(d => d != null && d._uniqueId == upgradeId);
        if (definition == null || cachedEventProducer == null ||
            !IRecipeFactory.TryGet(out var factory) || factory == null)
            return false;
        item = new PlayerUpgradeFabricatableItem(factory, definition, cachedEventProducer);
        source = "built from recipe factory + definition + cached event producer";
        return true;
    }

    private static bool TryFindNativeItemCore(byte upgradeId,
        out PlayerUpgradeFabricatableItem? item, out string source)
    {
        Crumb($"build item upgrade={upgradeId}");
        if (TryBuildNativeItem(upgradeId, out item, out source))
            return true;
        var buildReason = source;
        if (TryFindLoadedNativeItem(upgradeId, out item, out source))
        {
            if (item?._eventProducer != null)
                cachedEventProducer = item._eventProducer;
            return true;
        }
        source = $"{buildReason}; {source}";
        return false;
    }

    private static bool TryFindLoadedNativeItem(byte upgradeId,
        out PlayerUpgradeFabricatableItem? item, out string source)
    {
        item = null;
        source = string.Empty;
        // Never the live FabricatorContent runtime item: it belongs to the open UI. Items below are fresh objects.
        if (!IRecipeFactory.TryGet(out var recipeFactory) || recipeFactory == null)
        {
            source = "IRecipeFactory.TryGet returned no factory";
            return false;
        }

        foreach (var configuration in Resources.FindObjectsOfTypeAll<FabricateMenuConfiguration>())
        {
            if (!configuration || configuration.Categories == null)
                continue;

            foreach (var category in configuration.Categories)
            {
                var upgradeCategory = category?.TryCast<PlayerUpgradeFabricateCategory>();
                if (upgradeCategory == null || upgradeCategory._upgradePurchaseEvent == null)
                    continue;

                if (TryGetItem(upgradeCategory.GetItems(recipeFactory), upgradeId, out item))
                {
                    source = "FabricateMenuConfiguration category.GetItems(factory)";
                    return true;
                }
            }
        }

        foreach (var upgradeCategory in Resources.FindObjectsOfTypeAll<PlayerUpgradeFabricateCategory>())
        {
            if (!upgradeCategory || upgradeCategory._upgradePurchaseEvent == null)
                continue;

            if (TryGetItem(upgradeCategory.GetItems(recipeFactory), upgradeId, out item))
            {
                source = "loaded PlayerUpgradeFabricateCategory.GetItems(factory)";
                return true;
            }
        }

        source = "no loaded player-upgrade Fabricator category contains this upgrade";
        return false;
    }

    private static bool TryGetItem(
        Il2CppSystem.Collections.Generic.List<IFabricatableItem>? items,
        byte upgradeId,
        out PlayerUpgradeFabricatableItem? item)
    {
        item = null;
        if (items == null)
            return false;

        foreach (var candidate in items)
        {
            var upgradeItem = candidate?.TryCast<PlayerUpgradeFabricatableItem>();
            if (upgradeItem == null || upgradeItem.UpgradeDefinition?._uniqueId != upgradeId ||
                !HasNativeProviders(upgradeItem))
                continue;

            item = upgradeItem;
            return true;
        }

        return false;
    }

    private static bool HasNativeProviders(PlayerUpgradeFabricatableItem item)
        => item._recipeFactory != null && item._eventProducer != null;
}

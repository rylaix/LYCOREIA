using System.Net;
using Il2CppMonomiPark.SlimeRancher.RecipePinning;
using Il2CppMonomiPark.SlimeRancher.UI.Fabricator;
using SR2MP.Packets.Upgrade;
using UnityEngine;

namespace SR2MP.Shared.Managers;

/// <summary>
/// Runs player-upgrade purchases through the game's IFabricatableItem transaction
/// on the host. The host uses a configured Fabricator category to supply items,
/// recipes and the upgrade event producer; it never constructs a synthetic item.
/// </summary>
internal static class NetworkUpgradePurchaseManager
{
    private const int MaxRememberedRequests = 4096;
    private static readonly UpgradePurchaseRequestLedger Decisions = new(MaxRememberedRequests);

    internal static void ResetHostSession() => Decisions.Clear();

    internal static bool RequestClientPurchase(PlayerUpgradeFabricatableItem item, int count)
    {
        if (HandlingPacket || !Main.Client.IsConnected || item == null || count != 1)
            return false;

        var definition = item.UpgradeDefinition;
        if (definition == null || definition._uniqueId is < byte.MinValue or > byte.MaxValue)
            return false;

        var currentLevel = item.CurrentUpgradeLevel;
        if (currentLevel is < 0 or > sbyte.MaxValue)
            return false;

        var packet = new PlayerUpgradePacket
        {
            UpgradeID = (byte)definition._uniqueId,
            Kind = PlayerUpgradeMessageKind.Request,
            Level = (sbyte)currentLevel,
            RequestId = Guid.NewGuid()
        };

        NetworkUpgradeManager.NoteClientRequest(packet.UpgradeID, packet.RequestId);
        try
        {
            Main.Client.SendPacket(packet);
            return true;
        }
        catch (Exception exception)
        {
            NetworkUpgradeManager.ForgetClientRequest(packet.RequestId);
            SrLogger.LogError($"Could not send upgrade purchase request {packet.RequestId}: {exception}");
            return false;
        }
    }

    internal static void HandleHostRequest(PlayerUpgradePacket packet, IPEndPoint? endpoint)
    {
        if (endpoint == null || !Main.Server.IsRunning ||
            !Main.Server.ClientManager.TryGetClient(endpoint, out var clientInfo) || clientInfo == null)
            return;

        var playerId = clientInfo.PlayerId;
        if (!UpgradePurchaseRequestLedger.IsValidRequest(playerId, packet.RequestId, packet.Level))
            return;

        var model = SceneContext.Instance?.PlayerState?._model?.upgradeModel;
        var upgrade = model?.upgradeDefinitions?.items?._items?.FirstOrDefault(
            definition => definition != null && definition._uniqueId == packet.UpgradeID);
        var currentLevel = upgrade == null || model == null ? -1 : model.GetUpgradeLevel(upgrade);
        var previousRequest = Decisions.TryGetDecision(
            playerId!, packet.RequestId, packet.UpgradeID, packet.Level, out var previousDecision);
        if (previousRequest == UpgradePurchaseRequestLookup.Duplicate)
        {
            SrLogger.LogMessage(
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
            SrLogger.LogWarning(
                $"Rejected conflicting upgrade purchase request reuse request={packet.RequestId} " +
                $"player={playerId} upgrade={packet.UpgradeID} level={packet.Level}.");
            SendRejection(endpoint, packet.UpgradeID, packet.RequestId);
            return;
        }

        if (upgrade == null || model == null)
        {
            Decisions.Remember(playerId!, packet.RequestId, packet.UpgradeID, packet.Level, accepted: false);
            SrLogger.LogWarning(
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
                    var wasExecutingHostPurchase = NetworkUpgradeManager.ExecutingHostPurchase;
                    NetworkUpgradeManager.ExecutingHostPurchase = true;
                    FabricationBlockedReason result;
                    try
                    {
                        // Keep the native cost, component, model and event behavior.
                        // Only the remote host-upgrade postfix is deferred so this
                        // method can emit one correlated revision below.
                        result = fabricatableItem.FabricateAndSpendCost(1);
                    }
                    finally
                    {
                        NetworkUpgradeManager.ExecutingHostPurchase = wasExecutingHostPurchase;
                    }

                    currentLevel = model.GetUpgradeLevel(upgrade);
                    accepted = result == FabricationBlockedReason.NONE && currentLevel == expectedLevel + 1;
                    if (!accepted)
                        rejection = $"transaction={result} hostLevel={currentLevel}";
                    else
                        SrLogger.LogMessage(
                            $"Committed native upgrade request={packet.RequestId} player={playerId} " +
                            $"upgrade={packet.UpgradeID} level={currentLevel} source={itemSource}.");
                }
            }
            catch (Exception exception)
            {
                currentLevel = model.GetUpgradeLevel(upgrade);
                rejection = $"native transaction threw {exception.GetType().Name}";
                SrLogger.LogError(
                    $"Upgrade purchase request={packet.RequestId} player={playerId} failed: {exception}");
            }
        }

        Decisions.Remember(playerId!, packet.RequestId, packet.UpgradeID, packet.Level, accepted);
        if (!accepted)
            SrLogger.LogWarning(
                $"Rejected upgrade purchase request={packet.RequestId} player={playerId} " +
                $"upgrade={packet.UpgradeID}: {rejection}.");

        currentLevel = model.GetUpgradeLevel(upgrade);
        SendAuthoritativeState(packet.UpgradeID, currentLevel, packet.RequestId);
    }

    private static void SendRejection(IPEndPoint endpoint, byte upgradeId, Guid requestId)
    {
        try
        {
            Main.Server.SendToClient(new PlayerUpgradePacket
            {
                UpgradeID = upgradeId,
                Kind = PlayerUpgradeMessageKind.Rejected,
                RequestId = requestId
            }, endpoint);
        }
        catch (Exception exception)
        {
            SrLogger.LogError($"Could not send upgrade rejection request={requestId}: {exception}");
        }
    }

    private static void SendAuthoritativeState(byte upgradeId, int level, Guid requestId)
    {
        if (level is < sbyte.MinValue or > sbyte.MaxValue)
        {
            SrLogger.LogError($"Cannot sync upgrade {upgradeId}: level {level} is outside packet range.");
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
            SrLogger.LogWarning($"Could not resolve native upgrade item {upgradeId}: {exception}");
            return false;
        }
    }

    private static bool TryFindNativeItemCore(byte upgradeId,
        out PlayerUpgradeFabricatableItem? item, out string source)
    {
        item = null;
        source = string.Empty;
        var runtimeMatches = new List<PlayerUpgradeFabricatableItem>();
        foreach (var content in Resources.FindObjectsOfTypeAll<FabricatorContent>())
        {
            if (!content)
                continue;

            var categories = content.Categories?
                .TryCast<Il2CppSystem.Collections.IEnumerable>()?.GetEnumerator();
            while (categories != null && categories.MoveNext())
            {
                var runtime = categories.Current?.TryCast<PlayerUpgradeFabricateCategoryRuntime>();
                if (runtime?.Items == null)
                    continue;

                foreach (var candidate in runtime.Items)
                {
                    var upgradeItem = candidate?.TryCast<PlayerUpgradeFabricatableItem>();
                    if (upgradeItem != null && upgradeItem.UpgradeDefinition?._uniqueId == upgradeId)
                        runtimeMatches.Add(upgradeItem);
                }
            }
        }

        foreach (var runtimeMatch in runtimeMatches)
        {
            if (!HasNativeProviders(runtimeMatch))
                continue;

            item = runtimeMatch;
            source = "FabricatorContent runtime cache";
            return true;
        }

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

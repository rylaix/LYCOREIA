// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System.Collections.Concurrent;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Economy;
using Il2CppMonomiPark.SlimeRancher.Shop;
using Il2CppMonomiPark.SlimeRancher.Shop.Runtime;
using Lycoreia.Owned.Components.UI;
using ShopPurchaseRequestPacket = Lycoreia.Net.Messages.ShopPurchaseRequest;
using ShopPurchaseResultPacket = Lycoreia.Net.Messages.ShopPurchaseResult;

namespace Lycoreia.Owned.Shared.Managers;

internal static partial class NetworkShopManager
{
    private const int MaxPurchaseCount = 1000;
    private const int MaxRememberedRequests = 8192;
    private const int MaxRuntimes = 64;
    private const int MaxCategoriesPerRuntime = 512;
    private const int MaxItemsPerCategory = 4096;
    private const int MaxPurchaseSets = 4096;
    private const int MaxPurchasesPerSet = 4096;
    private const int MaxPendingSources = 2048;
    private const int MaxPendingItemsPerSource = 8192;
    private const float PublishRetryDelaySeconds = 1f;

    private static readonly HashSet<string> PartialStockWarnings = new(StringComparer.Ordinal);
    private static readonly ShopPurchaseRequestLedger PurchaseDecisions = new(MaxRememberedRequests);
    private static readonly HashSet<Guid> PendingClientRequests = new();
    private static readonly ConcurrentDictionary<string, Lycoreia.Net.Peer> PendingHostSnapshots = new();
    private static readonly ShopSnapshotPublishRetryState PendingPublishRetry = new();

    private static Guid serverSessionId;
    private static ulong serverRevision;
    private static bool serverWasRunning;
    private static Guid lastClientSessionId;
    private static ulong lastClientRevision;
    private static ShopStateSnapshotPacket? pendingClientSnapshot;
    private static float nextPendingApplyLog;
    private static float nextPendingApplyAt;
    private const float PendingApplyRetrySeconds = 2f;
    private static ShopDirector? cachedDirector;
    private static float nextHostSnapshotRetryAt;
    private static float nextHostSnapshotRetryLogAt;
    private static string? pendingRestockSnapshotReason;
    private static int pendingRestockSnapshotAfterFrame = -1;

    internal static ulong ServerRevisionForTest => serverRevision;

    static NetworkShopManager()
    {
        ModFacade.Client.OnConnected += _ => ResetClientState();
        ModFacade.Client.OnDisconnected += ResetClientState;
        ModFacade.Server.ClientManager.OnClientRemoved += client =>
            { if (client != null) PendingHostSnapshots.TryRemove(client.Id, out _); };
        ModFacade.Server.OnServerStarted += () =>
        {
            serverWasRunning = false;
            PurchaseDecisions.Clear();
            PendingHostSnapshots.Clear();
            PendingPublishRetry.Clear();
            pendingRestockSnapshotReason = null;
            pendingRestockSnapshotAfterFrame = -1;
            nextHostSnapshotRetryAt = 0;
            nextHostSnapshotRetryLogAt = 0;
        };
    }

    internal static void SendSnapshot(Lycoreia.Net.Peer endpoint, bool advanceRevision = false)
    {
        TraceRestockPhase("send-snapshot-enter", "shop snapshot",
            $"server={ModFacade.Server.IsRunning} endpoint={endpoint} revision={serverRevision}");
        if (!ModFacade.Server.IsRunning)
            return;

        TraceRestockPhase("send-snapshot-session-begin", "shop snapshot", "ensuring server session");
        EnsureServerSession();
        TraceRestockPhase("send-snapshot-session-end", "shop snapshot",
            $"session={serverSessionId} revision={serverRevision}");
        if (!ModFacade.Server.ClientManager.TryGetClient(endpoint, out _))
        {
            TraceRestockPhase("send-snapshot-client-missing", "shop snapshot", $"endpoint={endpoint}");
            return;
        }

        if (advanceRevision)
        {
            if (!ShopSnapshotRevisionPolicy.TryAdvance(serverRevision, out var nextRevision))
            {
                OwnedLog.LogError("Shop snapshot revision overflow; cannot send a purchase correction snapshot.");
                return;
            }

            // Even when a purchase was rejected and the host's shop data did not
            // change, the guest must process the authoritative snapshot. A fresh
            // revision makes it newer than the guest's last applied state.
            serverRevision = nextRevision;
        }

        TraceRestockPhase("send-snapshot-capture-begin", "shop snapshot", $"endpoint={endpoint}");
        if (!TrySendSnapshot(endpoint, out var error))
        {
            TraceRestockPhase("send-snapshot-capture-failed", "shop snapshot", $"error={error}");
            PendingHostSnapshots[GetEndpointKey(endpoint)] = endpoint;
            OwnedLog.LogWarning($"Shop snapshot for {endpoint} is queued until shop data is ready: {error}");
            return;
        }

        TraceRestockPhase("send-snapshot-complete", "shop snapshot",
            $"endpoint={endpoint} revision={serverRevision}");
    }

    internal static void PublishSnapshot(string reason)
    {
        TraceRestockPhase("publish-enter", reason,
            $"server={ModFacade.Server.IsRunning} handlingPacket={HandlingPacket} revision={serverRevision}");
        if (!ModFacade.Server.IsRunning || HandlingPacket)
        {
            TraceRestockPhase("publish-skipped", reason,
                $"server={ModFacade.Server.IsRunning} handlingPacket={HandlingPacket}");
            return;
        }

        try
        {
            EnsureServerSession();
            var revision = serverRevision + 1;
            if (revision == 0)
            {
                OwnedLog.LogError("Shop snapshot revision overflow; refusing to publish state.");
                return;
            }

            TraceRestockPhase("capture-begin", reason, $"revision={revision}");
            if (!TryCreateSnapshot(serverSessionId, revision, out var snapshot, out var error, reason))
            {
                TraceRestockPhase("capture-failed", reason, $"revision={revision} error={error}");
                PendingPublishRetry.Schedule(reason, Time.unscaledTime + PublishRetryDelaySeconds);
                OwnedLog.LogWarning($"Shop state change '{reason}' was not synchronized: {error}");
                return;
            }

            TraceRestockPhase("send-begin", reason, $"revision={revision}");
            serverRevision = revision;
            ModFacade.Server.SendToAll(snapshot!);
            PendingPublishRetry.Clear();
            TraceRestockPhase("send-end", reason, $"revision={revision}");
            OwnedLog.LogMessage(
                $"Shop state snapshot published revision={revision} reason={reason} " +
                $"runtimes={snapshot!.Runtimes.Length} purchaseSets={snapshot.PurchaseSets.Length} " +
                $"pendingSources={snapshot.PendingSpawns.Length}");
            DiagLog.LogDiagnostic("shop", "snapshot-published",
                $"session={serverSessionId}; revision={revision}; reason={reason}; " +
                $"runtimes={snapshot.Runtimes.Length}; purchaseSets={snapshot.PurchaseSets.Length}; " +
                $"pendingSources={snapshot.PendingSpawns.Length}");
#if LYCOREIA_TEST && LYC_TEST_HARNESS // TestShopTrace belongs to the unported test harness
            if (Environment.GetEnvironmentVariable("LYCOREIA_TEST_TRACE_SHOP") == "1")
                Lycoreia.Owned.Shared.Utils.TestShopTrace.RequestDump($"host published revision={revision} reason={reason}");
#endif
        }
        catch (Exception exception)
        {
            PendingPublishRetry.Schedule(reason, Time.unscaledTime + PublishRetryDelaySeconds);
            TraceRestockPhase("publish-exception", reason,
                $"revision={serverRevision} exception={exception.GetType().Name}: {exception.Message}");
            OwnedLog.LogError(
                $"Shop state change '{reason}' publication threw at frame {Time.frameCount}; " +
                $"revision={serverRevision}: {exception}");
        }
    }

    internal static void QueueSnapshotAfterNextFrame(string reason)
    {
        var serverRunning = ModFacade.Server.IsRunning;
        var handlingPacket = HandlingPacket;
        TraceRestockPhase("restock-postfix", reason,
            $"server={serverRunning} handlingPacket={handlingPacket} frame={Time.frameCount}");
        if (!serverRunning || handlingPacket)
        {
            TraceRestockPhase("restock-postfix-skipped", reason,
                $"server={serverRunning} handlingPacket={handlingPacket}");
            return;
        }

        var publishAfterFrame = Time.frameCount + 1;
        if (pendingRestockSnapshotReason == null)
        {
            pendingRestockSnapshotReason = reason;
            pendingRestockSnapshotAfterFrame = publishAfterFrame;
        }
        else
        {
            pendingRestockSnapshotAfterFrame = Math.Max(pendingRestockSnapshotAfterFrame, publishAfterFrame);
        }

        TraceRestockPhase("restock-publish-queued", pendingRestockSnapshotReason,
            $"frame={Time.frameCount} publishAfterFrame={pendingRestockSnapshotAfterFrame}");
    }

    internal static bool RequestPurchase(ShopRuntimeItem item, int count)
    {
        if (HandlingPacket || !ModFacade.Client.IsConnected || item == null || count is < 1 or > MaxPurchaseCount)
            return false;

        if (PendingClientRequests.Count >= 2048)
        {
            OwnedLog.LogWarning("Could not request shop synchronization: too many purchase decisions are pending.");
            return false;
        }

        var category = item.OwningCategory;
        var shopTag = category?.SourceShopAssetTag;
        var itemGuid = item.AssetGuid;
        if (string.IsNullOrWhiteSpace(shopTag) || string.IsNullOrWhiteSpace(itemGuid))
        {
            OwnedLog.LogWarning("Could not request shop synchronization: the purchased item has no stable shop/item identity.");
            return false;
        }

        var packet = new ShopPurchaseRequestPacket
        {
            RequestId = Guid.NewGuid(),
            ShopAssetTag = shopTag,
            ItemAssetGuid = itemGuid,
            Count = count,
            CategoryName = category?.AnalyticsName ?? string.Empty,
            UnlockType = (int)item.UnlockType
        };
        PendingClientRequests.Add(packet.RequestId);
        DiagLog.LogDiagnostic("shop", "purchase-requested",
            $"requestId={packet.RequestId}; shop={packet.ShopAssetTag}; category={packet.CategoryName}; item={packet.ItemAssetGuid}; unlock={packet.UnlockType}; count={packet.Count}");
        OwnedLog.LogMessage(
            $"Shop purchase intent sent requestId={packet.RequestId} " +
            $"shop={packet.ShopAssetTag} item={packet.ItemAssetGuid} count={packet.Count}");
        try
        {
            ModFacade.Client.SendPacket(packet);
            return true;
        }
        catch (Exception exception)
        {
            PendingClientRequests.Remove(packet.RequestId);
            OwnedLog.LogError($"Shop purchase intent requestId={packet.RequestId} could not be sent: {exception}");
            return false;
        }
    }

    /// <summary>rc11 guest purchase: the guest's own shop already ran the native purchase (its stock is generated locally and differs
    /// from the host's; rc10 logs: every host lookup found=0). Money and refinery spends reach the host through the wallet and
    /// refinery deltas; actors through actor sync. Only the shared purchase record (sold-out / limits) is sent here.</summary>
    internal static void RecordGuestPurchase(ShopRuntimeItem item, int count)
    {
        if (HandlingPacket || !ModFacade.Client.IsConnected || ModFacade.Server.IsRunning || item == null || count < 1)
            return;
        try
        {
            string itemId = item.UniqueId.Id ?? string.Empty;
            int unlock = (int)item.UnlockType;
            string key = string.Empty; int seed = 0;
            var sets = FindShopDirector()?._dataModel?._shopPurchasesByCategory;
            if (sets != null)
            {
                foreach (var pair in sets)
                {
                    var set = pair.Value;
                    if (set?.Purchases == null) continue;
                    foreach (var purchase in set.Purchases)
                        if (purchase != null && (int)purchase.UnlockType == unlock && purchase.UniqueId.Id == itemId) { key = pair.Key; seed = set.Seed; break; }
                    if (key.Length > 0) break;
                }
            }
            var category = item.OwningCategory;
            var packet = new ShopPurchaseRequestPacket
            {
                RequestId = Guid.NewGuid(), Record = true,
                ShopAssetTag = category?.SourceShopAssetTag ?? "shop", ItemAssetGuid = string.IsNullOrEmpty(item.AssetGuid) ? itemId : item.AssetGuid,
                CategoryName = category?.AnalyticsName ?? string.Empty, UnlockType = unlock, Count = count,
                PurchaseSetKey = key, PurchaseSetSeed = seed, ItemId = itemId
            };
            DiagLog.LogDiagnostic("shop", "guest-purchase-recorded",
                $"category={packet.CategoryName}; set={key}; seed={seed}; item={packet.ItemAssetGuid}; id={itemId}; unlock={unlock}; count={count}");
            OwnedLog.LogMessage($"Shop purchase done locally, sent to the host: category={packet.CategoryName} item={packet.ItemAssetGuid} id={itemId} set={(key.Length > 0 ? key : "-")} count={count}");
            ModFacade.Client.SendPacket(packet);
        }
        catch (Exception exception)
        {
            OwnedLog.LogWarning($"Could not send the shop purchase record: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void ApplyGuestPurchaseRecord(ShopPurchaseRequestPacket packet, string playerId)
    {
        var model = FindShopDirector()?._dataModel;
        var sets = model?._shopPurchasesByCategory;
        if (sets == null || string.IsNullOrWhiteSpace(packet.PurchaseSetKey) || string.IsNullOrWhiteSpace(packet.ItemId))
        {
            // No set key: the guest's game kept no purchase record for this item (e.g. unlimited stock). Money and items already synced.
            OwnedLog.LogMessage($"Guest shop purchase from {playerId}: {packet.CategoryName} {packet.ItemAssetGuid} x{packet.Count} (no purchase set to share).");
            DiagLog.LogDiagnostic("shop", "guest-purchase-applied", $"player={playerId}; category={packet.CategoryName}; item={packet.ItemAssetGuid}; set=-; count={packet.Count}");
            QueueSnapshotAfterNextFrame("guest purchase");
            return;
        }
        ShopDataPurchaseSet set;
        if (sets.ContainsKey(packet.PurchaseSetKey) && sets[packet.PurchaseSetKey] != null)
            set = sets[packet.PurchaseSetKey];
        else
        {
            set = new ShopDataPurchaseSet { _categoryIdentifier = packet.PurchaseSetKey, _seed = packet.PurchaseSetSeed };
            sets[packet.PurchaseSetKey] = set;
        }
        var uniqueId = new ShopItemId { _id = packet.ItemId };
        var itemInfo = ShopDataItemInfo.FromUniqueIdExplicit(ref uniqueId, (AcquireGadgetType)Math.Max(0, packet.UnlockType));
        set.AddPurchase(ref itemInfo, packet.Count);
        OwnedLog.LogMessage($"Guest shop purchase from {playerId} recorded: set={packet.PurchaseSetKey} item={packet.ItemAssetGuid} id={packet.ItemId} x{packet.Count}");
        DiagLog.LogDiagnostic("shop", "guest-purchase-applied",
            $"player={playerId}; category={packet.CategoryName}; item={packet.ItemAssetGuid}; set={packet.PurchaseSetKey}; seed={set.Seed}/{packet.PurchaseSetSeed}; count={packet.Count}");
        QueueSnapshotAfterNextFrame("guest purchase");
    }

    internal static void HandlePurchaseResult(ShopPurchaseResultPacket packet)
    {
        if (packet.RequestId == Guid.Empty || !PendingClientRequests.Remove(packet.RequestId))
        {
            OwnedLog.LogWarning(
                $"Ignoring shop purchase result for unknown requestId={packet.RequestId} " +
                $"accepted={packet.Accepted}.");
            return;
        }

        OwnedLog.LogMessage(
            $"Shop purchase result received requestId={packet.RequestId} " +
            $"result={(packet.Accepted ? "accepted" : "rejected")}");
        DiagLog.LogDiagnostic("shop", "purchase-result",
            $"requestId={packet.RequestId}; accepted={packet.Accepted}");

        if (packet.Accepted)
            return;

        try
        {
            MultiplayerUI.Instance?.ShowLocalSystemMessage(Lycoreia.Text.CoopText.Pick(
                "The host could not complete that purchase; the shop is being resynchronized.",
                "Хост не смог провести эту покупку - магазин синхронизируется заново.",
                "El anfitrión no pudo completar esa compra; la tienda se está sincronizando de nuevo."));
        }
        catch (Exception exception)
        {
            OwnedLog.LogWarning($"Could not display rejected shop purchase {packet.RequestId}: {exception.Message}");
        }
    }

    internal static bool HandlePurchaseRequest(ShopPurchaseRequestPacket packet, Lycoreia.Net.Peer? endpoint)
    {
        if (endpoint == null || !ModFacade.Server.IsRunning ||
            !ModFacade.Server.ClientManager.TryGetClient(endpoint, out var clientInfo))
            return false;

        if (!ShopPurchaseRequestLedger.IsValidRequest(packet.RequestId,
                packet.ShopAssetTag, packet.ItemAssetGuid, packet.Count, MaxPurchaseCount))
            return false;

        var playerId = clientInfo!.PlayerId;
        if (packet.Record)
        {
            try { ApplyGuestPurchaseRecord(packet, playerId); }
            catch (Exception exception) { OwnedLog.LogWarning($"Guest shop purchase record from {playerId} failed: {exception.GetType().Name}: {exception.Message}"); }
            return false;
        }
        var previousRequest = PurchaseDecisions.TryGetDecision(playerId, packet.RequestId,
            packet.ShopAssetTag, packet.ItemAssetGuid, packet.Count, out var previousDecision);
        if (previousRequest == ShopPurchaseRequestLookup.Duplicate)
        {
            OwnedLog.LogWarning(
                $"Shop purchase decision requestId={packet.RequestId} result=duplicate " +
                $"from={playerId}; replaying result={previousDecision}.");
            SendPurchaseResult(endpoint, packet.RequestId, previousDecision);
            SendSnapshot(endpoint, advanceRevision: !previousDecision);
            return false;
        }

        if (previousRequest == ShopPurchaseRequestLookup.ConflictingDuplicate)
        {
            OwnedLog.LogWarning(
                $"Rejected conflicting shop purchase request reuse requestId={packet.RequestId} " +
                $"from={playerId} shop={packet.ShopAssetTag} item={packet.ItemAssetGuid} count={packet.Count}.");
            SendPurchaseResult(endpoint, packet.RequestId, accepted: false);
            SendSnapshot(endpoint, advanceRevision: true);
            return false;
        }

        if (previousRequest == ShopPurchaseRequestLookup.Invalid)
            return false;

        var accepted = false;
        if (!TryFindRuntimeItem(packet.ShopAssetTag, packet.ItemAssetGuid, packet.CategoryName, packet.UnlockType, out _, out var item, out var lookup))
        {
            OwnedLog.LogWarning(
                $"Shop purchase decision requestId={packet.RequestId} result=rejected " +
                $"from={playerId}: " +
                $"item '{packet.ItemAssetGuid}' in shop '{packet.ShopAssetTag}' category '{packet.CategoryName}' is not uniquely loaded on host ({lookup}).");
            DiagLog.LogDiagnostic("shop", "purchase-item-missing",
                $"requestId={packet.RequestId}; shop={packet.ShopAssetTag}; category={packet.CategoryName}; item={packet.ItemAssetGuid}; unlock={packet.UnlockType}; {lookup}");
        }
        else if (ShopRuntimeTransactor.Instance == null)
        {
            OwnedLog.LogWarning(
                $"Shop purchase decision requestId={packet.RequestId} result=rejected " +
                $"from={playerId}: the host shop transactor is not available.");
        }
        else
        {
            try
            {
                // Run the game's native transaction on the host. It validates the
                // current price, affordability, stock and purchase side effects.
                // Do not raise HandlingPacket here: currency and shop patches must
                // observe the native commit and publish its resulting state.
                accepted = ShopRuntimeTransactor.Instance.Purchase(item!, packet.Count, default);
            }
            catch (Exception exception)
            {
                OwnedLog.LogError(
                    $"Shop purchase decision requestId={packet.RequestId} result=error " +
                    $"from={playerId}: {exception}");
            }
        }

        PurchaseDecisions.Remember(playerId, packet.RequestId,
            packet.ShopAssetTag, packet.ItemAssetGuid, packet.Count, accepted);
        DiagLog.LogDiagnostic("shop", "purchase-resolved",
            $"requestId={packet.RequestId}; player={playerId}; shop={packet.ShopAssetTag}; " +
            $"item={packet.ItemAssetGuid}; count={packet.Count}; accepted={accepted}");
        OwnedLog.LogMessage(
            $"Shop purchase decision requestId={packet.RequestId} " +
            $"result={(accepted ? "accepted" : "rejected")} " +
            $"from={playerId} shop={packet.ShopAssetTag} item={packet.ItemAssetGuid} " +
            $"count={packet.Count}");

        SendPurchaseResult(endpoint, packet.RequestId, accepted);
        if (!accepted)
            SendSnapshot(endpoint, advanceRevision: true);
        return false;
    }

    internal static void ApplySnapshot(ShopStateSnapshotPacket packet)
    {
        if (packet.SessionId == Guid.Empty || packet.Revision == 0)
        {
            OwnedLog.LogWarning($"Ignoring shop snapshot with invalid session/revision: revision={packet.Revision}.");
            return;
        }

        if (!ValidatePacket(packet, out var validationError))
        {
            OwnedLog.LogWarning($"Ignoring invalid shop snapshot revision={packet.Revision}: {validationError}");
            return;
        }

        if (!ShouldAcceptSnapshot(lastClientSessionId, lastClientRevision, packet.SessionId, packet.Revision))
            return;

        if (!TryApplySnapshot(packet, out var error))
        {
            pendingClientSnapshot = packet;
            nextPendingApplyAt = Time.unscaledTime + PendingApplyRetrySeconds;
            if (Time.unscaledTime >= nextPendingApplyLog)
            {
                nextPendingApplyLog = Time.unscaledTime + 10f;
                OwnedLog.LogWarning($"Shop snapshot revision={packet.Revision} is queued until the shop data is ready: {error}");
            }
            return;
        }

        lastClientSessionId = packet.SessionId;
        lastClientRevision = packet.Revision;
        pendingClientSnapshot = null;
        OwnedLog.LogMessage(
            $"Shop snapshot applied revision={packet.Revision} runtimes={packet.Runtimes.Length} " +
            $"purchaseSets={packet.PurchaseSets.Length} pendingSources={packet.PendingSpawns.Length}");
        DiagLog.LogDiagnostic("shop", "snapshot-applied",
            $"session={packet.SessionId}; revision={packet.Revision}; runtimes={packet.Runtimes.Length}; " +
            $"purchaseSets={packet.PurchaseSets.Length}; pendingSources={packet.PendingSpawns.Length}");
#if LYCOREIA_TEST && LYC_TEST_HARNESS // TestShopTrace belongs to the unported test harness
        if (Environment.GetEnvironmentVariable("LYCOREIA_TEST_TRACE_SHOP") == "1")
            Lycoreia.Owned.Shared.Utils.TestShopTrace.RequestDump($"client applied revision={packet.Revision}");
#endif
    }

    internal static void PumpPendingSnapshot()
    {
        PumpPendingHostSnapshots();
        PumpPendingRestockSnapshot();
        PumpPendingPublishRetry();

        if (pendingClientSnapshot == null || !ModFacade.Client.IsConnected)
            return;

        // a snapshot that could not be applied was retried EVERY FRAME (a full shop capture, ~60 ms on the guest, plus a
        // FindObjectsOfTypeAll), so one failed purchase made the guest's game stutter until they left. Retry every 2 s instead.
        if (Time.unscaledTime < nextPendingApplyAt)
            return;
        nextPendingApplyAt = Time.unscaledTime + PendingApplyRetrySeconds;

        var snapshot = pendingClientSnapshot;
        if (!ShouldAcceptSnapshot(lastClientSessionId, lastClientRevision, snapshot.SessionId, snapshot.Revision))
        {
            pendingClientSnapshot = null;
            return;
        }

        if (!TryApplySnapshot(snapshot, out var error))
        {
            if (Time.unscaledTime >= nextPendingApplyLog)
            {
                nextPendingApplyLog = Time.unscaledTime + 10f;
                OwnedLog.LogWarning($"Shop snapshot revision={snapshot.Revision} is still pending: {error}");
            }
            return;
        }

        lastClientSessionId = snapshot.SessionId;
        lastClientRevision = snapshot.Revision;
        pendingClientSnapshot = null;
        OwnedLog.LogMessage(
            $"Shop snapshot applied revision={snapshot.Revision} runtimes={snapshot.Runtimes.Length} " +
            $"purchaseSets={snapshot.PurchaseSets.Length} pendingSources={snapshot.PendingSpawns.Length}");
#if LYCOREIA_TEST && LYC_TEST_HARNESS // TestShopTrace belongs to the unported test harness
        if (Environment.GetEnvironmentVariable("LYCOREIA_TEST_TRACE_SHOP") == "1")
            Lycoreia.Owned.Shared.Utils.TestShopTrace.RequestDump($"client applied revision={snapshot.Revision}");
#endif
    }

    private static void PumpPendingPublishRetry()
    {
        if (!ModFacade.Server.IsRunning)
        {
            PendingPublishRetry.Clear();
            return;
        }

        if (HandlingPacket)
            return;

        if (!PendingPublishRetry.TryTake(Time.unscaledTime, out var reason))
            return;

        TraceRestockPhase("retry-publish-start", reason, $"revision={serverRevision}");
        PublishSnapshot(reason);
        TraceRestockPhase("retry-publish-end", reason, $"revision={serverRevision}");
    }

    private static void PumpPendingRestockSnapshot()
    {
        if (!ModFacade.Server.IsRunning)
        {
            pendingRestockSnapshotReason = null;
            pendingRestockSnapshotAfterFrame = -1;
            return;
        }

        var reason = pendingRestockSnapshotReason;
        if (reason == null || Time.frameCount < pendingRestockSnapshotAfterFrame)
            return;

        pendingRestockSnapshotReason = null;
        pendingRestockSnapshotAfterFrame = -1;
        TraceRestockPhase("deferred-publish-start", reason,
            $"frame={Time.frameCount} revision={serverRevision}");
        PublishSnapshot(reason);
        TraceRestockPhase("deferred-publish-end", reason,
            $"frame={Time.frameCount} revision={serverRevision}");
    }

    private static void TraceRestockPhase(string phase, string reason, string details)
    {
#if LYCOREIA_TEST
        var isRestock = reason.Contains("restock", StringComparison.OrdinalIgnoreCase);
        var tracingEnabled = isRestock
            ? Environment.GetEnvironmentVariable("LYCOREIA_TEST_TRACE_SHOP_RESTOCK") == "1"
            : Environment.GetEnvironmentVariable("LYCOREIA_TEST_TRACE_SHOP") == "1";
        if (!tracingEnabled)
            return;

        OwnedLog.LogMessage(
            $"[LYCOREIA_TEST] shop {(isRestock ? "restock" : "snapshot")} phase={phase} frame={Time.frameCount} " +
            $"reason={reason} {details}");
#endif
    }

    internal static bool ShouldAcceptSnapshot(Guid currentSession, ulong currentRevision,
        Guid incomingSession, ulong incomingRevision)
        => ShopSnapshotRevisionPolicy.ShouldAccept(
            currentSession, currentRevision, incomingSession, incomingRevision);

    private static void EnsureServerSession()
    {
        if (!ModFacade.Server.IsRunning)
        {
            serverWasRunning = false;
            PendingHostSnapshots.Clear();
            return;
        }

        if (serverWasRunning)
            return;

        serverWasRunning = true;
        serverSessionId = Guid.NewGuid();
        serverRevision = 0;
        PurchaseDecisions.Clear();
        PendingPublishRetry.Clear();
    }

    private static void PumpPendingHostSnapshots()
    {
        if (!ModFacade.Server.IsRunning)
        {
            PendingHostSnapshots.Clear();
            return;
        }

        if (PendingHostSnapshots.IsEmpty || Time.unscaledTime < nextHostSnapshotRetryAt)
            return;

        EnsureServerSession();
        nextHostSnapshotRetryAt = Time.unscaledTime + 1f;
        foreach (var pending in PendingHostSnapshots.ToArray())
        {
            if (!ModFacade.Server.ClientManager.TryGetClient(pending.Value, out _))
            {
                PendingHostSnapshots.TryRemove(pending.Key, out _);
                continue;
            }

            if (TrySendSnapshot(pending.Value, out var error))
                continue;

            if (Time.unscaledTime >= nextHostSnapshotRetryLogAt)
            {
                nextHostSnapshotRetryLogAt = Time.unscaledTime + 10f;
                OwnedLog.LogWarning(
                    $"Shop snapshot for {pending.Value} is still queued until shop data is ready: {error}");
            }
        }
    }

    private static bool TrySendSnapshot(Lycoreia.Net.Peer endpoint, out string error)
    {
        error = string.Empty;
        if (serverRevision == 0)
            serverRevision = 1;

        TraceRestockPhase("send-snapshot-create-begin", "shop snapshot", $"revision={serverRevision}");
        if (!TryCreateSnapshot(serverSessionId, serverRevision, out var snapshot, out error))
            return false;
        TraceRestockPhase("send-snapshot-create-end", "shop snapshot",
            $"revision={serverRevision} runtimes={snapshot!.Runtimes.Length} " +
            $"purchaseSets={snapshot.PurchaseSets.Length} pendingSources={snapshot.PendingSpawns.Length}");

        try
        {
            TraceRestockPhase("send-snapshot-wire-begin", "shop snapshot", $"endpoint={endpoint}");
            ModFacade.Server.SendToClient(snapshot!, endpoint);
            TraceRestockPhase("send-snapshot-wire-end", "shop snapshot", $"endpoint={endpoint}");
            PendingHostSnapshots.TryRemove(GetEndpointKey(endpoint), out _);
            return true;
        }
        catch (Exception exception)
        {
            error = $"sending failed ({exception.GetType().Name}: {exception.Message})";
            return false;
        }
    }

    private static string GetEndpointKey(Lycoreia.Net.Peer endpoint)
        => endpoint.PlayerId ?? string.Empty;

    private static void ResetClientState()
    {
        lastClientSessionId = Guid.Empty;
        lastClientRevision = 0;
        pendingClientSnapshot = null;
        nextPendingApplyLog = 0;
        nextPendingApplyAt = 0;
        PendingClientRequests.Clear();
    }

    private static void SendPurchaseResult(Lycoreia.Net.Peer endpoint, Guid requestId, bool accepted)
    {
        try
        {
            ModFacade.Server.SendToClient(new ShopPurchaseResultPacket
            {
                RequestId = requestId,
                Accepted = accepted
            }, endpoint);
        }
        catch (Exception exception)
        {
            OwnedLog.LogError($"Could not send shop purchase result requestId={requestId}: {exception}");
        }
    }

    private static bool TryCreateSnapshot(Guid sessionId, ulong revision,
        out ShopStateSnapshotPacket? packet, out string error, string reason = "shop snapshot")
    {
        packet = null;
        error = string.Empty;

        TraceRestockPhase("capture-director-begin", reason, $"revision={revision}");
        var director = FindShopDirector();
        if (!director || director!._dataModel == null || director._runtimes == null)
        {
            error = "ShopDirector or its data model is not loaded.";
            TraceRestockPhase("capture-director-failed", reason, $"revision={revision} error={error}");
            return false;
        }

        TraceRestockPhase("capture-runtimes-begin", reason, $"revision={revision}");
        if (!TryCaptureRuntimes(director, out var runtimeStates, out var loadedItems, out error, reason))
        {
            TraceRestockPhase("capture-runtimes-failed", reason, $"revision={revision} error={error}");
            return false;
        }
        TraceRestockPhase("capture-runtimes-end", reason,
            $"revision={revision} runtimes={runtimeStates.Length} items={loadedItems.Count}");

        TraceRestockPhase("capture-purchase-sets-begin", reason, $"revision={revision}");
        if (!TryCapturePurchaseSets(director._dataModel, loadedItems, out var purchaseSets, out error))
        {
            TraceRestockPhase("capture-purchase-sets-failed", reason,
                $"revision={revision} error={error}");
            return false;
        }
        TraceRestockPhase("capture-purchase-sets-end", reason,
            $"revision={revision} purchaseSets={purchaseSets.Length}");

        TraceRestockPhase("capture-pending-spawns-begin", reason, $"revision={revision}");
        if (!TryCapturePendingSpawns(director._dataModel, out var pendingSpawns, out error))
        {
            TraceRestockPhase("capture-pending-spawns-failed", reason,
                $"revision={revision} error={error}");
            return false;
        }
        TraceRestockPhase("capture-pending-spawns-end", reason,
            $"revision={revision} sources={pendingSpawns.Length}");

        packet = new ShopStateSnapshotPacket
        {
            SessionId = sessionId,
            Revision = revision,
            Runtimes = runtimeStates,
            PurchaseSets = purchaseSets,
            PendingSpawns = pendingSpawns
        };

        TraceRestockPhase("capture-validation-begin", reason, $"revision={revision}");
        if (!ValidatePacket(packet, out error))
        {
            packet = null;
            TraceRestockPhase("capture-validation-failed", reason,
                $"revision={revision} error={error}");
            return false;
        }

        TraceRestockPhase("capture-end", reason, $"revision={revision}");
        return true;
    }

    private static bool TryCaptureRuntimes(ShopDirector director,
        out ShopRuntimeState[] runtimeStates, out List<ShopRuntimeItem> loadedItems, out string error,
        string reason = "shop snapshot", bool allowDuplicateItems = false)
    {
        runtimeStates = Array.Empty<ShopRuntimeState>();
        loadedItems = new List<ShopRuntimeItem>();
        error = string.Empty;

        TraceRestockPhase("capture-runtime-list-read-begin", reason, "accessing ShopDirector._runtimes");
        var runtimes = director._runtimes;
        TraceRestockPhase("capture-runtime-list-read-end", reason, $"null={runtimes == null}");
        if (runtimes == null)
        {
            error = "Shop runtime collection is missing.";
            return false;
        }

        TraceRestockPhase("capture-runtime-count-read-begin", reason, "reading ShopDirector._runtimes.Count");
        var runtimeCount = runtimes.Count;
        TraceRestockPhase("capture-runtime-count-read-end", reason, $"count={runtimeCount}");
        if (runtimeCount < 0 || runtimeCount > MaxRuntimes)
        {
            error = $"Runtime count is missing or exceeds {MaxRuntimes}.";
            return false;
        }

        var runtimeTags = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ShopRuntimeState>(runtimeCount);
        TraceRestockPhase("capture-runtime-list-ready", reason, $"count={runtimeCount}");
        for (var runtimeIndex = 0; runtimeIndex < runtimeCount; runtimeIndex++)
        {
            TraceRestockPhase("capture-runtime-read-begin", reason, $"index={runtimeIndex}");
            var runtime = runtimes[runtimeIndex];
            TraceRestockPhase("capture-runtime-read-end", reason, $"index={runtimeIndex} null={runtime == null}");
            if (runtime == null)
            {
                error = "A shop runtime is absent or still loading.";
                return false;
            }

            var isDoneLoading = runtime.IsDoneLoading;
            TraceRestockPhase("capture-runtime-readiness", reason,
                $"index={runtimeIndex} done={isDoneLoading}");
            var definition = runtime.Definition;
            TraceRestockPhase("capture-runtime-definition", reason,
                $"index={runtimeIndex} null={definition == null}");
            if (!isDoneLoading || definition == null)
            {
                error = "A shop runtime is absent or still loading.";
                return false;
            }

            var tag = definition.ShopAssetTag;
            TraceRestockPhase("capture-runtime-tag", reason, $"index={runtimeIndex} tag={tag}");
            if (string.IsNullOrWhiteSpace(tag) || !runtimeTags.Add(tag))
            {
                error = "Shop runtime tags are empty or duplicated.";
                return false;
            }

            var nextTime = runtime._nextRestockTime;
            var state = new ShopRuntimeState
            {
                ShopAssetTag = tag,
                IsAvailable = runtime.IsShopAvailable,
                HasNewStock = runtime.HasNewStock,
                HasGameWorldTime = nextTime.HasGameWorldTime,
                GameWorldTimeSeconds = nextTime.GameWorldTimeSeconds,
                HasRealTime = nextTime.HasRealTime,
                RealTimeSeconds = nextTime.RealTimeSeconds
            };

            TraceRestockPhase("capture-categories-begin", reason, $"index={runtimeIndex} tag={tag}");
            var categories = GetCategories(runtime);
            TraceRestockPhase("capture-categories-end", reason,
                $"index={runtimeIndex} tag={tag} count={categories.Count}");
            if (categories.Count > MaxCategoriesPerRuntime)
            {
                error = $"Shop '{tag}' has more than {MaxCategoriesPerRuntime} categories.";
                return false;
            }

            var categoryNames = new HashSet<string>(StringComparer.Ordinal);
            var categoryStates = new List<ShopCategoryState>(categories.Count);
            for (var categoryIndex = 0; categoryIndex < categories.Count; categoryIndex++)
            {
                TraceRestockPhase("capture-category-begin", reason,
                    $"runtime={runtimeIndex} category={categoryIndex}");
                var category = categories[categoryIndex];
                if (category == null || !category.IsDoneLoading || category._cachedItemList == null)
                {
                    error = $"A category in shop '{tag}' is absent or still loading.";
                    return false;
                }

                var analyticsName = category.AnalyticsName;
                if (string.IsNullOrWhiteSpace(analyticsName) || !categoryNames.Add(analyticsName))
                {
                    error = $"Shop '{tag}' has empty or duplicate category names.";
                    return false;
                }

                if (category._cachedItemList.Count > MaxItemsPerCategory)
                {
                    error = $"Category '{analyticsName}' has more than {MaxItemsPerCategory} stock items.";
                    return false;
                }

                var stock = new List<ShopItemStockState>(category._cachedItemList.Count);
                var itemGuids = new HashSet<string>(StringComparer.Ordinal);
                for (var itemIndex = 0; itemIndex < category._cachedItemList.Count; itemIndex++)
                {
                    var item = category._cachedItemList[itemIndex];
                    if (item == null || string.IsNullOrWhiteSpace(item.AssetGuid))
                    {
                        error = $"Category '{analyticsName}' has missing item asset IDs.";
                        return false;
                    }

                    if (!itemGuids.Add(item.AssetGuid))
                    {
                        // after a reseed with the host's seed the guest's RangeExchange listed one item twice (its stock also
                        // depends on local unlocks); the readiness check failed forever and no later snapshot could apply.
                        if (allowDuplicateItems)
                            continue;
                        error = $"Category '{analyticsName}' has duplicate item asset IDs.";
                        return false;
                    }

                    loadedItems.Add(item);
                    stock.Add(new ShopItemStockState { AssetGuid = item.AssetGuid, IsSoldOut = item.IsSoldOut });
                }

                categoryStates.Add(new ShopCategoryState
                {
                    AnalyticsName = analyticsName,
                    LastSeedUsed = category.LastSeedUsed,
                    NextOverrideIndex = category._nextOverrideIndex,
                    Items = stock.OrderBy(item => item.AssetGuid, StringComparer.Ordinal).ToArray()
                });
                TraceRestockPhase("capture-category-end", reason,
                    $"runtime={runtimeIndex} category={categoryIndex} items={stock.Count}");
            }

            state.Categories = categoryStates
                .OrderBy(category => category.AnalyticsName, StringComparer.Ordinal)
                .ToArray();
            result.Add(state);
        }

        runtimeStates = result.OrderBy(runtime => runtime.ShopAssetTag, StringComparer.Ordinal).ToArray();
        return true;
    }

    private static bool TryCapturePurchaseSets(ShopDataModel model, List<ShopRuntimeItem> loadedItems,
        out ShopPurchaseSetState[] purchaseSets, out string error)
    {
        purchaseSets = Array.Empty<ShopPurchaseSetState>();
        error = string.Empty;
        var source = model._shopPurchasesByCategory;
        if (source == null || source.Count > MaxPurchaseSets)
        {
            error = $"Shop purchase-set count is missing or exceeds {MaxPurchaseSets}.";
            return false;
        }

        var result = new List<ShopPurchaseSetState>(source.Count);
        foreach (var pair in source)
        {
            var set = pair.Value;
            if (set == null || string.IsNullOrWhiteSpace(pair.Key) || set.Purchases == null ||
                set.Purchases.Count > MaxPurchasesPerSet)
            {
                error = "A shop purchase set is invalid or exceeds its entry limit.";
                return false;
            }

            var purchases = new List<ShopPurchaseEntryState>(set.Purchases.Count);
            foreach (var purchase in set.Purchases)
            {
                if (purchase == null || purchase.Count <= 0)
                {
                    error = $"Purchase set '{pair.Key}' contains an invalid purchase count.";
                    return false;
                }

                // rc11: entries travel by the game's ShopItemId, so a purchase of an item only one seat stocks is kept.
                var itemId = purchase.UniqueId.Id ?? string.Empty;
                var matchingGuids = loadedItems
                    .Where(item => item.UnlockType == purchase.UnlockType &&
                                   item.UniqueId.Equals(purchase.UniqueId))
                    .Select(item => item.AssetGuid)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (itemId.Length == 0 && matchingGuids.Length != 1)
                    continue;

                purchases.Add(new ShopPurchaseEntryState
                {
                    ItemAssetGuid = matchingGuids.Length == 1 ? matchingGuids[0] : string.Empty,
                    ItemId = itemId,
                    UnlockType = (int)purchase.UnlockType,
                    Count = purchase.Count
                });
            }

            result.Add(new ShopPurchaseSetState
            {
                CategoryIdentifier = pair.Key,
                Seed = set.Seed,
                Purchases = purchases.ToArray()
            });
        }

        purchaseSets = result.OrderBy(set => set.CategoryIdentifier, StringComparer.Ordinal).ToArray();
        return true;
    }

    private static bool TryCapturePendingSpawns(ShopDataModel model,
        out ShopPendingSourceState[] pendingSpawns, out string error)
    {
        pendingSpawns = Array.Empty<ShopPendingSourceState>();
        error = string.Empty;
        var source = model._itemsPendingSpawn;
        if (source == null || source.Count > MaxPendingSources)
        {
            error = $"Pending shop-spawn source count is missing or exceeds {MaxPendingSources}.";
            return false;
        }

        var result = new List<ShopPendingSourceState>(source.Count);
        foreach (var pair in source)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null ||
                pair.Value.Count > MaxPendingItemsPerSource)
            {
                error = "A pending shop-spawn queue is invalid or exceeds its item limit.";
                return false;
            }

            var ids = new List<int>(pair.Value.Count);
            foreach (var item in pair.Value)
            {
                if (item == null)
                {
                    error = $"Pending shop-spawn queue '{pair.Key}' contains a null item.";
                    return false;
                }

                int persistentId;
                try
                {
                    persistentId = NetworkActorManager.GetPersistentID(item);
                }
                catch (Exception exception)
                {
                    error = $"Could not identify pending spawn item in '{pair.Key}': {exception.Message}";
                    return false;
                }

                if (persistentId < 0)
                {
                    error = $"Pending shop-spawn item in '{pair.Key}' has no registered persistent ID.";
                    return false;
                }

                ids.Add(persistentId);
            }

            result.Add(new ShopPendingSourceState { SourceId = pair.Key, IdentifiableIds = ids.ToArray() });
        }

        pendingSpawns = result.OrderBy(source => source.SourceId, StringComparer.Ordinal).ToArray();
        return true;
    }

    private static bool TryApplySnapshot(ShopStateSnapshotPacket packet, out string error)
    {
        error = string.Empty;
        var director = FindShopDirector();
        if (!director || director!._dataModel == null || director._runtimes == null)
        {
            error = "ShopDirector or its data model has not loaded yet.";
            return false;
        }

        if (!TryCaptureRuntimes(director, out _, out _, out error, allowDuplicateItems: true))
            return false;

        var localRuntimes = director._runtimes.ToArray()
            .Where(runtime => runtime != null)
            .ToDictionary(runtime => runtime.Definition.ShopAssetTag, StringComparer.Ordinal);
        var targetRuntimes = packet.Runtimes.ToDictionary(runtime => runtime.ShopAssetTag, StringComparer.Ordinal);
        if (!localRuntimes.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(targetRuntimes.Keys))
        {
            error = "Local and host shop runtime tags differ.";
            return false;
        }

        var localCategories = new Dictionary<(string Shop, string Category), ShopCategoryRuntime>();
        var targetCategories = new Dictionary<(string Shop, string Category), ShopCategoryState>();
        foreach (var runtimeState in packet.Runtimes)
        {
            var runtime = localRuntimes[runtimeState.ShopAssetTag];
            foreach (var category in GetCategories(runtime))
            {
                var key = (runtimeState.ShopAssetTag, category.AnalyticsName);
                if (!localCategories.TryAdd(key, category))
                {
                    error = $"Local shop category key is duplicated: {key}.";
                    return false;
                }
            }

            foreach (var categoryState in runtimeState.Categories)
            {
                var key = (runtimeState.ShopAssetTag, categoryState.AnalyticsName);
                if (!targetCategories.TryAdd(key, categoryState))
                {
                    error = $"Host shop category key is duplicated: {key}.";
                    return false;
                }
            }
        }

        if (!localCategories.Keys.ToHashSet().SetEquals(targetCategories.Keys))
        {
            error = "Local and host shop category keys differ.";
            return false;
        }

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        var oldCategorySeeds = new List<(ShopCategoryRuntime Category, int Seed, int OverrideIndex)>();
        try
        {
            foreach (var (key, categoryState) in targetCategories)
            {
                var category = localCategories[key];
                var localAssetGuids = GetCategoryItems(category).Select(item => item.AssetGuid)
                    .ToHashSet(StringComparer.Ordinal);
                var targetAssetGuids = categoryState.Items.Select(item => item.AssetGuid)
                    .ToHashSet(StringComparer.Ordinal);
                var needsRepopulate = category.LastSeedUsed != categoryState.LastSeedUsed ||
                                      category._nextOverrideIndex != categoryState.NextOverrideIndex ||
                                      !localAssetGuids.SetEquals(targetAssetGuids);
                if (!needsRepopulate)
                    continue;

                oldCategorySeeds.Add((category, category.LastSeedUsed, category._nextOverrideIndex));
                category._lastSeedUsed = categoryState.LastSeedUsed;
                category._nextOverrideIndex = categoryState.NextOverrideIndex;
                category.RepopulateImmediately();
                var repopulatedGuids = GetCategoryItems(category).Select(item => item.AssetGuid)
                    .ToHashSet(StringComparer.Ordinal);
                if (!repopulatedGuids.SetEquals(targetAssetGuids))
                {
                    // Seeded generation also depends on local unlocks. Keep the reseeded stock and
                    // sync every item both peers share instead of rejecting the whole snapshot.
                    if (PartialStockWarnings.Add(key.Category))
                        OwnedLog.LogWarning($"Category '{key.Category}' stock differs from the host after reseed; syncing shared items only.");
                }
            }

            var stockUpdates = new List<(ShopRuntimeItem Item, bool IsSoldOut)>();
            foreach (var runtimeState in packet.Runtimes)
            {
                foreach (var categoryState in runtimeState.Categories)
                {
                    var category = localCategories[(runtimeState.ShopAssetTag, categoryState.AnalyticsName)];
                    var items = GetCategoryItems(category);
                    var hostItems = categoryState.Items.ToDictionary(item => item.AssetGuid, item => item.IsSoldOut, StringComparer.Ordinal);
                    // rc11: the guest's stock is generated locally and can differ from the host's; the guest buys its own items natively.
                    // Only items the host also lists take the host's sold-out state; the rest keep the local state.
                    foreach (var localItem in items)
                        if (hostItems.TryGetValue(localItem.AssetGuid, out var soldOut))
                            stockUpdates.Add((localItem, soldOut));
                }
            }

            if (!TryBuildPurchaseSets(packet.PurchaseSets, localCategories,
                    out var purchaseSets, out error) ||
                !TryBuildPendingQueues(packet.PendingSpawns, out var pendingQueues, out error))
            {
                RollBackCategorySeeds(oldCategorySeeds);
                return false;
            }

            // rc11: pending shop spawns stay per seat. Each seat delivers what it bought itself (the spawned actors sync); copying the
            // host's queues would drop the guest's own deliveries and could spawn the host's twice.
            _ = pendingQueues;
            director._dataModel._shopPurchasesByCategory = purchaseSets;

            foreach (var runtimeState in packet.Runtimes)
            {
                var runtime = localRuntimes[runtimeState.ShopAssetTag];
                runtime._isAvailable = runtimeState.IsAvailable;
                runtime._hasNewStock = runtimeState.HasNewStock;
                runtime._nextRestockTime = new ShopUpdateTimePair
                {
                    GameWorldTimeSeconds = runtimeState.GameWorldTimeSeconds,
                    RealTimeSeconds = runtimeState.RealTimeSeconds
                };
            }

            foreach (var update in stockUpdates)
            {
                update.Item._isSoldOut = update.IsSoldOut;
                try
                {
                    update.Item.ItemChanged?.Invoke(update.Item);
                }
                catch (Exception exception)
                {
                    OwnedLog.LogWarning($"Shop item UI notification failed: {exception.Message}");
                }
            }

        }
        catch (Exception exception)
        {
            RollBackCategorySeeds(oldCategorySeeds);
            error = $"Applying shop state failed: {exception.GetType().Name}: {exception.Message}";
            return false;
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        return true;
    }

    private static bool TryBuildPurchaseSets(ShopPurchaseSetState[] states,
        Dictionary<(string Shop, string Category), ShopCategoryRuntime> localCategories,
        out CppCollections.Dictionary<string, ShopDataPurchaseSet> purchaseSets, out string error)
    {
        purchaseSets = new CppCollections.Dictionary<string, ShopDataPurchaseSet>();
        error = string.Empty;
        foreach (var state in states)
        {
            var set = new ShopDataPurchaseSet
            {
                _categoryIdentifier = state.CategoryIdentifier,
                _seed = state.Seed
            };

            foreach (var purchase in state.Purchases)
            {
                if (!string.IsNullOrEmpty(purchase.ItemId))
                {
                    var id = new ShopItemId { _id = purchase.ItemId };
                    var info = ShopDataItemInfo.FromUniqueIdExplicit(ref id, (AcquireGadgetType)purchase.UnlockType);
                    set.AddPurchase(ref info, purchase.Count);
                    continue;
                }
                var candidates = localCategories.Values
                    .SelectMany(GetCategoryItems)
                    .Where(item => item.AssetGuid == purchase.ItemAssetGuid &&
                                   (int)item.UnlockType == purchase.UnlockType)
                    .ToArray();
                if (candidates.Length != 1)
                    continue; // not stocked in this peer's loaded rotation; history only

                var uniqueId = candidates[0].UniqueId;
                var itemInfo = ShopDataItemInfo.FromUniqueIdExplicit(
                    ref uniqueId, (AcquireGadgetType)purchase.UnlockType);
                set.AddPurchase(ref itemInfo, purchase.Count);
            }

            purchaseSets[state.CategoryIdentifier] = set;
        }

        return true;
    }

    private static bool TryBuildPendingQueues(ShopPendingSourceState[] states,
        out CppCollections.Dictionary<string, CppCollections.Queue<IdentifiableType>> pendingQueues,
        out string error)
    {
        pendingQueues = new CppCollections.Dictionary<string, CppCollections.Queue<IdentifiableType>>();
        error = string.Empty;
        foreach (var state in states)
        {
            var queue = new CppCollections.Queue<IdentifiableType>();
            foreach (var persistentId in state.IdentifiableIds)
            {
                if (!ActorManager.ActorTypes.TryGetValue(persistentId, out var item) || item == null)
                {
                    error = $"Pending shop item persistent ID {persistentId} is not registered locally.";
                    return false;
                }

                queue.Enqueue(item);
            }

            pendingQueues[state.SourceId] = queue;
        }

        return true;
    }

    /// <summary>
    /// (rc8 logs): the game fills a category's item list only when that seat opens the shop UI (ShopRuntime.StartUISession clears
    /// the cache, the UI repopulates it). A host that never opened the shop had EMPTY lists: every snapshot told the guest "no stock" and
    /// every guest purchase was rejected (found=0). The host now builds empty categories itself through the same call the shop page uses
    /// (GetRuntimeItems with the shop's event director), which is the stock its own UI would show.
    /// </summary>
    private static string StockSummary(ShopDirector director)
    {
        try
        {
            var parts = new List<string>();
            foreach (var runtime in director._runtimes)
                if (runtime != null)
                    foreach (var category in GetCategories(runtime))
                        if (category != null)
                            parts.Add($"{category.AnalyticsName}:{category._cachedItemList?.Count ?? -1}");
            return string.Join(",", parts);
        }
        catch (Exception exception) { return "error:" + exception.GetType().Name; }
    }

    private static bool ValidatePacket(ShopStateSnapshotPacket packet, out string error)
    {
        error = string.Empty;
        if (packet.Runtimes == null || packet.Runtimes.Length > MaxRuntimes ||
            packet.PurchaseSets == null || packet.PurchaseSets.Length > MaxPurchaseSets ||
            packet.PendingSpawns == null || packet.PendingSpawns.Length > MaxPendingSources)
        {
            error = "Shop snapshot exceeds its top-level entry limits.";
            return false;
        }

        var runtimeTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var runtime in packet.Runtimes)
        {
            if (runtime == null || string.IsNullOrWhiteSpace(runtime.ShopAssetTag) ||
                !runtimeTags.Add(runtime.ShopAssetTag) || runtime.Categories == null ||
                runtime.Categories.Length > MaxCategoriesPerRuntime ||
                !double.IsFinite(runtime.GameWorldTimeSeconds) || !double.IsFinite(runtime.RealTimeSeconds))
            {
                error = "Shop snapshot contains an invalid runtime entry.";
                return false;
            }

            var categoryNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var category in runtime.Categories)
            {
                if (category == null || string.IsNullOrWhiteSpace(category.AnalyticsName) ||
                    !categoryNames.Add(category.AnalyticsName) || category.Items == null ||
                    category.Items.Length > MaxItemsPerCategory)
                {
                    error = $"Shop snapshot contains an invalid category in '{runtime.ShopAssetTag}'.";
                    return false;
                }

                var itemGuids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in category.Items)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.AssetGuid) || !itemGuids.Add(item.AssetGuid))
                    {
                        error = $"Shop snapshot contains a missing or duplicate stock item in '{runtime.ShopAssetTag}'.";
                        return false;
                    }
                }
            }
        }

        var categoryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var set in packet.PurchaseSets)
        {
            if (set == null || string.IsNullOrWhiteSpace(set.CategoryIdentifier) ||
                !categoryIds.Add(set.CategoryIdentifier) || set.Purchases == null ||
                set.Purchases.Length > MaxPurchasesPerSet)
            {
                error = "Shop snapshot contains an invalid or duplicate purchase set.";
                return false;
            }

            var entries = new HashSet<(string, int)>();
            foreach (var purchase in set.Purchases)
            {
                if (purchase == null || (string.IsNullOrWhiteSpace(purchase.ItemAssetGuid) && string.IsNullOrWhiteSpace(purchase.ItemId)) ||
                    purchase.UnlockType < 0 || purchase.Count <= 0 ||
                    !entries.Add((string.IsNullOrEmpty(purchase.ItemId) ? "g:" + purchase.ItemAssetGuid : "i:" + purchase.ItemId, purchase.UnlockType)))
                {
                    error = $"Shop purchase set '{set.CategoryIdentifier}' contains an invalid purchase.";
                    return false;
                }
            }
        }

        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pending in packet.PendingSpawns)
        {
            if (pending == null || string.IsNullOrWhiteSpace(pending.SourceId) ||
                !sourceIds.Add(pending.SourceId) || pending.IdentifiableIds == null ||
                pending.IdentifiableIds.Length > MaxPendingItemsPerSource ||
                pending.IdentifiableIds.Any(id => id < 0))
            {
                error = "Shop snapshot contains an invalid pending-spawn source.";
                return false;
            }
        }

        return true;
    }

    private static ShopDirector? FindShopDirector()
    {
        if (cachedDirector && cachedDirector!._dataModel != null)
            return cachedDirector;
        cachedDirector = null;
        var directors = Resources.FindObjectsOfTypeAll<ShopDirector>()
            .Where(director => director && director._dataModel != null)
            .ToArray();
        if (directors.Length == 1)
            cachedDirector = directors[0];
        return cachedDirector;
    }

    /// <summary>
    /// Finds the host's runtime item for a guest purchase. Before rc7 the lookup demanded exactly one item with this asset guid among the
    /// runtimes tagged like the guest's category; the same item listed in two categories (or a category whose source tag differs from its
    /// runtime's tag) was rejected as "not uniquely loaded" and the guest's toy purchases did nothing. Now: every loaded runtime,
    /// narrowed by runtime tag, then category, then unlock type; several left that are equivalent items count as one.
    /// </summary>
    private static bool TryFindRuntimeItem(string shopTag, string itemGuid, string? categoryName, int unlockType,
        out ShopDirector? director, out ShopRuntimeItem? item, out string lookup)
    {
        director = FindShopDirector();
        item = null;
        lookup = "no shop director";
        if (!director || director!._runtimes == null)
            return false;

        var all = new List<(ShopRuntimeItem Item, bool Tag, bool Category, bool Unlock, string Where)>();
        int runtimes = 0, tagged = 0, categories = 0;
        foreach (var runtime in director._runtimes)
        {
            if (runtime == null || runtime.Definition == null)
                continue;
            runtimes++;
            var runtimeTag = runtime.Definition.ShopAssetTag;
            var tagMatch = runtimeTag == shopTag;
            if (tagMatch) tagged++;
            foreach (var category in GetCategories(runtime))
            {
                categories++;
                var categoryMatch = !string.IsNullOrEmpty(categoryName) && category.AnalyticsName == categoryName;
                foreach (var candidate in GetCategoryItems(category))
                {
                    if (candidate.AssetGuid != itemGuid)
                        continue;
                    all.Add((candidate, tagMatch || category.SourceShopAssetTag == shopTag, categoryMatch,
                        unlockType < 0 || (int)candidate.UnlockType == unlockType, runtimeTag + "/" + category.AnalyticsName));
                }
            }
        }

        var pick = all;
        if (pick.Any(c => c.Tag)) pick = pick.Where(c => c.Tag).ToList();
        if (pick.Count > 1 && pick.Any(c => c.Category)) pick = pick.Where(c => c.Category).ToList();
        if (pick.Count > 1 && pick.Any(c => c.Unlock)) pick = pick.Where(c => c.Unlock).ToList();
        lookup = $"runtimes={runtimes}; tagged={tagged}; categories={categories}; items={StockSummary(director)}; found={all.Count}" +
                 (all.Count > 0 ? $" in {string.Join(",", all.Select(c => c.Where))}" : string.Empty);
        if (pick.Count == 0)
            return false;

        var first = pick[0].Item;
        foreach (var other in pick.Skip(1))
        {
            if (other.Item.Pointer == first.Pointer)
                continue;
            bool same;
            try { same = first.EquivalentTo(other.Item); } catch { same = false; }
            if (!same && (int)other.Item.UnlockType != (int)first.UnlockType)
                return false;   // really different items under one guid: refuse rather than sell the wrong one
        }

        item = first;
        if (all.Count > 1)
            OwnedLog.LogMessage($"Shop purchase lookup: item '{itemGuid}' is listed {all.Count} times ({lookup}); using {pick[0].Where}.");
        return true;
    }

    private static List<ShopCategoryRuntime> GetCategories(ShopRuntime runtime)
    {
        var result = new List<ShopCategoryRuntime>();
        TraceRestockPhase("capture-category-provider-read-begin", "shop snapshot",
            "accessing ShopRuntime._dataProvider");
        var provider = runtime._dataProvider;
        TraceRestockPhase("capture-category-provider-read-end", "shop snapshot",
            $"null={provider == null}");
        if (provider == null)
            return result;

        TraceRestockPhase("capture-loaded-categories-call-begin", "shop snapshot",
            "calling IShopRuntimeDataProvider.GetLoadedCategories");
        var categories = provider.GetLoadedCategories();
        TraceRestockPhase("capture-loaded-categories-call-end", "shop snapshot",
            $"null={categories == null}");
        if (categories == null)
            return result;

        TraceRestockPhase("capture-loaded-categories-collection-cast-begin", "shop snapshot",
            "casting to IReadOnlyCollection<ShopCategoryRuntime>");
        var categoryCollection = categories.TryCast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<ShopCategoryRuntime>>();
        TraceRestockPhase("capture-loaded-categories-collection-cast-end", "shop snapshot",
            $"null={categoryCollection == null}");
        if (categoryCollection == null)
            return result;

        TraceRestockPhase("capture-loaded-category-count-begin", "shop snapshot",
            "reading IReadOnlyCollection<ShopCategoryRuntime>.Count");
        var count = categoryCollection.Count;
        TraceRestockPhase("capture-loaded-category-count-end", "shop snapshot", $"count={count}");
        var countToRead = Math.Min(count, MaxCategoriesPerRuntime + 1);
        for (var index = 0; index < countToRead; index++)
        {
            TraceRestockPhase("capture-loaded-category-read-begin", "shop snapshot", $"index={index}");
            var category = categories[index];
            TraceRestockPhase("capture-loaded-category-read-end", "shop snapshot",
                $"index={index} null={category == null}");
            result.Add(category!);
        }

        return result;
    }

    private static List<ShopRuntimeItem> GetCategoryItems(ShopCategoryRuntime category)
    {
        if (category?._cachedItemList == null)
            return new List<ShopRuntimeItem>();

        var result = new List<ShopRuntimeItem>(category._cachedItemList.Count);
        for (var index = 0; index < category._cachedItemList.Count; index++)
        {
            var item = category._cachedItemList[index];
            if (item != null)
                result.Add(item);
        }

        return result;
    }

    private static void RollBackCategorySeeds(
        IEnumerable<(ShopCategoryRuntime Category, int Seed, int OverrideIndex)> changedCategories)
    {
        foreach (var oldState in changedCategories.Reverse())
        {
            try
            {
                oldState.Category._lastSeedUsed = oldState.Seed;
                oldState.Category._nextOverrideIndex = oldState.OverrideIndex;
                oldState.Category.RepopulateImmediately();
            }
            catch (Exception exception)
            {
                OwnedLog.LogError($"Could not roll back shop category after snapshot failure: {exception}");
            }
        }
    }
}

/// <summary>Pure sequencing rule for authoritative shop snapshots and corrections.</summary>
internal static class ShopSnapshotRevisionPolicy
{
    internal static bool TryAdvance(ulong currentRevision, out ulong nextRevision)
    {
        if (currentRevision == ulong.MaxValue)
        {
            nextRevision = currentRevision;
            return false;
        }

        nextRevision = currentRevision + 1;
        return nextRevision != 0;
    }

    internal static bool ShouldAccept(Guid currentSession, ulong currentRevision,
        Guid incomingSession, ulong incomingRevision)
        => incomingSession != Guid.Empty && incomingRevision > 0 &&
           (incomingSession != currentSession || incomingRevision > currentRevision);

    internal static bool VerifyRules()
    {
        var session = Guid.Parse("7474c45b-5ca6-41eb-8dd4-293a135ef028");
        var nextSession = Guid.Parse("85f37b94-462d-4cb4-9eaa-d3c0ba8d8e82");
        return !ShouldAccept(session, 7, session, 7)
               && TryAdvance(7, out var correctionRevision)
               && correctionRevision == 8
               && ShouldAccept(session, 7, session, correctionRevision)
               && !ShouldAccept(session, 8, session, correctionRevision)
               && ShouldAccept(session, 8, nextSession, 1)
               && !TryAdvance(ulong.MaxValue, out var exhaustedRevision)
               && exhaustedRevision == ulong.MaxValue;
    }
}

/// <summary>
/// Retains the latest full-state publication reason until capture/send succeeds.
/// A snapshot contains current state, so coalescing several failed publications is safe.
/// </summary>
internal sealed class ShopSnapshotPublishRetryState
{
    private string? pendingReason;
    private float retryAt;

    internal void Schedule(string reason, float retryAtTime)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return;

        pendingReason = reason;
        retryAt = retryAtTime;
    }

    internal bool TryTake(float now, out string reason)
    {
        if (pendingReason == null || now < retryAt)
        {
            reason = string.Empty;
            return false;
        }

        reason = pendingReason;
        Clear();
        return true;
    }

    internal void Clear()
    {
        pendingReason = null;
        retryAt = 0;
    }

    internal static bool VerifyRules()
    {
        var state = new ShopSnapshotPublishRetryState();
        state.Schedule("purchase", 2f);
        if (state.TryTake(1.99f, out _) || !state.TryTake(2f, out var first) || first != "purchase")
            return false;

        // Model a failed capture after the retry is claimed: the caller must
        // enqueue it again and keep it pending until a later successful capture.
        state.Schedule(first, 3f);
        if (state.TryTake(2.99f, out _) || !state.TryTake(3f, out var retried) || retried != first)
            return false;

        state.Schedule(retried, 4f);
        state.Schedule("restock", 5f);
        return !state.TryTake(4.99f, out _) &&
               state.TryTake(5f, out var coalesced) && coalesced == "restock" &&
               !state.TryTake(5f, out _);
    }
}

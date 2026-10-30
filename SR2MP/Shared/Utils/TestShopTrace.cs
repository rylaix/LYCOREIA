#if SR2MP_TEST
using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Economy;
using Il2CppMonomiPark.SlimeRancher.Shop;
using Il2CppMonomiPark.SlimeRancher.Shop.Runtime;
using Il2CppMonomiPark.SlimeRancher.UI.Framework.Data;
using SR2MP.Packets.Shop;
using SR2MP.Shared.Managers;

namespace SR2MP.Shared.Utils;

/// <summary>
/// Test-harness-only shop tracing. It observes both peers by default. Explicit
/// SR2MP_TEST_SHOP_FORCE_RESTOCK=1 can force one in-memory host restock; use only
/// with a disposable save. Purchase tracing never starts or changes a purchase.
/// </summary>
internal static class TestShopTrace
{
    private static bool wasConnected;
    private static bool baselineRequested;
    private static bool protocolSelfTestAttempted;
    private static bool purchaseTraceCaptured;
    private static string? tracedPurchaseKey;
    private static int activePurchaseCallDepth;
    private static bool forcedHostRestockAttempted;
    private static bool forcingHostRestock;
    private static bool forcedHostRestockObserved;
    private static string? forcedHostRestockTag;
    private static float connectedAt = -1f;
    private static float nextDumpAt;
    private static string? pendingReason;
    private static int dumpSequence;

    internal static void RequestDump(string reason)
    {
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_SHOP") != "1")
            return;

        pendingReason = reason;
    }

    internal static void Pump()
    {
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_SHOP") != "1")
        {
            ResetConnectionState();
            return;
        }

        RunProtocolSelfTestOnce();

        var connected = Main.Client.IsJoined ||
                        (Main.Server.IsRunning && Main.Server.GetClientCount > 0);
        if (!connected)
        {
            ResetConnectionState();
            return;
        }

        if (!wasConnected)
        {
            wasConnected = true;
            baselineRequested = false;
            connectedAt = Time.unscaledTime;
        }

        if (!baselineRequested && Time.unscaledTime - connectedAt >= 10f)
        {
            pendingReason ??= "post-connection baseline";
            baselineRequested = true;
        }

        TryRunOptInHostRestock();

        if (string.IsNullOrWhiteSpace(pendingReason) || Time.unscaledTime < nextDumpAt)
            return;

        var reason = pendingReason;
        pendingReason = null;
        nextDumpAt = Time.unscaledTime + 0.5f;
        Dump(reason);
    }

    private static void RunProtocolSelfTestOnce()
    {
        if (protocolSelfTestAttempted)
            return;

        protocolSelfTestAttempted = true;
        var requestSerializationPassed = ShopPurchaseRequestPacket.VerifySerializationRoundTrip();
        var resultSerializationPassed = ShopPurchaseResultPacket.VerifySerializationRoundTrip();
        var requestLedgerPassed = ShopPurchaseRequestLedger.VerifyRules();
        var serializationPassed = ShopStateSnapshotPacket.VerifySerializationRoundTrip();
        var orderingPassed = ShopStateSnapshotPacket.VerifyRevisionOrdering();
        var currencyProtocolPassed = NetworkCurrencyManager.VerifyProtocolRules();
        var currencySnapshotPassed = SR2MP.Packets.Internal.ConnectionApprovePacket.VerifySerializationRoundTrip();
        SrLogger.LogMessage(
            $"[SR2MP_TEST] shop-protocol-selftest=" +
            $"{(requestSerializationPassed && resultSerializationPassed && requestLedgerPassed && serializationPassed && orderingPassed && currencyProtocolPassed && currencySnapshotPassed ? "PASS" : "FAIL")} " +
            $"requestSerialization={(requestSerializationPassed ? "PASS" : "FAIL")} " +
            $"resultSerialization={(resultSerializationPassed ? "PASS" : "FAIL")} " +
            $"requestLedger={(requestLedgerPassed ? "PASS" : "FAIL")} " +
            $"serialization={(serializationPassed ? "PASS" : "FAIL")} " +
            $"revisionOrdering={(orderingPassed ? "PASS" : "FAIL")} " +
            $"currencyProtocol={(currencyProtocolPassed ? "PASS" : "FAIL")} " +
            $"currencySnapshot={(currencySnapshotPassed ? "PASS" : "FAIL")}");
    }

    private static void ResetConnectionState()
    {
        wasConnected = false;
        baselineRequested = false;
        connectedAt = -1f;
        pendingReason = null;
    }

    private static void Dump(string reason)
    {
        var dumpId = ++dumpSequence;
        SrLogger.LogMessage(
            $"[SR2MP_TEST] shop dump begin id={dumpId} reason={reason} " +
            $"host={Main.Server.IsRunning} clients={Main.Server.GetClientCount} joined={Main.Client.IsJoined}");

        var directors = Resources.FindObjectsOfTypeAll<ShopDirector>()
            .Where(director => director)
            .ToArray();
        DumpCurrency(dumpId, "snapshot");
        if (directors.Length == 0)
        {
            SrLogger.LogWarning($"[SR2MP_TEST] shop dump id={dumpId}: no ShopDirector is loaded");
            return;
        }

        foreach (var director in directors)
        {
            try
            {
                DumpDirector(dumpId, director);
            }
            catch (Exception exception)
            {
                SrLogger.LogWarning(
                    $"[SR2MP_TEST] shop dump id={dumpId}: ShopDirector failed " +
                    $"({exception.GetType().Name}: {exception.Message})");
            }
        }

        SrLogger.LogMessage($"[SR2MP_TEST] shop dump end id={dumpId}");
    }

    internal static void TracePurchaseFlow(string method, string phase, ShopRuntimeItem? item,
        int count, Il2CppSystem.Guid uiSessionId, bool? result = null, string? detail = null)
    {
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_SHOP") != "1" ||
            Environment.GetEnvironmentVariable("SR2MP_TEST_SHOP_TRACE_PURCHASE") != "1")
            return;

        var itemGuid = item?.AssetGuid ?? "<null>";
        var purchaseKey = $"{itemGuid}:{uiSessionId}";
        if (tracedPurchaseKey == null && phase == "prefix" &&
            method is "ShopRuntimeTransactor.Purchase" or "ShopRuntimeItem.TryPurchase")
        {
            tracedPurchaseKey = purchaseKey;
            purchaseTraceCaptured = true;
            LogFirstPurchaseStack(method, itemGuid, count, uiSessionId);
        }

        if (!purchaseTraceCaptured || tracedPurchaseKey != purchaseKey)
            return;

        var isPurchaseEntry = method is "ShopRuntimeTransactor.Purchase" or "ShopRuntimeItem.TryPurchase";
        if (isPurchaseEntry && phase == "prefix")
            activePurchaseCallDepth++;

        var soldOut = item == null ? "n/a" : item.IsSoldOut.ToString();
        int purchased;
        try
        {
            purchased = item == null ? -1 : ShopRuntimeTransactor.Instance?.GetCountPurchased(item) ?? -1;
        }
        catch
        {
            purchased = -1;
        }

        SrLogger.LogMessage(
            $"[SR2MP_TEST] shop purchase-flow method={method} phase={phase} " +
            $"item={itemGuid} count={count} uiSession={uiSessionId} result={result?.ToString() ?? "n/a"} " +
            $"soldOut={soldOut} purchased={purchased} detail={detail ?? ""}");
        DumpCurrency(++dumpSequence, $"purchase-flow:{method}:{phase}");

        if (isPurchaseEntry && phase == "postfix" && activePurchaseCallDepth > 0)
            activePurchaseCallDepth--;
    }

    private static void LogFirstPurchaseStack(string method, string itemGuid, int count,
        Il2CppSystem.Guid uiSessionId)
    {
        // File info is disabled so logs contain no local source paths. Limit the stack
        // to a small set of type/method names around the first shop entry point.
        var frames = new System.Diagnostics.StackTrace(2, false).GetFrames() ?? Array.Empty<System.Diagnostics.StackFrame>();
        var callSites = frames
            .Select(frame => frame.GetMethod())
            .Where(calledMethod => calledMethod != null)
            .Take(12)
            .Select(calledMethod =>
                $"{calledMethod!.DeclaringType?.FullName ?? "<unknown>"}.{calledMethod.Name}")
            .ToArray();
        SrLogger.LogMessage(
            $"[SR2MP_TEST] shop purchase-flow first-entry method={method} item={itemGuid} " +
            $"count={count} uiSession={uiSessionId} stack=[{string.Join(" <- ", callSites)}]");
    }

    internal static void TracePurchaseSideEffect(string method, string phase, string details)
    {
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_SHOP") != "1" ||
            Environment.GetEnvironmentVariable("SR2MP_TEST_SHOP_TRACE_PURCHASE") != "1" ||
            !purchaseTraceCaptured || activePurchaseCallDepth <= 0)
            return;

        SrLogger.LogMessage(
            $"[SR2MP_TEST] shop purchase-flow key={tracedPurchaseKey} method={method} " +
            $"phase={phase} {details}");
    }

    internal static void ObserveShopRestock(ShopRuntime runtime)
    {
        var tag = runtime?.Definition?.ShopAssetTag ?? "<unknown>";
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_SHOP") == "1")
        {
            SrLogger.LogMessage(
                $"[SR2MP_TEST] shop restock-flow phase=HandleShopRestock-postfix shop={tag} " +
                $"host={Main.Server.IsRunning} revision={NetworkShopManager.ServerRevisionForTest}");
        }

        if (forcingHostRestock && string.Equals(tag, forcedHostRestockTag, StringComparison.Ordinal))
            forcedHostRestockObserved = true;

        RequestDump($"restock shop={tag}");
    }

    private static void TryRunOptInHostRestock()
    {
        if (forcedHostRestockAttempted ||
            Environment.GetEnvironmentVariable("SR2MP_TEST_SHOP_FORCE_RESTOCK") != "1" ||
            !Main.Server.IsRunning || Main.Server.GetClientCount == 0 ||
            Time.unscaledTime - connectedAt < 15f)
            return;

        var directors = Resources.FindObjectsOfTypeAll<ShopDirector>()
            .Where(director => director)
            .ToArray();
        if (directors.Length != 1 || directors[0]._dataModel == null ||
            directors[0]._runtimes == null || directors[0]._timeProvider == null)
            return;

        var director = directors[0];
        var runtime = director._runtimes.ToArray()
            .Where(candidate => candidate != null && candidate.IsDoneLoading && candidate.Definition != null)
            .OrderBy(candidate => candidate.Definition.ShopAssetTag, StringComparer.Ordinal)
            .FirstOrDefault(candidate =>
            {
                var next = candidate._nextRestockTime;
                return next.HasGameWorldTime || next.HasRealTime;
            });
        if (runtime == null)
            return;

        var timeProvider = director._timeProvider;
        if (timeProvider == null)
            return;

        var nextRestock = runtime._nextRestockTime;
        if (nextRestock.HasGameWorldTime)
            nextRestock.GameWorldTimeSeconds = timeProvider.GetGameWorldTimeTotalSeconds() - 1;
        if (nextRestock.HasRealTime)
            nextRestock.RealTimeSeconds = timeProvider.GetSystemTimeSpanSinceEpoch().TotalSeconds - 1;

        // A single explicit opt-in can modify the current test save. Use only a disposable save.
        forcedHostRestockAttempted = true;
        forcedHostRestockObserved = false;
        forcedHostRestockTag = runtime.Definition.ShopAssetTag;
        forcingHostRestock = true;
        var revisionBefore = NetworkShopManager.ServerRevisionForTest;
        SrLogger.LogMessage(
            $"[SR2MP_TEST] shop forced-restock begin shop={forcedHostRestockTag} " +
            $"revisionBefore={revisionBefore} nextGame={nextRestock.GameWorldTimeSeconds:F1} " +
            $"nextReal={nextRestock.RealTimeSeconds:F1}");
        Dump($"before forced restock shop={forcedHostRestockTag}");
        nextDumpAt = Time.unscaledTime + 0.5f;

        try
        {
            runtime._nextRestockTime = nextRestock;
            runtime.Update(timeProvider);
        }
        catch (Exception exception)
        {
            SrLogger.LogError(
                $"[SR2MP_TEST] shop forced-restock failed shop={forcedHostRestockTag}: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            forcingHostRestock = false;
        }

        var revisionAfter = NetworkShopManager.ServerRevisionForTest;
        SrLogger.LogMessage(
            $"[SR2MP_TEST] shop forced-restock end shop={forcedHostRestockTag} " +
            $"observedHandleShopRestock={forcedHostRestockObserved} " +
            $"revisionBefore={revisionBefore} revisionAfter={revisionAfter}");
        pendingReason = null;
        Dump($"after forced restock shop={forcedHostRestockTag}");
        nextDumpAt = Time.unscaledTime + 0.5f;
    }

    private static void DumpCurrency(int dumpId, string scope)
    {
        var currencyEntries = GameContext.Instance?.LookupDirector?._currencyList?._currencies;
        var scene = SceneContext.Instance;
        if (currencyEntries == null || !scene || scene.PlayerState == null)
        {
            SrLogger.LogWarning($"[SR2MP_TEST] shop dump id={dumpId} scope={scope}: currency state is not loaded");
            return;
        }

        for (var index = 0; index < currencyEntries.Length; index++)
        {
            var entry = currencyEntries[index];
            if (entry == null)
                continue;

            var currency = entry.Cast<ICurrency>();
            SrLogger.LogMessage(
                $"[SR2MP_TEST] shop currency id={dumpId} scope={scope} index={index} " +
                $"persistenceId={currency.PersistenceId} amount={scene.PlayerState.GetCurrency(currency)}");
        }
    }

    private static void DumpDirector(int dumpId, ShopDirector director)
    {
        var model = director._dataModel;
        var timeProvider = director._timeProvider;
        var runtimes = director._runtimes;
        double? currentGameTime = timeProvider == null
            ? null
            : timeProvider.GetGameWorldTimeTotalSeconds();
        double? currentSystemTime = timeProvider == null
            ? null
            : timeProvider.GetSystemTimeSpanSinceEpoch().TotalSeconds;
        SrLogger.LogMessage(
            $"[SR2MP_TEST] shop director id={dumpId} name={director.name} " +
            $"runtimes={runtimes?.Count ?? 0} " +
            $"gameNowSeconds={FormatSeconds(currentGameTime)} systemNowSeconds={FormatSeconds(currentSystemTime)} " +
            $"purchaseCategories={model?._shopPurchasesByCategory?.Count ?? 0} " +
            $"pendingSources={model?._itemsPendingSpawn?.Count ?? 0}");

        if (runtimes != null)
        {
            foreach (var runtime in runtimes)
            {
                if (runtime == null)
                    continue;

                try
                {
                    var definition = runtime.Definition;
                    var nextUpdate = runtime.GetNextUpdateTime();
                    var gameSecondsUntilRestock = nextUpdate.HasGameWorldTime && currentGameTime.HasValue
                        ? nextUpdate.GameWorldTimeSeconds - currentGameTime.Value
                        : (double?)null;
                    var realSecondsUntilRestock = nextUpdate.HasRealTime && currentSystemTime.HasValue
                        ? nextUpdate.RealTimeSeconds - currentSystemTime.Value
                        : (double?)null;

                    SrLogger.LogMessage(
                        $"[SR2MP_TEST] shop runtime id={dumpId} tag={definition?.ShopAssetTag ?? "<null>"} " +
                        $"done={runtime.IsDoneLoading} available={runtime.IsShopAvailable} " +
                        $"newStock={runtime.HasNewStock} inStock={runtime.GetInStockItemCount()} " +
                        $"nextGameRestockSeconds={FormatSeconds(gameSecondsUntilRestock)} " +
                        $"nextSystemRestockSeconds={FormatSeconds(realSecondsUntilRestock)} " +
                        $"nextGameTime={(nextUpdate.HasGameWorldTime ? nextUpdate.GameWorldTimeSeconds.ToString("F1") : "n/a")} " +
                        $"nextSystemTime={(nextUpdate.HasRealTime ? nextUpdate.RealTimeSeconds.ToString("F1") : "n/a")}");

                    var categoryEnumerator = runtime.AllCategories?
                        .TryCast<Il2CppSystem.Collections.IEnumerable>()?
                        .GetEnumerator();
                    while (categoryEnumerator != null && categoryEnumerator.MoveNext())
                    {
                        var category = categoryEnumerator.Current?.TryCast<ShopCategoryRuntime>();
                        if (category == null)
                            continue;

                        try
                        {
                            // GetRuntimeItems requires IEventDirector. Inspect the generated
                            // ShopCategoryRuntime backing cache directly instead.
                            var items = category._cachedItemList;
                            SrLogger.LogMessage(
                                $"[SR2MP_TEST] shop category id={dumpId} shop={definition?.ShopAssetTag ?? "<null>"} " +
                                $"name={category.AnalyticsName} loaded={category.IsDoneLoading} " +
                                $"lastSeed={category.LastSeedUsed} nextOverride={category._nextOverrideIndex} " +
                                $"cachedItems={items?.Count ?? 0}");
                            var itemCount = 0;
                            if (items == null)
                                continue;

                            foreach (var item in items)
                            {
                                if (item == null)
                                    continue;

                                int purchased;
                                try
                                {
                                    var transactor = ShopRuntimeTransactor.Instance;
                                    purchased = transactor == null ? -1 : transactor.GetCountPurchased(item);
                                }
                                catch
                                {
                                    purchased = -1;
                                }

                                SrLogger.LogMessage(
                                    $"[SR2MP_TEST] shop item id={dumpId} shop={definition?.ShopAssetTag ?? "<null>"} " +
                                    $"category={category.AnalyticsName} asset={item.AssetGuid} type={item.ItemType} " +
                                    $"loaded={item.IsDoneLoading} soldOut={item.IsSoldOut} purchased={purchased}");

                                if (++itemCount >= 500)
                                {
                                    SrLogger.LogWarning(
                                        $"[SR2MP_TEST] shop dump id={dumpId}: category item output capped at 500");
                                    break;
                                }
                            }
                        }
                        catch (Exception exception)
                        {
                            SrLogger.LogWarning(
                                $"[SR2MP_TEST] shop dump id={dumpId} category={category.AnalyticsName} " +
                                $"failed ({exception.GetType().Name}: {exception.Message})");
                        }
                    }
                }
                catch (Exception exception)
                {
                    SrLogger.LogWarning(
                        $"[SR2MP_TEST] shop dump id={dumpId} runtime failed " +
                        $"({exception.GetType().Name}: {exception.Message})");
                }
            }
        }

        DumpPurchaseSets(dumpId, model);
        DumpPendingItems(dumpId, model);
    }

    private static void DumpPurchaseSets(int dumpId, ShopDataModel? model)
    {
        var purchaseSets = model?._shopPurchasesByCategory;
        if (purchaseSets == null)
            return;

        foreach (var entry in purchaseSets)
        {
            var purchaseSet = entry.Value;
            if (purchaseSet == null)
                continue;

            SrLogger.LogMessage(
                $"[SR2MP_TEST] shop purchase set id={dumpId} key={entry.Key} " +
                $"category={purchaseSet.CategoryIdentifier} seed={purchaseSet.Seed} " +
                $"entries={purchaseSet.Purchases?.Count ?? 0} " +
                $"categoryPurchases={purchaseSet.GetCountPurchasedByCategory()}");

            if (purchaseSet.Purchases == null)
                continue;

            foreach (var purchase in purchaseSet.Purchases)
            {
                if (purchase == null)
                    continue;

                SrLogger.LogMessage(
                    $"[SR2MP_TEST] shop purchase id={dumpId} category={purchaseSet.CategoryIdentifier} " +
                    $"item={purchase.UniqueId} unlockType={purchase.UnlockType} count={purchase.Count}");
            }
        }
    }

    private static void DumpPendingItems(int dumpId, ShopDataModel? model)
    {
        var pendingItems = model?._itemsPendingSpawn;
        if (pendingItems == null)
            return;

        foreach (var source in pendingItems)
        {
            var queue = source.Value;
            if (queue == null)
                continue;

            var itemNames = new List<string>();
            foreach (var item in queue)
                itemNames.Add(item == null ? "<null>" : item.name);

            SrLogger.LogMessage(
                $"[SR2MP_TEST] shop pending id={dumpId} source={source.Key} count={queue.Count} " +
                $"items=[{string.Join(",", itemNames)}]");
        }
    }

    private static string FormatSeconds(double? seconds)
        => seconds.HasValue ? seconds.Value.ToString("F1") : "n/a";
}

[HarmonyPatch(typeof(ShopRuntime), "HandleShopRestock")]
internal static class TestShopRestockTracePatch
{
    private static void Postfix(ShopRuntime __instance)
    {
        TestShopTrace.ObserveShopRestock(__instance);
    }
}

[HarmonyPatch(typeof(ShopRuntimeTransactor), "TrackPurchase")]
internal static class TestShopPurchaseTracePatch
{
    private static void Prefix(ShopRuntimeItem item, int count, Il2CppSystem.Guid uiSessionId)
        => TestShopTrace.TracePurchaseFlow("ShopRuntimeTransactor.TrackPurchase", "prefix",
            item, count, uiSessionId);

    private static void Postfix(ShopRuntimeItem item, int count, Il2CppSystem.Guid uiSessionId)
    {
        TestShopTrace.TracePurchaseFlow("ShopRuntimeTransactor.TrackPurchase", "postfix",
            item, count, uiSessionId);
        TestShopTrace.RequestDump(
            $"purchase item={item?.AssetGuid ?? "<unknown>"} count={count}");
    }
}

[HarmonyPatch(typeof(ShopRuntimeTransactor), nameof(ShopRuntimeTransactor.Purchase))]
internal static class TestShopTransactionTracePatch
{
    private static void Prefix(ShopRuntimeItem item, int count, Il2CppSystem.Guid uiSessionId)
        => TestShopTrace.TracePurchaseFlow("ShopRuntimeTransactor.Purchase", "prefix",
            item, count, uiSessionId, detail: $"canAfford={ShopRuntimeTransactor.Instance?.CanAfford(item, count)}");

    private static void Postfix(ShopRuntimeItem item, int count, Il2CppSystem.Guid uiSessionId, bool __result)
        => TestShopTrace.TracePurchaseFlow("ShopRuntimeTransactor.Purchase", "postfix",
            item, count, uiSessionId, __result);
}

[HarmonyPatch(typeof(ShopRuntimeItem), nameof(ShopRuntimeItem.TryPurchase))]
internal static class TestShopTryPurchaseTracePatch
{
    private static void Prefix(ShopRuntimeItem __instance, int count, string spawnHandlerId,
        Il2CppSystem.Guid uiSessionId)
        => TestShopTrace.TracePurchaseFlow("ShopRuntimeItem.TryPurchase", "prefix",
            __instance, count, uiSessionId,
            detail: $"spawnHandlerId={spawnHandlerId} canAfford={__instance.CanAfford(count)} " +
                    $"canAcquireIgnoringCosts={__instance.CanAcquireIgnoringCosts(count)}");

    private static void Postfix(ShopRuntimeItem __instance, int count, string spawnHandlerId,
        Il2CppSystem.Guid uiSessionId, bool __result)
        => TestShopTrace.TracePurchaseFlow("ShopRuntimeItem.TryPurchase", "postfix",
            __instance, count, uiSessionId, __result, $"spawnHandlerId={spawnHandlerId}");
}

[HarmonyPatch(typeof(ShopDirector), nameof(ShopDirector.TrackItemPurchased))]
internal static class TestShopDirectorPurchaseTracePatch
{
    private static void Prefix(ShopRuntimeItem item, int count, Il2CppSystem.Guid uiSessionId)
        => TestShopTrace.TracePurchaseFlow("ShopDirector.TrackItemPurchased", "prefix",
            item, count, uiSessionId);

    private static void Postfix(ShopRuntimeItem item, int count, Il2CppSystem.Guid uiSessionId)
        => TestShopTrace.TracePurchaseFlow("ShopDirector.TrackItemPurchased", "postfix",
            item, count, uiSessionId);
}

[HarmonyPatch(typeof(ShopRuntimeTransactor), nameof(ShopRuntimeTransactor.NotifyPurchaseComplete))]
internal static class TestShopPurchaseCompleteTracePatch
{
    private static void Prefix(ShopRuntimeItem item, int count, Il2CppSystem.Guid uiSessionId)
        => TestShopTrace.TracePurchaseFlow("ShopRuntimeTransactor.NotifyPurchaseComplete", "prefix",
            item, count, uiSessionId);

    private static void Postfix(ShopRuntimeItem item, int count, Il2CppSystem.Guid uiSessionId)
        => TestShopTrace.TracePurchaseFlow("ShopRuntimeTransactor.NotifyPurchaseComplete", "postfix",
            item, count, uiSessionId);
}

[HarmonyPatch(typeof(PlayerState), nameof(PlayerState.SpendCurrency))]
internal static class TestShopSpendCurrencyTracePatch
{
    private static void Prefix(PlayerState __instance, ICurrency currency, int adjust,
        IUIDisplayData sourceOfChange)
    {
        if (!IsPurchaseTraceEnabled())
            return;

        var amount = currency == null ? -1 : __instance.GetCurrency(currency);
        TestShopTrace.TracePurchaseSideEffect(
            "PlayerState.SpendCurrency", "prefix",
            $"currency={currency?.PersistenceId.ToString() ?? "<null>"} adjust={adjust} " +
            $"amountBefore={amount} source={sourceOfChange?.GetType().FullName ?? "<null>"}");
    }

    private static void Postfix(PlayerState __instance, ICurrency currency, int adjust,
        IUIDisplayData sourceOfChange)
    {
        if (!IsPurchaseTraceEnabled())
            return;

        var amount = currency == null ? -1 : __instance.GetCurrency(currency);
        TestShopTrace.TracePurchaseSideEffect(
            "PlayerState.SpendCurrency", "postfix",
            $"currency={currency?.PersistenceId.ToString() ?? "<null>"} adjust={adjust} " +
            $"amountAfter={amount} source={sourceOfChange?.GetType().FullName ?? "<null>"}");
    }

    private static bool IsPurchaseTraceEnabled()
        => Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_SHOP") == "1" &&
           Environment.GetEnvironmentVariable("SR2MP_TEST_SHOP_TRACE_PURCHASE") == "1";
}

[HarmonyPatch(typeof(ShopDirector), nameof(ShopDirector.QueueToSpawnItem))]
internal static class TestShopQueueToSpawnTracePatch
{
    private static void Prefix(string sourceId, Il2Cpp.IdentifiableType item, int count)
        => TestShopTrace.TracePurchaseSideEffect(
            "ShopDirector.QueueToSpawnItem", "prefix",
            $"source={sourceId} item={item?.name ?? "<null>"} count={count}");

    private static void Postfix(string sourceId, Il2Cpp.IdentifiableType item, int count)
        => TestShopTrace.TracePurchaseSideEffect(
            "ShopDirector.QueueToSpawnItem", "postfix",
            $"source={sourceId} item={item?.name ?? "<null>"} count={count}");
}
#endif

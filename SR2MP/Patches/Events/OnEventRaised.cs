using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Event;
using System.Diagnostics;
using SR2MP.Shared.Managers;
using SR2MP.Shared.Utils;

namespace SR2MP.Patches.Events;

[HarmonyPatch(typeof(EventDirector), nameof(EventDirector.RaiseEvent))]
internal static class OnEventRaised
{
    private static int eventTraceSequence;

    public static bool Prefix(IGameEvent gameEvent, int count, out bool __state)
    {
        __state = false;
        if (gameEvent == null || HandlingPacket || Main.Server.IsRunning || !Main.Client.IsConnected ||
            !EventSyncPolicy.IsZoneUnlockedEvent(gameEvent.EventKey) ||
            !EventSyncPolicy.IsValidZoneDataKey(gameEvent.DataKey))
            return true;

        // A guest does not commit its local zone state before the host accepts it.
        // The accepted reliable event is echoed back by EventRaisedHandler.
        __state = true;
        NetworkEventManager.SendEventRaised(
            EventSyncPolicy.ZoneUnlockedEventKey,
            gameEvent.DataKey,
            EventSyncPolicy.UnlockedZoneCount);
        return false;
    }

    // Todo: Causes lags under **VERY SPECIFIC CIRCUMSTANCES**
    public static void Postfix(IGameEvent gameEvent, int count, bool __state)
    {
        if (gameEvent == null) return;

#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ALL_EVENTS") == "1")
        {
            var stack = new StackTrace(1, false).GetFrames()?
                .Select(frame => frame.GetMethod() is { } method
                    ? $"{method.DeclaringType?.FullName}.{method.Name}"
                    : null)
                .Where(frame => !string.IsNullOrWhiteSpace(frame))
                .Take(12);
            var source = string.Join(" <- ", stack ?? Enumerable.Empty<string>());
            var tag = __state ? "host-request" : HandlingPacket ? "packet-apply" : "local-or-gameplay";
            var sequence = Interlocked.Increment(ref eventTraceSequence);
            SrLogger.LogMessage(
                $"[SR2MP_TEST] event-raise seq={sequence} tag={tag} " +
                $"type={gameEvent.GetType().FullName} key={gameEvent.EventKey} " +
                $"data={gameEvent.DataKey} count={count} source={source}");
        }

        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_SHOP") == "1" &&
            string.Equals(gameEvent.EventKey, "restocked", StringComparison.OrdinalIgnoreCase))
        {
            TestShopTrace.RequestDump(
                $"event key={gameEvent.EventKey} data={gameEvent.DataKey} count={count}");
        }

        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_EVENTS") == "1" &&
            (string.Equals(gameEvent.EventKey, "Gift", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(gameEvent.EventKey, "restocked", StringComparison.OrdinalIgnoreCase)))
        {
            SrLogger.LogMessage(
                $"[SR2MP_TEST] observed event type={gameEvent.GetType().FullName} " +
                $"key={gameEvent.EventKey} data={gameEvent.DataKey} count={count}");
        }
#endif

        if (__state)
            return;

        // This is intentionally left behind
        // SrLogger.LogDebug($"GameEvent raised: EventKey='{gameEvent.EventKey}' DataKey='{gameEvent.DataKey}' Count={count}");

        if (HandlingPacket) return;
        if (!Main.Server.IsRunning && !Main.Client.IsConnected) return;

        var eventKey = gameEvent.EventKey;
        if (string.IsNullOrEmpty(eventKey)) return;

        if (!NetworkEventManager.CanApplyEvent(eventKey))
            return;

        if (!NetworkEventManager.ShouldSync(eventKey, gameEvent.DataKey)) return;

        var eventCount = NetworkEventManager.GetEventCount(eventKey, gameEvent.DataKey);
        if (eventCount > 0)
            NetworkEventManager.SendEventRaised(eventKey, gameEvent.DataKey, eventCount);
    }
}

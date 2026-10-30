#if SR2MP_TEST
using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Shared.Managers;

namespace SR2MP.Shared.Utils;

/// <summary>
/// Opt-in refinery smoke action for disposable two-client runs. This helper only
/// changes one count by one on a joined guest through the game's GadgetsModel API.
/// </summary>
internal static class TestRefinerySmoke
{
    private static int lastActionSequence;
    private static int? lastActionTypeKey;
    private static string? lastActionTypeName;
    private static int? lastActionBefore;
    private static int? lastActionExpected;
    private static int? lastActionObserved;
    private static string lastActionState = "none";
    private static string? lastActionError;
    private static bool lastActionPending;

    internal static TestRefinerySmokeState ReadState()
    {
        var state = CreateBaseState();
        if (!TryResolveTarget(out _, out _, out _, out var type, out var typeKey,
                out var typeName, out var count, out var refineryMax, out var error))
        {
            state.ReadinessError = error;
            CopyLastAction(state);
            return state;
        }

        state.Ready = true;
        state.TypeKey = typeKey;
        state.TypeName = typeName;
        state.LocalCount = count;
        state.RefineryMaxCount = refineryMax;
        CopyLastAction(state);
        return state;
    }

    /// <summary>Performs exactly one native +1 count mutation on a joined guest.</summary>
    internal static TestRefinerySmokeState ApplyGuestIncrement()
    {
        if (lastActionPending)
        {
            lastActionError = "new increment rejected while the previous action is awaiting host state";
            LogActionResult(lastActionObserved);
            return ReadState();
        }

        lastActionSequence++;
        lastActionTypeKey = null;
        lastActionTypeName = null;
        lastActionBefore = null;
        lastActionExpected = null;
        lastActionObserved = null;
        lastActionState = "validating";
        lastActionError = null;
        lastActionPending = false;

        if (!Main.Client.IsConnected || !Main.Client.IsJoined || Main.Server.IsRunning)
            return FailAction("action requires a connected, joined guest");

        if (HandlingPacket)
            return FailAction("action refused while HandlingPacket is true");

        var system = SystemContext.Instance;
        if (!system || system.SceneLoader == null || system.SceneLoader.IsSceneLoadInProgress)
            return FailAction("game scene is unavailable or still loading");

        if (!TryResolveTarget(out _, out _, out var model, out var type, out var typeKey,
                out var typeName, out var before, out var refineryMax, out var error))
            return FailAction(error);

        lastActionTypeKey = typeKey;
        lastActionTypeName = typeName;
        lastActionBefore = before;
        if (before >= refineryMax)
            return FailAction($"selected refinery count {before} is at the native limit {refineryMax}");

        var expected = before + 1;
        if (!RefineryMutationPolicy.IsRepresentableCount(expected))
            return FailAction($"expected count {expected} cannot be represented on the network");

        lastActionExpected = expected;
        lastActionState = "applying-native-increment";
        try
        {
            // Keep HandlingPacket false: the production GadgetsModel.SetCount postfix
            // must observe this local mutation and submit its normal refinery request.
            model.SetCount(type, expected);
        }
        catch (Exception exception)
        {
            var observedAfterException = TryReadCount(model, type, fallback: null);
            lastActionObserved = observedAfterException;
            lastActionPending = !observedAfterException.HasValue || observedAfterException != before;
            lastActionState = lastActionPending
                ? "exception-after-possible-local-mutation-awaiting-host-state"
                : "failed";
            lastActionError = $"native SetCount threw {exception.GetType().Name}: {exception.Message}";
            LogActionResult(observedAfterException);
            return ReadState();
        }

        var after = TryReadCount(model, type, fallback: null);
        lastActionObserved = after;
        if (!after.HasValue)
        {
            lastActionPending = true;
            lastActionState = "native-call-returned-count-unreadable-awaiting-host-state";
            lastActionError = "native SetCount returned but the resulting count could not be read";
            LogActionResult(after);
            return ReadState();
        }

        if (after.Value != expected)
        {
            lastActionPending = after.Value != before;
            lastActionState = lastActionPending ? "count-mismatch-awaiting-host-state" : "failed";
            lastActionError = $"native SetCount produced {after.Value}; expected {expected}";
            LogActionResult(after);
            return ReadState();
        }

        lastActionState = "submitted-awaiting-host-state";
        lastActionPending = true;
        LogActionResult(after);
        return ReadState();
    }

    private static TestRefinerySmokeState FailAction(string error)
    {
        lastActionState = "failed";
        lastActionError = error;
        LogActionResult(lastActionObserved);
        return ReadState();
    }

    private static TestRefinerySmokeState CreateBaseState()
        => new()
        {
            Connected = Main.Client.IsConnected,
            Joined = Main.Client.IsJoined,
            ServerRunning = Main.Server.IsRunning,
            HandlingPacket = HandlingPacket
        };

    private static void CopyLastAction(TestRefinerySmokeState state)
    {
        state.LastActionSequence = lastActionSequence;
        state.LastActionTypeKey = lastActionTypeKey;
        state.LastActionTypeName = lastActionTypeName;
        state.LastActionBefore = lastActionBefore;
        state.LastActionExpected = lastActionExpected;
        state.LastActionObserved = lastActionObserved;
        state.LastActionState = lastActionState;
        state.LastActionPending = lastActionPending;
        state.LastActionError = lastActionError;
    }

    private static void LogActionResult(int? observed)
        => SrLogger.LogMessage(
            $"[SR2MP_TEST] refinery-smoke action={lastActionSequence} " +
            $"state={lastActionState} type={lastActionTypeKey?.ToString() ?? "n/a"} " +
            $"name={lastActionTypeName ?? "n/a"} before={lastActionBefore?.ToString() ?? "n/a"} " +
            $"expected={lastActionExpected?.ToString() ?? "n/a"} " +
            $"observed={observed?.ToString() ?? "n/a"} pending={lastActionPending} " +
            $"error={lastActionError ?? ""}");

    private static bool TryResolveTarget(
        out SceneContext scene,
        out GadgetDirector director,
        out GadgetsModel model,
        out IdentifiableType type,
        out int typeKey,
        out string typeName,
        out int count,
        out int refineryMax,
        out string error)
    {
        scene = null!;
        director = null!;
        model = null!;
        type = null!;
        typeKey = -1;
        typeName = string.Empty;
        count = -1;
        refineryMax = GadgetDirector.REFINERY_MAX;
        error = string.Empty;

        scene = SceneContext.Instance;
        if (!scene)
        {
            error = "scene context is unavailable";
            return false;
        }

        director = scene.GadgetDirector;
        if (!director)
        {
            error = "gadget director is unavailable";
            return false;
        }

        model = director._model;
        if (model == null)
        {
            error = "gadget model is unavailable";
            return false;
        }

        if (refineryMax <= 0)
        {
            error = $"native refinery maximum is invalid ({refineryMax})";
            return false;
        }

        var refineryTypes = director.RefineryTypeGroup;
        if (refineryTypes == null)
        {
            error = "native refinery type group is unavailable";
            return false;
        }

        var candidates = new List<(int Key, string Name, IdentifiableType Type)>();
        try
        {
            var enumerator = refineryTypes.GetAllMembers().GetEnumerator();
            var iterator = enumerator.Cast<Il2CppSystem.Collections.IEnumerator>();
            while (iterator.MoveNext())
            {
                var candidate = enumerator.Current;
                if (candidate == null)
                    continue;

                int candidateKey;
                try
                {
                    candidateKey = NetworkActorManager.GetPersistentID(candidate);
                }
                catch
                {
                    continue;
                }

                if (candidateKey <= 0 ||
                    !ActorManager.ActorTypes.TryGetValue(candidateKey, out var registeredType) ||
                    registeredType == null || registeredType.Pointer != candidate.Pointer)
                    continue;

                candidates.Add((candidateKey, candidate.name ?? string.Empty, candidate));
            }
        }
        catch (Exception exception)
        {
            error = $"could not enumerate native refinery types ({exception.GetType().Name})";
            return false;
        }

        if (candidates.Count == 0)
        {
            error = "no registered identifiable type is available in the native refinery group";
            return false;
        }

        // Select from the game's refinery-only group and use the persisted key to make
        // both peers choose the same item without depending on collection iteration order.
        var selected = candidates
            .OrderBy(candidate => candidate.Key)
            .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
            .First();

        type = selected.Type;
        typeKey = selected.Key;
        typeName = selected.Name;
        try
        {
            count = model.GetCount(type);
        }
        catch (Exception exception)
        {
            error = $"could not read selected refinery count ({exception.GetType().Name})";
            return false;
        }
        if (!RefineryMutationPolicy.IsRepresentableCount(count))
        {
            error = $"selected refinery count {count} is outside the network range";
            return false;
        }

        return true;
    }

    private static int? TryReadCount(GadgetsModel model, IdentifiableType type, int? fallback)
    {
        try
        {
            return model.GetCount(type);
        }
        catch
        {
            return fallback;
        }
    }

    internal static void ObserveCanonicalCount(GadgetsModel model, IdentifiableType type)
    {
        if (!lastActionPending || !HandlingPacket || Main.Server.IsRunning ||
            !Main.Client.IsConnected || type == null || lastActionTypeKey == null)
            return;

        try
        {
            if (NetworkActorManager.GetPersistentID(type) != lastActionTypeKey.Value)
                return;

            var observed = model.GetCount(type);
            lastActionObserved = observed;
            lastActionPending = false;
            if (observed == lastActionExpected)
            {
                lastActionState = "confirmed-by-host-state";
                lastActionError = null;
            }
            else
            {
                lastActionState = "corrected-by-host-state";
                lastActionError = $"host state was {observed}; expected {lastActionExpected?.ToString() ?? "n/a"}";
            }

            LogActionResult(observed);
        }
        catch (Exception exception)
        {
            lastActionPending = false;
            lastActionState = "host-state-observed-with-error";
            lastActionError = $"could not inspect canonical state: {exception.GetType().Name}: {exception.Message}";
            LogActionResult(lastActionObserved);
        }
    }
}

/// <summary>Only plain CLR values so the runner can include this in JSON snapshots.</summary>
internal sealed class TestRefinerySmokeState
{
    public bool Ready { get; set; }
    public bool Connected { get; set; }
    public bool Joined { get; set; }
    public bool ServerRunning { get; set; }
    public bool HandlingPacket { get; set; }
    public int? TypeKey { get; set; }
    public string? TypeName { get; set; }
    public int? LocalCount { get; set; }
    public int? RefineryMaxCount { get; set; }
    public string? ReadinessError { get; set; }
    public int LastActionSequence { get; set; }
    public int? LastActionTypeKey { get; set; }
    public string? LastActionTypeName { get; set; }
    public int? LastActionBefore { get; set; }
    public int? LastActionExpected { get; set; }
    public int? LastActionObserved { get; set; }
    public string LastActionState { get; set; } = "none";
    public bool LastActionPending { get; set; }
    public string? LastActionError { get; set; }
}

[HarmonyPatch(typeof(GadgetsModel), nameof(GadgetsModel.SetCount))]
internal static class TestRefinerySmokeCanonicalUpdatePatch
{
    private static void Postfix(GadgetsModel __instance, IdentifiableType type)
        => TestRefinerySmoke.ObserveCanonicalCount(__instance, type);
}
#endif

#if SR2MP_DEEPLOG || SR2MP_TEST
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Components.Actor;
using SR2MP.Shared.Managers;

namespace SR2MP.Shared.Utils;

/// <summary>
/// Bounded actor lifecycle diagnostics (category "actor"). Every event carries the actor id,
/// a readable type name, owner, local ownership, scene group and position so both seats'
/// logs can be lined up. Observational only: it never changes behaviour.
/// </summary>
internal static class ActorDiag
{
    // Hard cap on lifecycle events per second, so a mass spawn/destroy cannot flood the file.
    private const int MaxEventsPerWindow = 40;
    private const long WindowMilliseconds = 1000;
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static long windowStart;
    private static int windowCount;
    private static int windowDropped;

    /// <param name="typeId">Persistent type id from a packet, used when the actor is not registered.</param>
    /// <param name="ownerHint">Owner from a packet, used when the actor is not registered.</param>
    /// <param name="sceneId">Scene group id from a packet, used when the actor is not registered.</param>
    /// <param name="positionHint">Position from a packet, used when the actor is not registered.</param>
    /// <param name="perActorMilliseconds">Optional per-actor-and-event rate limit.</param>
    internal static void Log(string eventName, long actorId, string reason = "", int typeId = int.MinValue,
        string? ownerHint = null, int sceneId = int.MinValue, Vector3? positionHint = null,
        int perActorMilliseconds = 0)
    {
        if (!Logger.DiagnosticsEnabled || !TakeBudget())
            return;

        try
        {
            var typeName = typeId != int.MinValue ? TypeName(typeId) : "<unknown>";
            var owner = ownerHint ?? "<unknown>";
            var scene = sceneId != int.MinValue ? SceneName(sceneId) : "<unknown>";
            var position = positionHint.HasValue ? positionHint.Value.ToString("F1") : "<unknown>";
            var registered = false;

            if (GlobalVariables.ActorManager.Actors.TryGetValue(actorId, out var model) && model != null)
            {
                registered = true;
                typeName = model.ident ? model.ident.name : typeName;
                scene = model.sceneGroup ? model.sceneGroup.name : scene;
                var obj = model.GetGameObject();
                if (obj)
                {
                    position = obj.transform.position.ToString("F1");
                    var net = obj.GetComponent<NetworkActor>();
                    if (net)
                        owner = net.CurrentOwnerId;
                }
            }

            if (GlobalVariables.ActorManager.OwnershipByActorId.TryGetValue(actorId, out var cachedOwner) &&
                (ownerHint == null || string.IsNullOrEmpty(owner) || owner == "<unknown>"))
                owner = cachedOwner;

            var locallyOwned = owner == GlobalVariables.LocalID;
            Logger.LogDiagnostic("actor", eventName,
                $"actorId={actorId:X}; type={typeName}; owner={owner}; locallyOwned={locallyOwned}; " +
                $"scene={scene}; pos={position}; registered={registered}" +
                (string.IsNullOrEmpty(reason) ? string.Empty : $"; reason={Trim(reason)}"),
                perActorMilliseconds > 0 ? $"actor-diag:{eventName}:{actorId:X}" : null,
                perActorMilliseconds);
        }
        catch (Exception exception)
        {
            Logger.LogDiagnostic("actor", "diag-error", $"event={eventName}; type={exception.GetType().Name}",
                "actor-diag-error", 30_000);
        }
    }

    /// <summary>Resolves an identifiable type name for diagnostics; never throws.</summary>
    internal static string TypeName(int typeId)
    {
        try
        {
            return GlobalVariables.ActorManager.ActorTypes.TryGetValue(typeId, out var type) && type
                ? type.name
                : $"type_{typeId}";
        }
        catch
        {
            return $"type_{typeId}";
        }
    }

    private static string SceneName(int sceneId)
    {
        try
        {
            return NetworkSceneManager.GetSceneGroup(sceneId).name;
        }
        catch
        {
            return $"scene_{sceneId}";
        }
    }

    private static string Trim(string value) => value.Length <= 160 ? value : value[..160];

    private static bool TakeBudget()
    {
        var now = Clock.ElapsedMilliseconds;
        if (now - windowStart >= WindowMilliseconds)
        {
            if (windowDropped > 0)
                Logger.LogDiagnostic("actor", "events-dropped", $"count={windowDropped}; window=1s");
            windowStart = now;
            windowCount = 0;
            windowDropped = 0;
        }

        if (windowCount < MaxEventsPerWindow)
        {
            windowCount++;
            return true;
        }

        windowDropped++;
        return false;
    }
}
#endif

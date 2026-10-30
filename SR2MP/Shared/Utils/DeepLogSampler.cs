#if SR2MP_DEEPLOG
using System.Text;
using Il2CppMonomiPark.SlimeRancher.Player.CharacterController;
using Il2CppMonomiPark.SlimeRancher.Player.PlayerItems;
using Il2CppMonomiPark.SlimeRancher.Slime;
using SR2MP.Components.Actor;
using SR2MP.Components.FX;

namespace SR2MP.Shared.Utils;

/// <summary>
/// Periodic world sampling for deep-log builds. Pumped from the main-thread dispatcher; every
/// event is interval-limited and capped so a full play session stays far below the log budget
/// (pose ~0.5 MB/h, Tarr census ~0.15 MB/h only while Tarrs are present, summary ~0.01 MB/h).
/// </summary>
internal static class DeepLogSampler
{
    private const float PoseInterval = 2f;
    private const float CensusInterval = 60f; // FindObjectsOfType over ~2k actors hitches the frame; keep it rare.
    private const float CensusRadius = 80f;
    private const int MaxTarrEntries = 30;
    private const int MaxUnregisteredIds = 20;

    private static float nextPoseAt;
    private static float nextCensusAt;
    private static bool summaryTick;

    internal static void Pump()
    {
        var now = UnityEngine.Time.unscaledTime;
        if (now < nextPoseAt && now < nextCensusAt)
            return;

        try
        {
            if (!Logger.DiagnosticsEnabled || (!Main.Server.IsRunning && !Main.Client.IsConnected))
                return;

            if (now >= nextPoseAt)
            {
                nextPoseAt = now + PoseInterval;
                SamplePoses();
            }

            if (now >= nextCensusAt)
            {
                nextCensusAt = now + CensusInterval;
                SampleCensus();
            }
        }
        catch (Exception exception)
        {
            Logger.LogDiagnostic("deeplog", "sampler-error", $"type={exception.GetType().Name}",
                "deeplog-sampler-error", 30_000);
        }
    }

    private static void SamplePoses()
    {
        var player = SceneContext.Instance ? SceneContext.Instance.player : null;
        if (player)
        {
            var controller = player.GetComponent<PlayerItemController>();
            var vac = controller ? controller._vacuumItem : null;
            var vacActive = vac && (vac._vacPressed || vac._vacMode != VacuumItem.VacMode.NONE);
            Logger.LogDiagnostic("player", "pose-sample",
                $"id={LocalID}; local=True; pos={player.transform.position.ToString("F2")}; " +
                $"yaw={player.transform.eulerAngles.y:F0}; vac={vacActive}" +
                (vac ? $"; vacMode={vac._vacMode}" : string.Empty));
        }

        foreach (var remote in PlayerManager.GetAllPlayers())
        {
            Logger.LogDiagnostic("player", "pose-sample",
                $"id={remote.PlayerId}; local=False; pos={remote.Position.ToString("F2")}; " +
                $"yaw={remote.Rotation:F0}; vac={RemoteVacuumSuctionFX.IsSuctionVisible(remote.PlayerId)}; " +
                $"scene={remote.SceneGroup}");
        }
    }

    private static void SampleCensus()
    {
        var player = SceneContext.Instance ? SceneContext.Instance.player : null;
        if (!player)
            return;

        summaryTick = !summaryTick;
        var origin = player.transform.position;
        var tarrs = new StringBuilder();
        var tarrCount = 0;
        var near = 0;
        var nearLocal = 0;
        var unregistered = new StringBuilder();
        var unregisteredCount = 0;

        foreach (var actor in UnityEngine.Object.FindObjectsOfType<IdentifiableActor>())
        {
            if (!actor || (actor.transform.position - origin).sqrMagnitude > CensusRadius * CensusRadius)
                continue;

            var typeName = actor.identType ? actor.identType.name : null;
            if (actor.identType && actor.identType.IsPlayer)
                continue;

            var id = actor.GetActorId().Value;
            var net = actor.GetComponent<NetworkActor>();
            var registered = id != 0 && ActorManager.Actors.TryGetValue(id, out var model) && model != null &&
                             model.GetGameObject() == actor.gameObject;
            near++;
            if (net && net.LocallyOwned)
                nearLocal++;

            if (!registered)
            {
                unregisteredCount++;
                if (unregisteredCount <= MaxUnregisteredIds)
                    unregistered.Append(unregistered.Length == 0 ? string.Empty : ",").Append(id.ToString("X"));
            }

            if (!IsTarrTypeName(typeName))
                continue;

            tarrCount++;
            if (tarrCount > MaxTarrEntries)
                continue;

            var grappleText = string.Empty;
            try
            {
                // An ordinary Tarr grapples through TentacleGrapple; TarrGrowthTentacleGrapple is the Labyrinth variant.
                var plain = actor.GetComponentInChildren<TentacleGrapple>(true);
                var growth = plain ? null : actor.GetComponentInChildren<TarrGrowthTentacleGrapple>(true);
                if (plain || growth)
                {
                    var target = plain ? plain!._target : growth!._target;
                    var tentacle = plain ? plain!._activeTentacle : growth!._activeTentacle;
                    var targetText = !target ? "-"
                        : target.GetComponentInParent<NetworkActor>() is { } targetActor && targetActor
                            ? targetActor.ActorId.Value.ToString("X")
                            : target.GetComponentInParent<SRCharacterController>() ? "player" : "unmapped";
                    grappleText = $",grapple={(plain ? (plain!._grappling ? "on" : "off") : "growth")}" +
                                  $"/tent={(tentacle && tentacle!.activeInHierarchy ? 1 : 0)}>{targetText}";
                }
            }
            catch (Exception exception)
            {
                grappleText = $",grapple=error:{exception.GetType().Name}";
            }

            tarrs.Append(tarrs.Length == 0 ? string.Empty : " | ")
                .Append(id.ToString("X")).Append(',').Append(typeName)
                .Append(",net=").Append(net ? 1 : 0)
                .Append(",owner=").Append(net ? net.CurrentOwnerId : "-")
                .Append(",local=").Append(net && net.LocallyOwned ? 1 : 0)
                .Append(",reg=").Append(registered ? 1 : 0)
                .Append(",pos=").Append(actor.transform.position.ToString("F1"))
                .Append(grappleText);
        }

        if (tarrCount > 0)
            Logger.LogDiagnostic("tarr", "census",
                $"count={tarrCount}; shown={Math.Min(tarrCount, MaxTarrEntries)}; {tarrs}");

        if (summaryTick)
            return;

        var ownedByMe = 0;
        foreach (var owner in ActorManager.OwnershipByActorId.Values)
            if (owner == LocalID)
                ownedByMe++;

        Logger.LogDiagnostic("actor", "census-summary",
            $"registered={ActorManager.Actors.Count}; ownedByMe={ownedByMe}; near={near}; nearLocal={nearLocal}; " +
            $"nearUnregistered={unregisteredCount}" +
            (unregisteredCount > 0 ? $"; unregisteredIds={unregistered}" : string.Empty));
    }

    // Same rule as TestGameplayBot.IsTarrTypeName (copied so deep logs do not depend on test code).
    private static bool IsTarrTypeName(string? name)
    {
        if (name == null || name.IndexOf("tarr", StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        foreach (var excluded in new[] { "gordo", "plort", "spawn", "tentacle" })
            if (name.IndexOf(excluded, StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
        return true;
    }
}

/// <summary>
/// Tracks the local native vacuum's pulled actors so a long-range "vanish vs pull" mismatch can be
/// matched against the other seat's log. Start/lost are limited to once per second per actor.
/// </summary>
internal static class DeepLogVacuumTracker
{
    private const int MaxTracked = 64;

    private sealed class Pull
    {
        internal long ActorId;
        internal string Type = string.Empty;
        internal Vacuumable? Target;
        internal float LastDistance;
        internal float MaxDistance;
    }

    private static readonly List<Pull> Tracked = new();

    /// <summary>Called after the native ForceJoint ran for the local vacuum.</summary>
    internal static void OnForceJoint(VacuumItem vacuumItem, Vacuumable target)
    {
        if (!vacuumItem || !target)
            return;

        try
        {
            var identifiable = target._identifiable;
            var actorId = identifiable ? identifiable.GetActorId().Value : 0;
            var type = identifiable && identifiable.identType ? identifiable.identType.name : "<unknown>";
            var distance = Distance(vacuumItem, target);
            var joint = target._captiveToJoint;
            var net = target.GetComponent<NetworkActor>();
            var detail = $"actorId={actorId:X}; type={type}; owner={(net ? net.CurrentOwnerId : "<none>")}; " +
                         $"locallyOwned={(net && net.LocallyOwned)}; distance={distance:F2}; " +
                         $"maxVacDist={vacuumItem.MaxVacDist:F2}; captureDist={vacuumItem.CaptureDist:F2}";
            if (!joint)
            {
                Logger.LogDiagnostic("vacuum", "pull-nojoint", detail, $"vac-pull-nojoint:{actorId:X}", 1000);
                return;
            }

            Logger.LogDiagnostic("vacuum", "pull-start", detail, $"vac-pull-start:{actorId:X}", 1000);
            foreach (var existing in Tracked)
                if (existing.ActorId == actorId)
                {
                    existing.Target = target;
                    return;
                }

            if (Tracked.Count < MaxTracked)
                Tracked.Add(new Pull { ActorId = actorId, Type = type, Target = target, LastDistance = distance, MaxDistance = distance });
        }
        catch (Exception exception)
        {
            Logger.LogDiagnostic("vacuum", "pull-track-error", $"type={exception.GetType().Name}",
                "vac-pull-track-error", 30_000);
        }
    }

    /// <summary>Called every local VacuumItem.Update.</summary>
    internal static void Update(VacuumItem vacuumItem)
    {
        if (Tracked.Count == 0 || !vacuumItem)
            return;

        try
        {
            for (var i = Tracked.Count - 1; i >= 0; i--)
            {
                var pull = Tracked[i];
                string? reason = null;
                var target = pull.Target;
                if (!target)
                    reason = "object-destroyed";
                else if (!target._captiveToJoint)
                    reason = "joint-released";

                if (reason == null)
                {
                    pull.LastDistance = Distance(vacuumItem, target!);
                    pull.MaxDistance = Math.Max(pull.MaxDistance, pull.LastDistance);
                    continue;
                }

                Tracked.RemoveAt(i);
                Logger.LogDiagnostic("vacuum", "pull-lost",
                    $"actorId={pull.ActorId:X}; type={pull.Type}; reason={reason}; " +
                    $"lastDistance={pull.LastDistance:F2}; maxDistance={pull.MaxDistance:F2}; " +
                    $"modelPresent={ActorManager.Actors.ContainsKey(pull.ActorId)}; vacPressed={vacuumItem._vacPressed}",
                    $"vac-pull-lost:{pull.ActorId:X}", 1000);
            }
        }
        catch (Exception exception)
        {
            Tracked.Clear();
            Logger.LogDiagnostic("vacuum", "pull-track-error", $"type={exception.GetType().Name}",
                "vac-pull-track-error", 30_000);
        }
    }

    private static float Distance(VacuumItem vacuumItem, Vacuumable target)
    {
        var origin = vacuumItem.VacOrigin;
        return origin ? Vector3.Distance(origin.transform.position, target.transform.position) : float.NaN;
    }
}
#endif

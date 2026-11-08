// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Player.CharacterController;
using UnityEngine.InputSystem;
using Lycoreia.Owned.Components.UI;
using Lycoreia.Game.Avatars;

namespace Lycoreia.Owned.Shared.Managers;

/// <summary>
/// Player-to-player fun. Each player moves only itself, so both features run on the affected player's client:
/// - Vac a friend: the nozzle poses of friends who are vacuuming already stream to everyone. When the local
///   player is inside a friend's suction cone it floats to a hold point in front of their nozzle (gravity
///   off); letting go of the vac drops it with whatever momentum it had, so a friend can be caught mid-fall
///   or flung. Jump wriggles free.
/// - Kick (V): the kicker picks a friend in front, everyone plays the kick on the kicker's avatar and the
///   kicked friend launches itself forward and up (parkour boost). With no friend in front, a largo in front
///   gets kicked instead: whoever owns that largo (moves it for everyone) applies the push.
/// Both can be switched off for yourself in Settings.
/// </summary>
internal static class FriendInteractions
{
    // Kick
    private const float KickCooldown = 0.7f;
    private const float KickReach = 2.8f;          // horizontal metres from the kicker
    private const float KickHeightTolerance = 2.2f;
    private const float KickHalfAngle = 60f;        // degrees either side of where the kicker faces
    private const float KickForward = 8f;
    private const float KickUp = 12f;
    private const float KickLookUpBonus = 5f;       // extra lift when the kicker aims up
    private const float MaxImpulse = 25f;
    private const float HostReachSlack = 6f;        // host-side distance check, allows for latency

    // Largo kick
    private const float LargoReach = 1.9f;          // probe sphere centre, metres ahead of the kicker
    private const float LargoProbeRadius = 1.7f;
    private const float LargoForward = 7f;          // velocity change, m/s
    private const float LargoUp = 6f;
    private const float LargoLookUpBonus = 4f;
    private const float LargoHostReachSlack = 9f;   // largos are big: centre can be further than a player's

    // Vac hold
    private const float ConeFreshSeconds = 0.2f;
    private const float GrabMinAlong = 0.4f;
    private const float GrabMaxAlong = 6.5f;
    private const float GrabRadius = 0.7f;
    private const float GrabRadiusPerMetre = 0.22f;
    private const float KeepMaxAlong = 9f;
    private const float HoldDistance = 2.6f;        // metres in front of the nozzle
    private const float HoldStiffness = 7f;
    private const float HoldMaxSpeed = 16f;
    private const float HoldResponse = 10f;
    private const float BodyCentreHeight = 0.9f;
    private const float WriggleFreeImmunity = 1.5f;

    private readonly record struct Cone(Vector3 Nozzle, Vector3 Aim, float SeenAt);

    private static readonly Dictionary<int, Cone> Cones = new();
    private static float nextKickAt;
    private static int heldBy;                       // cone key, 0 = not held
    private static float heldSince;
    private static int immuneTo;
    private static float immuneUntil;

    internal static bool CanBeVacced => ModFacade.FriendsCanVacMe;
    internal static bool CanBeKicked => ModFacade.FriendsCanKickMe;

    /// <summary>Called by each visible remote suction cone every frame.</summary>
    internal static void ReportRemoteCone(int key, Vector3 nozzle, Vector3 aim)
    {
        if (aim.sqrMagnitude < 0.5f)
            return;
        Cones[key] = new Cone(nozzle, aim.normalized, UnityEngine.Time.time);
    }

    /// <summary>ModFacade-thread tick (from the multiplayer UI's Update).</summary>
    internal static void Tick()
    {
        if (!ModFacade.IsNetworkActive)
        {
            Release("network inactive");
            Cones.Clear();
#if LYCOREIA_TEST
            // Test builds: V plays the local first-person kick offline too (to try the animation alone).
            if (IsGameplayInputFree() && Keyboard.current is { } offlineKeyboard && offlineKeyboard.vKey.wasPressedThisFrame)
                FirstPersonLegs.Play();
#endif
            return;
        }

        var player = SceneContext.Instance ? SceneContext.Instance.Player : null;
        var controller = player ? player!.GetComponent<SRCharacterController>() : null;
        if (!controller)
        {
            heldBy = 0;
            return;
        }

        if (IsGameplayInputFree() && Keyboard.current is { } keyboard && keyboard.vKey.wasPressedThisFrame)
            TryKick(player!);

        UpdateHold(controller!);
    }

    private static bool IsGameplayInputFree()
        => !ChatHud.IsOpen && !LycoreiaMenu.IsOpen && !Lycoreia.Game.Hooks.GameMenus.NativePaused &&
           !SystemContext.Instance.SceneLoader.IsSceneLoadInProgress;

    // ---------------------------------------------------------------- kick

    private static void TryKick(GameObject player)
    {
        var now = UnityEngine.Time.time;
        if (now < nextKickAt)
            return;
        nextKickAt = now + KickCooldown;

        var camera = Camera.main;
        var look = camera ? camera!.transform.forward : player.transform.forward;
        var facing = Vector3.ProjectOnPlane(look, Vector3.up);
        if (facing.sqrMagnitude < 1e-4f)
            facing = player.transform.forward;
        facing.Normalize();

        var origin = player.transform.position;
        string target = string.Empty;
        var best = float.MaxValue;
        foreach (var (playerId, playerObject) in PlayerObjects)
        {
            if (!playerObject || playerId == LocalID)
                continue;
            var delta = playerObject.transform.position - origin;
            var flat = Vector3.ProjectOnPlane(delta, Vector3.up);
            if (Mathf.Abs(delta.y) > KickHeightTolerance || flat.magnitude > KickReach ||
                (flat.sqrMagnitude > 0.01f && Vector3.Angle(facing, flat) > KickHalfAngle))
                continue;
            if (flat.magnitude < best)
            {
                best = flat.magnitude;
                target = playerId;
            }
        }

        var lookUp = Mathf.Clamp01(look.y);
        var impulse = facing * KickForward + Vector3.up * (KickUp + KickLookUpBonus * lookUp);
        long largoId = 0;
        var largoImpulse = Vector3.zero;
        if (target.Length == 0 && FindLargoInFront(origin, facing, out largoId))
            largoImpulse = facing * LargoForward + Vector3.up * (LargoUp + LargoLookUpBonus * lookUp);

        FirstPersonLegs.Play();
        var packet = new Lycoreia.Net.Messages.PlayerKick
        {
            KickerId = LocalID, TargetId = target, Impulse = impulse.ToWire(), TargetActorId = largoId, LargoImpulse = largoImpulse.ToWire(),
        };
        if (largoId != 0)
            PushLargoIfOwned(packet); // our own largo: push it now, the others only animate
        ModFacade.SendToAllOrServer(packet);
        DiagLog.LogDiagnostic("friend-fun", "kick-sent",
            $"target={(target.Length > 0 ? target : "none")}; largo={largoId:X}; impulse={impulse}");
    }

    /// <summary>The nearest largo whose body overlaps a sphere just in front of the kicker.</summary>
    private static bool FindLargoInFront(Vector3 origin, Vector3 facing, out long actorId)
    {
        actorId = 0;
        var centre = origin + facing * LargoReach + Vector3.up * 0.9f;
        var best = float.MaxValue;
        foreach (var collider in Physics.OverlapSphere(centre, LargoProbeRadius, Physics.DefaultRaycastLayers,
                     QueryTriggerInteraction.Ignore))
        {
            var actor = collider ? collider.GetComponentInParent<IdentifiableActor>() : null;
            if (!actor || !actor!.identType)
                continue;
            var definition = actor.identType.TryCast<SlimeDefinition>();
            if (definition == null || !definition.IsLargo)
                continue;
            var distance = (actor.transform.position - origin).sqrMagnitude;
            if (distance >= best)
                continue;
            var id = actor.GetActorId().Value;
            if (id == 0)
                continue;
            best = distance;
            actorId = id;
        }
        return actorId != 0;
    }

    /// <summary>Applies a largo kick if this game owns (simulates) that largo; true if it did.</summary>
    private static bool PushLargoIfOwned(Lycoreia.Net.Messages.PlayerKick packet)
    {
        var network = Lycoreia.Game.ActorLink.ById(packet.TargetActorId);   // actor table lookup
        if (!network || !network.LocallyOwned)
            return false;
        var body = network.GetComponent<Rigidbody>();
        if (!body || body!.isKinematic)
            return false;
        body.AddForce(packet.LargoImpulse.ToUnity(), ForceMode.VelocityChange);
        DiagLog.LogDiagnostic("friend-fun", "largo-kicked", $"actor={packet.TargetActorId:X}; by={packet.KickerId}; impulse={packet.LargoImpulse}");
        return true;
    }

    /// <summary>Basic sanity on any kick (and, on the host, that kicker and target are actually close).</summary>
    internal static bool IsPlausibleKick(Lycoreia.Net.Messages.PlayerKick packet)
    {
        var impulse = packet.Impulse.ToUnity();
        if (!float.IsFinite(impulse.x) || !float.IsFinite(impulse.y) || !float.IsFinite(impulse.z) ||
            impulse.magnitude > MaxImpulse)
            return false;
        if (packet.TargetActorId != 0)
        {
            var push = packet.LargoImpulse.ToUnity();
            if (!float.IsFinite(push.x) || !float.IsFinite(push.y) || !float.IsFinite(push.z) || push.magnitude > MaxImpulse)
                return false;
            if (ModFacade.Server.IsRunning)
            {
                var largoKicker = PositionOf(packet.KickerId);
                if (!largoKicker.HasValue || !(Lycoreia.Game.GameActorWorld.ObjectOf(packet.TargetActorId) is { } largoObject))
                    return false;
                var largoPosition = largoObject.transform.position;
                if (Vector3.Distance(largoKicker.Value, largoPosition) > LargoHostReachSlack)
                    return false;
            }
        }
        if (packet.TargetId.Length == 0 || !ModFacade.Server.IsRunning)
            return true;

        var kicker = PositionOf(packet.KickerId);
        var target = PositionOf(packet.TargetId);
        return kicker.HasValue && target.HasValue && Vector3.Distance(kicker.Value, target.Value) <= HostReachSlack;
    }

    private static Vector3? PositionOf(string playerId)
    {
        if (playerId == LocalID)
        {
            var player = SceneContext.Instance ? SceneContext.Instance.Player : null;
            return player ? player!.transform.position : null;
        }
        return PlayerObjects.TryGetValue(playerId, out var playerObject) && playerObject
            ? playerObject.transform.position
            : null;
    }

    internal static void ReceiveKick(Lycoreia.Net.Messages.PlayerKick packet)
    {
        // the kicker's avatar animation is played by RemoteAvatars.ApplyKick

        if (packet.TargetActorId != 0)
            PushLargoIfOwned(packet);

        if (packet.TargetId != LocalID || !CanBeKicked)
            return;

        var player = SceneContext.Instance ? SceneContext.Instance.Player : null;
        var controller = player ? player!.GetComponent<SRCharacterController>() : null;
        if (!controller)
            return;

        Release("kicked");
        controller!.ForceUnground();
        var velocity = controller.BaseVelocity;
        velocity.y = Mathf.Max(velocity.y, 0f);
        controller.BaseVelocity = velocity * 0.3f + packet.Impulse.ToUnity();
        DiagLog.LogDiagnostic("friend-fun", "kicked", $"by={packet.KickerId}; impulse={packet.Impulse}");
    }

    // ---------------------------------------------------------------- vac hold

    private static void UpdateHold(SRCharacterController controller)
    {
        var now = UnityEngine.Time.time;
        var centre = controller.Position + Vector3.up * BodyCentreHeight;

        if (heldBy != 0)
        {
            var keyboard = Keyboard.current;
            if (!CanBeVacced || !Cones.TryGetValue(heldBy, out var holding) || now - holding.SeenAt > ConeFreshSeconds ||
                Vector3.Dot(centre - holding.Nozzle, holding.Aim) > KeepMaxAlong)
            {
                Release("vac released");
                return;
            }
            if (keyboard != null && keyboard.spaceKey.wasPressedThisFrame && now - heldSince > 0.3f && IsGameplayInputFree())
            {
                immuneTo = heldBy;
                immuneUntil = now + WriggleFreeImmunity;
                Release("wriggled free");
                return;
            }

            Hold(controller, holding);
            return;
        }

        if (!CanBeVacced)
            return;

        foreach (var (key, cone) in Cones)
        {
            if (now - cone.SeenAt > ConeFreshSeconds || (key == immuneTo && now < immuneUntil))
                continue;
            var offset = centre - cone.Nozzle;
            var along = Vector3.Dot(offset, cone.Aim);
            if (along < GrabMinAlong || along > GrabMaxAlong)
                continue;
            var radial = (offset - cone.Aim * along).magnitude;
            if (radial > GrabRadius + GrabRadiusPerMetre * along)
                continue;

            heldBy = key;
            heldSince = now;
            controller.ForceUnground();
            DiagLog.LogDiagnostic("friend-fun", "vac-grabbed", $"along={along:F2}; radial={radial:F2}");
            Hold(controller, cone);
            return;
        }
    }

    private static void Hold(SRCharacterController controller, Cone cone)
    {
        controller.BypassGravity = true;
        var feetTarget = cone.Nozzle + cone.Aim * HoldDistance - Vector3.up * BodyCentreHeight;
        var desired = Vector3.ClampMagnitude((feetTarget - controller.Position) * HoldStiffness, HoldMaxSpeed);
        var blend = 1f - Mathf.Exp(-HoldResponse * UnityEngine.Time.deltaTime);
        controller.BaseVelocity = Vector3.Lerp(controller.BaseVelocity, desired, blend);
    }

    private static void Release(string reason)
    {
        if (heldBy == 0)
            return;
        heldBy = 0;

        var player = SceneContext.Instance ? SceneContext.Instance.Player : null;
        var controller = player ? player!.GetComponent<SRCharacterController>() : null;
        // Always back to normal gravity. Restoring the saved flag left a friend grabbed mid-jetpack (the jetpack
        // had gravity off) floating like a feather forever (owner 2026-11-01); an active jetpack re-applies itself.
        if (controller)
            controller!.BypassGravity = false;
        DiagLog.LogDiagnostic("friend-fun", "vac-released", reason);
    }
}

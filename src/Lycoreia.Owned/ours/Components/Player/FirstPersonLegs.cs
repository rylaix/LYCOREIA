// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Game.Avatars;

/// <summary>
/// The local player's own kick, seen in first person, built the way first-person games do it: a dedicated
/// first-person leg posed in camera space (like a weapon viewmodel), not the third-person body seen from the eyes.
/// A copy of the body with everything but the kicking leg collapsed hangs from the camera with its hip just below
/// and behind the view, so only the shin and foot drive up into the lower middle of the screen (left of the vac),
/// framed the same wherever the player looks ahead. It runs the same spring-driven <see cref="KickRig"/> as remote
/// avatars, played snappier, while the real body's kicking leg is hidden. Hung from the camera, "below the view"
/// turns into "in front" when looking at one's feet, and the game's own first-person body (headless, bent forward)
/// does not take a kick animation well, so a kick started while looking steeply down shows no leg at all: the body
/// stays as the game draws it and only the camera reacts. The camera adds a small dip, a slight roll and a short
/// shake on contact. Driven from NetworkPlayer.LateUpdate, once per frame, after the animator.
/// </summary>
internal static class FirstPersonLegs
{
    // Camera space (x right, y up, z forward, metres): where the kicking hip hangs, out of view.
    private static readonly Vector3 HipInCamera = new(-0.12f, -0.62f, -0.12f);
    private const float StrikeLift = 0.3f;      // leg lengths: the foot drives up into the lower middle of the view
    private const float Speed = 1.6f;           // first-person kicks play faster than the third-person one
    private const float LookDownSwitch = 40f;   // degrees: steeper than this, no leg is shown (camera only)

    // Camera keys on the KickPose timeline: (time, pitch down in degrees, roll in degrees).
    private static readonly (float Time, float Pitch, float Roll)[] CameraKeys =
    {
        (0.00f, 0f, 0f),
        (0.16f, -1.5f, 0.4f), // wind-up: the head lifts a little
        (0.34f, 1.5f, -0.8f),
        (0.47f, 4f, -1.8f),   // strike
        (0.58f, 3.5f, -1.5f),
        (0.76f, 1.5f, -0.5f),
        (0.96f, 0.3f, 0f),
        (1.18f, 0f, 0f),
    };
    private const float CameraFrequency = 4.5f;
    private const float CameraDamping = 0.55f;
    private const float ShakeDegrees = 1.2f;
    private const float ShakeHz = 17f;
    private const float ShakeDecay = 13f;
    private const float Collapsed = 0.001f;
    private const float MaxCameraPitch = 88f;        // degrees from level the kick tilt may reach
    private const float CameraSubStep = 1f / 240f;   // spring integration step, seconds
    private const float MaxKickTilt = 8f;            // degrees: the keys only go to 4; anything past this is a glitch

    private static GameObject? copy;
    private static Transform? copyHip;
    private static KickRig? rig;
    private static bool cameraOnly;             // the current kick shows no leg (looking down)
    private static float cameraOnlyTime;        // its time on the KickPose timeline
    private static Renderer[] renderers = Array.Empty<Renderer>();
    private static int cameraMask = -1;
    private static bool pendingPlay;
    private static bool missingLogged;
    private static int lastTickFrame = -1;

#if LYCOREIA_TEST
    private static float nextTraceAt;
#endif
    private static Transform? copyFoot;
    private static Transform? realLeg;       // the real body's kicking thigh, hidden while the copy kicks
    private static Vector3 realLegScale;

    private static Vector2 cameraValue;      // pitch, roll actually applied (spring)
    private static Vector2 cameraVelocity;
    private static Transform? cameraTransform;
    private static Quaternion cameraWritten;
    private static Quaternion cameraOffset = Quaternion.identity;
    private static bool cameraApplied;

    /// <summary>The local player pressed V; the kick starts on the next late update (after the animator).</summary>
    internal static void Play()
    {
        var camera = Camera.main;
        var lookDown = camera ? Mathf.Asin(Mathf.Clamp(-camera!.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg : 0f;
        if (lookDown > LookDownSwitch)
        {
            if (copy && copy!.activeSelf)
            {
                copy.SetActive(false);
                HideRealLeg(false);
            }
            cameraOnly = true;
            cameraOnlyTime = 0f;
            cameraValue = cameraVelocity = Vector2.zero;
            return;
        }
        cameraOnly = false;
        if (!Ensure())
            return;
        Place();
        copy!.SetActive(true);
        pendingPlay = true; // the animator poses the copy this frame; the kick starts from that pose
    }

    /// <summary>From NetworkPlayer.LateUpdate (after the animator); runs once per frame.</summary>
    internal static void LateTick()
    {
        if (UnityEngine.Time.frameCount == lastTickFrame)
            return;
        lastTickFrame = UnityEngine.Time.frameCount;
        UndoCamera();
        if (cameraOnly)
        {
            TickCameraOnly();
            return;
        }
        if (!copy || !copy!.activeSelf || rig == null)
            return;

        Place();
        RefreshLayers();
        if (pendingPlay)
        {
            pendingPlay = false;
            rig.Play();
            cameraValue = cameraVelocity = Vector2.zero;
            HideRealLeg(true);
        }
        rig.UpdateDynamics();
        rig.Animate();
#if LYCOREIA_TEST
        if (UnityEngine.Time.unscaledTime >= nextTraceAt && Camera.main)
        {
            nextTraceAt = UnityEngine.Time.unscaledTime + 0.2f;
            var view = Camera.main!.transform;
            var foot = copyFoot ? copyFoot!.position : Vector3.zero;
            OwnedLog.LogMessage($"[test-fplegs] t={rig.KickTime:F2} " +
                                $"hipCam={(copyHip ? Quaternion.Inverse(view.rotation) * (copyHip!.position - view.position) : Vector3.zero).ToString("F2")} " +
                                $"footCam={(Quaternion.Inverse(view.rotation) * (foot - view.position)).ToString("F2")} " +
                                $"footView={Camera.main.WorldToViewportPoint(foot).ToString("F2")} cam={Camera.main.name} " +
                                $"active={copy.activeInHierarchy} visible={renderers.Count(r => r && r.isVisible)}/{renderers.Length} " +
                                $"layers={string.Join(",", renderers.Where(r => r).Select(r => r.gameObject.layer).Distinct())} mask={Camera.main.cullingMask:X}");
        }
#endif
        if (rig.IsKicking)
        {
            ApplyCamera(rig.KickTime);
            return;
        }
        copy.SetActive(false);
        HideRealLeg(false);
    }

    private static void TickCameraOnly()
    {
        var dt = UnityEngine.Time.deltaTime * Speed;
#if LYCOREIA_TEST
        dt *= KickRig.TestKickSpeed;
#endif
        cameraOnlyTime += dt;
        if (cameraOnlyTime > KickPose.TEnd)
        {
            cameraOnly = false;
            return;
        }
        ApplyCamera(cameraOnlyTime);
    }

    /// <summary>Hangs the copy from the camera: its body axes are the camera's, its kicking hip at HipInCamera.</summary>
    private static void Place()
    {
        var camera = Camera.main;
        if (!camera || !copy || !copyHip)
            return;
        var view = camera!.transform;
        copy!.transform.rotation = view.rotation;
        var hipOffset = copyHip!.position - copy.transform.position;
        copy.transform.position = view.position + view.rotation * HipInCamera - hipOffset;
    }

    private static Animator? realAnimator;
    private static Transform? realHip;

    /// <summary>The real body's kicking hip (its left thigh bone; collapsing the thigh does not move it).</summary>
    private static Transform? RealHip()
    {
        var player = SceneContext.Instance ? SceneContext.Instance.Player : null;
        var animator = player ? player!.GetComponentInChildren<Animator>() : null;
        if (animator != realAnimator)
        {
            realAnimator = animator;
            realHip = animator && animator!.avatar && animator.avatar.isHuman
                ? animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg)
                : null;
        }
        return realHip;
    }

    private static void HideRealLeg(bool hide)
    {
        if (hide)
        {
            // A new kick while the leg is still hidden must not save the collapsed scale as the normal one
            // (the leg would stay invisible after the kick).
            if (realLeg)
                return;
            realLeg = RealHip();
            if (!realLeg)
                return;
            realLegScale = realLeg!.localScale;
            if (realLegScale.sqrMagnitude < 0.01f)
                realLegScale = Vector3.one;
            realLeg.localScale = Vector3.one * Collapsed;
            return;
        }
        if (realLeg)
            realLeg!.localScale = realLegScale;
        realLeg = null;
    }

    private static bool Ensure()
    {
        if (copy && rig != null)
            return true;

        // Like a remote avatar, the copy gets the local player's humanoid avatar and animation controller (the
        // prefab model itself carries a generic rig).
        // the humanoid body animator may sit on the player root or below it (shared lookup with the remote avatars)
        var playerAnimator = AvatarTemplate.LocalBodyAnimator();
        var source = PlayerPrefab ? BodyAnimatorIn(PlayerPrefab) : null;
        if (!source || !playerAnimator || !playerAnimator!.avatar || !playerAnimator.runtimeAnimatorController)
        {
            if (!missingLogged)
            {
                missingLogged = true;
                OwnedLog.LogWarning($"first-person kick leg unavailable: template={(PlayerPrefab ? "yes" : "no")}, body animator={(playerAnimator ? "yes" : "no")}");
            }
            return false;
        }
        var model = source!.transform;
        while (model.parent && model.parent != PlayerPrefab.transform)
            model = model.parent;

        copy = Object.Instantiate(model.gameObject);
        copy.name = "LYCOREIA_FirstPersonKickLeg";
        copy.transform.localScale = model.lossyScale;
        Object.DontDestroyOnLoad(copy);
        foreach (var collider in copy.GetComponentsInChildren<Collider>(true))
            collider.enabled = false;
        foreach (var body in copy.GetComponentsInChildren<Rigidbody>(true))
            body.isKinematic = true;

        var animator = BodyAnimatorIn(copy);
        if (!animator)
        {
            Object.Destroy(copy);
            copy = null;
            return false;
        }
        animator!.runtimeAnimatorController = Object.Instantiate(playerAnimator.runtimeAnimatorController);
        animator.avatar = playerAnimator.avatar;

        // Only the kicking leg stays: the upper body (arms, head, backpack, vac) and the other leg collapse.
        foreach (var bone in new[] { HumanBodyBones.Chest, HumanBodyBones.RightUpperLeg })
        {
            var transform = animator.GetBoneTransform(bone);
            if (transform)
                transform!.localScale = Vector3.one * Collapsed;
        }
        copyHip = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
        copyFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);

        renderers = copy.GetComponentsInChildren<Renderer>(true);
        foreach (var renderer in renderers)
        {
            if (!renderer)
                continue;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            // Skinned meshes are culled by bounds around the standing body: recompute them every frame.
            var skinned = renderer.TryCast<SkinnedMeshRenderer>();
            if (skinned)
                skinned!.updateWhenOffscreen = true;
        }
        cameraMask = -1;

        rig = new KickRig(animator, copy.transform, StrikeLift, Speed);
        copy.SetActive(false);
        return true;
    }

    /// <summary>The body animator: the top-most one (the vac hose under it has its own).</summary>
    private static Animator? BodyAnimatorIn(GameObject gameObject)
    {
        foreach (var candidate in gameObject.GetComponentsInChildren<Animator>(true))
            if (candidate && (!candidate.transform.parent || !candidate.transform.parent.GetComponentInParent<Animator>(true)))
                return candidate;
        return null;
    }

    /// <summary>Puts the copy on layers the gameplay camera draws.</summary>
    private static void RefreshLayers()
    {
        var camera = Camera.main;
        if (!camera || camera!.cullingMask == cameraMask)
            return;
        cameraMask = camera.cullingMask;
        var defaultLayer = LayerMask.NameToLayer("Default");
        foreach (var renderer in renderers)
        {
            if (!renderer)
                continue;
            var layer = RemotePlayerRenderLayerPolicy.ResolveRendererLayer(renderer.gameObject.layer, cameraMask, defaultLayer);
            if (layer >= 0)
                renderer.gameObject.layer = layer;
        }
    }

    // ---------------------------------------------------------------- camera

    private static Vector2 CameraTarget(float t)
    {
        var keys = CameraKeys;
        if (t <= keys[0].Time)
            return Vector2.zero;
        for (var i = 0; i < keys.Length - 1; i++)
        {
            if (t >= keys[i + 1].Time)
                continue;
            var u = Mathf.SmoothStep(0f, 1f, (t - keys[i].Time) / (keys[i + 1].Time - keys[i].Time));
            return Vector2.Lerp(new Vector2(keys[i].Pitch, keys[i].Roll), new Vector2(keys[i + 1].Pitch, keys[i + 1].Roll), u);
        }
        return Vector2.zero;
    }

    private static void ApplyCamera(float kickTime)
    {
        var camera = Camera.main;
        if (!camera)
            return;

        // Spring toward the keyed tilt (same clock as the leg).
        var dt = UnityEngine.Time.deltaTime * Speed;
#if LYCOREIA_TEST
        dt *= KickRig.TestKickSpeed;
#endif
        if (dt > 0f)
        {
            // Small fixed sub-steps: one big explicit step is unstable below ~23 FPS (w*dt > 2) and the tilt
            // blew up ("camera goes crazy" in a world with thousands of actors).
            var w = 2f * Mathf.PI * CameraFrequency;
            var target = CameraTarget(kickTime);
            var remaining = Mathf.Min(dt, 0.25f);
            while (remaining > 0f)
            {
                var h = Mathf.Min(remaining, CameraSubStep);
                remaining -= h;
                var accel = (target - cameraValue) * (w * w) - cameraVelocity * (2f * CameraDamping * w);
                cameraVelocity += accel * h;
                cameraValue += cameraVelocity * h;
            }
            if (!float.IsFinite(cameraValue.x) || !float.IsFinite(cameraValue.y))
                cameraValue = cameraVelocity = Vector2.zero;
            cameraValue = new Vector2(Mathf.Clamp(cameraValue.x, -MaxKickTilt, MaxKickTilt),
                Mathf.Clamp(cameraValue.y, -MaxKickTilt, MaxKickTilt));
        }

        // Contact shake: a quick decaying wobble from the strike key on.
        var sinceStrike = kickTime - 0.47f;
        var shake = sinceStrike > 0f
            ? ShakeDegrees * Mathf.Sin(sinceStrike * 2f * Mathf.PI * ShakeHz) * Mathf.Exp(-sinceStrike * ShakeDecay)
            : 0f;

        cameraTransform = camera!.transform;
        // Never tilt past straight down / straight up: looking fully down, the dip would flip the view
        // backwards (your own body shows up ahead and running looks like walking back).
        var lookDown = Mathf.Asin(Mathf.Clamp(-cameraTransform.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
        var pitch = Mathf.Clamp(cameraValue.x + shake, Mathf.Min(0f, -MaxCameraPitch - lookDown), Mathf.Max(0f, MaxCameraPitch - lookDown));
        cameraOffset = Quaternion.Euler(pitch, shake * 0.4f, cameraValue.y);
        cameraTransform.localRotation *= cameraOffset;
        cameraWritten = cameraTransform.localRotation;
        cameraApplied = true;
    }

    /// <summary>
    /// The camera controller normally rewrites the camera every frame; if it did not, take last frame's tilt
    /// back off first so it never accumulates.
    /// </summary>
    private static void UndoCamera()
    {
        if (!cameraApplied)
            return;
        cameraApplied = false;
        if (cameraTransform && Quaternion.Angle(cameraTransform!.localRotation, cameraWritten) < 0.01f)
            cameraTransform.localRotation *= Quaternion.Inverse(cameraOffset);
    }
}

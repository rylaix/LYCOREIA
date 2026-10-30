namespace SR2MP.Components.Player;

/// <summary>
/// Procedural idle life and left-hand fingers for remote avatars. The game's idle clips cannot be reused:
/// the first-person clips target the first-person arms' bones and the main-menu clips barely bind to this
/// model. So, while a player stands still, the left hand occasionally leaves the vac grip for a "hand
/// warm-up" (fingers open/close, wrist turns) and the head looks around; otherwise the left fingers curl
/// around the tactical grip.
/// </summary>
internal partial class NetworkPlayer
{
    // Test rig overrides (test builds only): force the warm-up (0..1 progress) or a look-around angle.
    internal static float TestWarmup = -1f;
    internal static float TestLookAround = float.NaN;

    private const float IdleStartDelay = 5f;
    private const float WarmupSeconds = 3.4f;
    private const float LookAroundSeconds = 3.2f;

    private static readonly HumanBodyBones[][] LeftFingers =
    {
        new[] { HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal },
        new[] { HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal },
        new[] { HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal },
        new[] { HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal },
    };

    private static readonly HumanBodyBones[] LeftThumb =
        { HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal };

    private static readonly float[] FingerJointShare = { 0.8f, 1f, 0.7f };

    private Transform?[][] leftFingerBones = Array.Empty<Transform?[]>();
    private Quaternion[][] leftFingerRest = Array.Empty<Quaternion[]>();
    private Transform?[] leftThumbBones = Array.Empty<Transform?>();
    private Quaternion[] leftThumbRest = Array.Empty<Quaternion>();

    private float stillSince = -1f;
    private float nextWarmupAt;
    private float warmupStartedAt = -100f;
    private float nextLookAroundAt;
    private float lookAroundStartedAt = -100f;
    private float lookAroundSide = 1f;

    private void SetupFingers()
    {
        leftFingerBones = LeftFingers.Select(f => f.Select(b => (Transform?)animator.GetBoneTransform(b)).ToArray()).ToArray();
        leftFingerRest = leftFingerBones.Select(f => f.Select(b => b ? b!.localRotation : Quaternion.identity).ToArray()).ToArray();
        leftThumbBones = LeftThumb.Select(b => (Transform?)animator.GetBoneTransform(b)).ToArray();
        leftThumbRest = leftThumbBones.Select(b => b ? b!.localRotation : Quaternion.identity).ToArray();
        LogHandRig();
        var now = UnityEngine.Time.time;
        nextWarmupAt = now + UnityEngine.Random.Range(8f, 18f);
        nextLookAroundAt = now + UnityEngine.Random.Range(6f, 14f);
    }

    private static bool handRigLogged;

    private void LogHandRig()
    {
        if (handRigLogged || !leftHand)
            return;
        handRigLogged = true;
        var mapped = string.Join(",", leftFingerBones.SelectMany(f => f).Select(b => b ? b!.name : "-"));
        var children = string.Join(",", leftHand!.GetComponentsInChildren<Transform>(true).Take(40).Select(t => t.name));
        var components = string.Join(",", animator.gameObject.GetComponentsInChildren<Component>(true)
            .Where(c => c).Select(c => c.GetIl2CppType().Name).Distinct().Take(60));
        Logger.LogDiagnostic("player", "hand-rig",
            $"mapped={mapped}; underLeftHand={children}; components={components}");
    }

    /// <summary>Updates the idle timers. Returns warm-up progress (0..1) or -1 when not warming up.</summary>
    private float UpdateIdleTimers()
    {
        var now = UnityEngine.Time.time;
        var moving = model != null && (model.Moving || model.AirborneState != 0);
        if (moving)
        {
            stillSince = -1f;
            warmupStartedAt = -100f;
            lookAroundStartedAt = -100f;
        }
        else if (stillSince < 0f)
        {
            stillSince = now;
        }

        if (TestWarmup >= 0f)
            return Mathf.Clamp01(TestWarmup);

        var idle = stillSince >= 0f && now - stillSince >= IdleStartDelay;
        if (idle && now >= nextWarmupAt && now - warmupStartedAt > WarmupSeconds)
        {
            warmupStartedAt = now;
            nextWarmupAt = now + UnityEngine.Random.Range(12f, 25f);
        }

        if (idle && now >= nextLookAroundAt && now - lookAroundStartedAt > LookAroundSeconds &&
            now - warmupStartedAt > WarmupSeconds)
        {
            lookAroundStartedAt = now;
            lookAroundSide = UnityEngine.Random.value < 0.5f ? -1f : 1f;
            nextLookAroundAt = now + UnityEngine.Random.Range(9f, 20f);
        }

        var progress = (now - warmupStartedAt) / WarmupSeconds;
        return progress is >= 0f and <= 1f ? progress : -1f;
    }

    /// <summary>Head yaw for the occasional look-around (degrees), eased in and out.</summary>
    private float LookAroundYaw()
    {
        if (!float.IsNaN(TestLookAround))
            return TestLookAround;
        var progress = (UnityEngine.Time.time - lookAroundStartedAt) / LookAroundSeconds;
        if (progress is < 0f or > 1f)
            return 0f;
        return Mathf.Sin(progress * Mathf.PI) * 35f * lookAroundSide;
    }

    /// <summary>How far the left hand is away from the grip during a warm-up (0 = on the grip).</summary>
    /// <summary>How far the left hand is away from the grip (0 = on the grip): long, smootherstep edges.</summary>
    private static float WarmupRelease(float progress)
    {
        if (progress < 0f)
            return 0f;
        var t = Mathf.Clamp01(Mathf.Min(progress, 1f - progress) / 0.28f);
        return t * t * t * (t * (t * 6f - 15f) + 10f);
    }

    /// <summary>
    /// Warm-up hand target: the left arm stretched out to the avatar's left side (away from the vac on the
    /// right, so the path never crosses it), with a small circling motion of the hand.
    /// </summary>
    private Vector3 WarmupHandTarget(Transform body, float progress)
    {
        var shoulder = leftArmUpper ? leftArmUpper!.position : body.position + body.up * 1.3f;
        var stretched = shoulder - body.right * AvatarPose.WarmupOutward + body.forward * AvatarPose.WarmupForward -
                        body.up * AvatarPose.WarmupDown;
        var circle = progress * Mathf.PI * 4f;
        return stretched + body.up * (Mathf.Sin(circle) * 0.035f) + body.forward * (Mathf.Cos(circle) * 0.035f);
    }

    /// <summary>Left-finger curl in degrees: gripping the vac, or opening/closing during the warm-up.</summary>
    private static float LeftFingerCurl(float progress, float release)
    {
        var grip = AvatarPose.FingerGripCurl;
        if (release <= 0f)
            return grip;
        var flex = 0.5f + 0.5f * Mathf.Cos(progress * Mathf.PI * 4f);
        return Mathf.Lerp(grip, Mathf.Lerp(5f, grip, flex), release);
    }

    /// <summary>Curls the left fingers across the palm (rig independent: axes are measured from the hand).</summary>
    private void ApplyLeftFingers(float curl, float wristTwist)
    {
        if (leftFingerBones.Length == 0 || !leftHand)
            return;

        var index = leftFingerBones[0][0];
        var little = leftFingerBones[3][0];
        if (!index || !little)
            return;

        if (Mathf.Abs(wristTwist) > 0.01f && leftArmLower)
            leftHand!.rotation = Quaternion.AngleAxis(wristTwist, leftHand.position - leftArmLower!.position) *
                                 leftHand.rotation;

        var across = (index!.position - little!.position).normalized * AvatarPose.FingerCurlSign;
        for (var f = 0; f < leftFingerBones.Length; f++)
        {
            for (var j = 0; j < leftFingerBones[f].Length; j++)
            {
                var bone = leftFingerBones[f][j];
                if (!bone) continue;
                bone!.localRotation = leftFingerRest[f][j];
                bone.rotation = Quaternion.AngleAxis(curl * FingerJointShare[j], across) * bone.rotation;
            }
        }

#if SR2MP_TEST
        if (UnityEngine.Time.frameCount % 120 == 0)
            SrLogger.LogMessage($"[test-avatar] finger curl={curl:F0} index1={index.localEulerAngles} across={across}");
#endif
        var middle = leftFingerBones[1][1] ? leftFingerBones[1][1]!.position : index.position;
        var thumbAxis = Vector3.Cross(across, middle - leftHand!.position).normalized * AvatarPose.ThumbCurlSign;
        for (var j = 0; j < leftThumbBones.Length; j++)
        {
            var bone = leftThumbBones[j];
            if (!bone) continue;
            bone!.localRotation = leftThumbRest[j];
            bone.rotation = Quaternion.AngleAxis(curl * 0.45f, thumbAxis) * bone.rotation;
        }
    }
}

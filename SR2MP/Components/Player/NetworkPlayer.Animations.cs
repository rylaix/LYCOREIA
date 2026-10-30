namespace SR2MP.Components.Player;

/// <summary>
/// Remote avatar pose tuning (degrees / metres). Static fields rather than constants so the test-only
/// avatar rig can adjust them live (control file "tune.Name=value"); players always use these defaults.
/// </summary>
internal static class AvatarPose
{
    public static float MaxArmPitch = 65f;
    // Upward vac aim is limited further so the vac never covers the face (the head still looks up).
    public static float MaxArmPitchUp = 45f;
    public static float MaxLookPitch = 85f;
    // Share of the look pitch taken by the torso (clamped); neck and head take the rest.
    public static float ChestPitchShare = 0.30f;
    public static float MaxChestPitch = 24f;
    public static float SpinePitchShare = 0.12f;
    public static float MaxSpinePitch = 10f;
    public static float NeckShareOfRest = 0.4f;
    // Procedural idle life: breathing and head sway.
    public static float BreathPeriodSeconds = 3.6f;
    public static float BreathChestDegrees = 1.2f;
    public static float HeadSwayYawDegrees = 2.5f;
    public static float HeadSwayPitchDegrees = 1.2f;
    // Right (vac) hand target from the upper-arm joint, in aim axes; looking down adds DownReach forward.
    public static float RightHandForward = 0.40f;
    public static float RightHandDown = 0.16f;
    public static float RightHandOutward = 0.16f;
    public static float RightHandDownReach = 0.25f;
    // How much the vac hand's position follows the look pitch (0 = stays at body height, 1 = moves
    // fully along the look). Low keeps the hand at the hip so looking up tilts the vac, not the arm.
    public static float HandAimFollow = 0.25f;
    // Looking up moves the vac hand out and down so the barrel passes beside the face, not across it.
    public static float RightHandUpOutward = 0.32f;
    public static float RightHandUpLower = 0.14f;
    // Looking down lowers the vac hand so the bent head clears it; the body bends at most this far.
    public static float RightHandDownLower = 0.12f;
    public static float MaxBodyLookDown = 40f;
    // Outward (to the right) vac turn at full upward aim, degrees.
    public static float VacUpYawOut = 28f;
    // Largest shift of the vac hand toward the left shoulder to keep the grip reachable, metres.
    public static float MaxGripPull = 0.12f;
    // Right elbow direction (body axes from the elbow): out, down, back.
    public static float RightElbowOut = 0.30f;
    public static float RightElbowDown = 0.30f;
    public static float RightElbowBack = 0.15f;
    // Left elbow direction: out (to the left), down.
    public static float LeftElbowOut = 0.35f;
    public static float LeftElbowDown = 0.35f;
    // Fine offset of the left-hand grip point in vac axes (side, up, along the barrel).
    public static float GripSide;
    public static float GripUp = 0.03f;
    public static float GripAlong;
    // Fallback grip when the vac has no grip mesh: fraction toward the barrel, and below the axis.
    public static float GripAlongBarrel = 0.55f;
    public static float GripBelowAxis = 0.12f;
    // Left fingers: curl per joint around the vac grip (degrees); signs pick the curl direction.
    public static float FingerGripCurl = 85f;
    public static float FingerCurlSign = 1f;
    public static float ThumbCurlSign = 1f;
    // Fraction of the wrist-to-knuckles vector that puts the palm centre (not the wrist) on the grip.
    public static float PalmShare = 0.6f;
    // Warm-up stretch target from the left shoulder: outward (to the avatar's left), forward, down.
    public static float WarmupOutward = 0.45f;
    public static float WarmupForward = 0.18f;
    public static float WarmupDown = 0.10f;
}

/// <summary>
/// Remote avatar arm pose: the right hand holds the vac low and close to the body (elbow bent) and
/// turns it toward the player's camera pitch; the left hand holds the vac's front "tactical" grip.
/// The collarbone is never forced (that tore the shoulder mesh). How the vac sits in the right hand is
/// measured once from the original upright pose, so the vac always points where the player looks.
/// </summary>
internal partial class NetworkPlayer
{
    public float ReceivedLookY;

    private static bool vacRigLogged;

    private Transform rightShoulder;
    private Transform rightArmUpper;
    private Transform rightArmLower;
    private Transform rightHand;
    private Transform? leftArmUpper;
    private Transform? leftArmLower;
    private Transform? leftHand;
    private Transform? spine;
    private Transform? chest;
    private Transform? neck;
    private Transform? head;
    private float idlePhase;
    // Additive torso/head turns: last animator base and last written value per bone (spine, chest,
    // neck, head), so a frame where the animator did not rewrite a bone never stacks the turn twice.
    private readonly Quaternion[] additiveBase = new Quaternion[4];
    private readonly Quaternion[] additiveWritten = new Quaternion[4];
    private readonly bool[] additiveValid = new bool[4];

    // Rest pose of the collarbone and every bone under it (twist/roll bones, fingers): restored each
    // frame so animation and the aim pose never fight.
    private Transform[] rightArmChain = Array.Empty<Transform>();
    private Quaternion[] rightArmRestRotations = Array.Empty<Quaternion>();
    private Vector3[] rightArmRestPositions = Array.Empty<Vector3>();

    private bool vacCalibrated;
    private bool vacInRightHand;
    private Vector3 vacAxisInHand;
    private Vector3 vacUpInHand;
    private Vector3 gripInHand;

    private void SetupAnimations()
    {
        rightArmUpper = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        rightArmLower = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
        rightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        rightShoulder = animator.GetBoneTransform(HumanBodyBones.RightShoulder);
        leftArmUpper = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        leftArmLower = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        leftHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
        spine = animator.GetBoneTransform(HumanBodyBones.Spine);
        chest = animator.GetBoneTransform(HumanBodyBones.UpperChest) ?? animator.GetBoneTransform(HumanBodyBones.Chest);
        neck = animator.GetBoneTransform(HumanBodyBones.Neck);
        head = animator.GetBoneTransform(HumanBodyBones.Head);
        idlePhase = (GetInstanceID() & 0xFF) * 0.37f;
        SetupFingers();

        if (rightShoulder)
        {
            var bones = rightShoulder.GetComponentsInChildren<Transform>(true).Where(t => t).ToArray();
            rightArmChain = bones;
            rightArmRestRotations = bones.Select(t => t.localRotation).ToArray();
            rightArmRestPositions = bones.Select(t => t.localPosition).ToArray();
        }
    }

    private void AnimateArmY()
    {
        if (IsLocal || !hasAnimationController) return;

        var warmup = UpdateIdleTimers();

        RestoreRightArmRest();
        if (!vacCalibrated)
        {
            CalibrateVac();
            RestoreRightArmRest();
        }

        var body = transform;
        var lookPitch = Mathf.Clamp(Mathf.DeltaAngle(0f, ReceivedLookY), -AvatarPose.MaxLookPitch, AvatarPose.MaxLookPitch);
        // The body stops bending past MaxBodyLookDown so the head never drops into the hands/vac.
        ApplyTorsoAndHead(body, Mathf.Min(lookPitch, AvatarPose.MaxBodyLookDown));

        var pitch = Mathf.Clamp(lookPitch, -AvatarPose.MaxArmPitchUp, AvatarPose.MaxArmPitch);
        var pitchRotation = Quaternion.AngleAxis(pitch, body.right);
        // Looking up turns the vac slightly outward so the barrel passes beside the head, not across it.
        var outward = Quaternion.AngleAxis(AvatarPose.VacUpYawOut * Mathf.Clamp01(-pitch / AvatarPose.MaxArmPitchUp), body.up);
        var aimForward = outward * (pitchRotation * body.forward);
        var aimUp = outward * (pitchRotation * body.up);

        if (vacInRightHand)
        {
            // Looking down pushes the vac further out so it does not sink into the legs.
            var forward = AvatarPose.RightHandForward + AvatarPose.RightHandDownReach * Mathf.Clamp01(pitch / AvatarPose.MaxArmPitch);
            var up01 = Mathf.Clamp01(-pitch / AvatarPose.MaxArmPitchUp);
            var down01 = Mathf.Clamp01(pitch / AvatarPose.MaxArmPitch);
            var placeForward = Vector3.Slerp(body.forward, aimForward, AvatarPose.HandAimFollow);
            var handTarget = rightArmUpper.position + placeForward * forward -
                             body.up * (AvatarPose.RightHandDown + AvatarPose.RightHandUpLower * up01 +
                                       AvatarPose.RightHandDownLower * down01) +
                             body.right * (AvatarPose.RightHandOutward + AvatarPose.RightHandUpOutward * up01);
            var handRotation = Quaternion.LookRotation(aimForward, aimUp) *
                               Quaternion.Inverse(Quaternion.LookRotation(vacAxisInHand, vacUpInHand));
            handTarget = KeepGripReachable(handTarget, handRotation);

            var elbowHint = rightArmLower.position + body.right * AvatarPose.RightElbowOut - body.up * AvatarPose.RightElbowDown -
                            body.forward * AvatarPose.RightElbowBack;
            SolveTwoBoneIk(rightArmUpper, rightArmLower, rightHand, handTarget, elbowHint);
            rightHand.rotation = handRotation;
        }
        else
        {
            ApplyLegacyArmPose(pitch);
        }

        PlaceLeftHandOnVac(body, warmup);
    }

    /// <summary>
    /// Bends spine/chest toward the look pitch, turns neck and head to where the player is looking, and
    /// adds breathing and a slight idle head sway. Additive on top of the animator's pose.
    /// </summary>
    private void ApplyTorsoAndHead(Transform body, float lookPitch)
    {
        var time = UnityEngine.Time.time + idlePhase;
        var breath = NextBreath(time);

        var spinePitch = Mathf.Clamp(lookPitch * AvatarPose.SpinePitchShare, -AvatarPose.MaxSpinePitch, AvatarPose.MaxSpinePitch);
        var chestPitch = Mathf.Clamp(lookPitch * AvatarPose.ChestPitchShare, -AvatarPose.MaxChestPitch, AvatarPose.MaxChestPitch);
        ApplyAdditive(0, spine, Quaternion.AngleAxis(spinePitch - breath * AvatarPose.BreathChestDegrees * 0.3f, body.right));
        ApplyAdditive(1, chest, Quaternion.AngleAxis(chestPitch + breath * AvatarPose.BreathChestDegrees, body.right));

        var remaining = lookPitch - spinePitch - chestPitch;
        var swayYaw = Mathf.Sin(time * 0.31f) * AvatarPose.HeadSwayYawDegrees + LookAroundYaw();
        var swayPitch = Mathf.Sin(time * 0.23f + 1.3f) * AvatarPose.HeadSwayPitchDegrees;
        ApplyAdditive(2, neck, Quaternion.AngleAxis(remaining * AvatarPose.NeckShareOfRest, body.right));
        ApplyAdditive(3, head, Quaternion.AngleAxis(swayYaw, body.up) *
                               Quaternion.AngleAxis(remaining * (neck ? 1f - AvatarPose.NeckShareOfRest : 1f) + swayPitch,
                                   body.right));
    }

    private void ApplyAdditive(int slot, Transform? bone, Quaternion worldDelta)
    {
        if (!bone)
            return;

        if (additiveValid[slot] && Quaternion.Angle(bone!.localRotation, additiveWritten[slot]) < 0.01f)
            bone.localRotation = additiveBase[slot]; // animator skipped this bone: undo last frame's turn
        additiveBase[slot] = bone!.localRotation;
        bone.rotation = worldDelta * bone.rotation;
        additiveWritten[slot] = bone.localRotation;
        additiveValid[slot] = true;
    }

    private float breathPhase;

    /// <summary>
    /// Breath value in [-1, 1]: a quicker inhale and a longer exhale, with pace and depth drifting slowly
    /// (Perlin noise) so consecutive breaths are never identical.
    /// </summary>
    private float NextBreath(float time)
    {
        var pace = 0.8f + 0.4f * Mathf.PerlinNoise(time * 0.05f, idlePhase);
        breathPhase = (breathPhase + UnityEngine.Time.deltaTime / (AvatarPose.BreathPeriodSeconds * pace)) % 1f;
        // Inhale over the first 40 % of the cycle, exhale over the rest, eased at both ends.
        var shape = breathPhase < 0.4f
            ? Mathf.SmoothStep(0f, 1f, breathPhase / 0.4f)
            : Mathf.SmoothStep(1f, 0f, (breathPhase - 0.4f) / 0.6f);
        var depth = 0.75f + 0.5f * Mathf.PerlinNoise(idlePhase, time * 0.07f);
        return (shape * 2f - 1f) * depth;
    }

    /// <summary>
    /// At steep pitches the front grip moved out of the left arm's reach and the hand detached: slide the
    /// vac hand toward the left shoulder just enough for the left hand to stay on the grip.
    /// </summary>
    private Vector3 KeepGripReachable(Vector3 handTarget, Quaternion handRotation)
    {
        if (!leftArmUpper || !leftArmLower || !leftHand)
            return handTarget;

        var reach = (Vector3.Distance(leftArmUpper!.position, leftArmLower!.position) +
                     Vector3.Distance(leftArmLower.position, leftHand!.position)) * 0.97f;
        var grip = handTarget + handRotation * Vector3.Scale(gripInHand, rightHand.lossyScale);
        var toGrip = grip - leftArmUpper.position;
        var excess = toGrip.magnitude - reach;
        if (excess <= 0f)
            return handTarget;
        // Pull toward the left shoulder only sideways/forward (never up into the face) and by a bounded
        // amount; at the steepest pitches the left hand may sit slightly short of the grip instead.
        var pull = Vector3.ProjectOnPlane(toGrip.normalized * excess, transform.up);
        return handTarget - Vector3.ClampMagnitude(pull, AvatarPose.MaxGripPull);
    }

    private void RestoreRightArmRest()
    {
        for (var i = 0; i < rightArmChain.Length; i++)
        {
            var bone = rightArmChain[i];
            if (!bone) continue;
            bone.localRotation = rightArmRestRotations[i];
            bone.localPosition = rightArmRestPositions[i];
        }
    }

    // The original fixed pose (straight arm), aim pitch on the upper arm. Used to measure the vac and
    // as a fallback when the vac is not carried by the right hand.
    private void ApplyLegacyArmPose(float pitch)
    {
        // Same upper-arm world rotation as the original (collarbone at Euler(320,180,-pitch+89), upper arm
        // identity), but with the collarbone left at rest: upper = inverse(collarboneRest) * aimed.
        var aimedShoulder = Quaternion.Euler(320, 180, -pitch + 89);
        rightHand.localRotation = Quaternion.Euler(90, 180, 0);
        rightArmUpper.localRotation = Quaternion.Inverse(rightShoulder.localRotation) * aimedShoulder;
        rightArmLower.localRotation = Quaternion.Euler(0, 6, 0);
    }

    private void CalibrateVac()
    {
        vacCalibrated = true;
        var all = transform.GetComponentsInChildren<Transform>(true);
        var vacAttach = all.FirstOrDefault(t => t && t.name == "vacAttach");
        if (!vacAttach || !rightHand || !vacAttach!.IsChildOf(rightHand))
        {
            Logger.LogDiagnostic("player", "vac-rig", $"vacAttach={(vacAttach ? vacAttach!.parent?.name : "<none>")}; inRightHand=False");
            return;
        }

        // Measure in the original upright pose (look straight ahead).
        var shoulderRotation = rightShoulder.localRotation;
        rightShoulder.localRotation = Quaternion.Euler(320, 180, 89);
        rightHand.localRotation = Quaternion.Euler(90, 180, 0);
        rightArmUpper.localRotation = Quaternion.identity;
        rightArmLower.localRotation = Quaternion.Euler(0, 6, 0);

        var vacParts = vacAttach.GetComponentsInChildren<Transform>(true);
        var barrel = vacParts.FirstOrDefault(t => t && t.name == "bone_vac_barrel");
        // The front "tactical" grip is the vac_handle mesh (vac_grip is the pistol grip the right hand
        // holds). Exported meshes keep their origin at the vac centre, so use the visible bounds centre.
        var grip = vacParts.FirstOrDefault(t => t && t.name.EndsWith("vac_handle", StringComparison.OrdinalIgnoreCase));

        var front = barrel ? barrel!.position : vacAttach.position + vacAttach.forward * 0.5f;
        var axis = front - rightHand.position;
        if (axis.sqrMagnitude < 1e-4f)
            axis = vacAttach.forward;
        vacAxisInHand = rightHand.InverseTransformDirection(axis.normalized);
        vacUpInHand = rightHand.InverseTransformDirection(transform.up);

        var gripWorld = grip
            ? VisibleCenter(grip!)
            : rightHand.position + axis * AvatarPose.GripAlongBarrel - transform.up * AvatarPose.GripBelowAxis;
        gripInHand = rightHand.InverseTransformPoint(gripWorld);
        vacInRightHand = true;

        if (!vacRigLogged)
        {
            vacRigLogged = true;
            // Each part's visible centre from the right hand in vac axes (along barrel, up, side), metres.
            var frame = Quaternion.Inverse(Quaternion.LookRotation(axis.normalized, transform.up));
            var parts = string.Join(",", vacParts.Where(t => t && t.GetComponent<Renderer>()).Take(40).Select(t =>
            {
                var local = frame * (VisibleCenter(t) - rightHand.position);
                return $"{t.name}:{local.z:F2}/{local.y:F2}/{local.x:F2}";
            }));
            Logger.LogDiagnostic("player", "vac-rig",
                $"barrel={(barrel ? "yes" : "no")}; grip={(grip ? grip!.name : "<fallback>")}; parts={parts}");
        }

        rightShoulder.localRotation = shoulderRotation;
    }

    private static Vector3 VisibleCenter(Transform part)
    {
        var renderer = part.GetComponent<Renderer>();
        return renderer ? renderer!.bounds.center : part.position;
    }

    private void PlaceLeftHandOnVac(Transform body, float warmup)
    {
        if (!leftArmUpper || !leftArmLower || !leftHand || !rightHand || !vacInRightHand)
            return;

        var vacFrame = rightHand.rotation * Quaternion.LookRotation(vacAxisInHand, vacUpInHand);
        var gripTarget = rightHand.TransformPoint(gripInHand) +
                         vacFrame * new Vector3(AvatarPose.GripSide, AvatarPose.GripUp, AvatarPose.GripAlong);
        var release = WarmupRelease(warmup);
        var target = release > 0f ? Vector3.Lerp(gripTarget, WarmupHandTarget(body, warmup), release) : gripTarget;
        var hint = leftArmLower!.position - body.right * AvatarPose.LeftElbowOut - body.up * AvatarPose.LeftElbowDown;
        SolveTwoBoneIk(leftArmUpper!, leftArmLower, leftHand!, target, hint);
        // The IK places the wrist; the fist is ahead of it. Solve again so the palm, not the wrist, sits
        // on the grip (only while holding; the warm-up targets the wrist).
        var palm = leftFingerBones.Length > 1 ? leftFingerBones[1][0] : null;
        if (palm && release < 1f)
        {
            var palmOffset = (palm!.position - leftHand!.position) * AvatarPose.PalmShare;
            var holdTarget = Vector3.Lerp(gripTarget - palmOffset, target, release);
            SolveTwoBoneIk(leftArmUpper!, leftArmLower, leftHand!, holdTarget, hint);
        }

        var wristTwist = release * Mathf.Sin(Mathf.Max(warmup, 0f) * Mathf.PI * 6f) * 25f;
        ApplyLeftFingers(LeftFingerCurl(warmup, release), wristTwist);
    }

    /// <summary>Analytic two-bone IK: bends upper/lower so the end bone reaches the target, elbow toward the hint.</summary>
    private static void SolveTwoBoneIk(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 hint)
    {
        var a = upper.position;
        var upperLength = Vector3.Distance(a, lower.position);
        var lowerLength = Vector3.Distance(lower.position, end.position);
        if (upperLength < 1e-4f || lowerLength < 1e-4f)
            return;

        var toTarget = target - a;
        var distance = Mathf.Clamp(toTarget.magnitude, 1e-3f, (upperLength + lowerLength) * 0.999f);
        var direction = toTarget.normalized;

        var bendNormal = Vector3.Cross(direction, hint - a);
        if (bendNormal.sqrMagnitude < 1e-8f)
            bendNormal = Vector3.Cross(direction, Vector3.up);
        bendNormal.Normalize();

        var cosAngle = Mathf.Clamp((upperLength * upperLength + distance * distance - lowerLength * lowerLength) /
                                   (2f * upperLength * distance), -1f, 1f);
        var angle = Mathf.Acos(cosAngle) * Mathf.Rad2Deg;
        var elbow = a + Quaternion.AngleAxis(-angle, bendNormal) * direction * upperLength;
        var alternative = a + Quaternion.AngleAxis(angle, bendNormal) * direction * upperLength;
        if ((alternative - hint).sqrMagnitude < (elbow - hint).sqrMagnitude)
            elbow = alternative;

        upper.rotation = Quaternion.FromToRotation(lower.position - a, elbow - a) * upper.rotation;
        lower.rotation = Quaternion.FromToRotation(end.position - lower.position, a + direction * distance - lower.position) *
                         lower.rotation;
    }
}

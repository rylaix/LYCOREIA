// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Game.Avatars;

/// <summary>
/// Procedural push kick on one humanoid body: a smooth spline through <see cref="KickPose"/> key poses
/// (wind-up, knee up, drive out with the hips, contact recoil, re-chamber, land) with springs on top so the body
/// lags, swings through and settles. The left leg kicks (the vac is held on the right); the standing leg only
/// absorbs the hip drop so its foot stays planted. Used by remote avatars and by the local first-person legs.
/// Plain class (not an Il2Cpp-injected component): call <see cref="UpdateDynamics"/> before any upper-body pose
/// and <see cref="Animate"/> after it, both from LateUpdate (after the animator).
/// </summary>
internal sealed class KickRig
{
    /// <summary>Second-order dynamics (spring-damper), semi-implicit and stable at any frame time.</summary>
    private struct Spring
    {
        internal Vector3 Value;
        private Vector3 velocity;

        internal void Reset(Vector3 value)
        {
            Value = value;
            velocity = Vector3.zero;
        }

        internal void Step(Vector3 target, float dt, float frequency, float damping)
        {
            if (dt <= 0f)
                return;
            var w = 2f * Mathf.PI * frequency;
            var k1 = damping / (Mathf.PI * frequency);
            var k2 = 1f / (w * w);
            var stableK2 = Mathf.Max(k2, dt * dt / 2f + dt * k1 / 2f, dt * k1);
            Value += dt * velocity;
            velocity += dt * (target - Value - k1 * velocity) / stableK2;
        }
    }

#if LYCOREIA_TEST
    // Test rig: play every kick slower (0.1 = ten times slower) to inspect the motion.
    internal static float TestKickSpeed = 1f;
    private static float nextTraceAt;
#endif

    private readonly Transform body;
    private readonly Transform? kickUpperLeg;
    private readonly Transform? kickLowerLeg;
    private readonly Transform? kickFoot;
    private readonly Transform? standUpperLeg;
    private readonly Transform? standLowerLeg;
    private readonly Transform? standFoot;
    private readonly Transform? hips;
    private readonly Vector3 kickHingeLocal;   // knee hinge axis in the kicking thigh's local space (standing pose)
    private readonly float strikeLift;
    private readonly float speed;

    private float kickTime = -1f;     // seconds into the current kick, -1 when idle
    private Spring footSpring;        // body-space offset body root -> foot (x right, y up, z forward); the root
                                      // does not move with the hip drop, so a planted foot stays planted
    private Spring leanSpring;        // x only
    private Spring twistSpring;       // x only
    private Spring hipSpring;         // x = drop (up), y = thrust (forward)
    private Vector3 restFoot;         // animator foot relative to the hip, body space, this frame
    private Vector3 hipFromRoot;      // animator hip relative to the body root (before the hip drop)
    private Vector3 standFootWorld;   // standing foot before the hip drop, kept planted
    private Vector3 hipsWritten;
    private Vector3 hipsOffsetLocal;
    private bool hipsMoved;

    /// <param name="strikeLift">Extra strike height in leg lengths (the first-person legs raise the foot into view).</param>
    /// <param name="speed">Playback speed of the whole kick, springs included (first person plays it snappier).</param>
    internal KickRig(Animator animator, Transform body, float strikeLift = 0f, float speed = 1f)
    {
        this.body = body;
        this.strikeLift = strikeLift;
        this.speed = speed;
        kickUpperLeg = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
        kickLowerLeg = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
        kickFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        standUpperLeg = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
        standLowerLeg = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
        standFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
        hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        // Taken from the animator's standing pose: the knee hinge is the body's side axis.
        if (kickUpperLeg)
            kickHingeLocal = kickUpperLeg!.InverseTransformDirection(body.right);
    }

    private bool Valid => kickUpperLeg && kickLowerLeg && kickFoot;

    internal bool IsKicking => kickTime >= 0f;

    /// <summary>Seconds into the kick on the <see cref="KickPose"/> timeline (-1 when idle).</summary>
    internal float KickTime => kickTime;

    private float Weight => 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(KickPose.TFadeStart, KickPose.TEnd, kickTime));

    /// <summary>Degrees the torso leans back (x) and twists (y) this frame.</summary>
    internal Vector2 Torso => IsKicking ? new Vector2(leanSpring.Value.x, twistSpring.Value.x) * Weight : Vector2.zero;

    internal void Play()
    {
        if (!Valid)
            return;
        if (kickTime < 0f)
        {
            restFoot = ToBody(kickFoot!.position - kickUpperLeg!.position);
            hipFromRoot = ToBody(kickUpperLeg.position - body.position);
            footSpring.Reset(hipFromRoot + restFoot);
            leanSpring.Reset(Vector3.zero);
            twistSpring.Reset(Vector3.zero);
            hipSpring.Reset(Vector3.zero);
        }
        kickTime = 0f; // a kick during a kick restarts from the current spring state, no pop
    }

    private Vector3 ToBody(Vector3 world) =>
        new(Vector3.Dot(world, body.right), Vector3.Dot(world, body.up), Vector3.Dot(world, body.forward));

    private Vector3 FromBody(Vector3 local) => body.right * local.x + body.up * local.y + body.forward * local.z;

    /// <summary>
    /// Before the upper-body pose: advances the springs, then drops/raises the hips so everything above rides
    /// the motion. The animator rewrites the hips every frame; if it skipped one, the previous offset is undone
    /// first so it never accumulates.
    /// </summary>
    internal void UpdateDynamics()
    {
        if (!Valid)
            return;

        if (hipsMoved && hips && (hips!.localPosition - hipsWritten).sqrMagnitude < 1e-10f)
            hips.localPosition -= hipsOffsetLocal;
        hipsMoved = false;

        restFoot = ToBody(kickFoot!.position - kickUpperLeg!.position);
        hipFromRoot = ToBody(kickUpperLeg.position - body.position);
        if (!IsKicking)
            return;

        var dt = UnityEngine.Time.deltaTime * speed; // the springs run on the same clock, so the motion scales whole
#if LYCOREIA_TEST
        dt *= TestKickSpeed;
#endif
        kickTime += dt;
        var (foot, pose) = KickPose.Sample(kickTime, restFoot, strikeLift);
        footSpring.Step(hipFromRoot + foot, dt, KickPose.FootFrequency, KickPose.FootDamping);
        leanSpring.Step(new Vector3(pose.x, 0f, 0f), dt, KickPose.LeanFrequency, KickPose.LeanDamping);
        twistSpring.Step(new Vector3(pose.y, 0f, 0f), dt, KickPose.TwistFrequency, KickPose.TwistDamping);
        hipSpring.Step(new Vector3(pose.z, pose.w, 0f), dt, KickPose.DropFrequency, KickPose.DropDamping);

        if (kickTime > KickPose.TEnd)
        {
            kickTime = -1f;
            return;
        }

        if (hips && standFoot)
        {
            standFootWorld = standFoot!.position;
            var offset = (body.up * hipSpring.Value.x + body.forward * hipSpring.Value.y) * Weight;
            hipsOffsetLocal = hips!.parent ? hips.parent.InverseTransformVector(offset) : offset;
            hips.localPosition += hipsOffsetLocal;
            hipsWritten = hips.localPosition;
            hipsMoved = true;
        }
    }

    /// <summary>
    /// The two-bone IK aims the thigh with a shortest-arc turn, which also rolls it around its own axis when the
    /// leg swings far (the knee then bends sideways). Roll the thigh back so its knee hinge lies across the body
    /// again, then re-aim the shin at the target: the knee always bends straight forward.
    /// </summary>
    private void KeepKneeHinge(Transform upper, Transform lower, Transform end, Vector3 target)
    {
        var thigh = lower.position - upper.position;
        if (thigh.sqrMagnitude < 1e-6f || kickHingeLocal.sqrMagnitude < 0.5f)
            return;
        var axis = thigh.normalized;
        var current = Vector3.ProjectOnPlane(upper.TransformDirection(kickHingeLocal), axis);
        var wanted = Vector3.ProjectOnPlane(body.right, axis);
        if (current.sqrMagnitude < 1e-6f || wanted.sqrMagnitude < 1e-6f)
            return;
        upper.rotation = Quaternion.AngleAxis(Vector3.SignedAngle(current, wanted, axis), axis) * upper.rotation;
        lower.rotation = Quaternion.FromToRotation(end.position - lower.position, target - lower.position) * lower.rotation;
    }

    /// <summary>
    /// After the upper-body pose: the kicking leg follows its spring, the standing leg stays on its spot, and
    /// both hand back to the animator's pose over the last moments so the kick never ends with a pop.
    /// </summary>
    internal void Animate()
    {
        if (!IsKicking || !Valid)
            return;

        var hip = kickUpperLeg!.position;
        var leg = Mathf.Max(0.3f, restFoot.magnitude);
        var target = body.position + FromBody(footSpring.Value);
        var local = footSpring.Value - hipFromRoot; // relative to the undropped hip
        var weight = Weight;

        var upperFrom = kickUpperLeg.rotation;
        var lowerFrom = kickLowerLeg!.rotation;
        var footFrom = kickFoot!.rotation;

        // The knee always bends to the forward-and-up side of the hip-to-foot line: forward for a hanging leg, up
        // for a level one, up-and-back for a high one. A fixed hint point would end up below a high kick's leg
        // line (the knee then folds down), or along a straight leg (the bend flips between above and below).
        var extend = Mathf.Clamp01((local.z / leg - 0.3f) / 0.7f); // 0 at the chamber, 1 at full extension
        var legLine = target - hip;
        var kneeSide = legLine.sqrMagnitude > 1e-6f
            ? Vector3.ProjectOnPlane(body.forward + body.up, legLine.normalized)
            : body.forward;
        if (kneeSide.sqrMagnitude < 1e-6f)
            kneeSide = body.forward;
        var kneeHint = hip + legLine * 0.5f + kneeSide.normalized * leg;
        RemoteAvatar.SolveTwoBoneIk(kickUpperLeg, kickLowerLeg, kickFoot, target, kneeHint);
        KeepKneeHinge(kickUpperLeg, kickLowerLeg, kickFoot, target);
        // The foot rides with the shin (its animator angle to the shin is kept), so with the leg out the sole
        // faces forward on its own; only a little extra toe-up is added.
        kickFoot.rotation = Quaternion.AngleAxis(-KickPose.FootFlex * extend, body.right) * kickFoot.rotation;
        if (weight < 1f)
        {
            kickUpperLeg.rotation = Quaternion.Slerp(upperFrom, kickUpperLeg.rotation, weight);
            kickLowerLeg.rotation = Quaternion.Slerp(lowerFrom, kickLowerLeg.rotation, weight);
            kickFoot.rotation = Quaternion.Slerp(footFrom, kickFoot.rotation, weight);
        }

#if LYCOREIA_TEST
        if (UnityEngine.Time.unscaledTime >= nextTraceAt)
        {
            nextTraceAt = UnityEngine.Time.unscaledTime + 0.25f;
            OwnedLog.LogMessage($"[test-kick] t={kickTime:F2} target={ToBody(target - hip).ToString("F2")} " +
                                $"knee={ToBody(kickLowerLeg.position - hip).ToString("F2")} foot={ToBody(kickFoot.position - hip).ToString("F2")} " +
                                $"rest={restFoot.ToString("F2")}");
        }
#endif
        // Standing leg: only absorb the hip drop/thrust so the foot stays planted. The bend direction is the
        // animator's own knee (pushed out from the hip-foot line), so the leg keeps its natural stance shape, and
        // the correction fades in and out with the kick instead of snapping to a new pose.
        if (hipsMoved && standUpperLeg && standLowerLeg && standFoot)
        {
            var standUpperFrom = standUpperLeg!.rotation;
            var standLowerFrom = standLowerLeg!.rotation;
            var standRotation = standFoot!.rotation;
            var mid = (standUpperLeg.position + standFoot.position) * 0.5f;
            var bend = standLowerLeg.position - mid;
            if (bend.sqrMagnitude < 1e-6f)
                bend = body.forward * 0.05f;
            RemoteAvatar.SolveTwoBoneIk(standUpperLeg, standLowerLeg, standFoot, standFootWorld,
                standLowerLeg.position + bend.normalized * leg);
            standUpperLeg.rotation = Quaternion.Slerp(standUpperFrom, standUpperLeg.rotation, weight);
            standLowerLeg.rotation = Quaternion.Slerp(standLowerFrom, standLowerLeg.rotation, weight);
            standFoot.rotation = standRotation;
        }
    }
}

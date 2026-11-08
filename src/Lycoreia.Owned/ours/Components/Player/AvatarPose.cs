// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Game.Avatars;

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
    /// <summary>Left fore-grip under the vac front: share of FingerGripCurl for the wrapping fingers and for the index.</summary>
    public static float ForeGripCurlShare = 0.75f, ForeGripIndexShare = 0.4f;
    public static float FingerCurlSign = 1f;
    public static float ThumbCurlSign = 1f;
    // Fraction of the wrist-to-knuckles vector that puts the palm centre (not the wrist) on the grip.
    public static float PalmShare = 0.6f;
    // Warm-up stretch target from the left shoulder: outward (to the avatar's left), forward, down.
    public static float WarmupOutward = 0.45f;
    public static float WarmupForward = 0.18f;
    public static float WarmupDown = 0.10f;
}


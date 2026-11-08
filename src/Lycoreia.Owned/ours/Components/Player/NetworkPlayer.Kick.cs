// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Game.Avatars;

/// <summary>Shape and feel of the remote avatar's front kick (V). Tunable like <see cref="AvatarPose"/>.</summary>
internal static class KickPose
{
    /// <summary>
    /// One key pose of the push kick. Foot = hip-to-foot offset in leg lengths (x outward to the kicking side,
    /// y up, z forward; NaN x = the animator's own foot). Lean back / twist in degrees, hip drop and hip
    /// thrust in metres. The keys are joined by a smooth spline; springs add weight on top.
    /// </summary>
    internal readonly record struct Key(float Time, Vector3 Foot, float Lean, float Twist, float Drop, float Thrust);

    private static readonly Vector3 Rest = new(float.NaN, 0f, 0f);

    internal static readonly Key[] Keys =
    {
        new(0.00f, Rest,                          0f,   0f,  0.00f, 0.00f),
        new(0.16f, Rest,                         -7f,  -4f, -0.06f, -0.02f), // wind-up: sink and lean in
        new(0.34f, new Vector3(0f, -0.40f, 0.38f), 10f,   5f, -0.02f, 0.00f), // knee up, thigh about level
        new(0.47f, new Vector3(0.03f, 0.08f, 0.98f), 24f,  8f,  0.04f, 0.10f), // drive out at belly height, hips through
        new(0.58f, new Vector3(0.03f, 0.04f, 0.88f), 20f,  6f,  0.03f, 0.07f), // contact recoil
        new(0.76f, new Vector3(0f, -0.45f, 0.36f), 7f,   3f,  0.00f, 0.00f), // re-chamber
        new(0.96f, Rest,                         -4f,   0f, -0.06f, 0.02f), // land: weight forward, absorb
        new(1.18f, Rest,                          0f,   0f,  0.00f, 0.00f),
    };

    public static float TFadeStart = 1.18f;     // the leg hands back to the animator over this window
    public static float TEnd = 1.45f;
    public static float FootFlex = 12f;          // extra toes-up on top of the foot following the shin

    /// <summary>
    /// Spline through the key poses at time t: foot (body space, metres; restFoot = the animator's foot) and
    /// body (lean, twist, drop, thrust). Catmull-Rom keeps the target continuous, so nothing snaps key to key.
    /// </summary>
    internal static (Vector3 Foot, Vector4 Body) Sample(float t, Vector3 restFoot, float strikeLift = 0f)
    {
        var leg = Mathf.Max(0.3f, restFoot.magnitude);
        var segment = 0;
        while (segment < Keys.Length - 2 && t >= Keys[segment + 1].Time)
            segment++;
        var k0 = Keys[Mathf.Max(segment - 1, 0)];
        var k1 = Keys[segment];
        var k2 = Keys[segment + 1];
        var k3 = Keys[Mathf.Min(segment + 2, Keys.Length - 1)];
        var u = Mathf.Clamp01((t - k1.Time) / Mathf.Max(1e-4f, k2.Time - k1.Time));

        return (CatmullRom(FootOf(k0, restFoot, leg, strikeLift), FootOf(k1, restFoot, leg, strikeLift),
                    FootOf(k2, restFoot, leg, strikeLift), FootOf(k3, restFoot, leg, strikeLift), u),
            CatmullRom(BodyOf(k0), BodyOf(k1), BodyOf(k2), BodyOf(k3), u));
    }

    // The left leg kicks, so "outward" is -right. Sideways offsets are measured from the foot's own stance
    // line (the foot stands well outside the hip), so the leg travels straight forward instead of
    // swinging in toward the middle.
    private static Vector3 FootOf(Key key, Vector3 restFoot, float leg, float strikeLift) => float.IsNaN(key.Foot.x)
        ? restFoot
        : new Vector3(restFoot.x * StanceLine - key.Foot.x * leg,
            (key.Foot.y + strikeLift * Mathf.Clamp01((key.Foot.z - 0.3f) / 0.7f)) * leg, key.Foot.z * leg);

    public static float StanceLine = 0.75f;      // share of the stance width the kicking foot keeps

    private static Vector4 BodyOf(Key key) => new(key.Lean, key.Twist, key.Drop, key.Thrust);

    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u)
        => 0.5f * (2f * p1 + (p2 - p0) * u + (2f * p0 - 5f * p1 + 4f * p2 - p3) * (u * u) +
                   (3f * p1 - p0 - 3f * p2 + p3) * (u * u * u));

    private static Vector4 CatmullRom(Vector4 p0, Vector4 p1, Vector4 p2, Vector4 p3, float u)
        => 0.5f * (2f * p1 + (p2 - p0) * u + (2f * p0 - 5f * p1 + 4f * p2 - p3) * (u * u) +
                   (3f * p1 - p0 - 3f * p2 + p3) * (u * u * u));

    // Springs on top of the spline: frequency (Hz) and damping (below 1 swings past and settles).
    public static float FootFrequency = 4.5f;
    public static float FootDamping = 0.78f;
    public static float LeanFrequency = 2.3f;
    public static float LeanDamping = 0.5f;
    public static float TwistFrequency = 2.0f;
    public static float TwistDamping = 0.5f;
    public static float DropFrequency = 3.0f;
    public static float DropDamping = 0.42f;
}

/// <summary>The remote avatar's kick: a <see cref="KickRig"/> on its own body, driven from LateUpdate.</summary>
public sealed partial class RemoteAvatar
{
    private KickRig? kickRig;

    /// <summary>Starts the kick animation (another player pressed V).</summary>
    internal void PlayKick()
    {
        if (IsLocal || !hasAnimationController)
            return;
        kickRig ??= new KickRig(animator, transform);
        kickRig.Play();
    }

    /// <summary>Degrees the torso leans back (x) and twists (y) this frame; read by the torso pose.</summary>
    private Vector2 KickTorso() => kickRig?.Torso ?? Vector2.zero;

    private void UpdateKickDynamics()
    {
        if (!IsLocal && hasAnimationController)
            kickRig?.UpdateDynamics();
    }

    private void AnimateKick()
    {
        if (!IsLocal && hasAnimationController)
            kickRig?.Animate();
    }
}

/// <summary>Ping gesture: the remote avatar points its left arm at the spot it pinged.</summary>
public sealed partial class RemoteAvatar
{
    private const float PointIn = 0.3f;
    private const float PointHold = 2f;
    private const float PointOut = 0.45f;
    private Vector3 pointTarget;
    private float pointStartedAt = -100f;

    internal void PlayPoint(Vector3 target)
    {
        if (IsLocal || !hasAnimationController)
            return;
        pointTarget = target;
        // Already pointing: move the arm to the new spot without dropping it first.
        var now = UnityEngine.Time.time;
        pointStartedAt = PointWeight() > 0.5f ? now - PointIn : now;
    }

    private float PointWeight()
    {
        var t = UnityEngine.Time.time - pointStartedAt;
        if (t < 0f || t > PointIn + PointHold + PointOut)
            return 0f;
        var w = t < PointIn ? t / PointIn : t > PointIn + PointHold ? 1f - (t - PointIn - PointHold) / PointOut : 1f;
        w = Mathf.Clamp01(w);
        return w * w * (3f - 2f * w);
    }
}

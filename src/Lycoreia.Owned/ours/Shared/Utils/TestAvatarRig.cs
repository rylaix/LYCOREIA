// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
#if LYCOREIA_TEST
using Lycoreia.Game.Avatars;
using Lycoreia.Owned.Shared.Managers;

namespace Lycoreia.Owned.Shared.Utils;

/// <summary>
/// Test-only visual rig (test-harness builds): spawns a remote-style avatar in front of the local player
/// through the normal remote-player path, drives its look pitch from a control file and renders it from
/// a debug camera, so arm/torso poses can be screenshotted without a second game.
/// Enabled by LYCOREIA_TEST_AVATAR=&lt;control file&gt;. Control file lines: pitch=&lt;deg, + = down&gt;,
/// view=front|side|back|left|threequarter|top, dist=&lt;m&gt;, moving=0|1, vac=0|1 (remote vac cone on/off).
/// Also writes UserData/Lycoreia/test-animclips.txt (all loaded AnimationClips) once.
/// </summary>
internal static class TestAvatarRig
{
    private const string AvatarId = "TEST_AVATAR";
    private static readonly string? ControlPath = Environment.GetEnvironmentVariable("LYCOREIA_TEST_AVATAR");
    internal static bool Enabled => !string.IsNullOrEmpty(ControlPath);

    private static RemoteAvatar? avatar;
    private static int airborne;
    private static Camera? debugCamera;
    private static Vector3 anchor;
    private static float anchorYaw;
    private static float nextReadAt;
    private static DateTime lastWrite;
    private static bool clipsDumped;

    private static float pitch;
    private static string view = "front";
    private static float distance = 2.6f;
    private static bool moving;
    private static bool vacOn;
    private static float baseYaw;
    private static uint vacPoseSequence;
    private static float focusHeight = 1.05f;
    private static float orbitOverride = float.NaN;
    private static string focusBone = string.Empty;

    internal static void Pump()
    {
        if (string.IsNullOrEmpty(ControlPath))
            return;

        try
        {
            var scene = SceneContext.Instance;
            if (!scene || !scene.player || !Lycoreia.Game.GameState.Ready || PlayerPrefab == null)
                return;

            if (avatar == null)
                Spawn(scene);
            ReadControl();
            Drive();
            if (!clipsDumped)
                DumpClips();
        }
        catch (Exception ex)
        {
            OwnedLog.LogWarning($"[test-avatar] {ex}");
        }
    }

    private static void Spawn(SceneContext scene)
    {
        var player = scene.player.transform;
        var flatForward = Vector3.ProjectOnPlane(player.forward, Vector3.up).normalized;
        anchor = player.position + flatForward * 4f;
        anchorYaw = Quaternion.LookRotation(flatForward).eulerAngles.y;

        avatar = RemoteAvatars.Current?.Ensure(AvatarId, "Test Avatar");   // the normal remote-avatar path
        if (avatar == null)
            return;

        var cameraObject = new GameObject("LycoreiaTestCamera");
        debugCamera = cameraObject.AddComponent<Camera>();
        debugCamera.depth = 90f;
        debugCamera.nearClipPlane = 0.05f;
        debugCamera.fieldOfView = 50f;
        OwnedLog.LogMessage($"[test-avatar] spawned at {anchor} yaw={anchorYaw:F0}");
    }

    private static void ReadControl()
    {
        var now = UnityEngine.Time.unscaledTime;
        if (now < nextReadAt || !File.Exists(ControlPath))
            return;
        nextReadAt = now + 0.25f;

        var write = File.GetLastWriteTimeUtc(ControlPath!);
        if (write == lastWrite)
            return;
        lastWrite = write;

        foreach (var line in File.ReadAllLines(ControlPath!))
        {
            var parts = line.Split('=', 2);
            if (parts.Length != 2)
                continue;
            var value = parts[1].Trim();
            switch (parts[0].Trim())
            {
                case "pitch": float.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out pitch); break;
                case "view": view = value; break;
                case "dist": float.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out distance); break;
                case "moving": moving = value == "1"; break;
                case "here" when avatar && value == "1":
                    var scene = SceneContext.Instance;
                    var flat = Vector3.ProjectOnPlane(scene.player.transform.forward, Vector3.up).normalized;
                    anchor = scene.player.transform.position + flat * 4f;
                    anchorYaw = Quaternion.LookRotation(flat).eulerAngles.y;
                    baseYaw = anchorYaw;
                    OwnedLog.LogMessage($"[test-avatar] moved to {anchor} yaw={anchorYaw:F0}");
                    break;
                case "away" when avatar && value == "1":
                {
                    // Park the test avatar 30 m ahead (keeps it out of first-person kick tests).
                    var player = SceneContext.Instance.player;
                    var ahead = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up).normalized;
                    anchor = player.transform.position + ahead * 30f;
                    OwnedLog.LogMessage($"[test-avatar] parked at {anchor}");
                    break;
                }
                case "face" when avatar:
                    if (float.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var face))
                        anchorYaw = baseYaw + face;
                    break;
                case "foliage":
                    float.TryParse(value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out Lycoreia.Owned.Components.FX.RemoteVacuumFoliage.TestStrengthScale);
                    break;
                case "vac":
                    vacOn = value == "1";
                    Lycoreia.Owned.Components.FX.RemoteVacuumSuctionFX.SetSuction(AvatarId, value == "1", out var vacResult);
                    OwnedLog.LogMessage($"[test-avatar] vac={value}: {vacResult}");
                    break;
                case "focus": focusBone = value == "body" ? string.Empty : value; break;
                case "focusY": float.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out focusHeight); break;
                case "yaw": orbitOverride = value == "off" ? float.NaN :
                    float.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "warmup": float.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out RemoteAvatar.TestWarmup); break;
                case "kick" when avatar != null && value != "0":
                    avatar!.PlayKick(); // any new value replays (e.g. kick=1, kick=2, ...)
                    break;
                case "air": int.TryParse(value, out airborne); break;
                case "fplook" when SceneContext.Instance && SceneContext.Instance.Player:
                {
                    // First-person look pitch in degrees down (0 = level, 85 = at the feet), keeping the yaw.
                    var player = SceneContext.Instance.Player;
                    var down = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture) * Mathf.Deg2Rad;
                    var lookFlat = Vector3.ProjectOnPlane(Camera.main ? Camera.main.transform.forward : player.transform.forward, Vector3.up).normalized;
                    var eye = Camera.main ? Camera.main.transform.position : player.transform.position + Vector3.up * 1.8f;
                    AimCameraAt(player, eye + lookFlat * Mathf.Cos(down) * 5f - Vector3.up * Mathf.Sin(down) * 5f);
                    break;
                }
                case "ping" when value != "0":
                    PlayerPings.TryPing(); // local ping where the camera looks; any new value repeats
                    break;
                case "avping" when value != "0" && SceneContext.Instance && SceneContext.Instance.Player:
                {
                    // The test avatar pings the ground <value> m ahead of the local player (a "friend's" ping).
                    var player = SceneContext.Instance.Player.transform;
                    var ahead = float.TryParse(value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var metres) ? metres : 8f;
                    // Aim from chest height at the ground <ahead> m in front (stays under roofs).
                    var pingEye = Camera.main ? Camera.main.transform : player;
                    var pingFlat = Vector3.ProjectOnPlane(pingEye.forward, Vector3.up).normalized;
                    var from = pingEye.position;
                    var aim = (player.position + pingFlat * ahead - from).normalized;
                    if (Physics.Raycast(from, aim, out var hit, 80f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                        PlayerPings.Show(AvatarId, hit.point, hit.normal,
                            Lycoreia.Game.SceneGroups.IdOf(SystemContext.Instance.SceneLoader._currentSceneGroup));
                    break;
                }
                case "fpkick" when value != "0":
                    FirstPersonLegs.Play(); // any new value replays
                    break;
                case "kickspeed": KickRig.TestKickSpeed =
                    float.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "look": RemoteAvatar.TestLookAround = value == "off" ? float.NaN :
                    float.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                default:
                    if (parts[0].StartsWith("tune.", StringComparison.Ordinal) &&
                        float.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var number))
                    {
                        var field = typeof(AvatarPose).GetField(parts[0][5..].Trim()) ??
                                    typeof(KickPose).GetField(parts[0][5..].Trim());
                        if (field != null)
                            field.SetValue(null, number);
                        else
                            OwnedLog.LogWarning($"[test-avatar] unknown tuning {parts[0]}");
                    }
                    break;
            }
        }

        OwnedLog.LogMessage($"[test-avatar] pose pitch={pitch} view={view} dist={distance} moving={moving}");
    }

    private static void Drive()
    {
        if (avatar == null)
            return;

        // Camera eulerAngles.x convention: down = positive, up = 270..360.
        var lookY = pitch >= 0f ? pitch : 360f + pitch;
        avatar.Push(new Lycoreia.Net.Messages.PlayerUpdate   // a synthetic player update
        {
            PlayerId = AvatarId, Position = anchor.ToWire(), BodyYaw = anchorYaw, LookPitch = lookY,
            SceneGroup = Lycoreia.Game.SceneGroups.IdOf(SystemContext.Instance.SceneLoader._currentSceneGroup),
            Moving = moving, Airborne = airborne, SpeedZ = moving ? 6f : 0f, MoveZ = moving ? 1f : 0f
        }, UnityEngine.Time.unscaledTimeAsDouble);

        if (vacOn)
        {
            // Stand-in for the network pose: body-relative nozzle aimed along the look pitch.
            Lycoreia.Owned.Components.FX.RemoteVacuumSuctionFX.ApplySuctionPose(AvatarId, unchecked(++vacPoseSequence),
                new Vector3(0.25f, 1.1f, 0.6f), Quaternion.Euler(90f + pitch, 0f, 0f), false); // aim = local up
        }

        if (!debugCamera)
            return;
        // view=fp: back to the player's own first-person camera (to see the local first-person kick legs).
        debugCamera!.enabled = view != "fp";
        if (view == "fp")
            return;
        // Same layers as the player camera (hides the environment-interaction-only particles).
        if (Camera.main && Camera.main != debugCamera)
            debugCamera.cullingMask = Camera.main.cullingMask;

        var avatarRotation = Quaternion.Euler(0f, anchorYaw, 0f);
        // view=local*: orbit the local player instead of the test avatar (local, localside, localback, localleft).
        var localView = view.StartsWith("local", StringComparison.Ordinal) && SceneContext.Instance && SceneContext.Instance.player;
        if (localView)
            avatarRotation = Quaternion.Euler(0f, SceneContext.Instance.player.transform.eulerAngles.y, 0f);
        var orbitYaw = view switch
        {
            "side" or "localside" => 90f,
            "back" or "localback" => 180f,
            "left" or "localleft" => -90f,
            "threequarter" => 40f,
            _ => 0f
        };
        if (!float.IsNaN(orbitOverride))
            orbitYaw = orbitOverride;
        var lift = view == "top" ? 1.5f : 0.2f;
        var offset = Quaternion.Euler(0f, orbitYaw, 0f) * new Vector3(0f, 0f, distance);
        var focus = (localView ? SceneContext.Instance.player.transform.position : avatar!.transform.position) +
                    Vector3.up * focusHeight;
        if (focusBone == "lefthand" || focusBone == "righthand")
        {
            var bone = avatar.GetComponentInChildren<Animator>()?.GetBoneTransform(
                focusBone == "lefthand" ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            if (bone)
                focus = bone!.position;
        }
        debugCamera!.transform.position = focus + avatarRotation * offset + Vector3.up * lift;
        debugCamera.transform.LookAt(focus);
    }

    private static void DumpClips()
    {
        clipsDumped = true;
        var clips = Resources.FindObjectsOfTypeAll<AnimationClip>();
        var lines = clips.Where(c => c)
            .Select(c => $"{c.name}\thuman={c.humanMotion}\tlen={c.length:F2}")
            .Distinct()
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        File.WriteAllLines(Path.Combine(ModPaths.DataDirectory, "test-animclips.txt"), lines);
        OwnedLog.LogMessage($"[test-avatar] dumped {lines.Length} animation clips");
    }

    // Stand-in (the gameplay bot that owned AimCameraAt is not included): turns the player to the target's yaw and
    // the camera to its pitch.
    private static void AimCameraAt(GameObject player, Vector3 target)
    {
        var cam = Camera.main; if (!player || !cam) return;
        var dir = target - cam.transform.position; if (dir.sqrMagnitude < 1e-6f) return;
        var look = Quaternion.LookRotation(dir.normalized);
        player.transform.rotation = Quaternion.Euler(0f, look.eulerAngles.y, 0f);
        cam.transform.rotation = look;
    }
}
#endif

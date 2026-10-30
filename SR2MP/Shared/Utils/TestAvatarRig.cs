#if SR2MP_TEST
using SR2MP.Components.Player;
using SR2MP.Shared.Managers;

namespace SR2MP.Shared.Utils;

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

    private static NetworkPlayer? avatar;
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
            if (!scene || !scene.player || !Starlight.ContextShortcuts.inGame || PlayerPrefab == null)
                return;

            if (!avatar)
                Spawn(scene);
            ReadControl();
            Drive();
            if (!clipsDumped)
                DumpClips();
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"[test-avatar] {ex}");
        }
    }

    private static void Spawn(SceneContext scene)
    {
        var player = scene.player.transform;
        var flatForward = Vector3.ProjectOnPlane(player.forward, Vector3.up).normalized;
        anchor = player.position + flatForward * 4f;
        anchorYaw = Quaternion.LookRotation(flatForward).eulerAngles.y;

        var model = PlayerManager.GetPlayer(AvatarId) ?? PlayerManager.AddPlayer(AvatarId);
        model.Username = "Test Avatar";
        var instance = Object.Instantiate(PlayerPrefab).GetComponent<NetworkPlayer>();
        instance.gameObject.SetActive(true);
        instance.ID = AvatarId;
        instance.gameObject.name = AvatarId;
        PlayerObjects[AvatarId] = instance.gameObject;
        avatar = instance;

        var cameraObject = new GameObject("LycoreiaTestCamera");
        debugCamera = cameraObject.AddComponent<Camera>();
        debugCamera.depth = 90f;
        debugCamera.nearClipPlane = 0.05f;
        debugCamera.fieldOfView = 50f;
        SrLogger.LogMessage($"[test-avatar] spawned at {anchor} yaw={anchorYaw:F0}");
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
                    SrLogger.LogMessage($"[test-avatar] moved to {anchor} yaw={anchorYaw:F0}");
                    break;
                case "face" when avatar:
                    if (float.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var face))
                        anchorYaw = baseYaw + face;
                    break;
                case "foliage":
                    float.TryParse(value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out SR2MP.Components.FX.RemoteVacuumFoliage.TestStrengthScale);
                    break;
                case "vac":
                    vacOn = value == "1";
                    SR2MP.Components.FX.RemoteVacuumSuctionFX.SetSuction(AvatarId, value == "1", out var vacResult);
                    SrLogger.LogMessage($"[test-avatar] vac={value}: {vacResult}");
                    break;
                case "focus": focusBone = value == "body" ? string.Empty : value; break;
                case "focusY": float.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out focusHeight); break;
                case "yaw": orbitOverride = value == "off" ? float.NaN :
                    float.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "warmup": float.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out NetworkPlayer.TestWarmup); break;
                case "look": NetworkPlayer.TestLookAround = value == "off" ? float.NaN :
                    float.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                default:
                    if (parts[0].StartsWith("tune.", StringComparison.Ordinal) &&
                        float.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var number))
                    {
                        var field = typeof(AvatarPose).GetField(parts[0][5..].Trim());
                        if (field != null)
                            field.SetValue(null, number);
                        else
                            SrLogger.LogWarning($"[test-avatar] unknown tuning {parts[0]}");
                    }
                    break;
            }
        }

        SrLogger.LogMessage($"[test-avatar] pose pitch={pitch} view={view} dist={distance} moving={moving}");
    }

    private static void Drive()
    {
        var model = PlayerManager.GetPlayer(AvatarId);
        if (model == null || !avatar)
            return;

        // Camera eulerAngles.x convention: down = positive, up = 270..360.
        var lookY = pitch >= 0f ? pitch : 360f + pitch;
        model.Position = anchor;
        model.Rotation = anchorYaw;
        model.LastLookY = lookY;
        model.LookY = lookY;
        model.HasReceivedState = true;
        model.SceneGroup = NetworkSceneManager.GetPersistentID(SystemContext.Instance.SceneLoader._currentSceneGroup);
        model.Moving = moving;
        model.ForwardSpeed = moving ? 6f : 0f;
        model.ForwardMovement = moving ? 1f : 0f;

        if (vacOn)
        {
            // Stand-in for the network pose: body-relative nozzle aimed along the look pitch.
            SR2MP.Components.FX.RemoteVacuumSuctionFX.ApplySuctionPose(AvatarId, unchecked(++vacPoseSequence),
                new Vector3(0.25f, 1.1f, 0.6f), Quaternion.Euler(90f + pitch, 0f, 0f)); // aim = local up
        }

        if (!debugCamera)
            return;
        // Same layers as the player camera (hides the environment-interaction-only particles).
        if (Camera.main && Camera.main != debugCamera)
            debugCamera.cullingMask = Camera.main.cullingMask;

        var avatarRotation = Quaternion.Euler(0f, anchorYaw, 0f);
        var orbitYaw = view switch
        {
            "side" => 90f,
            "back" => 180f,
            "left" => -90f,
            "threequarter" => 40f,
            _ => 0f
        };
        if (!float.IsNaN(orbitOverride))
            orbitYaw = orbitOverride;
        var lift = view == "top" ? 1.5f : 0.2f;
        var offset = Quaternion.Euler(0f, orbitYaw, 0f) * new Vector3(0f, 0f, distance);
        var focus = avatar!.transform.position + Vector3.up * focusHeight;
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
        SrLogger.LogMessage($"[test-avatar] dumped {lines.Length} animation clips");
    }
}
#endif

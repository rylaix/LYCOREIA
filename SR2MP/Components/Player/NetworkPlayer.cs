using Il2CppMonomiPark.SlimeRancher.Player.CharacterController;
using Il2CppMonomiPark.SlimeRancher.Player.PlayerItems;
using Il2CppTMPro;
using JetBrains.Annotations;
using SR2MP.Client.Models;
using SR2MP.Components.FX;
using SR2MP.Components.Utils;
using SR2MP.Shared.Managers;
using Starlight.Storage;
using static SR2MP.Shared.Utils.Timers;

namespace SR2MP.Components.Player;

[InjectIntoIL]
internal partial class NetworkPlayer : MonoBehaviour
{
    private static readonly int HorizontalMovement = Animator.StringToHash("HorizontalMovement");
    private static readonly int ForwardMovement = Animator.StringToHash("ForwardMovement");
    private static readonly int Yaw = Animator.StringToHash("Yaw");
    private static readonly int AirborneState = Animator.StringToHash("AirborneState");
    private static readonly int Moving = Animator.StringToHash("Moving");
    private static readonly int HorizontalSpeed = Animator.StringToHash("HorizontalSpeed");
    private static readonly int ForwardSpeed = Animator.StringToHash("ForwardSpeed");
    private static readonly int Sprinting = Animator.StringToHash("Sprinting");

    // private MeshRenderer[] renderers;
    private Collider collider;
    private Renderer[] remoteRenderers = Array.Empty<Renderer>();
    private bool[] remoteRendererDefaults = Array.Empty<bool>();
    private int[] remoteRendererLayers = Array.Empty<int>();
    private int remoteRendererCameraMask;
    private bool remoteRendererCameraMaskKnown;
    private bool remoteBodyVisible;

    public int previousScene;
    
    public Vector3 previousPosition;
    public Vector3 nextPosition;

    public Vector2 previousRotation;
    public Vector2 nextRotation;

    private float interpolationStart;
    private float interpolationEnd;

    public TextMeshPro UsernamePanel;

    private float transformTimer = PlayerTimer;

    private Animator animator;
    private bool hasAnimationController;

    private RemotePlayer? model;

    public Transform camera;

    public string ID { get; internal set; }

    public bool IsLocal { get; internal set; }

    internal bool RemoteMoving => model?.Moving == true;
    internal bool RemoteSprinting => model?.Sprinting == true;

    internal void BindLocalIdentity(string playerId)
    {
        ID = playerId;
        IsLocal = true;
        model = null;
        previousPosition = nextPosition = transform.position;
        previousRotation = nextRotation = new Vector2(transform.eulerAngles.y, 0f);
        transformTimer = PlayerTimer;

        var controller = GetComponent<SRCharacterController>();
        if (controller && controller._cameraController != null)
            camera = controller._cameraController.transform;

        SetRemoteBodyVisible(true);
    }

    private static TMP_FontAsset GetFont(string fontName) =>
        Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(x => x.name == fontName)!;

    private TMP_FontAsset usernameFont;
    
    public void SetUsername(string username)
    {
        username = username.Trim();

        UsernamePanel = transform.GetChild(1).GetComponent<TextMeshPro>();
        UsernamePanel.text = username;
        UsernamePanel.alignment = TextAlignmentOptions.Center;
        UsernamePanel.fontSize = 3;
        UsernamePanel.font = GetFont("Runsell Type - HemispheresCaps2 (Latin)");
        usernameFont = UsernamePanel.font;

        if (!UsernamePanel.GetComponent<TransformLookAtCamera>())
        {
            UsernamePanel.gameObject.AddComponent<TransformLookAtCamera>().TargetTransform =
                UsernamePanel.transform;
        }
        
        var nameLabel = GetCompassLabel(0);
        if (nameLabel != null)
            nameLabel.SetText(username);
    }

    [UsedImplicitly]
    public void Awake()
    {
        PlayerManager.OnPlayerGadgetUpdated += OnGadgetUpdate;
        if (transform.GetComponents<NetworkPlayer>().Length > 1)
        {
            Destroy(this);
            return;
        }

        animator = GetComponentInChildren<Animator>();

        if (animator == null)
            SrLogger.LogWarning("NetworkPlayer has no Animator component!");
        
        AwakeGadgetMode();
    }

    public void Start()
    {
        if (IsLocal)
        {
            camera = GetComponent<SRCharacterController>()._cameraController.transform;
            GetComponent<PlayerItemController>()._vacuumItem.AddComponent<NetworkPlayerSound>();
        }
        else
        {
            PlayerMarkerTransforms[ID] = new();
            if (ActiveMapUI != null)
                CreateMapMarker(ActiveMapUI);
        }

        UsernamePanel = transform.GetChild(1).GetComponent<TextMeshPro>();

        SetupRenderersAndCollision();
    }

    public void OnDestroy()
    {
        PlayerManager.OnPlayerGadgetUpdated -= OnGadgetUpdate;

        // if (IsLocal)
        // {
        //     var modelRenderers = GetComponentsInChildren<MeshRenderer>();
        //     var cameraRenderers = camera.GetComponentsInChildren<MeshRenderer>();
        //     var allRenderers = new MeshRenderer[modelRenderers.Length + cameraRenderers.Length];

        //     modelRenderers.CopyTo(allRenderers, 0);
        //     cameraRenderers.CopyTo(allRenderers, modelRenderers.Length);

        //     renderers = allRenderers;
        // }
        // else
        // {
        //     renderers = GetComponentsInChildren<MeshRenderer>();
        // }
        
        if (IsLocal) return;

        CleanupGadgetPreview();

        DestroyMapMarker();

        if (PlayerMarkerTransforms.TryGetValue(ID, out var marker))
        {
            if (marker.mainMarker != null)
                Destroy(marker.mainMarker.gameObject);

            PlayerMarkerTransforms.Remove(ID);
        }
    }

    private void SetupRenderersAndCollision()
    {
        collider = GetComponentInChildren<Collider>();
        if (IsLocal)
            return;

        remoteRenderers = GetComponentsInChildren<Renderer>(true).ToArray();
        remoteRendererDefaults = remoteRenderers.Select(renderer => renderer && renderer.enabled).ToArray();
        remoteRendererLayers = remoteRenderers.Select(renderer => renderer ? renderer.gameObject.layer : -1).ToArray();
        EnsureRemoteRendererLayersVisibleToGameplayCamera();
        SetRemoteBodyVisible(false);
    }

    private void EnsureRemoteRendererLayersVisibleToGameplayCamera()
    {
        if (IsLocal || remoteRenderers.Length == 0)
            return;

        var gameplayCamera = Camera.main;
        if (!gameplayCamera)
            return;

        var cameraMask = gameplayCamera.cullingMask;
        if (remoteRendererCameraMaskKnown && remoteRendererCameraMask == cameraMask)
            return;

        remoteRendererCameraMask = cameraMask;
        remoteRendererCameraMaskKnown = true;

        var defaultLayer = LayerMask.NameToLayer("Default");
        var adjustedRenderers = 0;
        for (var index = 0; index < remoteRenderers.Length; index++)
        {
            var renderer = remoteRenderers[index];
            if (!renderer || index >= remoteRendererLayers.Length)
                continue;

            var targetLayer = RemotePlayerRenderLayerPolicy.ResolveRendererLayer(
                remoteRendererLayers[index], cameraMask, defaultLayer);
            if (targetLayer < 0 || renderer.gameObject.layer == targetLayer)
                continue;

            renderer.gameObject.layer = targetLayer;
            adjustedRenderers++;
        }

        if (adjustedRenderers > 0)
        {
            SrLogger.LogMessage(
                $"Adjusted {adjustedRenderers} remote avatar renderer layer(s) for the gameplay camera " +
                $"(cullingMask=0x{cameraMask:X8}, fallbackLayer={defaultLayer}).");
        }
    }

    private void SetRemoteBodyVisible(bool visible)
    {
        for (var index = 0; index < remoteRenderers.Length; index++)
        {
            var renderer = remoteRenderers[index];
            if (renderer)
                renderer.enabled = visible && index < remoteRendererDefaults.Length && remoteRendererDefaults[index];
        }

        remoteBodyVisible = visible;
    }

    public void Update()
    {
        if (model == null)
        {
            model = PlayerManager.GetPlayer(ID) ?? PlayerManager.AddPlayer(ID);

            if (!UsernamePanel)
                return;

            UsernamePanel.gameObject.AddComponent<TransformLookAtCamera>().TargetTransform =
                UsernamePanel.transform;

            SetupMarker();
            SetUsername(model.Username);

            return;
        }

        if (!IsLocal)
        {
            EnsureRemoteRendererLayersVisibleToGameplayCamera();

            var hasPose = RemotePlayerPosePolicy.ShouldRenderRemoteBody(model.HasReceivedState);
            if (!hasPose)
            {
                if (remoteBodyVisible)
                    SetRemoteBodyVisible(false);
                return;
            }

            if (!remoteBodyVisible)
                SetRemoteBodyVisible(true);
        }

        transformTimer -= UnityEngine.Time.unscaledDeltaTime;

        if (!IsLocal)
        {
            var timer = Mathf.InverseLerp(interpolationStart, interpolationEnd, UnityEngine.Time.unscaledTime);

            var networkPosition = Vector3.LerpUnclamped(previousPosition, nextPosition, timer);
            var networkLookY = Mathf.LerpAngle(previousRotation.y, nextRotation.y, timer);
            var networkYaw = Mathf.LerpAngle(previousRotation.x, nextRotation.x, timer);

            if (Vector3.SqrMagnitude(transform.position - networkPosition) > 9f)
            {
                transform.position = networkPosition;
                transform.eulerAngles = new Vector3(0, networkYaw, 0);
                ReceivedLookY = networkLookY;
            }
            else
            {
                var blendSpeed = UnityEngine.Time.unscaledDeltaTime * 15f;

                transform.position = Vector3.Lerp(transform.position, networkPosition, blendSpeed);

                ReceivedLookY = Mathf.LerpAngle(ReceivedLookY, networkLookY, blendSpeed);

                var targetRot = Quaternion.Euler(0, networkYaw, 0);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, blendSpeed);
            }
        }

        ReloadMeshTransform();

        UpdateGadgetMode();
        UpdateForceField();

        UpdateMarker();
        
        if (transformTimer >= 0f)
            return;

        transformTimer = PlayerTimer;

        if (IsLocal)
        {
            RemotePlayerManager.SendPlayerUpdate(
                position: transform.position,
                rotation: transform.eulerAngles.y,
                horizontalMovement: animator.GetFloat(HorizontalMovement),
                forwardMovement: animator.GetFloat(ForwardMovement),
                yaw: animator.GetFloat(Yaw),
                airborneState: animator.GetInteger(AirborneState),
                moving: animator.GetBool(Moving),
                horizontalSpeed: animator.GetFloat(HorizontalSpeed),
                forwardSpeed: animator.GetFloat(ForwardSpeed),
                sprinting: animator.GetBool(Sprinting),
                lookY: camera.eulerAngles.x,
                sceneGroup: NetworkSceneManager.GetPersistentID(SystemContext.Instance.SceneLoader._currentSceneGroup)
            );
        }
        else
        {
            if (!hasAnimationController)
            {
                var playerAnimatorController = sceneContext.player?.GetComponent<Animator>().runtimeAnimatorController;

                if (animator.runtimeAnimatorController != null)
                {
                    hasAnimationController = true;
                    animator.runtimeAnimatorController =
                        Instantiate(playerAnimatorController);
                    animator.avatar = sceneContext.player?.GetComponent<Animator>().avatar;
                    SetupAnimations();
                }
            }

            previousPosition = nextPosition;
            nextPosition = model.Position;

            previousRotation = new Vector2(transform.eulerAngles.y, model.LastLookY);
            nextRotation = new Vector2(model.Rotation, model.LookY);

            interpolationStart = UnityEngine.Time.unscaledTime;
            interpolationEnd = UnityEngine.Time.unscaledTime + PlayerTimer;

            animator.SetFloat(HorizontalMovement, model.HorizontalMovement);
            animator.SetFloat(ForwardMovement, model.ForwardMovement);
            animator.SetFloat(Yaw, model.Yaw);
            animator.SetInteger(AirborneState, model.AirborneState);
            animator.SetBool(Moving, model.Moving);
            animator.SetFloat(HorizontalSpeed, model.HorizontalSpeed);
            animator.SetFloat(ForwardSpeed, model.ForwardSpeed);
            animator.SetBool(Sprinting, model.Sprinting);
        }
    }

    private void ReloadMeshTransform()
    {
        // foreach (var renderer in renderers)
        // {
        //     // This is for the getter to refresh the render position stuff qwq
        //     var bounds = renderer.bounds;
        //     var localBounds = renderer.localBounds;
        // }

        // The remote avatar collider used to be toggled off/on every frame to "refresh" it. That forced a PhysX
        // shape remove/re-add each frame (heavy next to many rigidbodies); a moved collider is synced by Unity
        // at the next physics step anyway.
    }

    [UsedImplicitly]
    public void LateUpdate()
    {
        AnimateArmY();
        ScaleUsernameWithDistance();
    }

    // Nametag size by distance: smaller up close, readable far away (constant on-screen size from
    // NameTagReferenceDistance to NameTagMaxDistance), never absurdly large.
    private const float NameTagReferenceDistance = 10f;
    private const float NameTagMinScale = 0.7f;
    private const float NameTagMaxDistance = 45f;
    private Vector3 usernameBaseScale;

    private void ScaleUsernameWithDistance()
    {
        if (IsLocal || !UsernamePanel)
            return;

        var camera = Camera.main;
        if (!camera)
            return;

        if (usernameBaseScale == Vector3.zero)
        {
            usernameBaseScale = UsernamePanel.transform.localScale;
            UsernamePanel.outlineWidth = 0.2f;
            UsernamePanel.outlineColor = new Color32(0, 0, 0, 220);
        }

        var distance = Vector3.Distance(camera!.transform.position, UsernamePanel.transform.position);
        var scale = Mathf.Clamp(distance, NameTagReferenceDistance * NameTagMinScale, NameTagMaxDistance) /
                    NameTagReferenceDistance;
        UsernamePanel.transform.localScale = usernameBaseScale * scale;
    }
    
    private bool IsInLocalSceneGroup()
    {
        var localSceneGroup = NetworkSceneManager.GetPersistentID(
            SystemContext.Instance.SceneLoader._currentSceneGroup);
        return localSceneGroup >= 0 && (model?.SceneGroup ?? -1) == localSceneGroup;
    }
}

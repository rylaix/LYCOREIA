using Il2CppMonomiPark.SlimeRancher.Player.PlayerItems;
using SR2MP.Client.Models;
using SR2MP.Components.Player;
using SR2MP.Packets.FX;
using Starlight.Storage;
using UnityEngine.VFX;

namespace SR2MP.Components.FX;

/// <summary>
/// Client-local, visual-only copy of the Vaccone effect attached to a remote player's vacpack.
/// Every non-visual component is removed while the copy is inactive, before it can run.
/// </summary>
[InjectIntoIL]
internal sealed class RemoteVacuumSuctionFX : MonoBehaviour
{
    private const string VisualTemplateName = "SR2MP_RemoteVacuumVisualTemplate";
    private const string VisualInstanceName = "SR2MP_RemoteVacuumSuctionFX";
    // The Vaccone shader clips the cone to a sphere (_VacVisualWorldPosition, _VacWindDistance) that the native
    // VacuumItem sets globally for the LOCAL player only: the local eye and the distance to what the vac hits
    // (0 while idle). Override both per renderer so a remote cone is clipped around its own nozzle instead.
    private static readonly int VacVisualWorldPositionId = Shader.PropertyToID("_VacVisualWorldPosition");
    private static readonly int VacWindDistanceId = Shader.PropertyToID("_VacWindDistance");
    private const float RemoteVacWindMaxDistance = 1000f;
    private const float RemoteVacWindRayLength = 30f;

    private static GameObject? visualTemplate;
    private static readonly Dictionary<string, bool> PendingPlayerSuctionStates = new();
    private static readonly Dictionary<string, PendingPose> PendingPlayerPoses = new();
    private static uint localPoseSequence;
    private static float nextLocalPoseAt;
    private GameObject? visualInstance;
    private readonly RemoteSuctionFxLifecyclePolicy lifecycle = new();
    private Renderer[] visualRenderers = Array.Empty<Renderer>();
    private int[] sourceRendererLayers = Array.Empty<int>();
    private int rendererCameraMask;
    private int rendererCameraInstanceId;
    private bool rendererCameraMaskKnown;
    private float nextRendererLayerRefreshAt;
    private bool hasRemotePose;
    private uint lastRemotePoseSequence;
    private Vector3 remoteNozzlePosition;
    private Quaternion remoteNozzleRotation = Quaternion.identity;
    private MaterialPropertyBlock? nozzlePropertyBlock;
    private readonly RemoteVacuumFoliage foliage = new();
    private Vector3 displayedLocalPosition;
    private Quaternion displayedLocalRotation = Quaternion.identity;
    private NetworkPlayer? remoteBody;
    private Animator? visualAnimator;
    private static readonly int ConeMovingId = Animator.StringToHash("moving");
    private static readonly int ConeSprintingId = Animator.StringToHash("sprinting");
    private const float PoseEaseRate = 18f;
    private Transform? gunBone;
    // Visual-only "lasso" sway like the native first-person cone: the base stays on the nozzle while a lagging
    // tip point trails movement and smoothly settles back; the joint chain bends progressively toward it.
    private const float LassoStiffness = 16f;
    private const float LassoDamping = 2f; // ~0.25 of critical: slow, strongly bouncy, like the native cone
    private const float LassoMaxBendDegrees = 60f;
    private const float LassoBendGain = 2.2f;
    private bool swayInitialized;
    private Transform[] lassoJoints = Array.Empty<Transform>();
    private Quaternion[] lassoWritten = Array.Empty<Quaternion>();
    private Quaternion[] lassoBase = Array.Empty<Quaternion>();
    private Vector3 lassoTip;
    private Vector3 lassoTipVelocity;

    private readonly record struct PendingPose(uint Sequence, Vector3 Position, Quaternion Rotation);

    internal static void PublishLocalSuctionPose(VacuumItem vacuumItem)
    {
        if (!vacuumItem || !vacuumItem._vacPressed || vacuumItem._vacMode != VacuumItem.VacMode.VAC ||
            (!Main.Server.IsRunning && !Main.Client.IsConnected) ||
            UnityEngine.Time.unscaledTime < nextLocalPoseAt)
            return;

        var origin = vacuumItem.VacOrigin;
        if (!origin)
            return;
#if SR2MP_TEST
        TraceLocalVacVisualsOnce(vacuumItem);
#endif
        // The pose is sent relative to the replicated player body (the transform the local NetworkPlayer
        // sends), sampled in the same frame, so receivers can attach the cone rigidly to the remote avatar.
        var body = FindLocalReplicatedBody();
        if (!body)
            return;

        nextLocalPoseAt = UnityEngine.Time.unscaledTime + 0.1f;
        var bodyRotation = Quaternion.Euler(0f, body!.eulerAngles.y, 0f);
        var inverseBodyRotation = Quaternion.Inverse(bodyRotation);
        var packet = new VacuumSuctionPosePacket
        {
            PlayerId = LocalID,
            Sequence = unchecked(++localPoseSequence),
            Position = inverseBodyRotation * (origin.transform.position - body.position),
            Rotation = inverseBodyRotation * origin.transform.rotation
        };
        Main.SendToAllOrServer(packet);
        Logger.LogDiagnostic("vacuum-fx", "suction-pose-sent",
            $"sequence={packet.Sequence}; position={packet.Position.ToString("F2")}; forward={packet.Rotation * Vector3.forward}",
            rateKey: "vacuum-fx-local-pose", minimumIntervalMilliseconds: 2000);
    }

    private static Transform? localReplicatedBody;

    private static Transform? FindLocalReplicatedBody()
    {
        if (localReplicatedBody && localReplicatedBody!.gameObject.activeInHierarchy)
            return localReplicatedBody;

        localReplicatedBody = null;
        var scenePlayer = SceneContext.Instance ? SceneContext.Instance.Player : null;
        var direct = scenePlayer ? scenePlayer!.GetComponentInChildren<NetworkPlayer>() : null;
        if (direct && direct!.IsLocal)
        {
            localReplicatedBody = direct.transform;
            Trace($"local replicated body={localReplicatedBody.name} (scene player)");
            return localReplicatedBody;
        }
        foreach (var networkPlayer in Object.FindObjectsOfType<NetworkPlayer>())
        {
            if (networkPlayer && networkPlayer.IsLocal)
            {
                localReplicatedBody = networkPlayer.transform;
                Trace($"local replicated body={localReplicatedBody.name}");
                break;
            }
        }
        return localReplicatedBody;
    }

    internal static void ApplySuctionPose(string playerId, uint sequence, Vector3 position, Quaternion rotation)
    {
        if (PlayerObjects.TryGetValue(playerId, out var remotePlayer) && remotePlayer)
        {
            PendingPlayerPoses.Remove(playerId);
            var component = remotePlayer.GetComponent<RemoteVacuumSuctionFX>();
            if (!component)
                component = remotePlayer.AddComponent<RemoteVacuumSuctionFX>();
            if (!component)
                return;

            component.ApplyPose(sequence, position, rotation);
            return;
        }

        PendingPlayerPoses[playerId] = new PendingPose(sequence, position, rotation);
    }

    private void ApplyPose(uint sequence, Vector3 position, Quaternion rotation)
    {
        if (hasRemotePose && !IsNewerSequence(sequence, lastRemotePoseSequence))
            return;

        var snap = !hasRemotePose;
        hasRemotePose = true;
        lastRemotePoseSequence = sequence;
        // Body-local target; LateUpdate eases the displayed pose toward it on the smoothed avatar.
        remoteNozzlePosition = position;
        remoteNozzleRotation = rotation.normalized;
        if (snap)
        {
            displayedLocalPosition = remoteNozzlePosition;
            displayedLocalRotation = remoteNozzleRotation;
        }

        Logger.LogDiagnostic("vacuum-fx", "suction-pose-received",
            $"player={transform.name}; sequence={sequence}; position={position.ToString("F2")}; forward={remoteNozzleRotation * Vector3.forward}",
            rateKey: $"vacuum-fx-remote-pose:{transform.name}", minimumIntervalMilliseconds: 2000);
    }

    private static bool IsNewerSequence(uint candidate, uint current)
        => candidate != current && unchecked((int)(candidate - current)) > 0;

    internal static bool SetSuction(string playerId, bool active, out string result)
    {
        if (PlayerObjects.TryGetValue(playerId, out var remotePlayer) && remotePlayer)
        {
            PendingPlayerSuctionStates.Remove(playerId);
            return SetSuction(remotePlayer, active, out result);
        }

        // Join and FX packets use different packet types and channels, so UDP may
        // deliver the first suction edge before the remote avatar is instantiated.
        // Keep only the latest state; ReliableOrdered preserves start/end order.
        PendingPlayerSuctionStates[playerId] = active;
        result = "deferred until remote player is instantiated";
        return true;
    }

    internal static void PumpPendingPlayerStates()
    {
        if (!Main.Server.IsRunning && !Main.Client.IsConnected)
        {
            PendingPlayerSuctionStates.Clear();
            PendingPlayerPoses.Clear();
            localPoseSequence = 0;
            nextLocalPoseAt = 0f;
            foreach (var remotePlayer in PlayerObjects.Values)
            {
                if (!remotePlayer)
                    continue;
#if SR2MP_TEST
                // The offline avatar rig drives its own avatar's cone.
                if (SR2MP.Shared.Utils.TestAvatarRig.Enabled)
                    continue;
#endif
                var component = remotePlayer.GetComponent<RemoteVacuumSuctionFX>();
                if (component)
                    component.StopSuction(out _);
            }
            return;
        }

        foreach (var (playerId, active) in PendingPlayerSuctionStates.ToArray())
        {
            if (!PlayerObjects.TryGetValue(playerId, out var remotePlayer) || !remotePlayer)
                continue;

            if (!SetSuction(remotePlayer, active, out var result))
                SrLogger.LogWarning($"Could not apply deferred vacuum state for player {playerId}: {result}");

            PendingPlayerSuctionStates.Remove(playerId);
        }

        foreach (var (playerId, pose) in PendingPlayerPoses.ToArray())
        {
            if (!PlayerObjects.TryGetValue(playerId, out var remotePlayer) || !remotePlayer)
                continue;

            ApplySuctionPose(playerId, pose.Sequence, pose.Position, pose.Rotation);
            PendingPlayerPoses.Remove(playerId);
        }
    }

#if SR2MP_DEEPLOG
    internal static bool IsSuctionVisible(string playerId)
    {
        if (!PlayerObjects.TryGetValue(playerId, out var remotePlayer) || !remotePlayer)
            return false;

        var component = remotePlayer.GetComponent<RemoteVacuumSuctionFX>();
        return component && component.visualInstance && component.visualInstance.activeSelf;
    }
#endif

    internal static void ForgetPendingPlayer(string playerId)
    {
        PendingPlayerSuctionStates.Remove(playerId);
        PendingPlayerPoses.Remove(playerId);
        if (PlayerObjects.TryGetValue(playerId, out var remotePlayer) && remotePlayer)
        {
            var component = remotePlayer.GetComponent<RemoteVacuumSuctionFX>();
            if (component)
                component.StopSuction(out _);
        }
    }

    private void Update()
    {
        var now = UnityEngine.Time.unscaledTime;
        if (visualInstance && visualInstance.activeSelf && now >= nextRendererLayerRefreshAt)
        {
            nextRendererLayerRefreshAt = now + 0.25f;
            RefreshRendererLayers(force: false);
        }

#if SR2MP_TEST
        if (visualInstance && visualInstance.activeSelf && traceVisualStateAt > 0f && now >= traceVisualStateAt)
        {
            traceVisualStateAt = 0f;
            Trace($"player={transform.name} active-state windHit={lastWindHit} {DescribeVisualState(visualInstance)}");
        }
#endif

        if (!lifecycle.TryBeginStartAttempt(now, visualInstance))
            return;
        if (!StartSuction(out var result))
            Trace($"player={transform.name} suction visual retry scheduled: {result}");
    }

    private void LateUpdate()
    {
        if (!visualInstance || !visualInstance.activeSelf)
            return;

        FollowRemoteBody();

        // Runs after the Animator has written its own property-block values for this frame.
        nozzlePropertyBlock ??= new MaterialPropertyBlock();
        var nozzlePosition = visualInstance.transform.position;
        var windDistance = Physics.Raycast(nozzlePosition, visualInstance.transform.forward, out var hit,
            RemoteVacWindRayLength, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
            ? hit.distance
            : RemoteVacWindMaxDistance;
#if SR2MP_TEST
        lastWindHit = windDistance < RemoteVacWindMaxDistance ? $"{hit.collider.name}@{windDistance:F2}" : "none";
#endif
        foreach (var renderer in visualRenderers)
        {
            if (!renderer)
                continue;
            renderer.GetPropertyBlock(nozzlePropertyBlock);
            nozzlePropertyBlock.SetVector(VacVisualWorldPositionId, nozzlePosition);
            nozzlePropertyBlock.SetFloat(VacWindDistanceId, windDistance);
            renderer.SetPropertyBlock(nozzlePropertyBlock);
        }

        // The vac origin's aim axis is its local up (its forward points down/back, see suction-pose-sent).
        foliage.Update(nozzlePosition, visualInstance.transform.up, UnityEngine.Time.deltaTime);
    }

    private void FollowRemoteBody()
    {
        if (!hasRemotePose)
            return;

        // Ease only between the 10 Hz pose samples; the body itself is already the smoothed avatar.
        var ease = 1f - Mathf.Exp(-PoseEaseRate * UnityEngine.Time.unscaledDeltaTime);
        displayedLocalPosition = Vector3.Lerp(displayedLocalPosition, remoteNozzlePosition, ease);
        displayedLocalRotation = Quaternion.Slerp(displayedLocalRotation, remoteNozzleRotation, ease);
        var bodyRotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
        var packetPosition = transform.position + bodyRotation * displayedLocalPosition;
        var packetRotation = bodyRotation * displayedLocalRotation;

        // Base pinned exactly to the visible vac barrel; direction is the sender's own aim (body-relative, eased).
        // No learned bone->aim offset: over real latency it absorbed arm/aim timing differences and tilted the cone.
        if (!gunBone)
            gunBone = FindNozzleAnchor(transform);
        if (gunBone)
            packetPosition = gunBone!.position;
        visualInstance!.transform.SetPositionAndRotation(packetPosition, packetRotation);
        ApplyLasso();

        if (!remoteBody)
            remoteBody = GetComponent<NetworkPlayer>();
        if (visualAnimator && remoteBody)
        {
            visualAnimator!.SetBool(ConeMovingId, remoteBody!.RemoteMoving);
            visualAnimator.SetBool(ConeSprintingId, remoteBody.RemoteSprinting);
        }
    }

    private void ApplyLasso()
    {
        if (lassoJoints.Length == 0)
        {
            // joint1 is the base at the nozzle; joint2..joint6 are nested children toward the tip.
            lassoJoints = visualInstance!.GetComponentsInChildren<Transform>(true)
                .Where(child => child && child.name.StartsWith("joint"))
                .OrderBy(child => child.name, StringComparer.Ordinal)
                .ToArray();
            lassoWritten = new Quaternion[lassoJoints.Length];
            lassoBase = new Quaternion[lassoJoints.Length];
            swayInitialized = false;
        }
        if (lassoJoints.Length < 3)
            return;

        // Undo last frame's bend unless the Animator already rewrote the joint this frame.
        for (var index = 1; index < lassoJoints.Length; index++)
        {
            var joint = lassoJoints[index];
            if (swayInitialized && Quaternion.Angle(joint.localRotation, lassoWritten[index]) < 0.01f)
                joint.localRotation = lassoBase[index];
            lassoBase[index] = joint.localRotation;
        }

        var root = lassoJoints[0].position;
        var straightTip = lassoJoints[^1].position;
        var length = Vector3.Distance(root, straightTip);
        var deltaTime = Mathf.Min(UnityEngine.Time.unscaledDeltaTime, 0.05f);
        if (!swayInitialized || length < 0.01f || deltaTime <= 0f)
        {
            swayInitialized = true;
            lassoTip = straightTip;
            lassoTipVelocity = Vector3.zero;
            RememberLassoWrites();
            return;
        }

        lassoTipVelocity += (LassoStiffness * (straightTip - lassoTip) - LassoDamping * lassoTipVelocity) * deltaTime;
        lassoTip += lassoTipVelocity * deltaTime;
        var straightDirection = (straightTip - root) / length;
        var laggedDirection = (lassoTip - root).normalized;
        var bendAngle = Vector3.Angle(straightDirection, laggedDirection);
        if (bendAngle > LassoMaxBendDegrees)
        {
            laggedDirection = Vector3.RotateTowards(straightDirection, laggedDirection,
                LassoMaxBendDegrees * Mathf.Deg2Rad, 0f);
            lassoTip = root + laggedDirection * Vector3.Distance(root, lassoTip);
            bendAngle = LassoMaxBendDegrees;
        }

        if (bendAngle > 0.05f)
        {
            var axis = Vector3.Cross(straightDirection, laggedDirection).normalized;
            var perJoint = Mathf.Min(bendAngle * LassoBendGain, LassoMaxBendDegrees * LassoBendGain) /
                           (lassoJoints.Length - 1);
            for (var index = 1; index < lassoJoints.Length; index++)
            {
                var joint = lassoJoints[index];
                joint.rotation = Quaternion.AngleAxis(perJoint, axis) * joint.rotation;
            }
        }
        RememberLassoWrites();
    }

    private void RememberLassoWrites()
    {
        for (var index = 1; index < lassoJoints.Length; index++)
            lassoWritten[index] = lassoJoints[index].localRotation;
    }

    private void OnDestroy()
    {
        if (visualInstance)
        {
            Object.Destroy(visualInstance);
            visualInstance = null;
        }
        hasRemotePose = false;
        lastRemotePoseSequence = 0;
        remoteNozzlePosition = Vector3.zero;
        remoteNozzleRotation = Quaternion.identity;
        visualRenderers = Array.Empty<Renderer>();
        sourceRendererLayers = Array.Empty<int>();
        rendererCameraMaskKnown = false;
        rendererCameraInstanceId = 0;
        lifecycle.SetDesired(false);
    }

    internal static bool InitializeTemplate()
    {
        if (visualTemplate)
            return true;

        var scene = SceneContext.Instance;
        if (!scene)
            return false;

        var player = scene.Player;
        if (!player)
            return false;

        var itemController = player.GetComponent<PlayerItemController>();
        if (!itemController || !itemController._vacuumItem)
            return false;

        var source = itemController._vacuumItem.VacFX;
        if (!source)
            return false;

        // Cloning an active prefab can invoke its scripts before they can be stripped. Build the
        // sanitized template only from the idle, inactive local VacFX instance.
        if (source.activeSelf || source.activeInHierarchy)
        {
            Trace("template skipped: source VacFX is active");
            return false;
        }

        GameObject? candidate = null;
#if SR2MP_TEST
        Trace($"template source-state {DescribeVisualState(source)}");
#endif
        try
        {
            candidate = Object.Instantiate(source);
            if (!candidate)
                return false;

            candidate.name = VisualTemplateName;
            candidate.SetActive(false);
            candidate.transform.SetParent(null, false);
            candidate.transform.localPosition = Vector3.zero;
            candidate.transform.localRotation = Quaternion.identity;
            candidate.transform.localScale = Vector3.one;

            var removedComponents = 0;
            foreach (var component in candidate.GetComponentsInChildren<Component>(true))
            {
                if (!component || IsVisualOnly(component))
                    continue;

                Trace($"template strip node={component.gameObject.name} type={RuntimeTypeName(component)}");
                Object.DestroyImmediate(component);
                removedComponents++;
            }

            var unsupported = candidate.GetComponentsInChildren<Component>(true)
                .FirstOrDefault(component => component && !IsVisualOnly(component));
            if (unsupported)
            {
                Object.DestroyImmediate(candidate);
                Trace($"template rejected: unfiltered component {RuntimeTypeName(unsupported!)}");
                return false;
            }

            foreach (var component in candidate.GetComponentsInChildren<Component>(true))
            {
                if (component)
                    Trace($"template keep node={component.gameObject.name} type={RuntimeTypeName(component)}");
            }

            foreach (var particle in candidate.GetComponentsInChildren<ParticleSystem>(true))
                particle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            foreach (var effect in candidate.GetComponentsInChildren<VisualEffect>(true))
                effect.Stop();

            Object.DontDestroyOnLoad(candidate);
            visualTemplate = candidate;
            Trace($"template ready: removed={removedComponents}, particles={candidate.GetComponentsInChildren<ParticleSystem>(true).Length}, vfx={candidate.GetComponentsInChildren<VisualEffect>(true).Length}");
            return true;
        }
        catch (Exception ex)
        {
            if (candidate)
                Object.DestroyImmediate(candidate);

            SrLogger.LogWarning($"Could not prepare remote Vaccone visuals; suction visuals remain disabled: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    internal static bool SetSuction(GameObject remotePlayer, bool active, out string result)
    {
        if (!remotePlayer)
        {
            result = "remote player is null";
            return false;
        }

        var component = remotePlayer.GetComponent<RemoteVacuumSuctionFX>();
        if (!active)
        {
            if (!component)
                component = remotePlayer.AddComponent<RemoteVacuumSuctionFX>();
            if (!component)
            {
                result = "could not add remote visual controller";
                return false;
            }

            return component.StopSuction(out result);
        }

        if (!component)
            component = remotePlayer.AddComponent<RemoteVacuumSuctionFX>();
        if (!component)
        {
            result = "could not add remote visual controller";
            return false;
        }

        return component.StartSuction(out result);
    }

    private bool StartSuction(out string result)
    {
        lifecycle.SetDesired(true);
        lifecycle.MarkStartAttempt(UnityEngine.Time.unscaledTime);
        if (visualInstance)
        {
            if (!visualInstance.activeSelf)
            {
                RefreshRendererLayers(force: true);
                visualInstance.SetActive(true);
                PlayVisualSystems(visualInstance);
#if SR2MP_TEST
                traceVisualStateAt = UnityEngine.Time.unscaledTime + 0.5f;
#endif
            }

            result = "already active";
            return true;
        }

        if (!visualTemplate && !InitializeTemplate())
        {
            result = "safe visual template unavailable";
            return false;
        }

        var anchor = FindNozzleAnchor(transform);
        if (!anchor)
        {
            result = "remote vacpack anchor not found";
            return false;
        }

        visualInstance = Object.Instantiate(visualTemplate!, anchor, false);
        if (!visualInstance)
        {
            result = "visual clone failed";
            return false;
        }

        visualInstance.name = VisualInstanceName;
        visualInstance.transform.localPosition = Vector3.zero;
        visualInstance.transform.localRotation = Quaternion.identity;
        visualInstance.transform.localScale = Vector3.one;

        // Defend against later changes to the source: only the explicit visual whitelist may
        // survive in the instantiated copy.
        var unexpected = visualInstance.GetComponentsInChildren<Component>(true)
            .FirstOrDefault(component => component && !IsVisualOnly(component));
        if (unexpected)
        {
            Object.DestroyImmediate(visualInstance);
            visualInstance = null;
            result = $"visual clone rejected unexpected {RuntimeTypeName(unexpected!)}";
            return false;
        }

        visualRenderers = visualInstance.GetComponentsInChildren<Renderer>(true);
        sourceRendererLayers = visualRenderers
            .Select(renderer => renderer ? renderer.gameObject.layer : -1)
            .ToArray();
        rendererCameraMaskKnown = false;
        nextRendererLayerRefreshAt = UnityEngine.Time.unscaledTime + 0.25f;

        var initialPosition = anchor.position;
        var initialRotation = anchor.rotation;
        visualInstance.transform.SetParent(null, true);
        visualAnimator = visualInstance.GetComponent<Animator>();
        visualInstance.transform.SetPositionAndRotation(initialPosition, initialRotation);
        if (hasRemotePose)
        {
            displayedLocalPosition = remoteNozzlePosition;
            displayedLocalRotation = remoteNozzleRotation;
            FollowRemoteBody();
        }
        RefreshRendererLayers(force: true);
        visualInstance.SetActive(true);
        PlayVisualSystems(visualInstance);
#if SR2MP_TEST
        traceVisualStateAt = UnityEngine.Time.unscaledTime + 0.5f;
#endif
        result = $"active anchor={anchor.name} particles={visualInstance.GetComponentsInChildren<ParticleSystem>(true).Length} vfx={visualInstance.GetComponentsInChildren<VisualEffect>(true).Length}";
        Trace($"player={transform.name} {result}");
        return true;
    }

    private bool StopSuction(out string result)
    {
        lifecycle.SetDesired(false);
        swayInitialized = false;
        foliage.Reset();
        if (!visualInstance)
        {
            result = "already stopped";
            return true;
        }

        foreach (var particle in visualInstance.GetComponentsInChildren<ParticleSystem>(true))
            particle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        foreach (var effect in visualInstance.GetComponentsInChildren<VisualEffect>(true))
            effect.Stop();

        visualInstance.SetActive(false);
        result = "stopped";
        Trace($"player={transform.name} {result}");
        return true;
    }

    private void RefreshRendererLayers(bool force)
    {
        if (!visualInstance || visualRenderers.Length == 0)
            return;

        var gameplayCamera = Camera.main;
        if (!gameplayCamera)
            return;

        var cameraMask = gameplayCamera.cullingMask;
        var cameraInstanceId = gameplayCamera.GetInstanceID();
        if (!force && rendererCameraMaskKnown && rendererCameraMask == cameraMask &&
            rendererCameraInstanceId == cameraInstanceId)
            return;

        rendererCameraMask = cameraMask;
        rendererCameraInstanceId = cameraInstanceId;
        rendererCameraMaskKnown = true;
        var defaultLayer = LayerMask.NameToLayer("Default");
        var rendererDetails = new List<string>(visualRenderers.Length);
        for (var index = 0; index < visualRenderers.Length; index++)
        {
            var renderer = visualRenderers[index];
            if (!renderer)
                continue;

            var sourceLayer = index < sourceRendererLayers.Length ? sourceRendererLayers[index] : -1;
            var resolvedLayer = RemotePlayerRenderLayerPolicy.ResolveRendererLayer(
                sourceLayer, cameraMask, defaultLayer);
            if (resolvedLayer >= 0 && renderer.gameObject.layer != resolvedLayer)
                renderer.gameObject.layer = resolvedLayer;

            var bounds = renderer.bounds;
            rendererDetails.Add(
                $"node={GetRendererNodePath(renderer.transform)}; sourceLayer={sourceLayer}; layer={renderer.gameObject.layer}; " +
                $"enabled={renderer.enabled}; active={renderer.gameObject.activeInHierarchy}; " +
                $"boundsCenter={bounds.center.ToString("F2")}; boundsExtents={bounds.extents.ToString("F2")}");
        }

        Logger.LogDiagnostic("vacuum-fx", "renderer-layer-refresh",
            $"player={transform.name}; camera={gameplayCamera.name}#{gameplayCamera.GetInstanceID()}; " +
            $"cameraMask=0x{cameraMask:X8}; defaultLayer={defaultLayer}; renderers=[{string.Join(" | ", rendererDetails)}]",
            rateKey: "vacuum-fx-renderers",
            minimumIntervalMilliseconds: 0);
    }

    private string GetRendererNodePath(Transform rendererTransform)
    {
        var names = new List<string>();
        var current = rendererTransform;
        while (current && visualInstance && current != visualInstance.transform)
        {
            names.Add(current.name);
            current = current.parent;
        }
        names.Reverse();
        return names.Count == 0 ? visualInstance?.name ?? rendererTransform.name : string.Join("/", names);
    }

    private static void PlayVisualSystems(GameObject root)
    {
        foreach (var particle in root.GetComponentsInChildren<ParticleSystem>(true))
            particle.Play(true);
        foreach (var effect in root.GetComponentsInChildren<VisualEffect>(true))
            effect.Play();
    }

    private static Transform? FindNozzleAnchor(Transform playerRoot)
    {
        var allTransforms = playerRoot.GetComponentsInChildren<Transform>(true);
        var vacAttach = allTransforms.FirstOrDefault(child => child && child.name == "vacAttach");
        if (!vacAttach)
            return null;

        var barrelBone = vacAttach.GetComponentsInChildren<Transform>(true)
            .FirstOrDefault(child => child && child.name == "bone_vac_barrel");
        return barrelBone ? barrelBone : vacAttach;
    }

    private static bool IsVisualOnly(Component component)
    {
        // Unity's IL2CPP component wrappers do not reliably satisfy managed `is` checks
        // against UnityEngine base types. Use the runtime IL2CPP name instead so that
        // Transform and renderer components are preserved without widening the allowlist
        // to interaction/physics behaviours.
        return IsVisualOnlyTypeName(RuntimeTypeName(component));
    }

    private static bool IsVisualOnlyTypeName(string typeName)
    {
        return typeName is
            "UnityEngine.Transform"
            or "UnityEngine.Animator"
            or "UnityEngine.ParticleSystem"
            or "UnityEngine.ParticleSystemRenderer"
            or "UnityEngine.ParticleSystemForceField"
            or "UnityEngine.MeshRenderer"
            or "UnityEngine.SkinnedMeshRenderer"
            or "UnityEngine.MeshFilter"
            or "UnityEngine.LODGroup"
            or "UnityEngine.Light"
            or "UnityEngine.VFX.VisualEffect";
    }

    internal static bool VerifyVisualComponentPolicy()
        => IsVisualOnlyTypeName("UnityEngine.Transform")
           && IsVisualOnlyTypeName("UnityEngine.Animator")
           && IsVisualOnlyTypeName("UnityEngine.ParticleSystem")
           && IsVisualOnlyTypeName("UnityEngine.ParticleSystemForceField")
           && IsVisualOnlyTypeName("UnityEngine.VFX.VisualEffect")
           && !IsVisualOnlyTypeName("MonomiPark.SlimeRancher.VFX.EnvironmentInteraction.EnvironmentInteractionVacuum")
           && !IsVisualOnlyTypeName("MonomiPark.SlimeRancher.VFXGraphParticles.WorldParticleVacInteraction");

    private static string RuntimeTypeName(Component component)
    {
        try
        {
            var type = component.GetIl2CppType();
            return type.FullName ?? type.Name;
        }
        catch
        {
            return component.GetType().FullName ?? component.GetType().Name;
        }
    }

#if SR2MP_TEST
    private float traceVisualStateAt;
    private string lastWindHit = "unset";
    private static bool localVisualsTraced;

    private static void TraceLocalVacVisualsOnce(VacuumItem vacuumItem)
    {
        if (localVisualsTraced)
            return;
        var vacFx = vacuumItem.VacFX;
        if (!vacFx || !vacFx.activeInHierarchy)
            return;
        localVisualsTraced = true;
        var group = vacuumItem._vacAnimator;
        var groupAnimators = group && group.animators != null
            ? string.Join(",", group.animators.Where(animator => animator).Select(animator => animator.gameObject.name))
            : "none";
        Trace($"local-native vacAnimatorGroup=[{groupAnimators}] {DescribeVisualState(vacFx)}");
    }

    private static string DescribeVacGlobals()
        => $"_VacVisualWorldPosition={Shader.GetGlobalVector(VacVisualWorldPositionId).ToString("F2")} " +
           $"_VacWindDistance={Shader.GetGlobalFloat(VacWindDistanceId):F3}";

    private static string DescribeMaterial(Material? material)
    {
        if (!material || !material!.shader)
            return "none";
        var shader = material.shader;
        var values = new List<string>();
        for (var index = 0; index < shader.GetPropertyCount(); index++)
        {
            var name = shader.GetPropertyName(index);
            var value = shader.GetPropertyType(index) switch
            {
                UnityEngine.Rendering.ShaderPropertyType.Color => material.GetColor(name).ToString("F2"),
                UnityEngine.Rendering.ShaderPropertyType.Vector => material.GetVector(name).ToString("F2"),
                UnityEngine.Rendering.ShaderPropertyType.Float or UnityEngine.Rendering.ShaderPropertyType.Range => material.GetFloat(name).ToString("F3"),
                UnityEngine.Rendering.ShaderPropertyType.Texture => material.GetTexture(name) ? material.GetTexture(name).name : "null",
                _ => "?"
            };
            values.Add($"{name}={value}");
        }
        return $"queue={material.renderQueue} kw={string.Join("/", material.shaderKeywords)} {string.Join(",", values)}";
    }

    private static string DescribeVisualState(GameObject root)
    {
        var parts = new List<string>();
        foreach (var animator in root.GetComponentsInChildren<Animator>(true))
        {
            if (!animator)
                continue;
            var controller = animator.runtimeAnimatorController;
            var parameters = new List<string>();
            if (animator.isInitialized)
            {
                foreach (var parameter in animator.parameters)
                {
                    var value = parameter.type switch
                    {
                        AnimatorControllerParameterType.Bool => animator.GetBool(parameter.nameHash).ToString(),
                        AnimatorControllerParameterType.Float => animator.GetFloat(parameter.nameHash).ToString("F2"),
                        AnimatorControllerParameterType.Int => animator.GetInteger(parameter.nameHash).ToString(),
                        _ => "trigger"
                    };
                    parameters.Add($"{parameter.name}({parameter.nameHash})={value}");
                }
            }
            var stateInfo = animator.isInitialized ? animator.GetCurrentAnimatorStateInfo(0) : default;
            parts.Add($"animator={animator.gameObject.name} enabled={animator.enabled} init={animator.isInitialized} " +
                      $"controller={(controller ? controller.name : "null")} speed={animator.speed:F2} " +
                      $"state={stateInfo.fullPathHash} t={stateInfo.normalizedTime:F2} params=[{string.Join(",", parameters)}]");
        }
        foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (!renderer)
                continue;
            var bones = renderer.bones;
            var boneScales = bones == null ? "none" : string.Join(",", bones.Where(bone => bone).Take(8)
                .Select(bone => $"{bone.name}:{bone.localScale.ToString("F2")}@{bone.localPosition.ToString("F2")}"));
            var material = renderer.sharedMaterial;
            parts.Add($"skinned={renderer.gameObject.name} enabled={renderer.enabled} active={renderer.gameObject.activeInHierarchy} " +
                      $"visible={renderer.isVisible} mesh={(renderer.sharedMesh ? renderer.sharedMesh.name : "null")} " +
                      $"material={(material ? material.name : "null")}#{(material ? material.GetInstanceID() : 0)} " +
                      $"shader={(material && material.shader ? material.shader.name : "null")} " +
                      $"layer={renderer.gameObject.layer}:{LayerMask.LayerToName(renderer.gameObject.layer)} " +
                      $"cameras=[{string.Join(",", Camera.allCameras.Select(camera => $"{camera.name}#{camera.depth:F0}:0x{camera.cullingMask:X8}:{((camera.cullingMask >> renderer.gameObject.layer) & 1) == 1}"))}] " +
                      $"main={(Camera.main ? Camera.main.name : "null")} " +
                      $"bounds={renderer.bounds.extents.ToString("F2")} block={renderer.HasPropertyBlock()} " +
                      $"props=[{DescribeMaterial(material)}] bones=[{boneScales}]");
        }
        parts.Add($"globals {DescribeVacGlobals()}");
        foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (!renderer || !renderer.HasPropertyBlock())
                continue;
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            parts.Add($"block {renderer.gameObject.name} pos={block.GetVector(VacVisualWorldPositionId).ToString("F2")} " +
                      $"dist={block.GetFloat(VacWindDistanceId):F3} root={root.transform.position.ToString("F2")}");
        }
        return string.Join(" | ", parts);
    }
#endif

    private static void Trace(string message)
    {
#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_PLAYER_FX") == "1")
            SrLogger.LogMessage($"[SR2MP_TEST] remote vacuum visual {message}");
#endif
    }
}

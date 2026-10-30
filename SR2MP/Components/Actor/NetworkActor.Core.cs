using System.Collections;
using Il2CppInterop.Runtime.Attributes;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Player.CharacterController;
using Il2CppMonomiPark.SlimeRancher.Regions;
using Il2CppMonomiPark.SlimeRancher.Slime;
using JetBrains.Annotations;
using SR2MP.Packets.Actor;
using SR2MP.Shared.Managers;
using SR2MP.Shared.Utils;
using Starlight.Storage;
using Delegate = Il2CppSystem.Delegate;
using Type = Il2CppSystem.Type;

namespace SR2MP.Components.Actor;

[InjectIntoIL]
internal sealed partial class NetworkActor : MonoBehaviour
{
    public  RegionMember? RegionMember;
    private Identifiable identifiable;
    private ResourceCycle? cycle;
    private Rigidbody rigidbody;
    private SlimeEmotions emotions;
    private PlortModel? plortModel;

    public float SyncTimer = Timers.ActorTimer;
    public bool  ShouldUpdateResourceState;
    public bool  IsValid = true;
    public bool  IsDestroyed;
    // Scene transitions destroy and recreate actor GameObjects from the persistent
    // world model. That cleanup must not be broadcast as real actor destruction.
    internal bool SuppressDestroyPacket;
    public byte  AttemptedGetIdentifiable;
    public bool  CachedLocallyOwned;

    public string CurrentOwnerId = string.Empty;

    /// <summary>Ownership epoch of this actor (mirrors ActorManager.EpochByActorId; host-issued).</summary>
    internal uint Epoch => ActorManager.GetEpoch(ActorId.Value);

    private float lastOwnerHeardTime;
    private float ownerSilenceThreshold;
    private const float OwnerSilenceMin = 5f;
    private const float OwnerSilenceMax = 11f;

    internal bool OwnerRecentlyHeard => UnityEngine.Time.time - lastOwnerHeardTime < ownerSilenceThreshold;

    private void UpdateOwnerValidity() => lastOwnerHeardTime = UnityEngine.Time.time;

    private void ResetOwnerValidityTime()
    {
        lastOwnerHeardTime = UnityEngine.Time.time;
        ownerSilenceThreshold = UnityEngine.Random.Range(OwnerSilenceMin, OwnerSilenceMax);
    }

    private bool? CycleReleasing => cycle?._preparingToRelease;
    private bool? cachedCycleReleasing;

    private Vector3 savedVelocity;
    private bool localVacuumPrediction;

    internal bool IsLocalVacuumPrediction => localVacuumPrediction;

    internal bool isSlime;
    internal bool isResource;
    internal bool isPlort;

    public ActorId ActorId
    {
        get
        {
            if (IsDestroyed)
            {
                IsValid = false;
                return new ActorId(0);
            }

            if (identifiable != null)
                return GetActorIdSafe();

            if (AttemptedGetIdentifiable >= 10)
            {
                SrLogger.LogWarning("Failed to get Identifiable after 10 attempts");
                IsValid = false;
                return new ActorId(0);
            }

            try
            {
                identifiable = GetComponent<Identifiable>();
                AttemptedGetIdentifiable++;
            }
            catch (Exception ex)
            {
                SrLogger.LogWarning($"Failed to get Identifiable component: {ex.Message}");
                AttemptedGetIdentifiable++;
                IsValid = false;
                return new ActorId(0);
            }

            return identifiable ? GetActorIdSafe() : new ActorId(0);
        }
    }

    private ActorId GetActorIdSafe()
    {
        try
        {
            return identifiable.GetActorId();
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"Failed to get ActorId: {ex.Message}");
            IsValid = false;
            return new ActorId(0);
        }
    }

    public bool LocallyOwned { get; set; }

    private void InitializeComponents()
    {
        emotions     = GetComponent<SlimeEmotions>();
        rigidbody    = GetComponent<Rigidbody>();
        identifiable = GetComponent<Identifiable>();
        cycle        = GetComponent<ResourceCycle>();
        RegionMember = GetComponent<RegionMember>();

        InitializeLabyrinthComponents();
    }
    
    public void Start()
    {
        try
        {
            if (GetComponent<Gadget>() || GetComponent<SRCharacterController>())
            {
                Destroy(this);
                return;
            }

            InitializeComponents();

            CachedLocallyOwned = LocallyOwned;
            ResetOwnerValidityTime();

            GetActorType();

            SetRigidbodyState(LocallyOwned);
            ApplyLabyrinthSimulationGate();

            if (RegionMember != null)
                SetupHibernationEvent();
        }
        catch (Exception ex)
        {
            SrLogger.LogError($"NetworkActor.Start error: {ex}");
            IsValid = false;
        }
    }

    private void GetActorType()
    {
        if (ActorId.Value == 0 || !GameState.identifiables.TryGetValue(ActorId, out var identModel))
            return;

        isSlime    = identModel.TryCast<SlimeModel>()   != null;
        isResource = identModel.TryCast<ProduceModel>() != null;
        isPlort    = identModel.TryCast<PlortModel>()   != null;
    }

    private void SetupHibernationEvent()
    {
        try
        {
            var delegateType = Type.GetType("MonomiPark.SlimeRancher.Regions.RegionMember")
                ?.GetEvent("BeforeHibernationChanged")
                ?.EventHandlerType;

            if (delegateType == null)
                return;

            var hibernationDelegate = Delegate.CreateDelegate(
                delegateType,
                Cast<Il2CppSystem.Object>(),
                nameof(HibernationChanged),
                true);

            RegionMember?.add_BeforeHibernationChanged(hibernationDelegate.Cast<RegionMember.OnHibernationChange>());
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"Failed to add hibernation event: {ex.Message}");
        }
    }

    [HideFromIl2Cpp]
    private IEnumerator WaitOneFrameOnHibernationChange(bool hibernating)
    {
        yield return null;

        if (!IsValid || IsDestroyed)
            yield break;

        try
        {
            var actorId = ActorId;

            if (actorId.Value == 0)
                yield break;

            if (hibernating)
            {
                var releasedOwned = LocallyOwned;
                LocallyOwned = false;
                if (releasedOwned && Main.Server.IsRunning)
                {
                    // The host's own release never passes through the unload handler, so record it here.
                    NetworkActorManager.MarkReleasedByOwner(actorId.Value, LocalID);
                    OwnershipArbiter.EvaluateNow(actorId.Value);
                }
                Main.SendToAllOrServer(new ActorUnloadPacket
                {
                    ActorId  = actorId,
                    SenderId = LocalID
                });
            }
            else
            {
                // Waking never claims. Keep the current owner's view and tell the host we are interested; the
                // arbiter decides and the normal ownership broadcast updates LocallyOwned.
                LocallyOwned = CurrentOwnerId == LocalID;
                NetworkActorManager.ExpressOwnershipInterest(actorId, CurrentOwnerId, force: true);
            }
        }
        catch (Exception ex)
        {
            SrLogger.LogError($"WaitOneFrameOnHibernationChange error: {ex}");
            IsValid = false;
        }
    }

    public void HibernationChanged(bool value)
    {
        if (!IsValid || IsDestroyed)
            return;

        try
        {
            ContextShortcuts.StartCoroutine(WaitOneFrameOnHibernationChange(value));
        }
        catch (Exception ex)
        {
            SrLogger.LogError($"HibernationChanged error: {ex}");
        }
    }

    public void Update()
    {
        if (IsDestroyed)
            return;

        if (!IsValid)
        {
            IsDestroyed = true;
            Destroy(this);
            return;
        }

        try
        {
            UpdateResourceState();
            HandleOwnershipChange();
            HandleCycleReleasing();

            UpdateInterpolation();
            TraceVisibilityTransition();

            SyncTimer -= UnityEngine.Time.unscaledDeltaTime;

            if (SyncTimer >= 0)
                return;

            SyncTimer = Timers.ActorTimer;

            if (LocallyOwned)
            {
                SendStateUpdate();
                SendWorldUpdate();
            }
            else if (IsLocalVacuumPrediction &&
                     NetworkActorCaptureManager.ShouldReplicateApprovedConsumePull(ActorId.Value))
            {
                // Native suction is predicted on the guest for a host-owned
                // actor. Publish that motion while the host-approved consume is
                // pending so observers do not see a stationary object vanish.
                SendWorldUpdate();
            }
        }
        catch (Exception ex)
        {
            SrLogger.LogError($"NetworkActor.Update error: {ex}");
            IsValid = false;
        }
    }

    private void HandleOwnershipChange()
    {
        if (CachedLocallyOwned != LocallyOwned)
        {
            if (LocallyOwned)
                localVacuumPrediction = false;

            SetRigidbodyState(LocallyOwned);
            ApplyLabyrinthSimulationGate();

            if (LocallyOwned && rigidbody)
                rigidbody.velocity = savedVelocity;

            if (LocallyOwned)
                RestoreStateOnOwnership();
            else
                ResetOwnerValidityTime();
        }

        CachedLocallyOwned = LocallyOwned;
    }

    private void SetRigidbodyState(bool locallyOwned)
    {
        if (rigidbody == null || IsDestroyed)
            return;

        try
        {
            rigidbody.constraints = locallyOwned || localVacuumPrediction
                ? RigidbodyConstraints.None
                : RigidbodyConstraints.FreezeAll;
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"SetRigidbodyState error: {ex.Message}");
        }
    }

    /// <summary>
    /// Lets the local player's vanilla vacuum physics pull a visible remote-owned
    /// actor while the host still owns and replicates its authoritative state. The
    /// override ends as soon as the vacuum stops targeting this actor; a native
    /// capture/consume then uses the existing host-approved ownership transaction.
    /// </summary>
    internal void SetLocalVacuumPrediction(bool active)
    {
        if (active && (LocallyOwned || IsDestroyed || !IsValid))
            active = false;

        if (localVacuumPrediction == active)
            return;

        localVacuumPrediction = active;
        SetRigidbodyState(LocallyOwned);
        var diagnosticActorId = ActorId;
        Logger.LogDiagnostic("vacuum", active ? "prediction-started" : "prediction-stopped",
            $"actorId={diagnosticActorId.Value:X}; actor={gameObject.name}; owner={CurrentOwnerId}; " +
            $"position={transform.position}; velocity={(rigidbody ? rigidbody.velocity : Vector3.zero)}; " +
            $"bodyKinematic={(rigidbody ? rigidbody.isKinematic : true)}");

#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") == "1")
        {
            var actorId = ActorId;
            SrLogger.LogMessage(
                $"[SR2MP_TEST] actor-local-vacuum-prediction actor={actorId.Value:X} " +
                $"active={localVacuumPrediction} owner={CurrentOwnerId} position={transform.position}");
        }
#endif
    }

    [UsedImplicitly]
    public void OnDestroy()
    {
        // ActorId returns zero once IsDestroyed is true, so preserve the ID
        // before marking this component as destroyed. Scene-transition cleanup
        // already suppresses packet sending and does not need to resolve it.
        var actorId = SuppressDestroyPacket ? default : ActorId;
        IsDestroyed = true;
        IsValid     = false;

        ResetLabyrinthToVanilla();

        if (SuppressDestroyPacket)
            return;

        var locallyOwnedById = string.Equals(CurrentOwnerId, LocalID, StringComparison.Ordinal);
        if (!locallyOwnedById && actorId.Value != 0 &&
            ActorManager.OwnershipByActorId.TryGetValue(actorId.Value, out var cachedOwnerId))
            locallyOwnedById = string.Equals(cachedOwnerId, LocalID, StringComparison.Ordinal);

        if (locallyOwnedById)
        {
            if (SystemContext.Instance != null && SystemContext.Instance.SceneLoader != null && SystemContext.Instance.SceneLoader.IsSceneLoadInProgress)
                return;

            try
            {
                if (actorId.Value != 0)
                {
                    ActorManager.Actors.Remove(actorId.Value);
                    ActorManager.OwnershipByActorId.Remove(actorId.Value);
                    ActorManager.EpochByActorId.Remove(actorId.Value);

                    if (Main.Server.IsRunning || Main.Client.IsConnected)
                    {
                        var packet = new ActorDestroyPacket { ActorId = actorId, OwnerId = LocalID };
                        Main.SendToAllOrServer(packet);
                    }
                }
            }
            catch
            {
                // ignored
            }
        }
    }
}

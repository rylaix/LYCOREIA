using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Packets.TreasurePod;
using SR2MP.Packets.Utils;

namespace SR2MP.Shared.Managers;

internal static class NetworkTreasurePodManager
{
    private static readonly Dictionary<int, Il2Cpp.TreasurePod.State> PendingStates = new();
    private static readonly HashSet<int> PendingHostOpenRequests = new();
    private static readonly HashSet<int> HostOpenInProgress = new();
    private static readonly HashSet<int> HostOpenUncertain = new();
    private static readonly Dictionary<int, uint> PendingClientOpenRequests = new();
    private static readonly Dictionary<int, System.Net.IPEndPoint> DelegatedHostOpens = new();
    private static readonly Dictionary<int, string> ModelKeys = new();
    private static readonly HashSet<int> VisualOpenInstances = new();
    private static readonly HashSet<int> NetworkOpenIds = new();
    private static int clientGrantedActivation = -1;
    private static uint nextClientOpenRequestId = 1;
    private static readonly Dictionary<int, TreasurePodPacket.RewardSnapshot> HostRewardsByPod = new();
    private static readonly HashSet<int> HostOpenStatePublished = new();
    private static readonly HashSet<int> HostRewardPayloadPublished = new();
    private static readonly Dictionary<int, TreasurePodPacket.RewardSnapshot> PendingClientRewardsByPod = new();
    private static readonly HashSet<(int PodId, string ReferenceId)> AppliedBlueprintRewards = new();
    private static readonly HashSet<(int PodId, string ReferenceId)> ReportedBlueprintFailures = new();
    private static readonly HashSet<(int PodId, string SlimeDefinitionReferenceId, Il2Cpp.SlimeAppearance.AppearanceSaveSet SaveSet)> AppliedAppearanceRewards = new();
    private static readonly HashSet<(int PodId, string SlimeDefinitionReferenceId, Il2Cpp.SlimeAppearance.AppearanceSaveSet SaveSet)> ReportedAppearanceFailures = new();

    static NetworkTreasurePodManager()
    {
        Main.Server.OnServerStarted += ResetSession;
        Main.Client.OnConnected += _ => ResetSession();
        Main.Client.OnDisconnected += ResetSession;

#if SR2MP_TEST
        var rulesPassed = TreasurePodOpenPolicy.VerifyRules();
        var codecPassed = VerifyPacketRoundTrip();
        var rewardPolicyPassed = VerifyRewardIdempotence();
        var appearancePolicyPassed = VerifyAppearanceRewardIdempotence();
        var requestPolicyPassed = TreasurePodOpenPolicy.VerifyRules();
        SrLogger.LogMessage(
            $"[SR2MP_TEST] treasure-pod-open-rules={(rulesPassed ? "PASS" : "FAIL")} " +
            $"treasure-pod-request-policy={(requestPolicyPassed ? "PASS" : "FAIL")} " +
            $"treasure-pod-packet-codec={(codecPassed ? "PASS" : "FAIL")} " +
            $"treasure-pod-reward-idempotence={(rewardPolicyPassed ? "PASS" : "FAIL")} " +
            $"treasure-pod-appearance-idempotence={(appearancePolicyPassed ? "PASS" : "FAIL")}");
#endif
    }

    internal static bool TryGetPodId(Il2Cpp.TreasurePod? pod, out int id)
    {
        id = -1;
        if (!pod || string.IsNullOrWhiteSpace(pod._id))
            return false;

        var rawId = pod._id.StartsWith("pod", StringComparison.Ordinal)
            ? pod._id[3..]
            : pod._id;

        return int.TryParse(rawId, out id) && id >= 0;
    }

    internal static void RecordLocalOpen(Il2Cpp.TreasurePod pod)
    {
        if (!IsNetworkingActive() || !TryGetPodId(pod, out var id))
            return;

        // The local game has already run Activate() and produced its reward.
        // Only discard an older queued state; incoming open state is model-only.
        PendingStates.Remove(id);
    }

    internal static void RequestHostOpen(int id)
    {
        if (!Main.Client.IsConnected || id < 0 || PendingClientOpenRequests.ContainsKey(id))
            return;

        var requestId = nextClientOpenRequestId++;
        if (requestId == 0)
            requestId = nextClientOpenRequestId++;
        PendingClientOpenRequests[id] = requestId;
        Main.Client.SendPacket(new TreasurePodPacket { ID = id, RequestId = requestId });
        Logger.LogDiagnostic("pod", "open-requested", $"pod={id}; request={requestId}");
    }

    internal static void HandleHostOpenRejection(int id, uint requestId)
    {
        if (requestId == 0 ||
            !PendingClientOpenRequests.TryGetValue(id, out var pendingRequestId) ||
            !TreasurePodOpenPolicy.IsMatchingClientOpenRequest(pendingRequestId, requestId))
            return;

        PendingClientOpenRequests.Remove(id);
        SrLogger.LogWarning($"Host rejected Treasure Pod open request for {id}; the guest may retry after state changes.");
    }

    /// <summary>Accepts a guest's request, then lets only the host run native Activate().</summary>
    internal static bool HandleGuestOpenRequest(int id, uint requestId, System.Net.IPEndPoint? clientEndpoint)
    {
        var authenticated = clientEndpoint != null &&
                           Main.Server.ClientManager.TryGetClient(clientEndpoint, out var sender) && sender != null;
        var knownPod = false;
        var isOpen = false;
        if (id >= 0 && TryGetModel(id, out var model) && model != null)
        {
            knownPod = true;
            isOpen = model.state == Il2Cpp.TreasurePod.State.OPEN;
        }
        var loaded = TryGetLoadedPod(id, out var pod);
        Logger.LogDiagnostic("pod", "request-received",
            $"pod={id}; known={knownPod}; open={isOpen}; loaded={loaded}; inProgress={HostOpenInProgress.Contains(id)}");
        var action = TreasurePodOpenPolicy.DecideHostRequest(
            Main.Server.IsRunning,
            authenticated,
            knownPod,
            isOpen,
            HostOpenInProgress.Contains(id),
            HostOpenUncertain.Contains(id),
            loaded);

        if (action == TreasurePodOpenRequestAction.SendOpenState)
        {
            if (!PublishOpenStateIfNeeded(id))
                Main.Server.SendToClient(CreateOpenPacket(id), clientEndpoint!);
            return false;
        }

        if (action == TreasurePodOpenRequestAction.Reject)
        {
            if (authenticated && clientEndpoint != null && requestId != 0)
            {
                Main.Server.SendToClient(new TreasurePodPacket
                {
                    ID = id,
                    RequestId = requestId,
                    IsRejectedResponse = true
                }, clientEndpoint);
            }

            if (HostOpenUncertain.Contains(id))
                SrLogger.LogWarning($"Treasure pod {id} has an uncertain prior activation; refusing to replay its payout.");
            return false;
        }

        PendingHostOpenRequests.Add(id);
        if (action == TreasurePodOpenRequestAction.ActivateOnHost)
            OpenOnHost(pod!, id);
        else if (action == TreasurePodOpenRequestAction.WaitForCurrentActivation)
        {
            // The in-flight activation will broadcast its commit once complete.
        }
        else if (action == TreasurePodOpenRequestAction.QueueUntilLoaded)
        {
            // The host is not near this pod, so it cannot run Activate(). The requesting guest has it
            // loaded: let that guest run the native payout once and report the rewards back.
            PendingHostOpenRequests.Remove(id);
            HostOpenInProgress.Add(id);
            DelegatedHostOpens[id] = clientEndpoint!;
            Main.Server.SendToClient(new TreasurePodPacket
            {
                ID = id,
                RequestId = requestId,
                ClientActivated = true
            }, clientEndpoint!);
        }

        return false; // This request is never relayed as committed state.
    }

    /// <summary>
    /// A pod someone else opened live: grant its blueprint through GadgetDirector.AddBlueprint so the
    /// same reward popup the opener saw ("Pink Warp Storage") shows here too. Already-owned blueprints
    /// (join snapshot, progression sync, own delegated payout) return false and stay silent.
    /// </summary>
    internal static void ShowRewardPopups(int id, TreasurePodPacket.RewardSnapshot? rewards)
    {
        if (rewards == null)
            return;

        // Only a live open: pods already open here (save/join snapshot, host re-publishing an old pod
        // when it loads, or our own delegated payout which showed the native popup) stay silent.
        var alreadyOpen = TryGetModel(id, out var model)
            ? model?.state?.Value == Il2Cpp.TreasurePod.State.OPEN
            : PendingStates.TryGetValue(id, out var pending) && pending == Il2Cpp.TreasurePod.State.OPEN;
        if (alreadyOpen)
            return;

        ShowComponentPopups(id, rewards.UpgradeComponentReferenceIds);
        ShowAppearancePopups(id, rewards.AppearanceRewards);
        if (rewards.BlueprintReferenceIds.Count == 0)
            return;

        var director = SceneContext.Instance ? SceneContext.Instance.GadgetDirector : null;
        if (!director)
            return;

        foreach (var referenceId in rewards.BlueprintReferenceIds)
        {
            if (!IsSafeReferenceId(referenceId))
                continue;

            Il2Cpp.GadgetDefinition? definition = null;
            foreach (var type in ActorManager.ActorTypes.Values)
            {
                var gadget = type ? type.TryCast<Il2Cpp.GadgetDefinition>() : null;
                if (gadget && string.Equals(gadget!.ReferenceId, referenceId, StringComparison.Ordinal))
                {
                    definition = gadget;
                    break;
                }
            }

            if (!definition || director!.HasBlueprint(definition))
                continue;

            var wasHandlingPacket = HandlingPacket;
            HandlingPacket = true;
            try
            {
                // AddBlueprint's own notification is skipped when the gadget's pedia entry is already
                // unlocked (the opener's PediaUnlock packet often lands first), so it is added silently
                // and the pedia popup the opener saw is shown explicitly.
                var added = director.AddBlueprint(definition, false);
                var pedia = SceneContext.Instance.PediaDirector;
                var entry = pedia ? pedia!.GetEntry(definition) : null;
                var shown = false;
                if (entry)
                {
                    if (!pedia!.IsUnlocked(entry))
                        pedia.Unlock(entry, false);
                    pedia.ShowPopupIfUnlocked(entry);
                    shown = true;
                }
                Logger.LogDiagnostic("pod", "reward-popup",
                    $"pod={id}; blueprint={referenceId}; added={added}; pediaPopup={shown}");
            }
            catch (Exception ex)
            {
                SrLogger.LogWarning($"Could not show Treasure Pod {id} reward '{referenceId}': {ex.Message}");
            }
            finally
            {
                HandlingPacket = wasHandlingPacket;
            }
        }
    }

    /// <summary>Upgrade components have no native "show" call: rebuild the pod's preview popup.</summary>
    private static void ShowComponentPopups(int id, List<string> referenceIds)
    {
        if (referenceIds.Count == 0)
            return;

        var popups = SceneContext.Instance ? SceneContext.Instance.PopupDirector : null;
        if (!popups)
            return;

        foreach (var referenceId in referenceIds)
        {
            if (!IsSafeReferenceId(referenceId))
                continue;

            foreach (var component in Resources.FindObjectsOfTypeAll<Il2CppMonomiPark.SlimeRancher.Player.Component.UpgradeComponent>())
            {
                if (!component || !string.Equals(component.ReferenceId, referenceId, StringComparison.Ordinal))
                    continue;

                var config = popups!.ComponentPopupConfig;
                EnqueuePreviewPopup(popups, config
                    ? new Il2CppMonomiPark.SlimeRancher.UI.Popup.PreviewPopupViewModel(component, config)
                    : null, id, referenceId);
                break;
            }
        }
    }

    private static void ShowAppearancePopups(int id, List<TreasurePodPacket.RewardSnapshot.AppearanceReward> rewards)
    {
        if (rewards.Count == 0)
            return;

        var popups = SceneContext.Instance ? SceneContext.Instance.PopupDirector : null;
        var director = SceneContext.Instance ? SceneContext.Instance.SlimeAppearanceDirector : null;
        var definitions = director ? director!.SlimeDefinitions?.Slimes : null;
        if (!popups || definitions == null)
            return;

        foreach (var reward in rewards)
        {
            if (!IsValidAppearanceReward(reward))
                continue;

            foreach (var definition in definitions)
            {
                if (!definition || !string.Equals(definition!.ReferenceId, reward.SlimeDefinitionReferenceId,
                        StringComparison.Ordinal))
                    continue;

                var appearance = definition.GetAppearanceForSet(reward.SaveSet);
                // No "already unlocked" skip: progression sync may land first; the pod-not-open gate
                // in ShowRewardPopups already prevents duplicates.
                if (appearance && appearance!.SaveSet == reward.SaveSet)
                {
                    var config = popups!.SlimeAppearancePopupConfig;
                    EnqueuePreviewPopup(popups, config
                        ? new Il2CppMonomiPark.SlimeRancher.UI.Popup.PreviewPopupViewModel(appearance, config)
                        : null, id, $"{reward.SlimeDefinitionReferenceId}/{reward.SaveSet}");
                }
                break;
            }
        }
    }

    /// <summary>Same view-model constructors the native pod uses, so the popup looks identical.</summary>
    private static void EnqueuePreviewPopup(
        Il2CppMonomiPark.SlimeRancher.UI.Popup.PopupDirector popups,
        Il2CppMonomiPark.SlimeRancher.UI.Popup.PreviewPopupViewModel? model,
        int podId,
        string reward)
    {
        if (model == null)
            return;

        try
        {
            popups.EnqueuePopup(ref model);
            Logger.LogDiagnostic("pod", "reward-popup", $"pod={podId}; reward={reward}; added=True");
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"Could not show Treasure Pod {podId} reward popup '{reward}': {ex.Message}");
        }
    }

    internal static bool IsClientActivationGranted(int id)
        => id >= 0 && clientGrantedActivation == id;

    /// <summary>Guest: the host delegated this pod's activation to us.</summary>
    internal static void HandleHostActivationGrant(int id, uint requestId)
    {
        if (!Main.Client.IsConnected ||
            !PendingClientOpenRequests.TryGetValue(id, out var pendingRequestId) ||
            !TreasurePodOpenPolicy.IsMatchingClientOpenRequest(pendingRequestId, requestId))
            return;

        PendingClientOpenRequests.Remove(id);
        if (!TryGetLoadedPod(id, out var pod))
        {
            SrLogger.LogWarning($"Host delegated Treasure Pod {id} but it is no longer loaded here.");
            Main.Client.SendPacket(new TreasurePodPacket { ID = id, ClientActivated = true, IsRejectedResponse = true });
            return;
        }

        var rewards = CaptureConfiguredRewards(pod!);
        clientGrantedActivation = id;
        // Called from a packet handler: the native payout (item spawns, components) must publish.
        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = false;
        try
        {
            pod!.Activate();
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
            clientGrantedActivation = -1;
        }

        Logger.LogDiagnostic("pod", "delegated-activated", $"pod={id}; blueprints={rewards.BlueprintReferenceIds.Count}");

        Main.Client.SendPacket(new TreasurePodPacket { ID = id, ClientActivated = true, Rewards = rewards });
    }

    /// <summary>Host: a guest finished a delegated activation; commit and broadcast it.</summary>
    internal static bool HandleGuestActivationReport(
        int id,
        TreasurePodPacket.RewardSnapshot rewards,
        System.Net.IPEndPoint? clientEndpoint)
    {
        if (!Main.Server.IsRunning || clientEndpoint == null ||
            !DelegatedHostOpens.TryGetValue(id, out var delegatedTo) || !delegatedTo.Equals(clientEndpoint))
            return false;

        DelegatedHostOpens.Remove(id);
        HostOpenInProgress.Remove(id);
        var normalized = NormalizeRewardSnapshot(rewards);
        HostRewardsByPod[id] = normalized;
        ShowRewardPopups(id, normalized);
        ApplyState(id, Il2Cpp.TreasurePod.State.OPEN, normalized);
        PublishOpenStateIfNeeded(id);
        return false;
    }

    /// <summary>Host: the delegated guest could not activate; free the pod for the next request.</summary>
    internal static void HandleGuestActivationDeclined(int id, System.Net.IPEndPoint? clientEndpoint)
    {
        if (!Main.Server.IsRunning || clientEndpoint == null ||
            !DelegatedHostOpens.TryGetValue(id, out var delegatedTo) || !delegatedTo.Equals(clientEndpoint))
            return;

        DelegatedHostOpens.Remove(id);
        HostOpenInProgress.Remove(id);
    }

    internal static bool ApplyState(
        int id,
        Il2Cpp.TreasurePod.State state,
        TreasurePodPacket.RewardSnapshot? rewards = null)
    {
        if (id < 0)
            return false;

        if (state == Il2Cpp.TreasurePod.State.OPEN)
            PendingClientOpenRequests.Remove(id);

        if (state != Il2Cpp.TreasurePod.State.OPEN)
        {
            if (!TryGetModel(id, out var closedModel))
            {
                PendingStates[id] = state;
                return true;
            }

            PendingStates.Remove(id);
            SetModelState(id, closedModel!, state);
            return true;
        }

        var hasModel = TryGetModel(id, out var model) && model != null;
        var action = TreasurePodOpenPolicy.DecideOpenAction(hasModel);

        switch (action)
        {
            case TreasurePodOpenAction.CacheUntilModel:
                PendingStates[id] = Il2Cpp.TreasurePod.State.OPEN;
                CacheClientRewards(id, rewards);
                ApplyPendingRewardsForPod(id);
                return true;

            case TreasurePodOpenAction.MarkOpenModelOnly:
                SetModelState(id, model!, Il2Cpp.TreasurePod.State.OPEN);
                PendingStates.Remove(id);
                CacheClientRewards(id, rewards);
                ApplyPendingRewardsForPod(id);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Save keys keep leading zeros ("pod0118409926") while the wire id is an int, so "pod{id}" misses
    /// such pods on every peer. Resolve the real key once and cache it.
    /// </summary>
    internal static bool TryGetModel(int id, out TreasurePodModel? model)
    {
        model = null;
        var pods = GameState?.pods;
        if (id < 0 || pods == null)
            return false;

        if (ModelKeys.TryGetValue(id, out var key) && pods.TryGetValue(key, out model) && model != null)
            return true;

        foreach (var entry in pods)
        {
            if (entry.value == null || !TryParsePodKey(entry.key, out var parsed) || parsed != id)
                continue;

            ModelKeys[id] = entry.key;
            model = entry.value;
            return true;
        }

        model = null;
        return false;
    }

    private static bool TryParsePodKey(string? key, out int id)
    {
        id = -1;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var rawId = key.StartsWith("pod", StringComparison.Ordinal) ? key[3..] : key;
        return int.TryParse(rawId, out id) && id >= 0;
    }

    /// <summary>
    /// The pod view does not observe its model: it redraws only when _forceUpdate is set (native
    /// Activate/load do this). A state received from the network must raise it explicitly, otherwise
    /// the pod reports OPEN but stays visually closed and its button does nothing.
    /// </summary>
    private static void SetModelState(int id, TreasurePodModel model, Il2Cpp.TreasurePod.State state)
    {
        if (model.state == null)
            model.state = new ObservableValue<Il2Cpp.TreasurePod.State>(state);
        else if (model.state.Value != state)
            model.state.Set(state);

        if (state == Il2Cpp.TreasurePod.State.OPEN)
            NetworkOpenIds.Add(id);
        else
            NetworkOpenIds.Remove(id);

        var loaded = TryGetLoadedPod(id, out var pod);
        var sameModel = false;
        if (loaded && clientGrantedActivation != id)
        {
            var podModel = pod!._model;
            sameModel = podModel != null && podModel.Pointer == model.Pointer;
            if (podModel != null && !sameModel && podModel.state != null && podModel.state.Value != state)
                podModel.state.Set(state);

            // Live event on a visible pod: animate. Pods loading later snap open in ApplyPendingForLoadedPod.
            RefreshView(pod!, state, immediate: false);
        }

        Logger.LogDiagnostic("pod", "state-applied",
            $"pod={id}; state={state}; loaded={loaded}; sameModel={sameModel}; " +
            $"role={(Main.Server.IsRunning ? "host" : "client")}");
    }

    private static void RefreshView(Il2Cpp.TreasurePod pod, Il2Cpp.TreasurePod.State state, bool immediate)
    {
        try
        {
            if (immediate)
                pod.UpdateImmediate(state);
            pod._nextUpdateImmediate = immediate;
            pod._forceUpdate = true;
            if (state == Il2Cpp.TreasurePod.State.OPEN)
                VisualOpenInstances.Add(pod.GetInstanceID());
            else
                VisualOpenInstances.Remove(pod.GetInstanceID());
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"Could not refresh treasure pod view {pod._id}: {ex.Message}");
        }
    }

    /// <summary>A re-enabled pod resets its animator to closed; let the next update redraw it.</summary>
    internal static void OnPodEnabled(Il2Cpp.TreasurePod pod)
    {
        if (pod)
            VisualOpenInstances.Remove(pod.GetInstanceID());
    }

    internal static void ApplyPendingForLoadedPod(Il2Cpp.TreasurePod pod)
    {
        if (!TryGetPodId(pod, out var id))
            return;

        if (Main.Server.IsRunning && PendingHostOpenRequests.Contains(id))
        {
            OpenOnHost(pod, id);
            return;
        }

        if (Main.Server.IsRunning &&
            TryGetModel(id, out var loadedModel) &&
            loadedModel?.state == Il2Cpp.TreasurePod.State.OPEN)
            PublishOpenStateIfNeeded(id, pod);

        if (PendingStates.TryGetValue(id, out var state))
            ApplyState(id, state);

        // A pod opened through the network must look open once per enable (native open paths
        // redraw themselves and are not touched, so their animation is never cut short).
        if (NetworkOpenIds.Contains(id) && clientGrantedActivation != id &&
            !VisualOpenInstances.Contains(pod.GetInstanceID()) &&
            pod.gameObject && pod.gameObject.activeInHierarchy)
        {
            if (pod._model != null && pod._model.state != null &&
                pod._model.state.Value != Il2Cpp.TreasurePod.State.OPEN)
                pod._model.state.Set(Il2Cpp.TreasurePod.State.OPEN);
            RefreshView(pod, Il2Cpp.TreasurePod.State.OPEN, immediate: true);
            Logger.LogDiagnostic("pod", "view-opened-on-load", $"pod={id}");
        }

        if (PendingClientRewardsByPod.TryGetValue(id, out var rewards))
            ApplyRewardsToLoadedPod(id, pod, rewards);
    }

    internal static void RecordHostOpen(int id)
    {
        if (!Main.Server.IsRunning || id < 0)
            return;

        PendingHostOpenRequests.Remove(id);
        HostOpenUncertain.Remove(id);
        PendingStates.Remove(id);
        ApplyState(id, Il2Cpp.TreasurePod.State.OPEN);
        PublishOpenStateIfNeeded(id);
    }

    /// <summary>
    /// Host activation owns the native reward transaction. Its local Harmony postfix
    /// must not publish a second commit while this manager is still executing it.
    /// </summary>
    internal static bool IsHostActivationInProgress(int id)
        => id >= 0 && HostOpenInProgress.Contains(id);

    /// <summary>Copies the host's open-pod state and known per-player reward IDs for late join.</summary>
    internal static void PopulateInitialRewardSnapshot(InitialTreasurePodsPacket packet)
    {
        packet.RewardsByPod.Clear();

        foreach (var (podId, state) in packet.TreasurePods)
        {
            if (state != Il2Cpp.TreasurePod.State.OPEN)
                continue;

            if (!HostRewardsByPod.TryGetValue(podId, out var rewards))
            {
                if (!TryGetLoadedPod(podId, out var pod))
                {
                    SrLogger.LogWarning(
                        $"Treasure pod {podId} is already open but was never observed by this session and is not loaded; " +
                        "its configured per-player rewards cannot be reconstructed for the join snapshot.");
                    continue;
                }

                rewards = CaptureConfiguredRewards(pod!);
                HostRewardsByPod[podId] = rewards;
            }

            // Upgrade-component totals have their own authoritative full snapshot
            // and reliable absolute-count updates. Do not replay old per-pod counts
            // here: that could overwrite a newer count across network channels.
            var snapshotRewards = CloneRewardSnapshot(rewards);
            if (snapshotRewards.HasSupportedRewards)
                packet.RewardsByPod[podId] = snapshotRewards;
        }
    }

    private static void OpenOnHost(Il2Cpp.TreasurePod pod, int id)
    {
        if (!Main.Server.IsRunning || !pod || !PendingHostOpenRequests.Contains(id) ||
            !HostOpenInProgress.Add(id))
            return;

        try
        {
            if (TryGetModel(id, out var existingModel) &&
                existingModel?.state == Il2Cpp.TreasurePod.State.OPEN)
            {
                PendingHostOpenRequests.Remove(id);
                CacheHostRewardsFromLoadedPod(id, pod);
                PublishOpenStateIfNeeded(id, pod);
                return;
            }

            // Capture the reward IDs before native activation changes the pod state.
            HostRewardsByPod[id] = CaptureConfiguredRewards(pod);

            // Keep HandlingPacket false while native Activate runs. The game emits
            // actor spawns, component updates and pedia unlocks from this routine;
            // globally suppressing Harmony publication loses those host changes.
            pod.Activate();

            // Native Activate owns payout creation. Make the persisted model state
            // canonical without invoking Activate on any receiving peer.
            if (!ApplyState(id, Il2Cpp.TreasurePod.State.OPEN))
            {
                HostOpenUncertain.Add(id);
                PendingHostOpenRequests.Remove(id);
                SrLogger.LogWarning($"Treasure pod {id} activated but its model state could not be committed; automatic retry stopped.");
                return;
            }

            PendingHostOpenRequests.Remove(id);
            CacheHostRewardsFromLoadedPod(id, pod);
            PublishOpenStateIfNeeded(id, pod);
        }
        catch (Exception ex)
        {
            // Activation may have produced some rewards before failing. Never
            // replay a potentially partial payout automatically.
            PendingHostOpenRequests.Remove(id);
            var modelIsOpen = TryGetModel(id, out var failedModel) &&
                              failedModel?.state == Il2Cpp.TreasurePod.State.OPEN;
            if (TreasurePodOpenPolicy.ShouldCommitAfterActivationException(modelIsOpen))
            {
                try
                {
                    PendingStates.Remove(id);
                    HostOpenUncertain.Remove(id);
                    if (!ApplyState(id, Il2Cpp.TreasurePod.State.OPEN))
                        throw new InvalidOperationException("the open model state could not be committed");

                    CacheHostRewardsFromLoadedPod(id, pod);
                    PublishOpenStateIfNeeded(id, pod);
                    SrLogger.LogWarning(
                        $"Treasure pod {id} activation threw after its model reached OPEN; published the committed state " +
                        $"without replaying its potentially partial payout: {ex.Message}");
                }
                catch (Exception commitEx)
                {
                    HostOpenUncertain.Add(id);
                    SrLogger.LogWarning(
                        $"Treasure pod {id} activation threw after OPEN, but commit publication failed; " +
                        $"automatic retry stopped: {commitEx.Message}");
                }
            }
            else
            {
                HostOpenUncertain.Add(id);
                SrLogger.LogWarning(
                    $"Treasure pod {id} activation failed before its model reached OPEN; retry stopped to avoid " +
                    $"duplicating a potentially partial payout: {ex.Message}");
            }
        }
        finally
        {
            HostOpenInProgress.Remove(id);
        }
    }

    private static bool TryGetLoadedPod(int id, out Il2Cpp.TreasurePod? result)
    {
        result = null;
        foreach (var pod in Resources.FindObjectsOfTypeAll<Il2Cpp.TreasurePod>())
        {
            if (!pod || !TryGetPodId(pod, out var podId) || podId != id ||
                !pod.gameObject || !pod.gameObject.scene.IsValid() || !pod.gameObject.activeInHierarchy)
                continue;

            result = pod;
            return true;
        }

        return false;
    }

    private static TreasurePodPacket CreateOpenPacket(int id)
    {
        var rewards = HostRewardsByPod.TryGetValue(id, out var cached)
            ? CloneRewardSnapshot(cached)
            : new TreasurePodPacket.RewardSnapshot();
        return new TreasurePodPacket { ID = id, Rewards = rewards };
    }

    /// <summary>
    /// Publishes the open state once and publishes one later reward refresh if a
    /// saved, previously unloaded pod only becomes inspectable after its cell loads.
    /// </summary>
    private static bool PublishOpenStateIfNeeded(int id, Il2Cpp.TreasurePod? knownPod = null)
    {
        if (!Main.Server.IsRunning || id < 0)
            return false;

        CacheHostRewardsFromLoadedPod(id, knownPod);
        var hasKnownRewards = HostRewardsByPod.TryGetValue(id, out var rewards);
        var hasRewardPayload = hasKnownRewards && rewards!.HasSupportedRewards;
        var firstState = HostOpenStatePublished.Add(id);
        var firstRewardPayload = hasRewardPayload && HostRewardPayloadPublished.Add(id);
        if (!firstState && !firstRewardPayload)
            return false;

        Main.Server.SendToAll(CreateOpenPacket(id));
        Logger.LogDiagnostic("pod", "open-published", $"pod={id}; rewards={hasRewardPayload}");
        return true;
    }

    private static void CacheHostRewardsFromLoadedPod(int id, Il2Cpp.TreasurePod? knownPod = null)
    {
        if (id < 0)
            return;

        if (HostRewardsByPod.ContainsKey(id))
            return;

        if (knownPod && TryGetPodId(knownPod, out var knownId) && knownId == id)
        {
            HostRewardsByPod[id] = CaptureConfiguredRewards(knownPod);
            return;
        }

        if (TryGetLoadedPod(id, out var loadedPod))
            HostRewardsByPod[id] = CaptureConfiguredRewards(loadedPod!);
    }

    private static TreasurePodPacket.RewardSnapshot CaptureConfiguredRewards(Il2Cpp.TreasurePod pod)
    {
        var rewards = new TreasurePodPacket.RewardSnapshot();

        var blueprint = pod.Blueprint;
        if (blueprint && !string.IsNullOrWhiteSpace(blueprint!.ReferenceId))
            rewards.BlueprintReferenceIds.Add(blueprint.ReferenceId);

        var component = pod.UpgradeComponent;
        if (component && !string.IsNullOrWhiteSpace(component!._referenceId))
            rewards.UpgradeComponentReferenceIds.Add(component._referenceId);

        var unlockedAppearance = pod.UnlockedSlimeAppearance;
        var unlockedAppearanceDefinition = pod.UnlockedSlimeAppearanceDefinition;
        if (unlockedAppearance && unlockedAppearanceDefinition &&
            IsSupportedAppearanceSaveSet(unlockedAppearance!.SaveSet) &&
            IsSafeReferenceId(unlockedAppearanceDefinition!.ReferenceId))
        {
            rewards.AppearanceRewards.Add(new TreasurePodPacket.RewardSnapshot.AppearanceReward
            {
                SlimeDefinitionReferenceId = unlockedAppearanceDefinition.ReferenceId,
                SaveSet = unlockedAppearance.SaveSet
            });
        }

        return NormalizeRewardSnapshot(rewards);
    }

    private static TreasurePodPacket.RewardSnapshot CloneRewardSnapshot(TreasurePodPacket.RewardSnapshot source)
        => NormalizeRewardSnapshot(new TreasurePodPacket.RewardSnapshot
        {
            BlueprintReferenceIds = source.BlueprintReferenceIds?.ToList() ?? new List<string>(),
            UpgradeComponentReferenceIds = source.UpgradeComponentReferenceIds?.ToList() ?? new List<string>(),
            AppearanceRewards = source.AppearanceRewards?.Select(reward => new TreasurePodPacket.RewardSnapshot.AppearanceReward
            {
                SlimeDefinitionReferenceId = reward.SlimeDefinitionReferenceId,
                SaveSet = reward.SaveSet
            }).ToList() ?? new List<TreasurePodPacket.RewardSnapshot.AppearanceReward>()
        });

    private static TreasurePodPacket.RewardSnapshot NormalizeRewardSnapshot(TreasurePodPacket.RewardSnapshot source)
    {
        source.BlueprintReferenceIds = source.BlueprintReferenceIds
            .Where(IsSafeReferenceId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        source.UpgradeComponentReferenceIds = source.UpgradeComponentReferenceIds
            .Where(IsSafeReferenceId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        source.AppearanceRewards = source.AppearanceRewards
            .Where(IsValidAppearanceReward)
            .GroupBy(reward => (reward.SlimeDefinitionReferenceId, reward.SaveSet))
            .Select(group => group.First())
            .OrderBy(reward => reward.SlimeDefinitionReferenceId, StringComparer.Ordinal)
            .ThenBy(reward => reward.SaveSet)
            .ToList();
        return source;
    }

    private static bool IsSafeReferenceId(string? id)
        => !string.IsNullOrWhiteSpace(id) && id.Length <= 256;

    private static bool IsSupportedAppearanceSaveSet(Il2Cpp.SlimeAppearance.AppearanceSaveSet saveSet)
        => saveSet != Il2Cpp.SlimeAppearance.AppearanceSaveSet.NONE &&
           Enum.IsDefined(typeof(Il2Cpp.SlimeAppearance.AppearanceSaveSet), saveSet);

    private static bool IsValidAppearanceReward(TreasurePodPacket.RewardSnapshot.AppearanceReward? reward)
        => reward != null && IsSafeReferenceId(reward.SlimeDefinitionReferenceId) &&
           IsSupportedAppearanceSaveSet(reward.SaveSet);

    private static void CacheClientRewards(int podId, TreasurePodPacket.RewardSnapshot? rewards)
    {
        if (podId < 0 || rewards == null)
            return;

        var normalized = CloneRewardSnapshot(rewards);
        if (normalized.HasSupportedRewards)
            PendingClientRewardsByPod[podId] = normalized;
        else
            PendingClientRewardsByPod.Remove(podId);
    }

    private static void ApplyPendingRewardsForPod(int podId)
    {
        if (!PendingClientRewardsByPod.TryGetValue(podId, out var rewards))
            return;

        if (TryGetLoadedPod(podId, out var pod))
        {
            ApplyRewardsToLoadedPod(podId, pod!, rewards);
            return;
        }

        // Appearance IDs resolve through the loaded definition catalog and do not
        // require the pod's scene object. This also lets late-join rewards apply
        // when the opened pod is in an unloaded region.
        var remainingAppearances = ApplyAppearanceRewards(podId, rewards.AppearanceRewards);
        if (rewards.BlueprintReferenceIds.Count == 0 &&
            rewards.UpgradeComponentReferenceIds.Count == 0 &&
            remainingAppearances.Count == 0)
        {
            PendingClientRewardsByPod.Remove(podId);
            return;
        }

        PendingClientRewardsByPod[podId] = new TreasurePodPacket.RewardSnapshot
        {
            BlueprintReferenceIds = rewards.BlueprintReferenceIds.ToList(),
            UpgradeComponentReferenceIds = rewards.UpgradeComponentReferenceIds.ToList(),
            AppearanceRewards = remainingAppearances
        };
    }

    private static void ApplyRewardsToLoadedPod(
        int podId,
        Il2Cpp.TreasurePod pod,
        TreasurePodPacket.RewardSnapshot rewards)
    {
        var remainingBlueprints = new List<string>();
        var localBlueprint = pod.Blueprint;
        foreach (var referenceId in rewards.BlueprintReferenceIds)
        {
            if (!IsSafeReferenceId(referenceId) || !localBlueprint ||
                !string.Equals(localBlueprint!.ReferenceId, referenceId, StringComparison.Ordinal))
            {
                remainingBlueprints.Add(referenceId);
                continue;
            }

            if (!TryMarkRewardApplied(AppliedBlueprintRewards, podId, referenceId))
                continue;

            var gadgets = GameState?.GadgetsModel;
            var blueprints = gadgets?.blueprints;
            if (gadgets == null || blueprints == null)
            {
                AppliedBlueprintRewards.Remove((podId, referenceId));
                remainingBlueprints.Add(referenceId);
                continue;
            }

            if (!blueprints.Contains(localBlueprint))
            {
                var wasHandlingPacket = HandlingPacket;
                HandlingPacket = true;
                try
                {
                    gadgets.RegisterBlueprint(localBlueprint);
                }
                catch (Exception ex)
                {
                    AppliedBlueprintRewards.Remove((podId, referenceId));
                    remainingBlueprints.Add(referenceId);
                    if (ReportedBlueprintFailures.Add((podId, referenceId)))
                        SrLogger.LogWarning(
                            $"Could not apply Treasure Pod {podId} blueprint reward '{referenceId}': {ex.Message}");
                }
                finally
                {
                    HandlingPacket = wasHandlingPacket;
                }
            }
        }

        // Upgrade component IDs are carried as authoritative reward metadata.
        // Their per-player counts are applied by the existing absolute ComponentAdd
        // path and InitialComponents snapshot, never by calling GainComponent again.
        var localComponent = pod.UpgradeComponent;
        var componentsResolved = rewards.UpgradeComponentReferenceIds.All(referenceId =>
            IsSafeReferenceId(referenceId) && localComponent &&
            string.Equals(localComponent!._referenceId, referenceId, StringComparison.Ordinal));
        var remainingAppearances = ApplyAppearanceRewards(podId, rewards.AppearanceRewards);

        if (remainingBlueprints.Count == 0 && componentsResolved && remainingAppearances.Count == 0)
            PendingClientRewardsByPod.Remove(podId);
        else
            PendingClientRewardsByPod[podId] = new TreasurePodPacket.RewardSnapshot
            {
                BlueprintReferenceIds = remainingBlueprints,
                UpgradeComponentReferenceIds = componentsResolved
                    ? new List<string>()
                    : rewards.UpgradeComponentReferenceIds.ToList(),
                AppearanceRewards = remainingAppearances
            };
    }

    private static List<TreasurePodPacket.RewardSnapshot.AppearanceReward> ApplyAppearanceRewards(
        int podId,
        IEnumerable<TreasurePodPacket.RewardSnapshot.AppearanceReward> rewards)
    {
        var remaining = new List<TreasurePodPacket.RewardSnapshot.AppearanceReward>();
        var director = SceneContext.Instance?.SlimeAppearanceDirector;
        var definitions = director?.SlimeDefinitions?.Slimes;
        var appearancesModel = GameState?.GetAppearancesModel();
        if (!director || definitions == null || appearancesModel == null)
            return rewards.Select(CloneAppearanceReward).ToList();

        foreach (var reward in rewards)
        {
            if (!IsValidAppearanceReward(reward))
                continue;

            Il2Cpp.SlimeDefinition? definition = null;
            foreach (var candidate in definitions)
            {
                if (candidate && string.Equals(
                        candidate!.ReferenceId,
                        reward.SlimeDefinitionReferenceId,
                        StringComparison.Ordinal))
                {
                    definition = candidate;
                    break;
                }
            }

            if (!definition)
            {
                remaining.Add(CloneAppearanceReward(reward));
                continue;
            }

            var appearance = definition!.GetAppearanceForSet(reward.SaveSet);
            if (!appearance || appearance!.SaveSet != reward.SaveSet)
            {
                remaining.Add(CloneAppearanceReward(reward));
                continue;
            }

            var rewardKey = (podId, reward.SlimeDefinitionReferenceId, reward.SaveSet);
            if (director.IsAppearanceUnlocked(definition, appearance))
            {
                AppliedAppearanceRewards.Add(rewardKey);
                continue;
            }

            if (!TryMarkAppearanceRewardApplied(
                    AppliedAppearanceRewards,
                    podId,
                    reward.SlimeDefinitionReferenceId,
                    reward.SaveSet))
            {
                remaining.Add(CloneAppearanceReward(reward));
                continue;
            }

            var wasHandlingPacket = HandlingPacket;
            HandlingPacket = true;
            try
            {
                // This updates only the receiving player's AppearancesModel via
                // the director's native unlock event; it does not replace their
                // own unlocked sets or selected appearance.
                director.UnlockAppearance(definition, appearance);
                if (!director.IsAppearanceUnlocked(definition, appearance))
                {
                    AppliedAppearanceRewards.Remove(rewardKey);
                    remaining.Add(CloneAppearanceReward(reward));
                    if (ReportedAppearanceFailures.Add(rewardKey))
                        SrLogger.LogWarning(
                            $"Treasure Pod {podId} appearance '{reward.SlimeDefinitionReferenceId}/{reward.SaveSet}' " +
                            "did not become unlocked in the local appearance model.");
                }
            }
            catch (Exception ex)
            {
                AppliedAppearanceRewards.Remove(rewardKey);
                remaining.Add(CloneAppearanceReward(reward));
                if (ReportedAppearanceFailures.Add(rewardKey))
                    SrLogger.LogWarning(
                        $"Could not apply Treasure Pod {podId} appearance reward " +
                        $"'{reward.SlimeDefinitionReferenceId}/{reward.SaveSet}': {ex.Message}");
            }
            finally
            {
                HandlingPacket = wasHandlingPacket;
            }
        }

        return remaining;
    }

    private static TreasurePodPacket.RewardSnapshot.AppearanceReward CloneAppearanceReward(
        TreasurePodPacket.RewardSnapshot.AppearanceReward reward)
        => new()
        {
            SlimeDefinitionReferenceId = reward.SlimeDefinitionReferenceId,
            SaveSet = reward.SaveSet
        };

    private static bool IsNetworkingActive()
        => Main.Server.IsRunning || Main.Client.IsConnected;

    private static void ResetSession()
    {
        PendingStates.Clear();
        ModelKeys.Clear();
        VisualOpenInstances.Clear();
        NetworkOpenIds.Clear();
        PendingHostOpenRequests.Clear();
        HostOpenInProgress.Clear();
        HostOpenUncertain.Clear();
        PendingClientOpenRequests.Clear();
        DelegatedHostOpens.Clear();
        clientGrantedActivation = -1;
        nextClientOpenRequestId = 1;
        HostRewardsByPod.Clear();
        HostOpenStatePublished.Clear();
        HostRewardPayloadPublished.Clear();
        PendingClientRewardsByPod.Clear();
        AppliedBlueprintRewards.Clear();
        ReportedBlueprintFailures.Clear();
        AppliedAppearanceRewards.Clear();
        ReportedAppearanceFailures.Clear();
    }

    private static bool TryMarkRewardApplied(
        HashSet<(int PodId, string ReferenceId)> applied,
        int podId,
        string referenceId)
        => podId >= 0 && IsSafeReferenceId(referenceId) && applied.Add((podId, referenceId));

    private static bool TryMarkAppearanceRewardApplied(
        HashSet<(int PodId, string SlimeDefinitionReferenceId, Il2Cpp.SlimeAppearance.AppearanceSaveSet SaveSet)> applied,
        int podId,
        string slimeDefinitionReferenceId,
        Il2Cpp.SlimeAppearance.AppearanceSaveSet saveSet)
        => podId >= 0 && IsSafeReferenceId(slimeDefinitionReferenceId) &&
           IsSupportedAppearanceSaveSet(saveSet) &&
           applied.Add((podId, slimeDefinitionReferenceId, saveSet));

#if SR2MP_TEST
    internal static bool VerifyTestPolicies()
        => TreasurePodOpenPolicy.VerifyRules()
           && VerifyPacketRoundTrip()
           && VerifyRewardIdempotence()
           && VerifyAppearanceRewardIdempotence();

    private static bool VerifyPacketRoundTrip()
    {
        var expected = new TreasurePodPacket
        {
            ID = 314,
            RequestId = 17,
            Rewards = new TreasurePodPacket.RewardSnapshot
            {
                BlueprintReferenceIds = new List<string> { "gadget_test" },
                UpgradeComponentReferenceIds = new List<string> { "drone-core" },
                AppearanceRewards = new List<TreasurePodPacket.RewardSnapshot.AppearanceReward>
                {
                    new()
                    {
                        SlimeDefinitionReferenceId = "slime_pink",
                        SaveSet = Il2Cpp.SlimeAppearance.AppearanceSaveSet.SECRET_STYLE
                    }
                }
            }
        };
        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        var actual = new TreasurePodPacket();
        actual.Deserialise(reader);

        if (reader.BytesRemaining != 0 || actual.ID != expected.ID || actual.RequestId != expected.RequestId ||
            actual.IsRejectedResponse != expected.IsRejectedResponse ||
            !actual.Rewards.BlueprintReferenceIds.SequenceEqual(expected.Rewards.BlueprintReferenceIds) ||
            !actual.Rewards.UpgradeComponentReferenceIds.SequenceEqual(expected.Rewards.UpgradeComponentReferenceIds) ||
            !AppearanceRewardsEqual(actual.Rewards.AppearanceRewards, expected.Rewards.AppearanceRewards))
            return false;

        var expectedRejection = new TreasurePodPacket
        {
            ID = 314,
            RequestId = 23,
            IsRejectedResponse = true
        };
        using var rejectionWriter = PacketWriter.Borrow();
        expectedRejection.Serialise(rejectionWriter);
        using var rejectionReader = PacketReader.Borrow(rejectionWriter.ToSpan().ToArray());
        var actualRejection = new TreasurePodPacket();
        actualRejection.Deserialise(rejectionReader);
        if (rejectionReader.BytesRemaining != 0 || actualRejection.ID != expectedRejection.ID ||
            actualRejection.RequestId != expectedRejection.RequestId ||
            actualRejection.IsRejectedResponse != expectedRejection.IsRejectedResponse)
            return false;

        var expectedInitial = new InitialTreasurePodsPacket
        {
            TreasurePods = new Dictionary<int, Il2Cpp.TreasurePod.State>
            {
                [314] = Il2Cpp.TreasurePod.State.OPEN
            },
            RewardsByPod = new Dictionary<int, TreasurePodPacket.RewardSnapshot>
            {
                [314] = expected.Rewards
            }
        };

        using var initialWriter = PacketWriter.Borrow();
        expectedInitial.Serialise(initialWriter);
        var initialBytes = initialWriter.ToSpan().ToArray();

        using var initialReader = PacketReader.Borrow(initialBytes);
        var actualInitial = new InitialTreasurePodsPacket();
        actualInitial.Deserialise(initialReader);

        return initialReader.BytesRemaining == 0 &&
               actualInitial.TreasurePods.TryGetValue(314, out var actualState) &&
               actualState == Il2Cpp.TreasurePod.State.OPEN &&
               actualInitial.RewardsByPod.TryGetValue(314, out var actualRewards) &&
               actualRewards.BlueprintReferenceIds.SequenceEqual(expected.Rewards.BlueprintReferenceIds) &&
               actualRewards.UpgradeComponentReferenceIds.SequenceEqual(expected.Rewards.UpgradeComponentReferenceIds) &&
               AppearanceRewardsEqual(actualRewards.AppearanceRewards, expected.Rewards.AppearanceRewards);
    }

    private static bool AppearanceRewardsEqual(
        IEnumerable<TreasurePodPacket.RewardSnapshot.AppearanceReward> left,
        IEnumerable<TreasurePodPacket.RewardSnapshot.AppearanceReward> right)
        => left.Select(reward => (reward.SlimeDefinitionReferenceId, reward.SaveSet))
            .SequenceEqual(right.Select(reward => (reward.SlimeDefinitionReferenceId, reward.SaveSet)));

    private static bool VerifyRewardIdempotence()
    {
        var applied = new HashSet<(int PodId, string ReferenceId)>();
        return TryMarkRewardApplied(applied, 17, "gadget_test") &&
               !TryMarkRewardApplied(applied, 17, "gadget_test") &&
               TryMarkRewardApplied(applied, 18, "gadget_test") &&
               !TryMarkRewardApplied(applied, -1, "gadget_test") &&
               !TryMarkRewardApplied(applied, 17, "");
    }

    private static bool VerifyAppearanceRewardIdempotence()
    {
        var applied = new HashSet<(int PodId, string SlimeDefinitionReferenceId, Il2Cpp.SlimeAppearance.AppearanceSaveSet SaveSet)>();
        var style = Il2Cpp.SlimeAppearance.AppearanceSaveSet.SECRET_STYLE;
        return TryMarkAppearanceRewardApplied(applied, 17, "slime_pink", style) &&
               !TryMarkAppearanceRewardApplied(applied, 17, "slime_pink", style) &&
               TryMarkAppearanceRewardApplied(applied, 18, "slime_pink", style) &&
               !TryMarkAppearanceRewardApplied(applied, 17, "slime_pink", Il2Cpp.SlimeAppearance.AppearanceSaveSet.NONE) &&
               !TryMarkAppearanceRewardApplied(applied, 17, "", style);
    }
#endif
}

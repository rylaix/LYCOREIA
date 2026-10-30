using System.Collections;
using System.Net;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.SceneManagement;
using Il2CppMonomiPark.SlimeRancher.World.Teleportation;
using SR2MP.Packets.Gadget;
using SR2MP.Packets.Loading;
using SR2MP.Shared.Utils;

namespace SR2MP.Shared.Managers;

internal static partial class NetworkGadgetManager
{
    private static readonly Dictionary<string, SceneGroup?> KnownTeleporterNodes = new();
    private static readonly Dictionary<string, long> KnownTeleporterGadgetActorIds = new();
    
    private static readonly List<(string Source, string Destination)> CachedTeleporterLinks = new();
    
    private static readonly List<(string Source, string Destination, byte SceneGroup)> KnownTeleporterLinks = new();
    private static readonly HashSet<(string Source, string Destination)> RemovedTeleporterLinks = new();
    private static bool teleporterLinkMonitorStarted;
    private static bool hasAuthoritativeTeleporterLinkSnapshot;
    
    internal static void CacheTeleporterStates()
    {
        // Before the first host snapshot, preserve local links while actors are
        // rebuilt. Once a snapshot has arrived, it is authoritative and the local
        // save must not repopulate its older links into the repair cache.
        if (!Main.Client.IsConnected || !hasAuthoritativeTeleporterLinkSnapshot)
            CachedTeleporterLinks.Clear();
        KnownTeleporterNodes.Clear();

        try
        {
            var gadgetNodes = GetTeleporterNodeIds();

            foreach (var pair in GameState.teleporters)
            {
                var model = pair.Value;
                if (model == null) continue;

                string? sourceNodeId = null;

                var sourceNode = model.source?.TeleporterNodeModel;
                if (sourceNode != null)
                {
                    sourceNodeId = sourceNode.NodeId;
                    CacheTeleporterNode(sourceNode, null);
                }

                var destinations = model.destinations;
                if (destinations == null) continue;

                foreach (var destPair in destinations)
                {
                    var info = destPair.Value;
                    if (info?.TeleporterNodeModel == null) continue;

                    CacheTeleporterNode(info.TeleporterNodeModel, info.SceneGroup);

                    var sceneGroupId = NetworkSceneManager.GetPersistentID(info.SceneGroup);
                    if (sourceNodeId is { Length: > 0 } &&
                        sceneGroupId is >= 0 and <= byte.MaxValue &&
                        (!Main.Client.IsConnected || !hasAuthoritativeTeleporterLinkSnapshot) &&
                        !RemovedTeleporterLinks.Contains((sourceNodeId, info.TeleporterNodeModel.NodeId)))
                    {
                        RememberTeleporterLink(
                            sourceNodeId, info.TeleporterNodeModel.NodeId, (byte)sceneGroupId);
                    }

                    if (sourceNodeId != null && !gadgetNodes.Contains(sourceNodeId) &&
                        (!Main.Client.IsConnected || !hasAuthoritativeTeleporterLinkSnapshot))
                    {
                        var edge = (Source: sourceNodeId, Destination: info.TeleporterNodeModel.NodeId);
                        if (TeleporterLinkSnapshotPolicy.ShouldIncludeSnapshotLink(
                                edge.Source, edge.Destination, RemovedTeleporterLinks.Contains(edge)))
                            CachedTeleporterLinks.Add(edge);
                    }
                }
            }

            SrLogger.LogDebug($"CacheTeleporterStates: {CachedTeleporterLinks.Count} link(s), {KnownTeleporterNodes.Count} node(s).");
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"CacheTeleporterStates: {ex.Message}");
        }
    }

    private static void CacheTeleporterNode(TeleporterNodeModel node, SceneGroup? scene)
    {
        if (node.NodeId is not { Length: > 0 } nodeId) return;

        if (scene != null || !KnownTeleporterNodes.ContainsKey(nodeId))
            KnownTeleporterNodes[nodeId] = scene;
    }

    private static HashSet<string> GetTeleporterNodeIds()
    {
        var nodeIds = new HashSet<string>();

        try
        {
            foreach (var model in GameState.AllGadgets())
            {
                if (model.TryCast<TeleporterGadgetModel>(out var teleporter)
                    && teleporter.teleporterModel?.source?.TeleporterNodeModel?.NodeId is { Length: > 0 } nodeId)
                {
                    nodeIds.Add(nodeId);
                    KnownTeleporterGadgetActorIds[nodeId] = teleporter.actorId.Value;
                }
            }
        }
        catch (Exception ex)
        {
            SrLogger.LogDebug($"GetGadgetTeleporterNodeIds: {ex.Message}");
        }

        return nodeIds;
    }

    private static TeleporterModel? FindTeleporterSourceModel(string sourceNodeId)
    {
        try
        {
            foreach (var pair in GameState.teleporters)
            {
                var sourceNode = pair.Value?.source?.TeleporterNodeModel;
                if (sourceNode != null && sourceNode.NodeId == sourceNodeId)
                    return pair.Value;
            }
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"FindTeleporterSourceModel: {ex.Message}");
        }

        return null;
    }

    private static bool TryResolveTeleporterNode(string nodeId, out TeleporterNodeModel? node, out SceneGroup? scene)
    {
        node = null;
        scene = null;

        try
        {
            foreach (var pair in GameState.teleporters)
            {
                var sourceNode = pair.Value?.source?.TeleporterNodeModel;
                if (sourceNode != null && sourceNode.NodeId == nodeId)
                {
                    node = sourceNode;
                    
                    if (KnownTeleporterNodes.TryGetValue(nodeId, out var known))
                        scene = known;

                    return true;
                }

                var destinations = pair.Value?.destinations;
                if (destinations == null)
                    continue;

                foreach (var destPair in destinations)
                {
                    var destNode = destPair.Value?.TeleporterNodeModel;
                    if (destNode == null || destNode.NodeId != nodeId)
                        continue;

                    node = destNode;
                    scene = destPair.Value?.SceneGroup;
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            SrLogger.LogDebug($"TryResolveTeleporterNode: {ex.Message}");
        }

        return false;
    }

    private static TeleporterGadgetModel? FindTeleporterGadgetModel(string? nodeId)
    {
        if (nodeId is not { Length: > 0 })
            return null;

        try
        {
            foreach (var model in GameState.AllGadgets())
            {
                if (model.TryCast<TeleporterGadgetModel>(out var teleporter)
                    && teleporter.teleporterModel?.source?.TeleporterNodeModel?.NodeId == nodeId)
                    return teleporter;
            }
        }
        catch (Exception ex)
        {
            SrLogger.LogDebug($"FindTeleporterGadgetModel ({nodeId}): {ex.Message}");
        }

        return null;
    }

    private static bool TryGetTeleporterGadgetActorId(string nodeId, out long actorId)
    {
        var model = FindTeleporterGadgetModel(nodeId);
        if (model != null)
        {
            actorId = model.actorId.Value;
            KnownTeleporterGadgetActorIds[nodeId] = actorId;
            return true;
        }

        return KnownTeleporterGadgetActorIds.TryGetValue(nodeId, out actorId);
    }

    internal static bool IsAuthorizedTeleporterMutation(
        string sourceNodeId,
        string destinationNodeId,
        IPEndPoint? clientEndpoint)
    {
        _ = GetTeleporterNodeIds();

        var authenticatedClient = clientEndpoint != null &&
                                  Main.Server.ClientManager.TryGetClient(clientEndpoint, out var sender) &&
                                  sender != null && !string.IsNullOrWhiteSpace(sender.PlayerId);

        var sourceModel = FindTeleporterSourceModel(sourceNodeId);
        var sourceGadget = FindTeleporterGadgetModel(sourceNodeId);
        var sourceIsGadget = TryGetTeleporterGadgetActorId(sourceNodeId, out var sourceActorId);
        var sourceExists = sourceModel != null &&
                           sourceModel.source?.TeleporterNodeModel?.NodeId == sourceNodeId;
        var sourceIsTeleporterGadget = sourceIsGadget && sourceGadget?.teleporterModel?.source?.TeleporterNodeModel?.NodeId == sourceNodeId;
        var sourceOwnedBySender = sourceIsGadget && NetworkActorManager.IsCurrentActorOwner(
            new ActorId(sourceActorId), clientEndpoint);

        var destinationExists = TryResolveTeleporterNode(
            destinationNodeId, out var destinationNode, out _) && destinationNode != null;
        var destinationIsGadget = TryGetTeleporterGadgetActorId(destinationNodeId, out var destinationActorId);
        var destinationAccessibleBySender = !destinationIsGadget || NetworkActorManager.IsCurrentActorOwner(
            new ActorId(destinationActorId), clientEndpoint);

        return TeleporterLinkSnapshotPolicy.CanAcceptMutation(
            authenticatedClient,
            sourceExists,
            sourceIsTeleporterGadget,
            sourceOwnedBySender,
            destinationExists,
            destinationIsTeleporterNode: destinationNode != null,
            destinationAccessibleBySender: destinationAccessibleBySender);
    }

    internal static void SendTeleporterLinkSnapshot(IPEndPoint clientEndpoint)
        => Main.Server.SendToClient(new InitialTeleporterLinksPacket
        {
            Links = GetAllTeleporterLinks()
        }, clientEndpoint);

    internal static void EnsureTeleporterLinkMonitor()
    {
        if (teleporterLinkMonitorStarted)
            return;

        teleporterLinkMonitorStarted = true;
        StartCoroutine(MonitorTeleporterLinkRemovals());
    }

    private static IEnumerator MonitorTeleporterLinkRemovals()
    {
        var missingPolls = new Dictionary<(string Source, string Destination), byte>();

        while (true)
        {
            yield return new WaitForSeconds(0.5f);

            if (!Main.Server.IsRunning && !Main.Client.IsConnected)
            {
                missingPolls.Clear();
                continue;
            }

            try
            {
                if (SystemContext.Instance.SceneLoader.IsSceneLoadInProgress)
                {
                    missingPolls.Clear();
                    continue;
                }
            }
            catch
            {
                continue;
            }

            foreach (var link in KnownTeleporterLinks.ToArray())
            {
                var edge = (link.Source, link.Destination);
                if (RemovedTeleporterLinks.Contains(edge))
                {
                    missingPolls.Remove(edge);
                    continue;
                }

                var sourceModel = FindTeleporterSourceModel(link.Source);
                if (sourceModel == null)
                    continue;

                if (HasTeleporterDestination(sourceModel, link.Destination))
                {
                    missingPolls.Remove(edge);
                    continue;
                }

                missingPolls.TryGetValue(edge, out var misses);
                misses++;
                if (misses < 2)
                {
                    missingPolls[edge] = misses;
                    continue;
                }

                missingPolls.Remove(edge);
                ForgetTeleporterLink(link.Source, link.Destination);
                SrLogger.LogDebug(
                    $"Teleporter unlink observed after model removal: {link.Source} -/-> {link.Destination}.");
                Main.SendToAllOrServer(new TeleporterUnlinkPacket
                {
                    SourceNodeId = link.Source,
                    DestinationNodeId = link.Destination
                });
            }
        }
    }

    private static void RestoreTeleporterChargeup(TeleporterGadgetModel? gadget, double? chargeupTime)
    {
        if (gadget == null || chargeupTime is not { } value)
            return;

        if (Math.Abs(gadget.waitForChargeupTime - value) >= 0.0001)
            gadget.waitForChargeupTime = value;
        
        ReSetChargeup(gadget, value);
    }

    private static readonly float[] ChargeupReSetDelays = { 0f, 0.5f, 1f, 2f, 4f };

    internal static void ReSetChargeup(GadgetModel? model, double chargeupTime)
    {
        if (model != null && chargeupTime > 0)
            StartCoroutine(ReSetChargeupCoroutine(model, chargeupTime));
    }
    
    private static IEnumerator ReSetChargeupCoroutine(GadgetModel model, double chargeupTime)
    {
        var stable = 0;
        foreach (var delay in ChargeupReSetDelays)
        {
            if (delay > 0f)
                yield return new WaitForSeconds(delay);
            else
                yield return null;

            bool corrected;
            try
            {
                corrected = model.waitForChargeupTime > chargeupTime + 0.0001;
                if (corrected)
                    model.waitForChargeupTime = chargeupTime;
                
            }
            catch (Exception)
            {
                // Gadget was removed
                yield break;
            }

            stable = corrected ? 0 : stable + 1;
            if (stable >= 2)
                yield break;
        }
    }
    
    private static void ResolveDestinationConflicts(TeleporterModel sourceModel, string keepNodeId)
    {
        var destinations = sourceModel.destinations;
        if (destinations == null)
            return;

        var conflicting = new List<string>();
        foreach (var pair in destinations)
        {
            if (pair.Key == keepNodeId || pair.Value?.TeleporterNodeModel == null)
                continue;

            conflicting.Add(pair.Key);
        }

        foreach (var nodeId in conflicting)
            RemoveTeleporterDestination(sourceModel, nodeId);

        if (conflicting.Count > 0)
            SrLogger.LogDebug($"TeleporterLink: dropped {conflicting.Count} conflicting destination(s) from {sourceModel.source?.TeleporterNodeModel?.NodeId}.");
    }

    private static bool HasTeleporterDestination(TeleporterModel sourceModel, string destinationNodeId, SceneGroup sceneGroup)
    {
        if (!HasTeleporterDestination(sourceModel, destinationNodeId))
            return false;

        try
        {
            var info = sourceModel.destinations![destinationNodeId];
            return info?.TeleporterNodeModel != null && info.SceneGroup == sceneGroup;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool HasTeleporterDestination(TeleporterModel sourceModel, string destinationNodeId)
    {
        try
        {
            var destinations = sourceModel.destinations;
            return destinations != null && destinations.TryGetValue(destinationNodeId, out var info) &&
                   info?.TeleporterNodeModel != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool RemoveTeleporterDestination(TeleporterModel sourceModel, string destinationNodeId)
    {
        try
        {
            var destinations = sourceModel.destinations;
            if (destinations == null || !destinations.ContainsKey(destinationNodeId))
                return false;

            var sourceNodeId = sourceModel.source?.TeleporterNodeModel?.NodeId;
            var wasHandlingPacket = HandlingPacket;
            HandlingPacket = true;
            try
            {
                // Avoid TeleporterModel.RemoveDestination: this IL2CPP method caused a
                // native access violation when Harmony-patched in this game build.
                destinations.Remove(destinationNodeId);
            }
            finally
            {
                HandlingPacket = wasHandlingPacket;
            }

            try
            {
                SceneContext.Instance?.TeleportNetwork?.OnLinkRegistered(sourceModel);
                RefreshTeleporterNode(sourceNodeId);
                RefreshTeleporterNode(destinationNodeId);
            }
            catch (Exception ex)
            {
                SrLogger.LogWarning($"TeleporterUnlink refresh ({destinationNodeId}): {ex.Message}");
            }

            return true;
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"TeleporterUnlink ({destinationNodeId}): {ex.Message}");
            return false;
        }
    }

    private static bool ForceApplyTeleporterLink(TeleporterModel sourceModel, TeleporterNodeModel destinationNode, SceneGroup sceneGroup)
    {
        var sourceNodeId = sourceModel.source?.TeleporterNodeModel?.NodeId;
        var destinationNodeId = destinationNode.NodeId;
        
        if (HasTeleporterDestination(sourceModel, destinationNodeId, sceneGroup))
        {
            SceneContext.Instance?.TeleportNetwork?.OnLinkRegistered(sourceModel);
            RefreshTeleporterNode(sourceNodeId);
            RefreshTeleporterNode(destinationNodeId);
            return true;
        }

        var sourceGadget = FindTeleporterGadgetModel(sourceNodeId);
        var destinationGadget = FindTeleporterGadgetModel(destinationNodeId);
        var sourceChargeup = sourceGadget?.waitForChargeupTime;
        var destinationChargeup = destinationGadget?.waitForChargeupTime;
        
        var nodeId = GameState.GetOrCreateTeleporterNodeModel(destinationNodeId);
        if (nodeId != null)
            destinationNode = nodeId;

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            var destinations = sourceModel.destinations;
            if (destinations?.ContainsKey(destinationNodeId) == true)
                RemoveTeleporterDestination(sourceModel, destinationNodeId);

            if (sourceGadget != null)
                ResolveDestinationConflicts(sourceModel, destinationNodeId);

            sourceModel.AddDestination(destinationNode, sceneGroup);
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"ForceApplyTeleporterLink ({destinationNodeId}): {ex.Message}");
            return false;
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        SceneContext.Instance?.TeleportNetwork?.OnLinkRegistered(sourceModel);
        
        // Refresh, if it was already present, it doesn't know about anything yet
        RefreshTeleporterNode(sourceNodeId);
        RefreshTeleporterNode(destinationNodeId);

        RestoreTeleporterChargeup(sourceGadget, sourceChargeup);
        RestoreTeleporterChargeup(destinationGadget, destinationChargeup);
        return true;
    }

    private static void RefreshTeleporterNode(string? nodeId)
    {
        if (nodeId is not { Length: > 0 })
            return;

        foreach (var node in Resources.FindObjectsOfTypeAll<GadgetTeleporterNode>())
        {
            try
            {
                if (node == null || node.NodeId != nodeId)
                    continue;

                node.OnLinkRegistered();
                return;
            }
            catch
            {
                // If anything is broken here,
                // it was already caught before,
                // we can silence it
            }
        }
    }

    private static bool TryApplyTeleporterLink(string sourceNodeId, string destinationNodeId, byte sceneGroupId, bool logRetry)
    {
        if (SceneContext.Instance == null)
            return false;

        var sourceModel = FindTeleporterSourceModel(sourceNodeId);
        if (sourceModel == null)
        {
            if (logRetry)
                SrLogger.LogDebug($"TeleporterLink: source {sourceNodeId} not registered yet, retrying.");
            return false;
        }

        if (!TryResolveTeleporterNode(destinationNodeId, out var destinationNode, out var cachedScene) || destinationNode == null)
        {
            if (logRetry)
                SrLogger.LogDebug($"TeleporterLink: destination {destinationNodeId} not registered yet, retrying.");
            return false;
        }

        SceneGroup? sceneGroup;
        try
        {
            sceneGroup = NetworkSceneManager.GetSceneGroup(sceneGroupId);
        }
        catch (Exception)
        {
            sceneGroup = null;
        }

        sceneGroup ??= cachedScene;
        if (sceneGroup == null)
            return false;

        if (!ForceApplyTeleporterLink(sourceModel, destinationNode, sceneGroup))
            return false;
        
        return true;
    }

    private static void RememberTeleporterLink(string sourceNodeId, string destinationNodeId, byte sceneGroupId)
    {
        if (!TeleporterLinkSnapshotPolicy.IsValidCachedLink(sourceNodeId, destinationNodeId))
            return;

        // Gadget teleporters are paired endpoints. Replacing a destination must also
        // evict the old cached edge or a later snapshot/repair pass can resurrect it.
        if (FindTeleporterGadgetModel(sourceNodeId) != null)
        {
            foreach (var previous in KnownTeleporterLinks.Where(link =>
                         link.Source == sourceNodeId && link.Destination != destinationNodeId).ToArray())
                RemovedTeleporterLinks.Add((previous.Source, previous.Destination));

            foreach (var previous in CachedTeleporterLinks.Where(link =>
                         link.Source == sourceNodeId && link.Destination != destinationNodeId).ToArray())
                RemovedTeleporterLinks.Add(previous);

            CachedTeleporterLinks.RemoveAll(link =>
                link.Source == sourceNodeId && link.Destination != destinationNodeId);
            KnownTeleporterLinks.RemoveAll(link =>
                link.Source == sourceNodeId && link.Destination != destinationNodeId);
        }

        RemovedTeleporterLinks.Remove((sourceNodeId, destinationNodeId));

        for (var i = 0; i < KnownTeleporterLinks.Count; i++)
        {
            var link = KnownTeleporterLinks[i];
            if (link.Source != sourceNodeId || link.Destination != destinationNodeId)
                continue;

            KnownTeleporterLinks[i] = (sourceNodeId, destinationNodeId, sceneGroupId);
            CacheTeleporterScene(destinationNodeId, sceneGroupId);
            return;
        }

        KnownTeleporterLinks.Add((sourceNodeId, destinationNodeId, sceneGroupId));
        CacheTeleporterScene(destinationNodeId, sceneGroupId);
    }

    private static void CacheTeleporterScene(string nodeId, byte sceneGroupId)
    {
        try
        {
            var scene = NetworkSceneManager.GetSceneGroup(sceneGroupId);
            if (scene != null)
                KnownTeleporterNodes[nodeId] = scene;
        }
        catch
        {
            // The scene registry can still be loading when a snapshot arrives.
        }
    }

    internal static void RecordTeleporterLink(string sourceNodeId, string destinationNodeId, byte sceneGroupId)
        => RememberTeleporterLink(sourceNodeId, destinationNodeId, sceneGroupId);
    
    private static void ForgetTeleporterLink(string sourceNodeId, string destinationNodeId)
    {
        if (!TeleporterLinkSnapshotPolicy.IsValidCachedLink(sourceNodeId, destinationNodeId))
            return;

        RemovedTeleporterLinks.Add((sourceNodeId, destinationNodeId));
        TeleporterLinkSnapshotPolicy.RemoveLink(KnownTeleporterLinks, sourceNodeId, destinationNodeId);
        TeleporterLinkSnapshotPolicy.RemoveLink(CachedTeleporterLinks, sourceNodeId, destinationNodeId);
    }

    internal static bool ApplyTeleporterUnlink(string sourceNodeId, string destinationNodeId)
    {
        if (!TeleporterLinkSnapshotPolicy.IsValidCachedLink(sourceNodeId, destinationNodeId))
            return false;

        ForgetTeleporterLink(sourceNodeId, destinationNodeId);

        var sourceModel = FindTeleporterSourceModel(sourceNodeId);
        if (sourceModel != null)
            RemoveTeleporterDestination(sourceModel, destinationNodeId);

        return true;
    }

    private static void ForgetTeleporterLinksFor(string? nodeId)
    {
        if (nodeId is not { Length: > 0 })
            return;

        foreach (var link in KnownTeleporterLinks.Where(link =>
                     link.Source == nodeId || link.Destination == nodeId).ToArray())
            RemovedTeleporterLinks.Add((link.Source, link.Destination));

        foreach (var link in CachedTeleporterLinks.Where(link =>
                     link.Source == nodeId || link.Destination == nodeId).ToArray())
            RemovedTeleporterLinks.Add(link);

        KnownTeleporterLinks.RemoveAll(link => link.Source == nodeId || link.Destination == nodeId);
        CachedTeleporterLinks.RemoveAll(link => link.Source == nodeId || link.Destination == nodeId);
        KnownTeleporterGadgetActorIds.Remove(nodeId);
    }
    
    internal static void RemoveTeleporterGadget(IdentifiableModel? model)
    {
        if (model == null)
            return;

        try
        {
            if (!model.TryCast<TeleporterGadgetModel>(out var teleporter) || teleporter == null)
                return;

            var definition = teleporter.ident?.TryCast<GadgetDefinition>();
            if (definition == null)
                return;

            var teleporterModel = teleporter.teleporterModel;
            var nodeId = teleporterModel?.source?.TeleporterNodeModel?.NodeId;
            var actorId = teleporter.actorId;

            GameState.DestroyGadgetTeleporterModel(definition, actorId);
            teleporterModel?.UnregisterGadget(definition);
            
            ForgetTeleporterLinksFor(nodeId);

            SrLogger.LogDebug($"Teleporter {actorId.Value} ({nodeId}) unregistered from the teleport network.");
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"Failed to unregister teleporter: {ex.Message}");
        }
    }

    private static bool IsTeleporterLinkKnown(string sourceNodeId, string destinationNodeId)
        => KnownTeleporterLinks.Exists(link => link.Source == sourceNodeId && link.Destination == destinationNodeId);

    internal static IEnumerator ApplyTeleporterLink(string sourceNodeId, string destinationNodeId, byte sceneGroupId)
    {
        EnsureTeleporterLinkMonitor();
        RememberTeleporterLink(sourceNodeId, destinationNodeId, sceneGroupId);

        var attempts = 0;
        while (attempts < 120)
        {
            if (!Main.Client.IsConnected && !Main.Server.IsRunning)
                yield break;

            if (!IsTeleporterLinkKnown(sourceNodeId, destinationNodeId))
                yield break;
            
            if (SystemContext.Instance.SceneLoader.IsSceneLoadInProgress)
            {
                yield return new WaitForSeconds(0.5f);
                continue;
            }

            attempts++;

            bool applied;
            try
            {
                applied = TryApplyTeleporterLink(sourceNodeId, destinationNodeId, sceneGroupId, attempts == 1);
            }
            catch (Exception ex)
            {
                SrLogger.LogWarning($"TeleporterLink: {ex.Message}");
                yield break;
            }

            if (applied)
            {
                // Retry may have started before the source gadget was registered; now
                // that resolution succeeded, prune any old destination from its cache.
                RememberTeleporterLink(sourceNodeId, destinationNodeId, sceneGroupId);
                yield break;
            }

            yield return new WaitForSeconds(0.5f);
        }

        ForgetTeleporterLink(sourceNodeId, destinationNodeId);
        if (Main.Server.IsRunning)
        {
            Main.SendToAllOrServer(new TeleporterUnlinkPacket
            {
                SourceNodeId = sourceNodeId,
                DestinationNodeId = destinationNodeId
            });
        }

        SrLogger.LogWarning($"TeleporterLink: could not link {sourceNodeId} -> {destinationNodeId} (nodes never found)");
    }

    internal static IEnumerator ApplyTeleporterLinkSnapshot(List<InitialTeleporterLinksPacket.Link> snapshot)
    {
        EnsureTeleporterLinkMonitor();

        var desired = new Dictionary<(string Source, string Destination), byte>();
        foreach (var link in snapshot ?? new List<InitialTeleporterLinksPacket.Link>())
        {
            if (link == null || !TeleporterLinkSnapshotPolicy.IsValidCachedLink(
                    link.SourceNodeId, link.DestinationNodeId))
                continue;

            desired[(link.SourceNodeId, link.DestinationNodeId)] = link.DestinationSceneGroup;
        }

        var previousEdges = new HashSet<(string Source, string Destination)>();
        foreach (var link in KnownTeleporterLinks)
            previousEdges.Add((link.Source, link.Destination));
        foreach (var link in CachedTeleporterLinks)
            previousEdges.Add(link);

        try
        {
            foreach (var pair in GameState.teleporters)
            {
                var sourceNodeId = pair.Value?.source?.TeleporterNodeModel?.NodeId;
                var destinations = pair.Value?.destinations;
                if (string.IsNullOrWhiteSpace(sourceNodeId) || destinations == null)
                    continue;

                foreach (var destPair in destinations)
                {
                    var destinationNodeId = destPair.Value?.TeleporterNodeModel?.NodeId;
                    if (!string.IsNullOrWhiteSpace(destinationNodeId))
                        previousEdges.Add((sourceNodeId, destinationNodeId));
                }
            }
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"Teleporter snapshot: failed to enumerate previous links: {ex.Message}");
        }

        RemovedTeleporterLinks.Clear();
        foreach (var edge in previousEdges)
            if (!desired.ContainsKey(edge))
                RemovedTeleporterLinks.Add(edge);

        KnownTeleporterLinks.Clear();
        CachedTeleporterLinks.Clear();
        foreach (var (edge, sceneGroupId) in desired)
        {
            RememberTeleporterLink(edge.Source, edge.Destination, sceneGroupId);
            CachedTeleporterLinks.Add(edge);
        }

        hasAuthoritativeTeleporterLinkSnapshot = true;

        // Reconcile the currently loaded models immediately. The tombstones above
        // also suppress stale links that are temporarily absent during actor rebuild.
        try
        {
            foreach (var pair in GameState.teleporters)
            {
                var sourceModel = pair.Value;
                var sourceNodeId = sourceModel?.source?.TeleporterNodeModel?.NodeId;
                var destinations = sourceModel?.destinations;
                if (sourceModel == null || string.IsNullOrWhiteSpace(sourceNodeId) || destinations == null)
                    continue;

                var destinationNodeIds = new List<string>();
                foreach (var destinationNodeId in destinations.Keys)
                    destinationNodeIds.Add(destinationNodeId);

                foreach (var destinationNodeId in destinationNodeIds)
                {
                    var edge = (Source: sourceNodeId, Destination: destinationNodeId);
                    if (!desired.ContainsKey(edge))
                        RemoveTeleporterDestination(sourceModel, destinationNodeId);
                }
            }
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"Teleporter snapshot: failed to remove stale local links: {ex.Message}");
        }

        foreach (var (edge, sceneGroupId) in desired)
            StartCoroutine(ApplyTeleporterLink(edge.Source, edge.Destination, sceneGroupId));

        yield break;
    }

    internal static IEnumerator RepairTeleporterLinks()
    {
        var waited = 0f;
        while ((SceneContext.Instance == null
                || !SceneContext.Instance.TeleportNetwork
                || SceneContext.Instance.player == null)
               && waited < 60f)
        {
            waited += 0.5f;
            yield return new WaitForSeconds(0.5f);
        }

        if (SceneContext.Instance?.TeleportNetwork != true)
            yield break;
        
        yield return new WaitForSeconds(2f);

        foreach (var edge in RemovedTeleporterLinks.ToArray())
        {
            var sourceModel = FindTeleporterSourceModel(edge.Source);
            if (sourceModel != null)
                RemoveTeleporterDestination(sourceModel, edge.Destination);
        }

        var pending = KnownTeleporterLinks
            .Where(link => !RemovedTeleporterLinks.Contains((link.Source, link.Destination)))
            .Select(link => (link.Source, link.Destination, link.SceneGroup))
            .Distinct()
            .ToList();
        var repaired = 0;
        var attempts = 0;

        while (pending.Count > 0 && attempts++ < 120)
        {
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                var (source, destination, sceneGroupId) = pending[i];
                var done = false;

                if (RemovedTeleporterLinks.Contains((source, destination)) ||
                    !IsTeleporterLinkKnown(source, destination))
                {
                    pending.RemoveAt(i);
                    continue;
                }

                try
                {
                    var sourceModel = FindTeleporterSourceModel(source);
                    if (sourceModel != null
                        && TryResolveTeleporterNode(destination, out var node, out var cachedScene)
                        && node != null)
                    {
                        SceneGroup? scene;
                        try
                        {
                            scene = NetworkSceneManager.GetSceneGroup(sceneGroupId);
                        }
                        catch
                        {
                            scene = null;
                        }

                        scene ??= cachedScene;
                        if (scene == null)
                            continue;

                        if (ForceApplyTeleporterLink(sourceModel, node, scene))
                            repaired++;
                        
                        done = true;
                    }
                }
                catch (Exception ex)
                {
                    SrLogger.LogWarning($"RepairTeleporterLinks: {ex.Message}");
                    done = true;
                }

                if (done)
                    pending.RemoveAt(i);
            }

            if (pending.Count > 0)
                yield return new WaitForSeconds(0.5f);
        }

        if (repaired > 0)
            SrLogger.LogDebug($"RepairTeleporterLinks: re-applied {repaired} teleporter link(s).");

        if (pending.Count > 0)
            SrLogger.LogWarning($"RepairTeleporterLinks: {pending.Count} link(s) could not be re-applied");
    }

    internal static List<InitialTeleporterLinksPacket.Link> GetAllTeleporterLinks()
    {
        EnsureTeleporterLinkMonitor();

        var links = new List<InitialTeleporterLinksPacket.Link>();
        var seen = new HashSet<(string Source, string Destination)>();

        try
        {
            foreach (var pair in GameState.teleporters)
            {
                var sourceNode = pair.Value?.source?.TeleporterNodeModel;
                var destinations = pair.Value?.destinations;
                if (sourceNode == null || destinations == null)
                    continue;

                foreach (var destPair in destinations)
                {
                    var info = destPair.Value;
                    if (info?.TeleporterNodeModel == null)
                        continue;

                    var edge = (Source: sourceNode.NodeId, Destination: info.TeleporterNodeModel.NodeId);
                    if (!TeleporterLinkSnapshotPolicy.ShouldIncludeSnapshotLink(
                            edge.Source,
                            edge.Destination,
                            RemovedTeleporterLinks.Contains(edge)))
                        continue;

                    var sceneGroupId = NetworkSceneManager.GetPersistentID(info.SceneGroup);
                    if (sceneGroupId is < 0 or > byte.MaxValue || !seen.Add(edge))
                        continue;

                    RememberTeleporterLink(edge.Source, edge.Destination, (byte)sceneGroupId);

                    links.Add(new InitialTeleporterLinksPacket.Link
                    {
                        SourceNodeId = sourceNode.NodeId,
                        DestinationNodeId = info.TeleporterNodeModel.NodeId,
                        DestinationSceneGroup = (byte)sceneGroupId
                    });
                }
            }
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"GetAllTeleporterLinks: {ex.Message}");
        }
        
        // A cached destination need not itself be a source node, and either endpoint
        // can be temporarily absent while its scene is unloaded. Tombstones prevent
        // stale live or cached edges from returning in join/resync snapshots.
        KnownTeleporterLinks.RemoveAll(link =>
            !TeleporterLinkSnapshotPolicy.IsValidCachedLink(link.Source, link.Destination));

        foreach (var link in KnownTeleporterLinks)
        {
            var edge = (link.Source, link.Destination);
            if (!TeleporterLinkSnapshotPolicy.ShouldIncludeSnapshotLink(
                    link.Source, link.Destination, RemovedTeleporterLinks.Contains(edge)))
                continue;

            if (seen.Add(edge))
            {
                links.Add(new InitialTeleporterLinksPacket.Link
                {
                    SourceNodeId = link.Source,
                    DestinationNodeId = link.Destination,
                    DestinationSceneGroup = link.SceneGroup
                });
            }
        }

        foreach (var edge in CachedTeleporterLinks)
        {
            if (!TeleporterLinkSnapshotPolicy.ShouldIncludeSnapshotLink(
                    edge.Source, edge.Destination, RemovedTeleporterLinks.Contains(edge)) ||
                !seen.Add(edge))
                continue;

            var knownIndex = KnownTeleporterLinks.FindIndex(link =>
                link.Source == edge.Source && link.Destination == edge.Destination);
            byte sceneGroupId;
            if (knownIndex >= 0)
            {
                sceneGroupId = KnownTeleporterLinks[knownIndex].SceneGroup;
            }
            else if (TryResolveTeleporterNode(edge.Destination, out _, out var scene) && scene != null)
            {
                var persistentId = NetworkSceneManager.GetPersistentID(scene);
                if (persistentId is < 0 or > byte.MaxValue)
                    continue;

                sceneGroupId = (byte)persistentId;
            }
            else
                continue;

            links.Add(new InitialTeleporterLinksPacket.Link
            {
                SourceNodeId = edge.Source,
                DestinationNodeId = edge.Destination,
                DestinationSceneGroup = sceneGroupId
            });
        }

        links.RemoveAll(link => RemovedTeleporterLinks.Contains(
            (link.SourceNodeId, link.DestinationNodeId)));

        return links;
    }
}

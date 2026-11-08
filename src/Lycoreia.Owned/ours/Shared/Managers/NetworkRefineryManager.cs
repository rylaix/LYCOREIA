// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System.Collections;
using RefineryUpdatePacket = Lycoreia.Net.Messages.RefineryUpdate;
using InitialRefineryPacket = Lycoreia.Net.Messages.InitialRefinery;

namespace Lycoreia.Owned.Shared.Managers;

internal static partial class NetworkRefineryManager
{
    private static readonly OrderedSnapshotDeltaQueue<InitialRefineryPacket, RefineryUpdatePacket> Pending = new();
    private static readonly RefineryRevisionTracker HostRevision = new();
    private static readonly RefineryRevisionTracker ClientRevision = new();
    private static readonly RefineryRequestLedger ProcessedRequests = new();
    private static bool hooksRegistered;
    private static bool pumpRunning;

    // Last count each item had when it was published or applied from the network. Native code
    // changes _itemCounts without the patched SetCount (shop purchases, gadget placement), so a
    // periodic reconcile publishes any difference through the same host-authoritative path.
    private static readonly Lycoreia.Owned.ItemCountBaseline Baseline = new();
    private static bool clientSnapshotApplied;
    private static float nextReconcileAt;
    private static float snapshotWaitDeadline;

    internal static long CurrentHostRevision
    {
        get
        {
            EnsureHooks();
            return HostRevision.Current;
        }
    }

    internal static void PublishLocalChange(int itemId, long delta, int currentCount)
    {
        EnsureHooks();

        if (delta == 0 || delta < int.MinValue || delta > int.MaxValue)
            return;

        if (!RefineryMutationPolicy.IsRepresentableCount(currentCount))
        {
            OwnedLog.LogWarning($"Refinery count {currentCount} for item {itemId} exceeds the packet range; update was not sent.");
            return;
        }

        Baseline.Record(itemId, currentCount);

        if (ModFacade.Server.IsRunning)
        {
            var update = CreateCanonicalState(
                itemId,
                currentCount,
                requestId: Guid.Empty,
                delta: (int)delta,
                revision: HostRevision.Next());
            ModFacade.Server.SendToAll(update);
            return;
        }

        if (!ModFacade.Client.IsConnected)
            return;

        var request = new RefineryUpdatePacket
        {
            RequestId = Guid.NewGuid(),
            Delta = (int)delta,
            ItemId = itemId,
            Revision = 0
        };
        if (!RefineryMutationPolicy.IsValidRequest(
                LocalID, request.RequestId, request.ItemId, request.Delta, request.Revision))
        {
            OwnedLog.LogWarning($"Refinery change for item {itemId} did not form a valid host request.");
            return;
        }

        ModFacade.Client.SendPacket(request);
    }

    /// <summary>
    /// Applies one guest count delta against the latest host count. Server packet
    /// handlers run on the main-thread dispatcher, so these read/apply/revision steps
    /// are serialized with host gameplay and other guest requests.
    /// </summary>
    internal static bool ReceiveRequest(RefineryUpdatePacket packet, string playerId, Lycoreia.Net.Peer clientEp)
    {
        EnsureHooks();
        if (!RefineryMutationPolicy.IsValidRequest(
                playerId, packet.RequestId, packet.ItemId, packet.Delta, packet.Revision, packet.ItemCount))
        {
            OwnedLog.LogWarning(
                $"Rejected malformed refinery request from '{playerId}' item={packet.ItemId} delta={packet.Delta} revision={packet.Revision}.");
            return false;
        }

        if (!ActorManager.ActorTypes.TryGetValue(packet.ItemId, out var identifiable) || identifiable == null)
        {
            OwnedLog.LogWarning($"Rejected refinery request for unknown item {packet.ItemId} from '{playerId}'.");
            return false;
        }

        var scene = SceneContext.Instance;
        var gadgetsModel = scene ? scene.GadgetDirector?._model : null;
        if (gadgetsModel == null)
            return false;

        var currentCount = gadgetsModel.GetCount(identifiable);
        var previousCount = currentCount;
        if (!RefineryMutationPolicy.IsRepresentableCount(currentCount))
        {
            OwnedLog.LogWarning(
                $"Cannot reconcile refinery item {packet.ItemId}; host count {currentCount} is outside the wire range.");
            return false;
        }

        var lookup = ProcessedRequests.Observe(playerId, packet.RequestId, packet.ItemId, packet.Delta);
        if (lookup == RefineryRequestLookup.Invalid)
            return false;

        if (lookup == RefineryRequestLookup.Duplicate)
        {
            // The request may have been applied even if its original acknowledgement
            // was lost. Reply with the newest canonical count but never run the delta again.
            SendCorrection(packet, clientEp, currentCount, forceNewRevision: false);
            return false;
        }

        if (lookup == RefineryRequestLookup.ConflictingDuplicate)
        {
            OwnedLog.LogWarning(
                $"Rejected refinery request ID reuse from '{playerId}' for item {packet.ItemId}.");
            SendCorrection(packet, clientEp, currentCount, forceNewRevision: true);
            return false;
        }

        if (!RefineryMutationPolicy.TryApplyNewRequest(lookup, currentCount, packet.Delta, out var nextCount))
        {
            OwnedLog.LogWarning(
                $"Rejected refinery delta item={packet.ItemId} delta={packet.Delta} current={currentCount} from '{playerId}'.");
            SendCorrection(packet, clientEp, currentCount, forceNewRevision: true);
            return false;
        }

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            gadgetsModel.SetCount(identifiable, nextCount);
        }
        catch (Exception exception)
        {
            currentCount = gadgetsModel.GetCount(identifiable);
            OwnedLog.LogWarning(
                $"Host failed refinery delta item={packet.ItemId} delta={packet.Delta}: {exception.Message}");
            if (RefineryMutationPolicy.IsRepresentableCount(currentCount))
                SendCorrection(packet, clientEp, currentCount, forceNewRevision: true);
            return false;
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        currentCount = gadgetsModel.GetCount(identifiable);
        if (currentCount != nextCount)
        {
            OwnedLog.LogWarning(
                $"Host refinery setter produced count {currentCount}, expected {nextCount}, for item {packet.ItemId}.");
            if (RefineryMutationPolicy.IsRepresentableCount(currentCount))
                SendCorrection(packet, clientEp, currentCount, forceNewRevision: true);
            return false;
        }

        if (Lycoreia.World.InventoryRecentPolicy.ShouldRecordGadgetObtained(
                identifiable.TryCast<Il2Cpp.GadgetDefinition>() != null, previousCount, currentCount))
            Lycoreia.Game.World.GadgetAdapter.NoteRecentObtained(identifiable);

        packet.ItemCount = currentCount;
        Baseline.Record(packet.ItemId, currentCount);
        packet.Revision = HostRevision.Next();
        // BasePacketHandler relays this now-canonicalized packet to all other peers.
        ModFacade.Server.SendToClient(packet, clientEp);
        return true;
    }

    private static void SendCorrection(
        RefineryUpdatePacket request,
        Lycoreia.Net.Peer clientEp,
        int currentCount,
        bool forceNewRevision)
    {
        var revision = HostRevision.Current;
        if (forceNewRevision || revision == 0)
            revision = HostRevision.Next();

        var correction = CreateCanonicalState(
            request.ItemId,
            currentCount,
            request.RequestId,
            delta: 0,
            revision: revision);
        ModFacade.Server.SendToClient(correction, clientEp);
    }

    private static RefineryUpdatePacket CreateCanonicalState(
        int itemId,
        int itemCount,
        Guid requestId,
        int delta,
        long revision)
        => new()
        {
            RequestId = requestId,
            Delta = delta,
            ItemId = itemId,
            ItemCount = itemCount,
            Revision = revision
        };

    internal static void ReceiveSnapshot(InitialRefineryPacket packet)
    {
        EnsureHooks();
        if (packet.Revision < 0)
        {
            OwnedLog.LogWarning($"Rejected refinery snapshot with invalid revision {packet.Revision}.");
            return;
        }

        Pending.SetSnapshot(packet);
        TryDrain();
        StartPumpIfNeeded();
    }

    internal static bool ReceiveUpdate(RefineryUpdatePacket packet)
    {
        EnsureHooks();
        if (packet.Revision <= 0 ||
            !RefineryMutationPolicy.IsRepresentableCount(packet.ItemCount) ||
            packet.ItemId == -1)
        {
            OwnedLog.LogWarning(
                $"Ignored invalid canonical refinery update item={packet.ItemId} count={packet.ItemCount} revision={packet.Revision}.");
            return false;
        }

        if (!Pending.HasPending && TryApplyUpdate(packet))
            return true;

        Pending.EnqueueDelta(packet);
        TryDrain();
        StartPumpIfNeeded();
        return true;
    }

    /// <summary>Records a count that arrived from the network so reconcile does not echo it.</summary>
    internal static void RecordSyncedCount(int itemId, int count)
        => Baseline.Record(itemId, count);

    /// <summary>ModFacade-thread pump: publishes item-count changes the SetCount patch did not see.</summary>
    internal static void Reconcile()
    {
        if (!ModFacade.Server.IsRunning && !ModFacade.Client.IsConnected)
            return;

        var now = UnityEngine.Time.unscaledTime;
        if (now < nextReconcileAt)
            return;
        nextReconcileAt = now + 1f;

        EnsureHooks();
        if (ModFacade.Client.IsConnected && (!clientSnapshotApplied || Pending.HasPending))
            return;
        if (SystemContext.Instance.SceneLoader.IsSceneLoadInProgress)
            return;

        var scene = SceneContext.Instance;
        var counts = scene ? scene.GadgetDirector?._model?._itemCounts : null;
        if (counts == null)
            return;

        var current = new List<(int ItemId, int Count)>();
        foreach (var pair in counts)
        {
            if (!pair.Key)
                continue;
            int itemId;
            try { itemId = NetworkActorManager.GetPersistentID(pair.Key); }
            catch { continue; }
            current.Add((itemId, pair.Value));
        }
        var changes = Baseline.Diff(current);
        foreach (var change in changes)
        {
            PublishLocalChange(change.ItemId, change.Delta, change.Count);
            DiagLog.LogDiagnostic("inventory", "reconcile-sent",
                $"item={change.ItemId}; delta={change.Delta}; count={change.Count}; role={(ModFacade.Server.IsRunning ? "host" : "client")}");
        }
    }

    private static void ResetBaseline()
    {
        Baseline.Reset();
    }

    private static void EnsureHooks()
    {
        if (hooksRegistered)
            return;

        hooksRegistered = true;
        ModFacade.Client.OnConnected += _ => ResetClientSession();
        ModFacade.Client.OnDisconnected += ResetClientSession;
        ModFacade.Server.OnServerStarted += ResetHostSession;
        ModFacade.Server.ClientManager.OnClientRemoved += client => { if (client != null) ProcessedRequests.ClearPlayer(client.Id); };
    }

    private static void ResetHostSession()
    {
        ResetBaseline();
        HostRevision.Clear();
        ProcessedRequests.Clear();
    }

    private static void ResetClientSession()
    {
        ResetBaseline();
        clientSnapshotApplied = false;
        snapshotWaitDeadline = 0f;
        ClientRevision.Clear();
        Pending.Clear();
    }

    private static void StartPumpIfNeeded()
    {
        if (pumpRunning || !Pending.HasPending)
            return;

        pumpRunning = true;
        StartCoroutine(Pump());
    }

    private static IEnumerator Pump()
    {
        while (Pending.HasPending)
        {
            TryDrain();
            if (!Pending.HasPending)
                break;

            for (var frame = 0; frame < 15; frame++) yield return null;
        }

        pumpRunning = false;
    }

    private static void TryDrain()
        => Pending.TryDrain(TryApplySnapshot, TryApplyUpdate);

    private static bool TryApplySnapshot(InitialRefineryPacket packet)
    {
        var scene = SceneContext.Instance;
        var gadgetsModel = scene ? scene.GadgetDirector?._model : null;
        if (gadgetsModel == null || packet.Items == null)
            return false;

        if (!ClientRevision.CanApplySnapshot(packet.Revision))
        {
            OwnedLog.LogDebug(
                $"Refinery snapshot revision {packet.Revision} was superseded by revision {ClientRevision.Current}.");
            return true;
        }

        if (snapshotWaitDeadline <= 0f)
            snapshotWaitDeadline = UnityEngine.Time.unscaledTime + 10f;

        var newItemCounts = new CppCollections.Dictionary<IdentifiableType, int>();
        var unresolved = 0;
        try
        {
            foreach (var item in packet.Items)
            {
                if (!RefineryMutationPolicy.IsRepresentableCount(item.Value))
                {
                    OwnedLog.LogWarning(
                        $"Refinery snapshot has invalid count {item.Value} for identifiable type {item.Key}.");
                    return false;
                }

                if (!ActorManager.ActorTypes.TryGetValue(item.Key, out var identType) || identType == null)
                {
                    // Types register during load; after a grace period apply what resolves instead of
                    // blocking the whole build inventory (and every later update) on one unknown id.
                    if (UnityEngine.Time.unscaledTime < snapshotWaitDeadline)
                        return false;
                    unresolved++;
                    continue;
                }

                newItemCounts.Add(identType, item.Value);
            }
        }
        catch (Exception ex)
        {
            OwnedLog.LogWarning($"Could not build refinery snapshot; it will be retried: {ex.Message}");
            return false;
        }

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            // Update the live dictionary in place: UI and GadgetDirector hold the existing instance.
            var counts = gadgetsModel._itemCounts;
            if (counts == null)
                gadgetsModel._itemCounts = newItemCounts;
            else
            {
                var stale = new List<IdentifiableType>();
                foreach (var pair in counts)
                    if (pair.Key && !newItemCounts.ContainsKey(pair.Key))
                        stale.Add(pair.Key);
                foreach (var key in stale)
                    counts.Remove(key);
                foreach (var pair in newItemCounts)
                    counts[pair.Key] = pair.Value;
            }

            snapshotWaitDeadline = 0f;
            DiagLog.LogDiagnostic("inventory", "snapshot-applied",
                $"items={newItemCounts.Count}; unresolved={unresolved}; inPlace={counts != null}; revision={packet.Revision}");
            ClientRevision.ApplySnapshot(packet.Revision);
            ResetBaseline();
            clientSnapshotApplied = true;
            return true;
        }
        catch (Exception ex)
        {
            OwnedLog.LogWarning($"Failed to apply refinery snapshot; it will be retried: {ex.Message}");
            return false;
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }
    }

    private static bool TryApplyUpdate(RefineryUpdatePacket packet)
    {
        if (packet.Revision <= 0 || !RefineryMutationPolicy.IsRepresentableCount(packet.ItemCount))
            return true;

        // ReliableOrdered packets and revisions make duplicate acknowledgements and
        // stale updates harmless. Consume them so they cannot block later queued deltas.
        if (!ClientRevision.CanAcceptUpdate(packet.Revision))
            return true;

        if (!ActorManager.ActorTypes.TryGetValue(packet.ItemId, out var identType) || identType == null)
            return false;

        var scene = SceneContext.Instance;
        var gadgetsModel = scene ? scene.GadgetDirector?._model : null;
        if (gadgetsModel == null)
            return false;

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            var before = gadgetsModel.GetCount(identType);
            gadgetsModel.SetCount(identType, packet.ItemCount);
            Baseline.Record(packet.ItemId, packet.ItemCount);
            if (Lycoreia.World.InventoryRecentPolicy.ShouldRecordGadgetObtained(
                    identType.TryCast<Il2Cpp.GadgetDefinition>() != null, before, packet.ItemCount))
                Lycoreia.Game.World.GadgetAdapter.NoteRecentObtained(identType);
            if (before != packet.ItemCount)
                DiagLog.LogDiagnostic("inventory", "update-applied", $"item={packet.ItemId}; name={identType.name}; before={before}; after={packet.ItemCount}; revision={packet.Revision}");
            if (!ClientRevision.TryAcceptUpdate(packet.Revision))
                return true;
            return true;
        }
        catch (Exception ex)
        {
            OwnedLog.LogWarning($"Failed to apply refinery update; it will be retried: {ex.Message}");
            return false;
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }
    }
}

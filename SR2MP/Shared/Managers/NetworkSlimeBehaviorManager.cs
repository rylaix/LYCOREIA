using System.Net;
using Il2CppMonomiPark.SlimeRancher.Slime.Dervish;
using Il2CppMonomiPark.SlimeRancher.Slime;
using Il2CppMonomiPark.SlimeRancher.Slime.Slumber;
using Il2CppMonomiPark.SlimeRancher.Slime.Tangle;
using SR2MP.Components.Actor;
using SR2MP.Packets.Slime;
using SR2MP.Packets.Slime.Dervish;
using SR2MP.Packets.Slime.Sloomber;
using SR2MP.Packets.Slime.Stalk;
using SR2MP.Packets.Slime.Tangle;
using SR2MP.Packets.Utils;
using SR2MP.Patches.Slime;
using SR2MP.Shared.Utils;

namespace SR2MP.Shared.Managers;

internal static class NetworkSlimeBehaviorManager
{
    private readonly record struct RevisionKey(InitialSlimeBehaviorSnapshotPacket.BehaviorKind Kind, long ActorId);
    private readonly record struct DeferredOwnerRequestKey(PacketType Type, long ActorId, string PlayerId);
    private sealed record DeferredOwnerRequest(IPEndPoint EndPoint, string PlayerId, object Packet, float QueuedAt);

    private enum ApplyStatus : byte
    {
        Applied,
        Retryable,
        AmbiguousFailure
    }

    private readonly record struct ApplyResult(ApplyStatus Status, string? Detail = null)
    {
        internal static ApplyResult Applied => new(ApplyStatus.Applied);
        internal static ApplyResult Retryable(string? detail = null) => new(ApplyStatus.Retryable, detail);
        internal static ApplyResult AmbiguousFailure(string? detail = null)
            => new(ApplyStatus.AmbiguousFailure, detail);
    }

    private sealed record AmbiguousApplyState(
        long Revision, object Packet, long RelatedActorId, bool RelatedActorWasPresent);

    private sealed class PendingApplyState<T>
    {
        private bool actorWasPresent;
        private bool relatedActorWasPresent;

        internal PendingApplyState(T packet, long actorId, long relatedActorId)
        {
            Packet = packet;
            ActorId = actorId;
            RelatedActorId = relatedActorId > 0 && relatedActorId != actorId ? relatedActorId : 0;
            Attempts = 1; // The immediate receive-side application has already run.
            NextAttemptAt = UnityEngine.Time.unscaledTime +
                            NetworkSlimeBehaviorManager.RetryDelayAfterFailure(Attempts);
            actorWasPresent = ActorManager.Actors.ContainsKey(ActorId);
            relatedActorWasPresent = RelatedActorId > 0 && ActorManager.Actors.ContainsKey(RelatedActorId);
        }

        internal T Packet { get; }
        internal long ActorId { get; }
        internal long RelatedActorId { get; }
        internal int Attempts { get; set; }
        internal float NextAttemptAt { get; set; }
        internal string? LastRetryDetail { get; set; }

        internal bool HasKnownActorDisappeared()
        {
            var actorExists = ActorManager.Actors.ContainsKey(ActorId);
            if (NetworkSlimeBehaviorManager.ShouldDiscardForMissingActor(actorWasPresent, actorExists))
                return true;
            actorWasPresent |= actorExists;

            if (RelatedActorId <= 0)
                return false;

            var relatedActorExists = ActorManager.Actors.ContainsKey(RelatedActorId);
            if (NetworkSlimeBehaviorManager.ShouldDiscardForMissingActor(
                    relatedActorWasPresent, relatedActorExists))
                return true;
            relatedActorWasPresent |= relatedActorExists;

            return false;
        }
    }

    private const int MaxPendingStates = InitialSlimeBehaviorSnapshotPacket.MaxEntries;
    private const int MaxPendingApplyAttempts = 10; // Includes the immediate receive-side attempt.
    private const float InitialPendingRetryDelaySeconds = 0.1f;
    private const float MaxPendingRetryDelaySeconds = 2f;
    private const float ApplyWarningIntervalSeconds = 5f;
    private const float GlobalApplyWarningIntervalSeconds = 0.5f;
    private const float DeferredOwnerRequestTimeoutSeconds = 10f;
    internal const float ActiveSloomberTimeoutSeconds = 30f;
    private static readonly Dictionary<long, PendingApplyState<StalkConsumableTargetPacket>> PendingStalkTargets = new();
    private static readonly Dictionary<long, PendingApplyState<TangleVineGrapplePacket>> PendingTangleStates = new();
    private static readonly Dictionary<long, PendingApplyState<DervishCyclonePacket>> PendingDervishStates = new();
    private static readonly Dictionary<long, PendingApplyState<SloomberYawnPacket>> PendingSloomberStates = new();
    private static readonly Dictionary<RevisionKey, long> Revisions = new();
    private static readonly Dictionary<RevisionKey, AmbiguousApplyState> AmbiguousApplyRevisions = new();
    private static readonly Dictionary<RevisionKey, float> LastApplyWarningAt = new();
    private static readonly Dictionary<long, StalkConsumableTargetPacket> LatestStalkTargets = new();
    private static readonly Dictionary<long, TangleVineGrapplePacket> LatestTangleStates = new();
    private static readonly Dictionary<long, DervishCyclonePacket> LatestDervishStates = new();
    private static readonly Dictionary<long, SloomberYawnPacket> LatestSloomberStates = new();
    private static readonly Dictionary<long, bool> AppliedSloomberStates = new();
    private static readonly Dictionary<long, float> ActiveSloomberSince = new();
    private static readonly Dictionary<DeferredOwnerRequestKey, DeferredOwnerRequest> DeferredOwnerRequests = new();
    private static float nextPendingLimitWarningAt;
    private static int suppressedPendingLimitWarnings;
    private static float nextGlobalApplyWarningAt;
    private static int suppressedApplyWarnings;

    internal static void Receive(SloomberYawnPacket packet)
        => Receive(packet, isSnapshot: false);

    private static bool Receive(SloomberYawnPacket packet, bool isSnapshot)
    {
        var actorId = packet.ActorId.Value;
        var key = new RevisionKey(InitialSlimeBehaviorSnapshotPacket.BehaviorKind.SloomberYawn, actorId);
        if (!IsValid(packet) || !CanAcceptBehaviorState(key, packet.Revision, isSnapshot, packet))
            return false;

        PendingSloomberStates.Remove(actorId);
        Remember(packet);
        FinishReceive(PendingSloomberStates, actorId, 0, Clone(packet), key, packet.Revision,
            TryApply(packet), $"Deferred Sloomber yawn state for slime {actorId} until its behavior is loaded.");
        return true;
    }

    internal static bool Receive(DervishCyclonePacket packet)
        => Receive(packet, isSnapshot: false);

    private static bool Receive(DervishCyclonePacket packet, bool isSnapshot)
    {
        var actorId = packet.ActorId.Value;
        var key = new RevisionKey(InitialSlimeBehaviorSnapshotPacket.BehaviorKind.DervishCyclone, actorId);
        if (!packet.IsValid || !CanAcceptBehaviorState(key, packet.Revision, isSnapshot, packet))
            return false;

        Remember(packet);
        PendingDervishStates.Remove(actorId);
        FinishReceive(PendingDervishStates, actorId, 0, Clone(packet), key, packet.Revision,
            TryApply(packet), $"Deferred Dervish cyclone state for slime {actorId} until its behavior is loaded.");
        return true;
    }

    internal static void Receive(StalkConsumableTargetPacket packet)
        => Receive(packet, isSnapshot: false);

    private static bool Receive(StalkConsumableTargetPacket packet, bool isSnapshot)
    {
        var actorId = packet.SlimeActorId.Value;
        var key = new RevisionKey(InitialSlimeBehaviorSnapshotPacket.BehaviorKind.StalkTarget, actorId);
        if (!IsValid(packet) || !CanAcceptBehaviorState(key, packet.Revision, isSnapshot, packet))
            return false;

        Remember(packet);

        // A later reliable state supersedes any earlier state that was waiting for
        // the target actor to load (for example, target A followed by clear).
        PendingStalkTargets.Remove(actorId);
        FinishReceive(PendingStalkTargets, actorId, packet.TargetActorId.Value, Clone(packet), key, packet.Revision,
            TryApply(packet),
            $"Deferred stalking target for slime {actorId}; slime, target, or behavior is not ready.");
        return true;
    }

    internal static void Receive(TangleVineGrapplePacket packet)
        => Receive(packet, isSnapshot: false);

    private static bool Receive(TangleVineGrapplePacket packet, bool isSnapshot)
    {
        var actorId = packet.TangleActorId.Value;
        var key = new RevisionKey(InitialSlimeBehaviorSnapshotPacket.BehaviorKind.TangleGrapple, actorId);
        if (!IsValid(packet) || !CanAcceptBehaviorState(key, packet.Revision, isSnapshot, packet))
            return false;

        Remember(packet);

        // Do not let a deferred grapple replay after a newer release or retarget.
        PendingTangleStates.Remove(actorId);
        FinishReceive(PendingTangleStates, actorId, packet.Active ? packet.TargetActorId.Value : 0,
            Clone(packet), key, packet.Revision, TryApply(packet),
            $"Deferred Tangle vine state for slime {actorId}; target {packet.TargetActorId.Value} is not ready.");
        return true;
    }

    /// <summary>
    /// Sends a local-owner behavior change. The host stamps it directly; clients
    /// submit revision zero and the authenticated server assigns the revision.
    /// </summary>
    internal static void Publish(StalkConsumableTargetPacket packet)
    {
        if (Main.Client.IsConnected)
        {
            var request = Clone(packet);
            request.Revision = 0;
            Main.Client.SendPacket(request);
        }

        if (!Main.Server.IsRunning)
            return;

        var authoritative = Clone(packet);
        if (!TryStampNext(authoritative))
            return;

        Remember(authoritative);
        Main.Server.SendToAll(authoritative);
    }

    internal static void Publish(TangleVineGrapplePacket packet)
    {
        if (Main.Client.IsConnected)
        {
            var request = Clone(packet);
            request.Revision = 0;
            Main.Client.SendPacket(request);
        }

        if (!Main.Server.IsRunning)
            return;

        var authoritative = Clone(packet);
        if (!TryStampNext(authoritative))
            return;

        Remember(authoritative);
        Main.Server.SendToAll(authoritative);
    }

    internal static void Publish(DervishCyclonePacket packet)
    {
        if (Main.Client.IsConnected)
        {
            var request = Clone(packet);
            request.Revision = 0;
            Main.Client.SendPacket(request);
        }

        if (!Main.Server.IsRunning)
            return;

        var authoritative = Clone(packet);
        if (!TryStampNext(authoritative))
            return;

        Remember(authoritative);
        Main.Server.SendToAll(authoritative);
    }

    internal static void Publish(SloomberYawnPacket packet)
    {
        if (Main.Client.IsConnected)
        {
            var request = Clone(packet);
            request.Revision = 0;
            Main.Client.SendPacket(request);
        }

        if (!Main.Server.IsRunning)
            return;

        var authoritative = Clone(packet);
        if (!TryStampNext(authoritative))
            return;

        Remember(authoritative);
        AppliedSloomberStates[authoritative.ActorId.Value] = authoritative.Active;
        Main.Server.SendToAll(authoritative);
    }

    internal static bool AcceptClientPacket(StalkConsumableTargetPacket packet)
    {
        if (!IsValid(packet) || packet.Revision != 0 || !TryStampNext(packet))
            return false;

        return Receive(packet, isSnapshot: false);
    }

    internal static bool AcceptClientPacket(TangleVineGrapplePacket packet)
    {
        if (!IsValid(packet) || packet.Revision != 0 || !TryStampNext(packet))
            return false;

        return Receive(packet, isSnapshot: false);
    }

    internal static bool AcceptClientPacket(DervishCyclonePacket packet)
    {
        if (!packet.IsValid || packet.Revision != 0 || !TryStampNext(packet))
            return false;

        return Receive(packet, isSnapshot: false);
    }

    internal static bool AcceptClientPacket(SloomberYawnPacket packet)
    {
        if (!IsValid(packet) || packet.ForceClear || packet.Revision != 0 || !TryStampNext(packet))
            return false;

        return Receive(packet, isSnapshot: false);
    }

    internal static bool TryDeferOwnerRequest(StalkConsumableTargetPacket packet, IPEndPoint? endpoint)
        => IsValid(packet) && packet.Revision == 0 &&
           TryDeferOwnerRequest(PacketType.StalkConsumableTarget, packet.SlimeActorId.Value, endpoint, Clone(packet));

    internal static bool TryDeferOwnerRequest(TangleVineGrapplePacket packet, IPEndPoint? endpoint)
        => IsValid(packet) && packet.Revision == 0 &&
           TryDeferOwnerRequest(PacketType.TangleVineGrapple, packet.TangleActorId.Value, endpoint, Clone(packet));

    internal static bool TryDeferOwnerRequest(DervishCyclonePacket packet, IPEndPoint? endpoint)
        => packet.IsValid && packet.Revision == 0 &&
           TryDeferOwnerRequest(PacketType.DervishCyclone, packet.ActorId.Value, endpoint, Clone(packet));

    internal static bool TryDeferOwnerRequest(SloomberYawnPacket packet, IPEndPoint? endpoint)
        => IsValid(packet) && !packet.ForceClear && packet.Revision == 0 &&
           TryDeferOwnerRequest(PacketType.SloomberYawn, packet.ActorId.Value, endpoint, Clone(packet));

    private static bool TryDeferOwnerRequest(PacketType type, long actorId, IPEndPoint? endpoint, object packet)
    {
        if (!Main.Server.IsRunning || endpoint == null || actorId <= 0 ||
            !Main.Server.ClientManager.TryGetClient(endpoint, out var client) || client == null ||
            string.IsNullOrWhiteSpace(client.PlayerId))
            return false;

        var idRangeStart = (long)PlayerIdGenerator.GetPlayerIDNumber(client.PlayerId) * ActorIdOffset;
        var actorBelongsToSender = actorId >= idRangeStart && actorId < idRangeStart + ActorIdOffset;
        if (!ShouldDeferOwnerRequest(isServer: true, hasKnownOwner: HasKnownOwner(actorId),
                actorBelongsToSender: actorBelongsToSender))
            return false;

        var key = new DeferredOwnerRequestKey(type, actorId, client.PlayerId);
        if (!DeferredOwnerRequests.ContainsKey(key) && DeferredOwnerRequests.Count >= MaxPendingStates)
        {
            SrLogger.LogWarning($"Dropped early slime behavior request for actor {actorId}; pending limit reached.");
            return false;
        }

        DeferredOwnerRequests[key] = new DeferredOwnerRequest(endpoint, client.PlayerId, packet,
            UnityEngine.Time.unscaledTime);
        return true;
    }

    internal static bool ShouldDeferOwnerRequest(bool isServer, bool hasKnownOwner, bool actorBelongsToSender)
        => isServer && !hasKnownOwner && actorBelongsToSender;

    internal static bool ShouldExpireDeferredOwnerRequest(float now, float queuedAt)
        => now - queuedAt > DeferredOwnerRequestTimeoutSeconds;

    internal static bool VerifyDeferredOwnerRequestPolicy()
        => ShouldDeferOwnerRequest(isServer: true, hasKnownOwner: false, actorBelongsToSender: true)
           && !ShouldDeferOwnerRequest(isServer: false, hasKnownOwner: false, actorBelongsToSender: true)
           && !ShouldDeferOwnerRequest(isServer: true, hasKnownOwner: true, actorBelongsToSender: true)
           && !ShouldDeferOwnerRequest(isServer: true, hasKnownOwner: false, actorBelongsToSender: false)
           && !ShouldExpireDeferredOwnerRequest(now: 10f, queuedAt: 1f)
           && ShouldExpireDeferredOwnerRequest(now: 12f, queuedAt: 1f)
           && !ShouldExpireSloomber(now: 30f, startedAt: 1f)
           && ShouldExpireSloomber(now: 32f, startedAt: 1f);

    private static bool ShouldExpireSloomber(float now, float startedAt)
        => now - startedAt > ActiveSloomberTimeoutSeconds;

    private static bool HasKnownOwner(long actorId)
    {
        if (ActorManager.Actors.TryGetValue(actorId, out var model) &&
            model.TryGetNetworkComponent(out var actor) && !string.IsNullOrWhiteSpace(actor.CurrentOwnerId))
            return true;

        return ActorManager.OwnershipByActorId.TryGetValue(actorId, out var ownerId) &&
               !string.IsNullOrWhiteSpace(ownerId);
    }

    private static bool TryGetKnownOwner(long actorId, out string ownerId)
    {
        if (ActorManager.Actors.TryGetValue(actorId, out var model) &&
            model.TryGetNetworkComponent(out var actor) && !string.IsNullOrWhiteSpace(actor.CurrentOwnerId))
        {
            ownerId = actor.CurrentOwnerId;
            return true;
        }

        if (ActorManager.OwnershipByActorId.TryGetValue(actorId, out var cachedOwnerId) &&
            !string.IsNullOrWhiteSpace(cachedOwnerId))
        {
            ownerId = cachedOwnerId;
            return true;
        }

        ownerId = string.Empty;
        return false;
    }

    private static void PumpDeferredOwnerRequests()
    {
        var now = UnityEngine.Time.unscaledTime;
        foreach (var (key, request) in DeferredOwnerRequests.ToArray())
        {
            if (ShouldExpireDeferredOwnerRequest(now, request.QueuedAt) ||
                !Main.Server.ClientManager.TryGetClient(request.EndPoint, out var client) || client == null ||
                !string.Equals(client.PlayerId, request.PlayerId, StringComparison.Ordinal))
            {
                DeferredOwnerRequests.Remove(key);
                continue;
            }

            if (!TryGetKnownOwner(key.ActorId, out var currentOwnerId))
                continue;

            if (!string.Equals(currentOwnerId, request.PlayerId, StringComparison.Ordinal))
            {
                DeferredOwnerRequests.Remove(key);
                continue;
            }

            var accepted = request.Packet switch
            {
                StalkConsumableTargetPacket packet when AcceptClientPacket(packet) => Broadcast(packet),
                TangleVineGrapplePacket packet when AcceptClientPacket(packet) => Broadcast(packet),
                DervishCyclonePacket packet when AcceptClientPacket(packet) => Broadcast(packet),
                SloomberYawnPacket packet when AcceptClientPacket(packet) => Broadcast(packet),
                _ => false
            };

            DeferredOwnerRequests.Remove(key);
            if (!accepted)
                SrLogger.LogDebug($"Dropped deferred slime behavior request for actor {key.ActorId}: state was invalid or stale.");
        }
    }

    private static bool Broadcast<T>(T packet) where T : IPacket
    {
        Main.Server.SendToAll(packet);
        return true;
    }

    private static void PumpSloomberTimeouts()
    {
        if (!Main.Server.IsRunning)
            return;

        var now = UnityEngine.Time.unscaledTime;
        foreach (var (actorId, packet) in LatestSloomberStates.ToArray())
        {
            if (!packet.Active || !ActiveSloomberSince.TryGetValue(actorId, out var startedAt) ||
                !ShouldExpireSloomber(now, startedAt))
                continue;

            var forceClear = Clone(packet);
            forceClear.Active = false;
            forceClear.ForceClear = true;
            if (!TryStampNext(forceClear))
                continue;

            if (Receive(forceClear, isSnapshot: false))
            {
                Main.Server.SendToAll(forceClear);
                SrLogger.LogWarning($"Cleared stale Sloomber yawn state for actor {actorId} after its owner stopped reporting.");
            }
        }
    }

    internal static void ReceiveSnapshot(InitialSlimeBehaviorSnapshotPacket packet)
    {
        if (!InitialSlimeBehaviorSnapshotPacket.HasUniqueValidEntries(packet.Entries))
        {
            SrLogger.LogWarning("Rejected invalid or oversized slime behavior snapshot.");
            return;
        }

        foreach (var entry in packet.Entries)
        {
            switch (entry.Kind)
            {
                case InitialSlimeBehaviorSnapshotPacket.BehaviorKind.StalkTarget:
                    Receive(new StalkConsumableTargetPacket
                    {
                        SlimeActorId = new(entry.ActorId),
                        TargetActorId = new(entry.TargetActorId),
                        Revision = entry.Revision
                    }, isSnapshot: true);
                    break;
                case InitialSlimeBehaviorSnapshotPacket.BehaviorKind.TangleGrapple:
                    Receive(new TangleVineGrapplePacket
                    {
                        TangleActorId = new(entry.ActorId),
                        TargetActorId = new(entry.TargetActorId),
                        Active = entry.Active,
                        Revision = entry.Revision
                    }, isSnapshot: true);
                    break;
                case InitialSlimeBehaviorSnapshotPacket.BehaviorKind.DervishCyclone:
                    Receive(new DervishCyclonePacket
                    {
                        ActorId = new(entry.ActorId),
                        Active = entry.Active,
                        Size = entry.Size,
                        FloatDir = entry.FloatDir,
                        Revision = entry.Revision
                    }, isSnapshot: true);
                    break;
                case InitialSlimeBehaviorSnapshotPacket.BehaviorKind.SloomberYawn:
                    Receive(new SloomberYawnPacket
                    {
                        ActorId = new(entry.ActorId),
                        Active = entry.Active,
                        // Inactive snapshot state is a reconciliation clear. It
                        // must end stale reactables without replaying DoYawn().
                        ForceClear = ShouldForceClearSloomberSnapshot(entry.Active),
                        Revision = entry.Revision
                    }, isSnapshot: true);
                    break;
            }
        }
    }

    internal static InitialSlimeBehaviorSnapshotPacket CreateSnapshot()
    {
        var states = new Dictionary<RevisionKey, InitialSlimeBehaviorSnapshotPacket.Entry>();
        var actorIds = ActorManager.Actors.Keys.ToHashSet();

        // Retain the last authoritative state for actors whose behavior object is
        // currently hibernated/unloaded. Actor IDs are not reused during a session.
        foreach (var (actorId, packet) in LatestStalkTargets)
        {
            if (actorIds.Contains(actorId))
                states[new(InitialSlimeBehaviorSnapshotPacket.BehaviorKind.StalkTarget, actorId)] = ToSnapshotEntry(packet);
        }

        foreach (var (actorId, packet) in LatestTangleStates)
        {
            if (actorIds.Contains(actorId))
                states[new(InitialSlimeBehaviorSnapshotPacket.BehaviorKind.TangleGrapple, actorId)] = ToSnapshotEntry(packet);
        }

        foreach (var (actorId, packet) in LatestDervishStates)
        {
            if (actorIds.Contains(actorId))
                states[new(InitialSlimeBehaviorSnapshotPacket.BehaviorKind.DervishCyclone, actorId)] = ToSnapshotEntry(packet);
        }

        // Keep inactive revisions as tombstones so a joining/resyncing peer can
        // clear an active yawn it already applied. The receive path marks those
        // snapshot entries ForceClear, so completed yawns are not replayed.
        foreach (var (actorId, packet) in LatestSloomberStates)
        {
            if (ShouldIncludeSloomberSnapshot(packet) && actorIds.Contains(actorId))
                states[new(InitialSlimeBehaviorSnapshotPacket.BehaviorKind.SloomberYawn, actorId)] = ToSnapshotEntry(packet);
        }

        foreach (var (actorId, model) in ActorManager.Actors)
        {
            if (actorId <= 0 || model == null)
                continue;

            try
            {
                var gameObject = model.GetGameObject();
                if (!gameObject)
                    continue;

                var stalk = gameObject.GetComponent<StalkConsumable>();
                if (stalk)
                {
                    var targetId = GetTargetActorId(stalk!._target);
                    if (stalk!._target && targetId == 0)
                    {
                        SrLogger.LogWarning($"Stalking target on actor {actorId} has no network ID; snapshot will represent it as cleared.");
                        AddCurrentStalkState(states, actorId, 0);
                    }
                    else
                    {
                        AddCurrentStalkState(states, actorId, targetId);
                    }
                }

                var vine = gameObject.GetComponent<GroundVine>();
                if (vine)
                {
                    var targetId = GetTargetActorId(vine!._target);
                    if (vine!._target && targetId == 0)
                    {
                        SrLogger.LogWarning($"Tangle target on actor {actorId} has no network ID; snapshot will represent it as released.");
                        AddCurrentTangleState(states, actorId, 0, active: false);
                    }
                    else
                    {
                        AddCurrentTangleState(states, actorId, targetId, targetId != 0);
                    }
                }

                var dervish = gameObject.GetComponent<DervishSlimeSpin>();
                if (dervish && !PendingDervishStates.ContainsKey(actorId))
                {
                    var active = !!dervish!._cyclone;
                    AddCurrentDervishState(states, actorId, active,
                        active ? (byte)(int)dervish!.GetCycloneSize() : (byte)0,
                        dervish!._floatDir);
                }
            }
            catch (Exception ex)
            {
                SrLogger.LogWarning($"Could not inspect slime behavior state for actor {actorId}: {ex.Message}");
            }
        }

        var entries = states.Values
            .OrderBy(entry => entry.Kind)
            .ThenBy(entry => entry.ActorId)
            .Take(InitialSlimeBehaviorSnapshotPacket.MaxEntries)
            .ToList();
        if (states.Count > InitialSlimeBehaviorSnapshotPacket.MaxEntries)
            SrLogger.LogWarning($"Slime behavior snapshot capped at {InitialSlimeBehaviorSnapshotPacket.MaxEntries} of {states.Count} states.");

        return new InitialSlimeBehaviorSnapshotPacket { Entries = entries };
    }

#if SR2MP_TEST
    internal static bool VerifyRevisionOrdering()
        => ShouldAcceptRevision(incoming: 2, current: 1, isSnapshot: false)
           && !ShouldAcceptRevision(incoming: 1, current: 2, isSnapshot: false)
           && !ShouldAcceptRevision(incoming: 2, current: 2, isSnapshot: false)
           && ShouldAcceptRevision(incoming: 2, current: 2, isSnapshot: true)
           && !ShouldAcceptRevision(incoming: 1, current: 2, isSnapshot: true)
           && !ShouldAcceptRevision(incoming: 0, current: 0, isSnapshot: true)
           && VerifyDeferredOwnerRequestPolicy()
           && VerifySnapshotPolicy()
           && VerifyPendingApplyRetryPolicy()
           && SlimeBehaviorAuthority.VerifyBehaviorEmissionPolicy()
           && DervishCyclonePacket.VerifySerializationRoundTrip()
           && SloomberYawnPacket.VerifySerializationRoundTrip()
           && InitialSlimeBehaviorSnapshotPacket.VerifySerializationRoundTrip();

    private static bool VerifyPendingApplyRetryPolicy()
        => MaxPendingApplyAttempts == 10
           && !ShouldStopPendingRetries(0)
           && !ShouldStopPendingRetries(9)
           && ShouldStopPendingRetries(10)
           && Math.Abs(RetryDelayAfterFailure(1) - 0.1f) < 0.001f
           && Math.Abs(RetryDelayAfterFailure(2) - 0.2f) < 0.001f
           && Math.Abs(RetryDelayAfterFailure(3) - 0.4f) < 0.001f
           && Math.Abs(RetryDelayAfterFailure(5) - 1.6f) < 0.001f
           && RetryDelayAfterFailure(6) == MaxPendingRetryDelaySeconds
           && RetryDelayAfterFailure(100) == MaxPendingRetryDelaySeconds
           && IsAmbiguousFailureRevision(incoming: 4, failed: 4)
           && !IsAmbiguousFailureRevision(incoming: 5, failed: 4)
           && HasSameBehaviorState(
               new SloomberYawnPacket { ActorId = new(1), Active = true, Revision = 4 },
               new SloomberYawnPacket { ActorId = new(1), Active = true, Revision = 5 })
           && !HasSameBehaviorState(
               new SloomberYawnPacket { ActorId = new(1), Active = true, Revision = 4 },
               new SloomberYawnPacket { ActorId = new(1), Active = false, Revision = 5 })
           && !ShouldDiscardForMissingActor(wasPresent: false, isPresent: false)
           && !ShouldDiscardForMissingActor(wasPresent: false, isPresent: true)
           && !ShouldDiscardForMissingActor(wasPresent: true, isPresent: true)
           && ShouldDiscardForMissingActor(wasPresent: true, isPresent: false);
#endif

    internal static bool VerifySnapshotPolicy()
        => ShouldIncludeSloomberSnapshot(new SloomberYawnPacket
           {
               ActorId = new(1), Active = true, Revision = 1
           })
           && ShouldIncludeSloomberSnapshot(new SloomberYawnPacket
           {
               ActorId = new(1), Active = false, Revision = 2, ForceClear = true
           })
           && !ShouldIncludeSloomberSnapshot(new SloomberYawnPacket
           {
               ActorId = new(0), Active = false, Revision = 2
           })
           && !ShouldForceClearSloomberSnapshot(active: true)
           && ShouldForceClearSloomberSnapshot(active: false);

    private static bool ShouldIncludeSloomberSnapshot(SloomberYawnPacket packet)
        => packet.ActorId.Value > 0 && packet.Revision > 0;

    private static bool ShouldForceClearSloomberSnapshot(bool active) => !active;

    private static bool CanAcceptBehaviorState<T>(
        RevisionKey key, long revision, bool isSnapshot, T packet)
    {
        if (!ShouldAcceptRevision(revision, GetRevision(key.Kind, key.ActorId), isSnapshot))
            return false;

        if (!AmbiguousApplyRevisions.TryGetValue(key, out var failedState))
            return true;

        // A duplicate snapshot must not replay a state whose apply threw after it
        // entered a native side effect. A newer authoritative revision is a new
        // operation and may be attempted normally.
        if (IsAmbiguousFailureRevision(revision, failedState.Revision))
            return false;

        // Some behavior emitters can report the same semantic state at a newer
        // revision. Do not replay that payload after an ambiguous partial apply.
        if (HasSameBehaviorState(failedState.Packet, packet))
            return false;

        if (revision > failedState.Revision)
            AmbiguousApplyRevisions.Remove(key);

        return true;
    }

    private static void FinishReceive<T>(
        Dictionary<long, PendingApplyState<T>> pending,
        long actorId,
        long relatedActorId,
        T packet,
        RevisionKey key,
        long revision,
        ApplyResult result,
        string deferredMessage)
    {
        switch (result.Status)
        {
            case ApplyStatus.Applied:
                return;
            case ApplyStatus.Retryable:
                if (EnqueuePending(pending, actorId, relatedActorId, packet, result.Detail))
                    SrLogger.LogDebug(deferredMessage);
                return;
            case ApplyStatus.AmbiguousFailure:
                RecordAmbiguousApplyFailure(key, revision, packet!, relatedActorId, result.Detail);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(result));
        }
    }

    private static float RetryDelayAfterFailure(int attemptsMade)
    {
        var exponent = Math.Clamp(attemptsMade - 1, 0, MaxPendingApplyAttempts);
        return Math.Min(MaxPendingRetryDelaySeconds,
            InitialPendingRetryDelaySeconds * (1 << exponent));
    }

    private static bool ShouldStopPendingRetries(int attemptsMade)
        => attemptsMade >= MaxPendingApplyAttempts;

    private static bool IsAmbiguousFailureRevision(long incoming, long failed)
        => incoming == failed;

    private static bool HasSameBehaviorState(object? failedPacket, object? incomingPacket)
        => (failedPacket, incomingPacket) switch
        {
            (StalkConsumableTargetPacket failed, StalkConsumableTargetPacket incoming) =>
                failed.SlimeActorId.Value == incoming.SlimeActorId.Value &&
                failed.TargetActorId.Value == incoming.TargetActorId.Value,
            (TangleVineGrapplePacket failed, TangleVineGrapplePacket incoming) =>
                failed.TangleActorId.Value == incoming.TangleActorId.Value &&
                failed.TargetActorId.Value == incoming.TargetActorId.Value &&
                failed.Active == incoming.Active,
            (DervishCyclonePacket failed, DervishCyclonePacket incoming) =>
                failed.ActorId.Value == incoming.ActorId.Value && failed.Active == incoming.Active &&
                failed.Size == incoming.Size && failed.FloatDir == incoming.FloatDir,
            (SloomberYawnPacket failed, SloomberYawnPacket incoming) =>
                failed.ActorId.Value == incoming.ActorId.Value && failed.Active == incoming.Active &&
                failed.ForceClear == incoming.ForceClear,
            _ => false
        };

    private static bool ShouldDiscardForMissingActor(bool wasPresent, bool isPresent)
        => wasPresent && !isPresent;

    private static void RecordAmbiguousApplyFailure(
        RevisionKey key, long revision, object packet, long relatedActorId, string? detail)
    {
        var relatedId = relatedActorId > 0 && relatedActorId != key.ActorId ? relatedActorId : 0;
        AmbiguousApplyRevisions[key] = new AmbiguousApplyState(
            revision, packet, relatedId, relatedId > 0 && ActorManager.Actors.ContainsKey(relatedId));
        LogApplyWarning(key,
            $"Slime behavior {key.Kind} for actor {key.ActorId} revision {revision} failed after entering an apply operation; " +
            $"the same revision will not be replayed because its effects may be partial. {detail ?? ""}");
    }

    private static void LogRetryExhausted(RevisionKey key, long revision, int attempts, string? detail)
        => LogApplyWarning(key,
            $"Slime behavior {key.Kind} for actor {key.ActorId} revision {revision} was not ready after " +
            $"{attempts} bounded attempts; it was removed from the retry queue. A later snapshot or newer update may retry it. " +
            $"{detail ?? ""}");

    private static void LogApplyWarning(RevisionKey key, string message)
    {
        var now = UnityEngine.Time.unscaledTime;
        if (LastApplyWarningAt.TryGetValue(key, out var lastLoggedAt) &&
            now - lastLoggedAt < ApplyWarningIntervalSeconds)
            return;

        LastApplyWarningAt[key] = now;
        if (now < nextGlobalApplyWarningAt)
        {
            suppressedApplyWarnings++;
            return;
        }

        EmitApplyWarning(now, message);
    }

    private static void EmitApplyWarning(float now, string message)
    {
        var suppressed = suppressedApplyWarnings;
        suppressedApplyWarnings = 0;
        nextGlobalApplyWarningAt = now + GlobalApplyWarningIntervalSeconds;
        SrLogger.LogWarning(message +
                            (suppressed > 0 ? $" Suppressed {suppressed} other slime behavior warnings." : ""));
    }

    private static void FlushSuppressedApplyWarnings()
    {
        var now = UnityEngine.Time.unscaledTime;
        if (suppressedApplyWarnings <= 0 || now < nextGlobalApplyWarningAt)
            return;

        EmitApplyWarning(now, "Additional slime behavior warnings were throttled during retry processing.");
    }

    private static void LogPendingLimitWarning(long actorId)
    {
        var now = UnityEngine.Time.unscaledTime;
        if (now < nextPendingLimitWarningAt)
        {
            suppressedPendingLimitWarnings++;
            return;
        }

        nextPendingLimitWarningAt = now + ApplyWarningIntervalSeconds;
        var suppressed = suppressedPendingLimitWarnings;
        suppressedPendingLimitWarnings = 0;
        SrLogger.LogWarning(
            $"Dropped deferred slime behavior state for actor {actorId}; pending limit {MaxPendingStates} reached." +
            (suppressed > 0 ? $" Suppressed {suppressed} similar warnings." : ""));
    }

    internal static void Pump()
    {
        if (!Main.Server.IsRunning && !Main.Client.IsConnected)
        {
            PendingStalkTargets.Clear();
            PendingTangleStates.Clear();
            PendingDervishStates.Clear();
            PendingSloomberStates.Clear();
            Revisions.Clear();
            AmbiguousApplyRevisions.Clear();
            LastApplyWarningAt.Clear();
            LatestStalkTargets.Clear();
            LatestTangleStates.Clear();
            LatestDervishStates.Clear();
            LatestSloomberStates.Clear();
            AppliedSloomberStates.Clear();
            ActiveSloomberSince.Clear();
            DeferredOwnerRequests.Clear();
            nextPendingLimitWarningAt = 0;
            suppressedPendingLimitWarnings = 0;
            nextGlobalApplyWarningAt = 0;
            suppressedApplyWarnings = 0;
            return;
        }

        if (Main.Server.IsRunning)
        {
            PumpDeferredOwnerRequests();
            PumpSloomberTimeouts();
        }

        PumpPending(PendingStalkTargets, TryApply,
            InitialSlimeBehaviorSnapshotPacket.BehaviorKind.StalkTarget, packet => packet.Revision);
        PumpPending(PendingTangleStates, TryApply,
            InitialSlimeBehaviorSnapshotPacket.BehaviorKind.TangleGrapple, packet => packet.Revision);
        PumpPending(PendingDervishStates, TryApply,
            InitialSlimeBehaviorSnapshotPacket.BehaviorKind.DervishCyclone, packet => packet.Revision);
        PumpPending(PendingSloomberStates, TryApply,
            InitialSlimeBehaviorSnapshotPacket.BehaviorKind.SloomberYawn, packet => packet.Revision);
        PruneAmbiguousApplyFailures();
        FlushSuppressedApplyWarnings();
    }

    private static void PumpPending<T>(
        Dictionary<long, PendingApplyState<T>> pending,
        Func<T, ApplyResult> tryApply,
        InitialSlimeBehaviorSnapshotPacket.BehaviorKind kind,
        Func<T, long> getRevision)
    {
        var now = UnityEngine.Time.unscaledTime;
        foreach (var (actorId, state) in pending.ToArray())
        {
            if (state.HasKnownActorDisappeared())
            {
                pending.Remove(actorId);
                continue;
            }

            if (now < state.NextAttemptAt)
                continue;

            var result = tryApply(state.Packet);
            var key = new RevisionKey(kind, actorId);
            var revision = getRevision(state.Packet);
            switch (result.Status)
            {
                case ApplyStatus.Applied:
                    pending.Remove(actorId);
                    break;
                case ApplyStatus.AmbiguousFailure:
                    pending.Remove(actorId);
                    RecordAmbiguousApplyFailure(key, revision, state.Packet!, state.RelatedActorId, result.Detail);
                    break;
                case ApplyStatus.Retryable:
                    state.LastRetryDetail = result.Detail ?? state.LastRetryDetail;
                    var attemptsAfterThis = state.Attempts + 1;
                    if (ShouldStopPendingRetries(attemptsAfterThis))
                    {
                        pending.Remove(actorId);
                        LogRetryExhausted(key, revision, attemptsAfterThis, state.LastRetryDetail);
                        break;
                    }

                    state.Attempts = attemptsAfterThis;
                    state.NextAttemptAt = now + RetryDelayAfterFailure(state.Attempts);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(result));
            }
        }
    }

    private static void PruneAmbiguousApplyFailures()
    {
        foreach (var (key, failure) in AmbiguousApplyRevisions.ToArray())
        {
            var primaryExists = ActorManager.Actors.ContainsKey(key.ActorId);
            var relatedExists = failure.RelatedActorId <= 0 ||
                                ActorManager.Actors.ContainsKey(failure.RelatedActorId);
            if (primaryExists &&
                !ShouldDiscardForMissingActor(failure.RelatedActorWasPresent, relatedExists))
                continue;

            AmbiguousApplyRevisions.Remove(key);
            LastApplyWarningAt.Remove(key);
        }

        foreach (var (key, _) in LastApplyWarningAt.ToArray())
        {
            if (!ActorManager.Actors.ContainsKey(key.ActorId))
                LastApplyWarningAt.Remove(key);
        }
    }

    private static bool IsValid(StalkConsumableTargetPacket packet)
        => packet.SlimeActorId.Value > 0 && packet.TargetActorId.Value >= 0 && packet.Revision >= 0;

    private static bool IsValid(TangleVineGrapplePacket packet)
        => packet.TangleActorId.Value > 0 && packet.TargetActorId.Value >= 0 && packet.Revision >= 0 &&
           (packet.Active ? packet.TargetActorId.Value > 0 : packet.TargetActorId.Value == 0);

    private static bool IsValid(SloomberYawnPacket packet)
        => packet.IsValid;

    private static bool ShouldAcceptRevision(long incoming, long current, bool isSnapshot)
        => incoming > 0 && (incoming > current || isSnapshot && incoming == current);

    private static long GetRevision(InitialSlimeBehaviorSnapshotPacket.BehaviorKind kind, long actorId)
        => Revisions.TryGetValue(new RevisionKey(kind, actorId), out var revision) ? revision : 0;

    private static bool TryStampNext(StalkConsumableTargetPacket packet)
    {
        var current = GetRevision(InitialSlimeBehaviorSnapshotPacket.BehaviorKind.StalkTarget, packet.SlimeActorId.Value);
        if (current == long.MaxValue)
        {
            SrLogger.LogWarning($"Stalk behavior revision exhausted for actor {packet.SlimeActorId.Value}.");
            return false;
        }

        packet.Revision = current + 1;
        return true;
    }

    private static bool TryStampNext(TangleVineGrapplePacket packet)
    {
        var current = GetRevision(InitialSlimeBehaviorSnapshotPacket.BehaviorKind.TangleGrapple, packet.TangleActorId.Value);
        if (current == long.MaxValue)
        {
            SrLogger.LogWarning($"Tangle behavior revision exhausted for actor {packet.TangleActorId.Value}.");
            return false;
        }

        packet.Revision = current + 1;
        return true;
    }

    private static bool TryStampNext(DervishCyclonePacket packet)
    {
        var current = GetRevision(InitialSlimeBehaviorSnapshotPacket.BehaviorKind.DervishCyclone, packet.ActorId.Value);
        if (current == long.MaxValue)
        {
            SrLogger.LogWarning($"Dervish behavior revision exhausted for actor {packet.ActorId.Value}.");
            return false;
        }

        packet.Revision = current + 1;
        return true;
    }

    private static bool TryStampNext(SloomberYawnPacket packet)
    {
        var current = GetRevision(InitialSlimeBehaviorSnapshotPacket.BehaviorKind.SloomberYawn, packet.ActorId.Value);
        if (current == long.MaxValue)
        {
            SrLogger.LogWarning($"Sloomber behavior revision exhausted for actor {packet.ActorId.Value}.");
            return false;
        }

        packet.Revision = current + 1;
        return true;
    }

    private static void Remember(StalkConsumableTargetPacket packet)
    {
        var actorId = packet.SlimeActorId.Value;
        var key = new RevisionKey(InitialSlimeBehaviorSnapshotPacket.BehaviorKind.StalkTarget, actorId);
        Revisions[key] = Math.Max(GetRevision(key.Kind, actorId), packet.Revision);
        LatestStalkTargets[actorId] = Clone(packet);
    }

    private static void Remember(TangleVineGrapplePacket packet)
    {
        var actorId = packet.TangleActorId.Value;
        var key = new RevisionKey(InitialSlimeBehaviorSnapshotPacket.BehaviorKind.TangleGrapple, actorId);
        Revisions[key] = Math.Max(GetRevision(key.Kind, actorId), packet.Revision);
        LatestTangleStates[actorId] = Clone(packet);
    }

    private static void Remember(DervishCyclonePacket packet)
    {
        var actorId = packet.ActorId.Value;
        var key = new RevisionKey(InitialSlimeBehaviorSnapshotPacket.BehaviorKind.DervishCyclone, actorId);
        Revisions[key] = Math.Max(GetRevision(key.Kind, actorId), packet.Revision);
        LatestDervishStates[actorId] = Clone(packet);
    }

    private static void Remember(SloomberYawnPacket packet)
    {
        var actorId = packet.ActorId.Value;
        var key = new RevisionKey(InitialSlimeBehaviorSnapshotPacket.BehaviorKind.SloomberYawn, actorId);
        Revisions[key] = Math.Max(GetRevision(key.Kind, actorId), packet.Revision);
        LatestSloomberStates[actorId] = Clone(packet);
        if (packet.Active)
            ActiveSloomberSince[actorId] = UnityEngine.Time.unscaledTime;
        else
            ActiveSloomberSince.Remove(actorId);
    }

    private static bool EnqueuePending<T>(
        Dictionary<long, PendingApplyState<T>> pending,
        long actorId,
        long relatedActorId,
        T packet,
        string? retryDetail)
    {
        if (!pending.ContainsKey(actorId) && pending.Count >= MaxPendingStates)
        {
            LogPendingLimitWarning(actorId);
            return false;
        }

        var state = new PendingApplyState<T>(packet, actorId, relatedActorId)
        {
            LastRetryDetail = retryDetail
        };
        pending[actorId] = state;
        return true;
    }

    private static StalkConsumableTargetPacket Clone(StalkConsumableTargetPacket packet)
        => new()
        {
            SlimeActorId = packet.SlimeActorId,
            TargetActorId = packet.TargetActorId,
            Revision = packet.Revision
        };

    private static TangleVineGrapplePacket Clone(TangleVineGrapplePacket packet)
        => new()
        {
            TangleActorId = packet.TangleActorId,
            TargetActorId = packet.TargetActorId,
            Active = packet.Active,
            Revision = packet.Revision
        };

    private static DervishCyclonePacket Clone(DervishCyclonePacket packet)
        => new()
        {
            ActorId = packet.ActorId,
            Active = packet.Active,
            Size = packet.Size,
            FloatDir = packet.FloatDir,
            Revision = packet.Revision
        };

    private static SloomberYawnPacket Clone(SloomberYawnPacket packet)
        => new()
        {
            ActorId = packet.ActorId,
            Active = packet.Active,
            ForceClear = packet.ForceClear,
            Revision = packet.Revision
        };

    private static InitialSlimeBehaviorSnapshotPacket.Entry ToSnapshotEntry(StalkConsumableTargetPacket packet)
        => new()
        {
            Kind = InitialSlimeBehaviorSnapshotPacket.BehaviorKind.StalkTarget,
            ActorId = packet.SlimeActorId.Value,
            TargetActorId = packet.TargetActorId.Value,
            Revision = packet.Revision,
            Active = packet.TargetActorId.Value != 0
        };

    private static InitialSlimeBehaviorSnapshotPacket.Entry ToSnapshotEntry(TangleVineGrapplePacket packet)
        => new()
        {
            Kind = InitialSlimeBehaviorSnapshotPacket.BehaviorKind.TangleGrapple,
            ActorId = packet.TangleActorId.Value,
            TargetActorId = packet.TargetActorId.Value,
            Revision = packet.Revision,
            Active = packet.Active
        };

    private static InitialSlimeBehaviorSnapshotPacket.Entry ToSnapshotEntry(DervishCyclonePacket packet)
        => new()
        {
            Kind = InitialSlimeBehaviorSnapshotPacket.BehaviorKind.DervishCyclone,
            ActorId = packet.ActorId.Value,
            TargetActorId = 0,
            Revision = packet.Revision,
            Active = packet.Active,
            Size = packet.Size,
            FloatDir = packet.FloatDir
        };

    private static InitialSlimeBehaviorSnapshotPacket.Entry ToSnapshotEntry(SloomberYawnPacket packet)
        => new()
        {
            Kind = InitialSlimeBehaviorSnapshotPacket.BehaviorKind.SloomberYawn,
            ActorId = packet.ActorId.Value,
            TargetActorId = 0,
            Revision = packet.Revision,
            Active = packet.Active
        };

    private static long GetTargetActorId(GameObject? target)
    {
        if (!target)
            return 0;

        var networkActor = target!.GetComponentInParent<NetworkActor>();
        return networkActor && networkActor.ActorId.Value > 0 ? networkActor.ActorId.Value : 0;
    }

    private static void AddCurrentStalkState(
        Dictionary<RevisionKey, InitialSlimeBehaviorSnapshotPacket.Entry> states,
        long actorId,
        long targetActorId)
    {
        var key = new RevisionKey(InitialSlimeBehaviorSnapshotPacket.BehaviorKind.StalkTarget, actorId);
        var currentRevision = GetRevision(key.Kind, actorId);
        var hasLatest = LatestStalkTargets.TryGetValue(actorId, out var latest);
        var sameState = hasLatest && latest!.TargetActorId.Value == targetActorId;
        if (!sameState && currentRevision == long.MaxValue)
        {
            SrLogger.LogWarning($"Stalk behavior revision exhausted for actor {actorId}; omitting it from snapshot.");
            return;
        }

        var packet = new StalkConsumableTargetPacket
        {
            SlimeActorId = new(actorId),
            TargetActorId = new(targetActorId),
            Revision = sameState ? currentRevision : currentRevision + 1
        };
        if (packet.Revision == 0)
            packet.Revision = 1;

        Remember(packet);
        states[key] = ToSnapshotEntry(packet);
    }

    private static void AddCurrentTangleState(
        Dictionary<RevisionKey, InitialSlimeBehaviorSnapshotPacket.Entry> states,
        long actorId,
        long targetActorId,
        bool active)
    {
        var key = new RevisionKey(InitialSlimeBehaviorSnapshotPacket.BehaviorKind.TangleGrapple, actorId);
        var currentRevision = GetRevision(key.Kind, actorId);
        var hasLatest = LatestTangleStates.TryGetValue(actorId, out var latest);
        var sameState = hasLatest && latest!.Active == active && latest.TargetActorId.Value == targetActorId;
        if (!sameState && currentRevision == long.MaxValue)
        {
            SrLogger.LogWarning($"Tangle behavior revision exhausted for actor {actorId}; omitting it from snapshot.");
            return;
        }

        var packet = new TangleVineGrapplePacket
        {
            TangleActorId = new(actorId),
            TargetActorId = new(targetActorId),
            Active = active,
            Revision = sameState ? currentRevision : currentRevision + 1
        };
        if (packet.Revision == 0)
            packet.Revision = 1;

        Remember(packet);
        states[key] = ToSnapshotEntry(packet);
    }

    private static void AddCurrentDervishState(
        Dictionary<RevisionKey, InitialSlimeBehaviorSnapshotPacket.Entry> states,
        long actorId,
        bool active,
        byte size,
        Vector3 floatDir)
    {
        var key = new RevisionKey(InitialSlimeBehaviorSnapshotPacket.BehaviorKind.DervishCyclone, actorId);
        var currentRevision = GetRevision(key.Kind, actorId);
        var hasLatest = LatestDervishStates.TryGetValue(actorId, out var latest);
        var sameState = hasLatest && latest!.Active == active && latest.Size == size && latest.FloatDir == floatDir;
        if (!sameState && currentRevision == long.MaxValue)
        {
            SrLogger.LogWarning($"Dervish behavior revision exhausted for actor {actorId}; omitting it from snapshot.");
            return;
        }

        // Do not fill snapshots with default inactive entries for every Dervish.
        if (!hasLatest && !active && currentRevision == 0)
            return;

        var packet = new DervishCyclonePacket
        {
            ActorId = new(actorId),
            Active = active,
            Size = size,
            FloatDir = floatDir,
            Revision = sameState ? currentRevision : currentRevision + 1
        };
        if (packet.Revision == 0)
            packet.Revision = 1;

        if (!packet.IsValid)
        {
            SrLogger.LogWarning($"Dervish behavior state for actor {actorId} is invalid; omitting it from snapshot.");
            return;
        }

        Remember(packet);
        states[key] = ToSnapshotEntry(packet);
    }

    private static ApplyResult TryApply(DervishCyclonePacket packet)
    {
        DervishSlimeSpin? spin;
        try
        {
            if (!ActorManager.Actors.TryGetValue(packet.ActorId.Value, out var model))
                return ApplyResult.Retryable();

            var slimeObject = model.GetGameObject();
            var networkActor = slimeObject ? slimeObject.GetComponent<NetworkActor>() : null;
            if (!networkActor)
                return ApplyResult.Retryable();

            spin = networkActor!.GetComponent<DervishSlimeSpin>();
            if (!spin)
                return ApplyResult.Retryable();

            if (packet.Active && !Enum.IsDefined(typeof(DervishSlimeSpin.CycloneSize), (int)packet.Size))
                return ApplyResult.Retryable("Cyclone size is not currently applicable.");
        }
        catch (Exception ex)
        {
            // These lookups have not invoked a behavior mutation yet.
            return ApplyResult.Retryable($"Pre-apply lookup failed ({ex.GetType().Name}: {ex.Message}).");
        }

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            if (packet.Active)
            {
                spin!._floatDir = packet.FloatDir;
                if (!spin!._cyclone)
                {
                    var cyclone = spin!.SpawnCyclone((DervishSlimeSpin.CycloneSize)packet.Size);
                    if (!cyclone)
                        return ApplyResult.AmbiguousFailure("SpawnCyclone returned no cyclone after invocation.");

                    spin._cyclone = cyclone;
                }

                spin!._mode = DervishSlimeSpin.Mode.CYCLONE;
            }
            else
            {
                var vortexer = spin!._currActorVortexer;
                if (vortexer)
                {
                    vortexer.StartFade();
                    var keepAligned = vortexer.GetComponent<KeepAlignedUnderActor>();
                    if (keepAligned)
                        keepAligned.enabled = false;
                }

                spin!._cyclone = null;
                spin!._mode = DervishSlimeSpin.Mode.NONE;
            }

            return ApplyResult.Applied;
        }
        catch (Exception ex)
        {
            return ApplyResult.AmbiguousFailure($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }
    }

    private static ApplyResult TryApply(SloomberYawnPacket packet)
    {
        var wasHandlingPacket = HandlingPacket;
        var sideEffectStarted = false;
        try
        {
            if (!ActorManager.Actors.TryGetValue(packet.ActorId.Value, out var model))
                return ApplyResult.Retryable();

            var slimeObject = model.GetGameObject();
            var networkActor = slimeObject ? slimeObject.GetComponent<NetworkActor>() : null;
            if (!networkActor)
                return ApplyResult.Retryable();

            var yawn = networkActor!.GetComponent<InitiateYawn>();
            if (!yawn)
                return ApplyResult.Retryable();

            var reactables = yawn!._yawnComponentReactables;
            if (reactables == null)
                return ApplyResult.Retryable();

            if (AppliedSloomberStates.TryGetValue(packet.ActorId.Value, out var appliedState) &&
                appliedState == packet.Active)
                return ApplyResult.Applied;

            // The owner already ran the native behavior locally; a resync must not
            // restart its animation or reactable effects.
            if (networkActor!.LocallyOwned && !HandlingPacket && !packet.ForceClear)
            {
                AppliedSloomberStates[packet.ActorId.Value] = packet.Active;
                return ApplyResult.Applied;
            }

            HandlingPacket = true;
            sideEffectStarted = true;
            if (packet.Active)
            {
                foreach (var reactable in reactables)
                {
                    if (reactable == null)
                        continue;

                    reactable.BeginYawn();
                }
            }
            else
            {
                if (!packet.ForceClear)
                    yawn!.DoYawn();

                foreach (var reactable in reactables)
                {
                    if (reactable == null)
                        continue;

                    reactable.EndYawn();
                }
            }

            AppliedSloomberStates[packet.ActorId.Value] = packet.Active;
            return ApplyResult.Applied;
        }
        catch (Exception ex)
        {
            var detail = $"{ex.GetType().Name}: {ex.Message}";
            if (sideEffectStarted)
            {
                // A reactable may already have changed before another one threw.
                return ApplyResult.AmbiguousFailure(detail);
            }

            // Object/component access has not invoked BeginYawn, DoYawn, or EndYawn.
            return ApplyResult.Retryable($"Pre-apply lookup failed ({detail}).");
        }
        finally
        {
            if (sideEffectStarted)
                HandlingPacket = wasHandlingPacket;
        }
    }

    private static ApplyResult TryApply(StalkConsumableTargetPacket packet)
    {
        StalkConsumable? stalkConsumable;
        GameObject? targetObject = null;
        try
        {
            if (!ActorManager.Actors.TryGetValue(packet.SlimeActorId.Value, out var slimeModel))
                return ApplyResult.Retryable();

            var slimeObject = slimeModel.GetGameObject();
            stalkConsumable = slimeObject ? slimeObject.GetComponent<StalkConsumable>() : null;
            if (!stalkConsumable)
                return ApplyResult.Retryable();

            if (packet.TargetActorId.Value != 0)
            {
                if (!ActorManager.Actors.TryGetValue(packet.TargetActorId.Value, out var targetModel))
                    return ApplyResult.Retryable();

                targetObject = targetModel.GetGameObject();
                if (!targetObject)
                    return ApplyResult.Retryable();
            }
        }
        catch (Exception ex)
        {
            return ApplyResult.Retryable($"Pre-apply lookup failed ({ex.GetType().Name}: {ex.Message}).");
        }

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            stalkConsumable!.SetTarget(targetObject);
            return ApplyResult.Applied;
        }
        catch (Exception ex)
        {
            // SetTarget may have changed its target before an event/callback threw.
            return ApplyResult.AmbiguousFailure($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }
    }

    private static ApplyResult TryApply(TangleVineGrapplePacket packet)
    {
        GroundVine? groundVine;
        GameObject? targetObject = null;
        try
        {
            if (!ActorManager.Actors.TryGetValue(packet.TangleActorId.Value, out var slimeModel))
                return ApplyResult.Retryable();

            var slimeObject = slimeModel.GetGameObject();
            groundVine = slimeObject ? slimeObject.GetComponent<GroundVine>() : null;
            if (!groundVine)
                return ApplyResult.Retryable();

            // GroundVine.Start populates this prefab. A packet can arrive between
            // actor instantiation and that initialization, so retry on the main loop.
            if (packet.Active && !groundVine!._vinePrefab)
                return ApplyResult.Retryable();

            if (packet.Active)
            {
                if (packet.TargetActorId.Value == 0 ||
                    !ActorManager.Actors.TryGetValue(packet.TargetActorId.Value, out var targetModel))
                    return ApplyResult.Retryable();

                targetObject = targetModel.GetGameObject();
                if (!targetObject)
                    return ApplyResult.Retryable();

                if (groundVine!._target == targetObject)
                    return ApplyResult.Applied;

                if (GroundVine.IsGrabbed(targetObject))
                    return ApplyResult.Retryable();
            }
        }
        catch (Exception ex)
        {
            return ApplyResult.Retryable($"Pre-apply lookup failed ({ex.GetType().Name}: {ex.Message}).");
        }

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            if (!packet.Active)
            {
                groundVine!.Release();
                return ApplyResult.Applied;
            }

            // A false result is the API's explicit refusal; an exception below is
            // treated as ambiguous because MaybeGrapple may have partially mutated.
            if (!groundVine!.MaybeGrapple(targetObject))
                return ApplyResult.Retryable();

            return ApplyResult.Applied;
        }
        catch (Exception ex)
        {
            return ApplyResult.AmbiguousFailure($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }
    }
}

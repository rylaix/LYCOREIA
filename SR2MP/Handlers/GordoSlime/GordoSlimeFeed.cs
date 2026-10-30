using System.Net;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.GordoSlime;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.GordoSlime;

[PacketHandler((byte)PacketType.GordoSlimeFeed)]
internal sealed class GordoSlimeFeedHandler : BasePacketHandler<GordoSlimeFeedPacket>
{
    protected override bool Handle(GordoSlimeFeedPacket packet, IPEndPoint? clientEp)
    {
        if (!RequireAuthenticatedClient(clientEp))
            return false;

        if (string.IsNullOrWhiteSpace(packet.ID) || string.IsNullOrWhiteSpace(packet.RequestId) ||
            (IsServerSide && packet.FoodActorId == 0) ||
            packet.NewFoodCount < 0 || packet.RequiredFoodCount <= 0)
            return false;

        if (IsServerSide)
        {
            if (clientEp == null || !Main.Server.ClientManager.TryGetClient(clientEp, out var sender) ||
                sender == null || string.IsNullOrWhiteSpace(sender.PlayerId) ||
                packet.FoodActorId == 0 ||
                !GameState.gordos.TryGetValue(packet.ID, out var authoritativeGordo) ||
                authoritativeGordo == null || !authoritativeGordo.identifiableType)
                return false;

            var target = authoritativeGordo.targetCount;
            var current = Math.Max(0, authoritativeGordo.GordoEatenCount);
            var canonicalTypeId = NetworkActorManager.GetPersistentID(authoritativeGordo.identifiableType);
            var actorAlreadyAccepted = NetworkGordoSlimeManager.TryGetAcceptedFeedActorCount(
                packet.ID, packet.FoodActorId, out var actorAcceptedCount);
            var requestAlreadyAccepted = NetworkGordoSlimeManager.HasAcceptedFeedRequest(packet.ID, packet.RequestId);
            var decision = packet.GordoSlimeType == canonicalTypeId
                ? GordoSlimeAuthorityPolicy.DecideFeed(
                    packet.ID,
                    packet.RequestId,
                    packet.FoodActorId,
                    current,
                    target,
                    packet.RequiredFoodCount,
                    packet.NewFoodCount,
                    NetworkGordoSlimeManager.HasPopped(packet.ID),
                    actorAlreadyAccepted,
                    actorAcceptedCount,
                    requestAlreadyAccepted)
                : GordoFeedDecision.Reject;

            if (decision == GordoFeedDecision.DuplicateSnapshot)
            {
                var hostAlreadyCoversSubmittedCount =
                    GordoSlimeAuthorityPolicy.HostCountCoversSubmittedCount(current, packet.NewFoodCount);
                if (actorAlreadyAccepted && !hostAlreadyCoversSubmittedCount)
                {
                    SrLogger.LogDebug(
                        $"Gordo feed actor {packet.FoodActorId} for {packet.ID} was already accepted at count " +
                        $"{actorAcceptedCount}; guest submitted {packet.NewFoodCount}, so returning host snapshot without incrementing.");
                }

                var canonicalCount = GordoSlimeAuthorityPolicy.CanonicalCountForDuplicateRequest(
                    current, actorAcceptedCount);
                authoritativeGordo.GordoEatenCount = Math.Min(target, canonicalCount);
                packet.NewFoodCount = authoritativeGordo.GordoEatenCount;
                packet.RequiredFoodCount = target;
                packet.GordoSlimeType = canonicalTypeId;
                if (!requestAlreadyAccepted)
                    NetworkGordoSlimeManager.TryAcceptFeedRequest(packet.ID, packet.RequestId);

                // A duplicate actor/request only needs a correction for its sender;
                // do not relay another feed event to the other clients.
                Main.Server.SendToClient(packet, clientEp);
                return false;
            }

            if (decision != GordoFeedDecision.CommitOne ||
                !NetworkGordoSlimeManager.TryAcceptFeedRequest(packet.ID, packet.RequestId))
            {
                SrLogger.LogDebug($"Rejected Gordo feed request for {packet.ID} from {sender.PlayerId}.");
                return false;
            }

            // The host advances exactly one canonical feed event. The guest's
            // absolute counter is only a consistency hint and cannot jump state.
            var nextCount = Math.Min(target, current + 1);
            if (!NetworkGordoSlimeManager.TryAcceptFeedActor(
                    packet.ID, packet.FoodActorId, nextCount, out _))
            {
                var latestCount = NetworkGordoSlimeManager.TryGetAcceptedFeedActorCount(
                    packet.ID, packet.FoodActorId, out var acceptedCount)
                    ? GordoSlimeAuthorityPolicy.CanonicalCountForDuplicateRequest(current, acceptedCount)
                    : current;
                authoritativeGordo.GordoEatenCount = Math.Min(target, latestCount);
                packet.NewFoodCount = authoritativeGordo.GordoEatenCount;
                packet.RequiredFoodCount = target;
                packet.GordoSlimeType = canonicalTypeId;
                Main.Server.SendToClient(packet, clientEp);
                return false;
            }

            authoritativeGordo.GordoEatenCount = nextCount;
            packet.NewFoodCount = authoritativeGordo.GordoEatenCount;
            packet.RequiredFoodCount = target;
            packet.GordoSlimeType = canonicalTypeId;

            // BasePacketHandler relays this canonical state to other clients. Echo
            // it to the requester to correct its local native counter.
            Main.Server.SendToClient(packet, clientEp);
            return true;
        }

        if (!ActorManager.ActorTypes.TryGetValue(packet.GordoSlimeType, out var gordoType) || !gordoType)
        {
            SrLogger.LogWarning($"GordoSlimeFeed: unknown Gordo type {packet.GordoSlimeType} for {packet.ID}; ignoring state.");
            return false;
        }

        if (!GameState.gordos.TryGetValue(packet.ID, out var gordoSlime))
        {
            gordoSlime = new GordoModel
            {
                fashions = new CppCollections.List<IdentifiableType>(0),
                gordoEatCount = NetworkGordoSlimeManager.HasPopped(packet.ID)
                    ? packet.RequiredFoodCount
                    : Math.Min(packet.NewFoodCount, packet.RequiredFoodCount),
                gordoSeen = false,
                gameObj = null,
                targetCount = packet.RequiredFoodCount,
                identifiableType = gordoType
            };

            GameState.gordos.Add(packet.ID, gordoSlime);
        }
        else if (gordoSlime.targetCount != packet.RequiredFoodCount)
        {
            // This is a host echo, so repair a stale client target from the
            // canonical value without allowing an incoming guest request to set it.
            gordoSlime.targetCount = packet.RequiredFoodCount;
        }

        if (!gordoSlime.identifiableType)
            gordoSlime.identifiableType = gordoType;
        // This packet is a host snapshot, not a client delta. Replace local state
        // exactly so a speculative duplicate native DoEat cannot keep the guest
        // ahead of the host's canonical counter.
        gordoSlime.GordoEatenCount = NetworkGordoSlimeManager.HasPopped(packet.ID)
            ? gordoSlime.targetCount
            : Math.Min(packet.NewFoodCount, gordoSlime.targetCount);

        return true;
    }
}

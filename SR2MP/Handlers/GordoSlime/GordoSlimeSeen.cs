using System.Net;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.GordoSlime;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.GordoSlime;

[PacketHandler((byte)PacketType.GordoSlimeSeen)]
internal sealed class GordoSlimeSeenHandler : BasePacketHandler<GordoSlimeSeenPacket>
{
    protected override bool Handle(GordoSlimeSeenPacket packet, IPEndPoint? clientEp)
    {
        if (!RequireAuthenticatedClient(clientEp))
            return false;

        if (string.IsNullOrWhiteSpace(packet.ID) || packet.EatenCount < 0 || packet.RequiredFoodCount <= 0)
            return false;

        if (!ActorManager.ActorTypes.TryGetValue(packet.GordoSlimeType, out var gordoType) || !gordoType)
        {
            SrLogger.LogWarning($"GordoSlimeSeen: unknown Gordo type {packet.GordoSlimeType} for {packet.ID}; ignoring state.");
            return false;
        }

        if (IsServerSide)
        {
            if (!GameState.gordos.TryGetValue(packet.ID, out var authoritativeModel) ||
                authoritativeModel == null || !authoritativeModel.identifiableType)
            {
                SrLogger.LogWarning($"GordoSlimeSeen: rejected unknown Gordo ID {packet.ID} from a client.");
                return false;
            }

            var canonicalTypeId = NetworkActorManager.GetPersistentID(authoritativeModel.identifiableType);
            if (!GordoSlimeAuthorityPolicy.CanAcceptSeen(
                    authenticated: true,
                    knownGordo: true,
                    typeMatches: packet.GordoSlimeType == canonicalTypeId,
                    targetMatches: packet.RequiredFoodCount == authoritativeModel.targetCount))
            {
                SrLogger.LogWarning($"GordoSlimeSeen: rejected noncanonical Gordo state for {packet.ID}.");
                return false;
            }

            authoritativeModel.gordoSeen = true;
            packet.EatenCount = Math.Max(0, authoritativeModel.GordoEatenCount);
            packet.RequiredFoodCount = authoritativeModel.targetCount;
            packet.GordoSlimeType = canonicalTypeId;
            return true;
        }

        if (!GameState.gordos.TryGetValue(packet.ID, out var gordoSlimeModel))
        {
            gordoSlimeModel = new GordoModel
            {
                fashions = new CppCollections.List<IdentifiableType>(0),
                gordoSeen = true,
                gameObj = null,
                gordoEatCount = packet.EatenCount,
                targetCount = packet.RequiredFoodCount,
                identifiableType = gordoType
            };

            GameState.gordos.Add(packet.ID, gordoSlimeModel);
            return true;
        }
        
        gordoSlimeModel.gordoSeen = true;
        if (gordoSlimeModel.targetCount <= 0)
            gordoSlimeModel.targetCount = packet.RequiredFoodCount;
        if (!gordoSlimeModel.identifiableType)
            gordoSlimeModel.identifiableType = gordoType;

        return true;
    }
}

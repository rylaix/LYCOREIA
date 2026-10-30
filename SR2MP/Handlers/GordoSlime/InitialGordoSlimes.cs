using System.Net;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Loading;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.GordoSlime;

[PacketHandler((byte)PacketType.InitialGordoSlimes, HandlerType.Client)]
internal sealed class InitialGordoSlimeLoadHandler : BasePacketHandler<InitialGordoSlimesPacket>
{
    protected override bool Handle(InitialGordoSlimesPacket packet, IPEndPoint? _)
    {
        var previouslyPopped = NetworkGordoSlimeManager.ResetSnapshotState();

        foreach (var gordoSlime in packet.GordoSlimes)
        {
            var wasPreviouslyPopped = previouslyPopped.Contains(gordoSlime.Id);

            if (GameState.gordos.TryGetValue(gordoSlime.Id, out var gordoModel))
            {
                wasPreviouslyPopped |= gordoModel.HasPopped();
                gordoModel.GordoEatenCount = gordoSlime.EatenCount;
                gordoModel.targetCount = gordoSlime.RequiredEatCount;
                if (!gordoModel.identifiableType &&
                    ActorManager.ActorTypes.TryGetValue(gordoSlime.GordoSlimeType, out var resolvedType) && resolvedType)
                    gordoModel.identifiableType = resolvedType;
            }
            else
            {
                if (!ActorManager.ActorTypes.TryGetValue(gordoSlime.GordoSlimeType, out var gordoType) || !gordoType)
                {
                    SrLogger.LogWarning($"InitialGordoSlimes: unknown Gordo type {gordoSlime.GordoSlimeType} for {gordoSlime.Id}; skipping entry.");
                    continue;
                }

                gordoModel = new GordoModel
                {
                    fashions = new CppCollections.List<IdentifiableType>(0),
                    gordoEatCount = gordoSlime.EatenCount,
                    gordoSeen = false,
                    identifiableType = gordoType,
                    gameObj = null,
                    targetCount = gordoSlime.RequiredEatCount
                };

                GameState.gordos.Add(gordoSlime.Id, gordoModel);
            }

            if (gordoSlime.Popped)
            {
                NetworkGordoSlimeManager.MarkPopped(gordoSlime.Id);
                NetworkGordoSlimeManager.MarkRewarded(gordoSlime.Id);
            }
            
            gordoModel.gordoSeen = gordoSlime.WasSeen && !gordoSlime.Popped;

            NetworkGordoSlimeManager.ApplySnapshotState(gordoSlime.Id, gordoModel, wasPreviouslyPopped);

            if (gordoSlime.Popped)
            {
                foreach (var reward in gordoSlime.ActiveRewards)
                {
                    if (reward != null && string.Equals(reward.GordoSlimeId, gordoSlime.Id, StringComparison.Ordinal))
                        GordoSlimeRewardHandler.ApplySnapshotReward(reward);
                }
            }
        }

        return false;
    }
}

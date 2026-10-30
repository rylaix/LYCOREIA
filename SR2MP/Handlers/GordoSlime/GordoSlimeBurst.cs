using System.Net;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.GordoSlime;
using SR2MP.Packets.Utils;
using SR2MP.Server.Models;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.GordoSlime;

[PacketHandler((byte)PacketType.GordoSlimeBurst)]
internal sealed class GordoSlimeBurstHandler : BasePacketHandler<GordoSlimeBurstPacket>
{
    protected override bool Handle(GordoSlimeBurstPacket packet, IPEndPoint? clientEp)
    {
        if (!RequireAuthenticatedClient(clientEp))
            return false;

        if (string.IsNullOrWhiteSpace(packet.ID) || packet.EatenCount < 0 || packet.RequiredFoodCount <= 0)
            return false;

        var hasPacketGordoType = ActorManager.ActorTypes.TryGetValue(packet.GordoSlimeType, out var packetGordoType)
                                 && packetGordoType;
        GameState.gordos.TryGetValue(packet.ID, out var gordoSlime);
        var inactiveLoadedGordo = gordoSlime != null && gordoSlime.gameObj &&
                                  !gordoSlime.gameObj!.activeSelf;
        var wasAlreadyPopped = NetworkGordoSlimeManager.HasPopped(packet.ID) || inactiveLoadedGordo;
        if (gordoSlime == null && !hasPacketGordoType)
        {
            SrLogger.LogWarning($"GordoSlimeBurst: unknown Gordo type {packet.GordoSlimeType} for {packet.ID}; rejecting state.");
            return false;
        }

        if (IsServerSide)
        {
            if (clientEp == null || !Main.Server.ClientManager.TryGetClient(clientEp, out var sender) ||
                sender == null || string.IsNullOrWhiteSpace(sender.PlayerId) ||
                gordoSlime == null || !gordoSlime.identifiableType)
                return false;

            var target = gordoSlime.targetCount;
            var current = Math.Max(0, gordoSlime.GordoEatenCount);
            var canonicalTypeId = NetworkActorManager.GetPersistentID(gordoSlime.identifiableType);
            if (packet.GordoSlimeType != canonicalTypeId ||
                !GordoSlimeAuthorityPolicy.CanAcceptBurst(
                    packet.ID,
                    current,
                    target,
                    packet.EatenCount,
                    packet.RequiredFoodCount,
                    wasAlreadyPopped))
            {
                SrLogger.LogMessage($"GordoSlimeBurst: rejected unconfirmed or duplicate burst for {packet.ID}.");
                return false;
            }

            var hostPlayerId = Main.Server.PlayerId;
            // The host owns payout execution so an accepted burst cannot lose its
            // rewards when the player who triggered it disconnects.
            if (!NetworkGordoSlimeManager.TryAcceptBurst(packet.ID, hostPlayerId))
                return false;

            packet.RewardAuthorityId = hostPlayerId;
            packet.EatenCount = current;
            packet.RequiredFoodCount = target;
            packet.GordoSlimeType = canonicalTypeId;
            Main.Server.SendToClient(packet, clientEp);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(packet.RewardAuthorityId) ||
                !NetworkGordoSlimeManager.TrySetRewardAuthority(packet.ID, packet.RewardAuthorityId))
                return false;

            NetworkGordoSlimeManager.MarkPopped(packet.ID);
        }

        if (gordoSlime == null)
        {
            gordoSlime = new GordoModel
            {
                fashions = new CppCollections.List<IdentifiableType>(0),
                gordoSeen = false,
                gameObj = null,
                gordoEatCount = Math.Max(packet.EatenCount, packet.RequiredFoodCount),
                targetCount = packet.RequiredFoodCount,
                identifiableType = packetGordoType
            };
            GameState.gordos.Add(packet.ID, gordoSlime);
        }
        else if (!IsServerSide)
        {
            gordoSlime.targetCount = packet.RequiredFoodCount;
        }

        var localPlayerId = Main.Server.IsRunning ? Main.Server.PlayerId : Main.Client.PlayerId;
        var isRewardAuthority = NetworkGordoSlimeManager.IsRewardAuthority(packet.ID, localPlayerId);
        if (!isRewardAuthority)
            NetworkGordoSlimeManager.MarkRewarded(packet.ID);

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            gordoSlime.GordoEatenCount = Math.Max(gordoSlime.GordoEatenCount,
                Math.Max(packet.EatenCount, gordoSlime.targetCount));
            gordoSlime.gordoSeen = false;

            if (isRewardAuthority)
            {
                // A client may burst a Gordo outside the host's loaded area. Defer
                // native generation until a real GordoRewardsBase component starts;
                // never call into an asset prefab with an uninitialized _eat field.
                NetworkGordoSlimeManager.MarkRewardPending(packet.ID);
                if (gordoSlime.gameObj)
                {
                    var eat = gordoSlime.gameObj!.GetComponent<GordoEat>();
                    var rewards = gordoSlime.gameObj!.GetComponent<GordoRewardsBase>();
                    if (eat && !wasAlreadyPopped)
                        eat.ImmediateReachedTarget();

                    if (rewards && eat)
                        rewards.GiveRewards();
                    else
                        SrLogger.LogWarning($"GordoSlimeBurst: host has no initialized reward component for {packet.ID}; reward generation is pending.");
                }
                else
                {
                    SrLogger.LogMessage($"GordoSlimeBurst: host Gordo {packet.ID} is not loaded; reward generation is pending.");
                }
            }
            else if (!wasAlreadyPopped && gordoSlime.gameObj)
            {
                gordoSlime.gameObj!.GetComponent<GordoEat>()?.ImmediateReachedTarget();
            }
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"GordoSlimeBurst: visual/reward application failed for Gordo Slime {packet.ID}: {ex.Message}");
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        return true;
    }
}

using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.GordoSlime;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.GordoSlime;

[PacketHandler((byte)PacketType.GordoSlimeReward)]
internal sealed class GordoSlimeRewardHandler : BasePacketHandler<GordoSlimeRewardPacket>
{
    protected override bool Handle(GordoSlimeRewardPacket packet, IPEndPoint? clientEp)
    {
        if (!RequireAuthenticatedClient(clientEp))
            return false;

        if (string.IsNullOrWhiteSpace(packet.GordoSlimeId) || packet.PrefabIndex < 0 || packet.RewardOrdinal < 0)
            return false;

        if (IsServerSide)
        {
            if (clientEp == null || !Main.Server.ClientManager.TryGetClient(clientEp, out var sender) ||
                sender == null || !NetworkGordoSlimeManager.IsRewardAuthority(packet.GordoSlimeId, sender.PlayerId))
                return false;
        }

        return SpawnReward(packet, fromSnapshot: false);
    }

    internal static bool ApplySnapshotReward(GordoSlimeRewardPacket packet)
        => SpawnReward(packet, fromSnapshot: true);

    private static bool SpawnReward(GordoSlimeRewardPacket packet, bool fromSnapshot)
    {
        if (string.IsNullOrWhiteSpace(packet.GordoSlimeId) || packet.PrefabIndex < 0 || packet.RewardOrdinal < 0)
            return false;

        if (!GameState.gordos.TryGetValue(packet.GordoSlimeId, out var gordoSlimeModel))
        {
            SrLogger.LogWarning($"GordoSlimeReward: no model for Gordo {packet.GordoSlimeId}; cannot resolve reward prefab.");
            return false;
        }

        var gordoObject = gordoSlimeModel.gameObj;
        if (!gordoObject && gordoSlimeModel.identifiableType)
            gordoObject = gordoSlimeModel.identifiableType.prefab;

        var rewardsBase = gordoObject ? gordoObject.GetComponent<GordoRewardsBase>() : null;
        var rewards = rewardsBase ? rewardsBase.TryCast<GordoRewards>() : null;
        var rewardPrefabs = rewards?.RewardPrefabs;
        if (rewardPrefabs == null || packet.PrefabIndex >= rewardPrefabs.Length)
            return false;

        var prefab = rewardPrefabs[packet.PrefabIndex];
        if (!prefab)
            return false;

        BreakOnImpact? existingReward = null;
        if (fromSnapshot)
        {
            var decision = NetworkGordoSlimeManager.PrepareSnapshotReward(
                packet.GordoSlimeId, packet.PrefabIndex, packet.RewardOrdinal, out existingReward);
            if (decision == SnapshotRewardRestoreDecision.Skip)
                return false;

            if ((decision == SnapshotRewardRestoreDecision.UpdateExisting ||
                 decision == SnapshotRewardRestoreDecision.Reactivate) && existingReward)
            {
                existingReward.transform.SetPositionAndRotation(packet.Position, packet.Rotation);
                if (decision == SnapshotRewardRestoreDecision.Reactivate)
                    existingReward.gameObject.SetActive(true);
                NetworkGordoSlimeManager.RegisterRewardObject(
                    packet.GordoSlimeId, packet.PrefabIndex, packet.RewardOrdinal, existingReward);
                return true;
            }
        }
        else if (!NetworkGordoSlimeManager.TryMarkRewardSpawned(
                     packet.GordoSlimeId, packet.PrefabIndex, packet.RewardOrdinal))
        {
            return false;
        }

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            var instance = Object.Instantiate(prefab);
            var breakable = instance.GetComponent<BreakOnImpact>();
            if (!breakable)
            {
                Object.Destroy(instance);
                NetworkGordoSlimeManager.ForgetRewardSpawned(
                    packet.GordoSlimeId, packet.PrefabIndex, packet.RewardOrdinal);
                SrLogger.LogWarning(
                    $"GordoSlimeRewardHandler: reward prefab {packet.PrefabIndex} for {packet.GordoSlimeId} " +
                    "has no BreakOnImpact component; cannot track it for join snapshots.");
                return false;
            }

            instance.transform.SetPositionAndRotation(packet.Position, packet.Rotation);
            NetworkGordoSlimeManager.RegisterRewardObject(
                packet.GordoSlimeId, packet.PrefabIndex, packet.RewardOrdinal, breakable);
        }
        catch (Exception ex)
        {
            NetworkGordoSlimeManager.ForgetRewardSpawned(
                packet.GordoSlimeId, packet.PrefabIndex, packet.RewardOrdinal);
            SrLogger.LogWarning($"GordoSlimeRewardHandler: failed to spawn reward for Gordo Slime {packet.GordoSlimeId}: {ex.Message}");
            return false;
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        return true;
    }
}

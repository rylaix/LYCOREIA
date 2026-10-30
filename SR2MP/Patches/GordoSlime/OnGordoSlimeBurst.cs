using HarmonyLib;
using SR2MP.Packets.GordoSlime;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.GordoSlime;

[HarmonyPatch(typeof(GordoEat), nameof(GordoEat.ImmediateReachedTarget))]
internal static class OnGordoSlimeBurst
{
    public static bool Prefix(GordoEat __instance)
    {
        if (HandlingPacket) return true;
        if (!Main.Server.IsRunning && !Main.Client.IsConnected) return true;

        var gordoId = __instance.Id;
        if (string.IsNullOrWhiteSpace(gordoId))
            return true;

        // The host accepts one burst per Gordo. Clients still run the local burst
        // visual, while GiveRewards stays suppressed until the host commits an owner.
        if (!NetworkGordoSlimeManager.TryMarkPopped(gordoId))
            return false;

        var rewardAuthorityId = Main.Server.IsRunning ? Main.Server.PlayerId : string.Empty;
        if (!string.IsNullOrEmpty(rewardAuthorityId) &&
            !NetworkGordoSlimeManager.TrySetRewardAuthority(gordoId, rewardAuthorityId))
            return false;

        if (!string.IsNullOrEmpty(rewardAuthorityId))
            NetworkGordoSlimeManager.MarkRewardPending(gordoId);

        var packet = new GordoSlimeBurstPacket
        {
            ID = gordoId,
            EatenCount = Math.Max(0, __instance.GordoModel.GordoEatenCount),
            RequiredFoodCount = __instance.GordoModel.targetCount,
            GordoSlimeType = NetworkActorManager.GetPersistentID(__instance.GordoModel.identifiableType),
            RewardAuthorityId = rewardAuthorityId
        };
        Main.SendToAllOrServer(packet);
        return true;
    }
}

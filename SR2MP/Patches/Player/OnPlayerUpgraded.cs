using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Player;

[HarmonyPatch(typeof(UpgradeModel), nameof(UpgradeModel.IncrementUpgradeLevel))]
internal static class OnPlayerUpgraded
{
    public static void Postfix(UpgradeDefinition definition)
    {
        if (HandlingPacket || NetworkUpgradeManager.ExecutingHostPurchase || !Main.Server.IsRunning)
            return;

        var level = SceneContext.Instance.PlayerState._model.upgradeModel.GetUpgradeLevel(definition);
        NetworkUpgradeManager.BroadcastHostUpgrade((byte)definition._uniqueId, (sbyte)level);
    }
}

using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Labyrinth;

[HarmonyPatch(typeof(PrismaDisruptionModel), nameof(PrismaDisruptionModel.NotifyParticipants))]
internal static class PrismaDisruptionNotifyParticipantsPatch
{
    public static void Postfix(PrismaDisruptionModel __instance)
        => NetworkPrismaDisruptionManager.Broadcast(__instance);
}

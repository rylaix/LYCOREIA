using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Player.PlayerItems;
using SR2MP.Packets.FX;

namespace SR2MP.Patches.FX;

[HarmonyPatch(typeof(VacuumItem), "VacTriggered")]
internal static class OnVacuumSuctionStarted
{
    public static void Postfix(VacuumItem __instance)
    {
        if (__instance && __instance._vacMode == VacuumItem.VacMode.VAC)
            Publish(PlayerFXPacket.PlayerFXType.VacRunningStart);
    }

    internal static void Publish(PlayerFXPacket.PlayerFXType type)
    {
        if (!Main.Server.IsRunning && !Main.Client.IsConnected)
            return;

        Main.SendToAllOrServer(new PlayerFXPacket { FX = type, Player = LocalID });
    }
}

[HarmonyPatch(typeof(VacuumItem), "VacCanceled")]
internal static class OnVacuumSuctionEnded
{
    public static void Prefix(VacuumItem __instance, out bool __state)
        => __state = __instance && __instance._vacMode == VacuumItem.VacMode.VAC;

    public static void Postfix(VacuumItem __instance, bool __state)
    {
        if (__state && (!__instance || __instance._vacMode != VacuumItem.VacMode.VAC))
            OnVacuumSuctionStarted.Publish(PlayerFXPacket.PlayerFXType.VacRunningEnd);
    }
}

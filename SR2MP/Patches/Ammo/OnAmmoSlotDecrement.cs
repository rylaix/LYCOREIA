using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Player;
using SR2MP.Packets.Ammo;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Ammo;

[HarmonyPatch(typeof(AmmoSlot), nameof(AmmoSlot.DecrementAmmo))]
internal static class OnAmmoSlotDecrement
{
    public static void Postfix(AmmoSlot __instance)
    {
#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_AMMO") == "1")
            SrLogger.LogMessage($"[SR2MP_TEST] AmmoSlot.DecrementAmmo plot={__instance.GetPlotID()} slot={__instance.GetSlotIndex()} handling={HandlingPacket}");
#endif

        if ((!Main.Client.IsConnected && !Main.Server.IsRunning) || HandlingPacket) return;

        if (__instance._count <= 0) __instance._id = null;

        var index = __instance.GetSlotIndex();
        if (index == null) return;

        var packet = new AmmoDecrementPacket()
        {
            SlotIndex = (int)index,
            Count = 1,
            ID = __instance.GetPlotID()!
        };

        if (packet.ID == null) return;

        Main.SendToAllOrServer(packet);
    }
}

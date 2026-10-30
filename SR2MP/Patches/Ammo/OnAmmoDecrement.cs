using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Player;
using SR2MP.Packets.Ammo;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Ammo;

[HarmonyPatch(typeof(AmmoSlotManager), nameof(AmmoSlotManager.Decrement), typeof(int), typeof(int))]
internal static class OnAmmoDecrement
{
    public static void Postfix(AmmoSlotManager __instance, int index, int count)
    {
#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_AMMO") == "1")
            SrLogger.LogMessage($"[SR2MP_TEST] AmmoSlotManager.Decrement index={index} count={count} handling={HandlingPacket}");
#endif

        if ((!Main.Client.IsConnected && !Main.Server.IsRunning) || HandlingPacket) return;

        if (__instance.Slots[index]!._count <= 0) __instance.Slots[index]!._id = null;

        var packet = new AmmoDecrementPacket()
        {
            SlotIndex = index,
            Count = count,
            ID = __instance.GetPlotID()
        };

        if (packet.ID == null) return;

        Main.SendToAllOrServer(packet);
    }
}

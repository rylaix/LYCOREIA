using HarmonyLib;
using SR2MP.Packets.LandPlots;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.LandPlots;

[HarmonyPatch(typeof(LandPlot), nameof(LandPlot.DestroyAttached))]
internal static class OnDestroyCrop
{
    public static void Postfix(LandPlot __instance)
    {
        if (HandlingPacket) return;

        if (!Main.Server.IsRunning && !Main.Client.IsConnected) return;

        if (!__instance)
            return;

        var plotLocation = __instance.GetComponentInParent<LandPlotLocation>();
        if (!plotLocation || string.IsNullOrWhiteSpace(plotLocation._id))
            return;

        var plotId = plotLocation._id;
        var packet = new GardenPlantPacket
        {
            ID = plotId,
            ActorType = 9
        };

        if (!NetworkGardenManager.PrepareLocalCropUpdate(packet))
            return;

        Main.SendToAllOrServer(packet);
    }
}

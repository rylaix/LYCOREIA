using HarmonyLib;
using SR2MP.Packets.TreasurePod;

using SR2MP.Shared.Managers;

namespace SR2MP.Patches.TreasurePod;

[HarmonyPatch(typeof(Il2Cpp.TreasurePod), nameof(Il2Cpp.TreasurePod.Activate))]
internal static class OnTreasurePodOpen
{
    public static bool Prefix(Il2Cpp.TreasurePod __instance)
    {
        if (!Main.Client.IsConnected || Main.Server.IsRunning)
            return true;

        if (!NetworkTreasurePodManager.TryGetPodId(__instance, out var podId))
        {
            SrLogger.LogWarning($"Could not read treasure pod ID '{__instance._id}'.");
            return true;
        }

        if (NetworkTreasurePodManager.IsClientActivationGranted(podId))
            return true;

        // A guest never pays out natively except for a host-delegated activation.
        if (HandlingPacket)
            return false;

        // Guests wait for the host to execute Activate(), so native reward
        // generation cannot race or run once per client.
        NetworkTreasurePodManager.RequestHostOpen(podId);
        return false;
    }

    public static void Postfix(Il2Cpp.TreasurePod __instance)
    {
        if (HandlingPacket || !Main.Server.IsRunning)
            return;

        if (!NetworkTreasurePodManager.TryGetPodId(__instance, out var podId))
        {
            SrLogger.LogWarning($"Could not read treasure pod ID '{__instance._id}'.");
            return;
        }

        // OpenOnHost explicitly publishes its commit after the complete native
        // activation. Do not publish an early duplicate from this postfix.
        if (NetworkTreasurePodManager.IsHostActivationInProgress(podId))
            return;

        NetworkTreasurePodManager.RecordHostOpen(podId);
    }
}

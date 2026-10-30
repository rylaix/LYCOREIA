using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Refinery;

[HarmonyPatch(typeof(GadgetsModel), nameof(GadgetsModel.SetCount))]
internal static class RefineryUpdate
{
    public static void Prefix(GadgetsModel __instance, IdentifiableType type, out int __state)
        => __state = __instance.GetCount(type);

    public static void Postfix(GadgetsModel __instance, IdentifiableType type, int __state)
    {
        if (!Main.Server.IsRunning && !Main.Client.IsConnected)
            return;

        if (HandlingPacket)
        {
            try
            {
                NetworkRefineryManager.RecordSyncedCount(NetworkActorManager.GetPersistentID(type), __instance.GetCount(type));
            }
            catch
            {
                // Unknown types are never published; reconcile skips them too.
            }
            return;
        }

        if (SystemContext.Instance.SceneLoader.IsSceneLoadInProgress)
            return;

        var currentCount = __instance.GetCount(type);
        var delta = (long)currentCount - __state;
        if (delta == 0)
            return;

        int itemId;
        try
        {
            itemId = NetworkActorManager.GetPersistentID(type);
        }
        catch (Exception exception)
        {
            SrLogger.LogWarning($"Could not identify refinery item '{type.name}' for network sync: {exception.Message}");
            return;
        }

        NetworkRefineryManager.PublishLocalChange(itemId, delta, currentCount);
    }
}

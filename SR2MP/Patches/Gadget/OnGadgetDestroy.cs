using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Packets.Actor;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Gadget;

[HarmonyPatch(typeof(Destroyer), nameof(Destroyer.DestroyGadget))]
internal static class OnGadgetDestroy
{
    private static System.Collections.IEnumerator SendDestroyAfterLoad(ActorId actorId)
    {
        var waited = 0f;
        while (SystemContext.Instance.SceneLoader.IsSceneLoadInProgress)
        {
            waited += UnityEngine.Time.unscaledDeltaTime;
            if (waited > 120f || (!Main.Server.IsRunning && !Main.Client.IsConnected))
                yield break;
            yield return null;
        }

        yield return null;

        if (!Main.Server.IsRunning && !Main.Client.IsConnected)
            yield break;

        // The scene unload may only destroy the object; send only if the model is gone too.
        if (GameState.identifiables.ContainsKey(actorId))
        {
            Logger.LogDiagnostic("gadget", "destroy-queued-dropped", $"actorId={actorId.Value:X}; reason=model-still-present");
            yield break;
        }

        Main.SendToAllOrServer(new ActorDestroyPacket { ActorId = actorId });
        NetworkGadgetManager.BroadcastLinkedPairDestroy(actorId);
        Logger.LogDiagnostic("gadget", "destroy-sent-after-load", $"actorId={actorId.Value:X}");
    }

    public static void Prefix(GameObject gadgetObj, string source)
    {
        var sceneLoading = SystemContext.Instance.SceneLoader.IsSceneLoadInProgress;

        if ((!Main.Server.IsRunning && !Main.Client.IsConnected) || HandlingPacket || !gadgetObj)
            return;
        
        if (source is "SR2MP.ActorDestroyHandler" or "SR2MP.InitialActors" or "SR2MP.RemoveExistingGadgetModel")
            return;

        if (gadgetObj.name.Contains("SR2MP_RemotePreview"))
            return;

        var gadget = gadgetObj.GetComponent<Il2CppMonomiPark.SlimeRancher.World.Gadget>();
        if (!gadget)
            return;

        if (sceneLoading)
        {
            try
            {
                var queuedId = gadget.GetActorId();
                SrLogger.LogWarning($"Gadget destroy {queuedId.Value:X} during scene load; queued until load finishes");
                Logger.LogDiagnostic("gadget", "destroy-queued", $"actorId={queuedId.Value:X}; source={source}");
                StartCoroutine(SendDestroyAfterLoad(queuedId));
            }
            catch (Exception ex)
            {
                SrLogger.LogWarning($"Could not queue gadget destroy during scene load: {ex.Message}");
            }
            return;
        }

        try
        {
            var actorId = gadget.GetActorId();

            Main.SendToAllOrServer(new ActorDestroyPacket { ActorId = actorId });
            
            NetworkGadgetManager.BroadcastLinkedPairDestroy(actorId);
        }
        catch (Exception ex)
        {
            SrLogger.LogError($"Failed to send GadgetDestroy packet: {ex.Message}");
        }
    }
}
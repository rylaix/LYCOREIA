using System.Collections;
using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.SceneManagement;
using SR2MP.Packets.Actor;
using SR2MP.Shared.Managers;
using SR2MP.Shared.Utils;
using Unity.Mathematics;

namespace SR2MP.Patches.Gadget;

[HarmonyPatch(typeof(GadgetDirector), nameof(GadgetDirector.InstantiateGadget))]
internal static class OnGadgetSpawn
{
    private static IEnumerator SpawnOverNetwork(
        GameObject result,
        SceneGroup sceneGroup,
        Vector3 position,
        Quaternion rotation)
    {
        yield return null;

        if (!result)
            yield break;

        var gadget = result.GetComponent<Il2CppMonomiPark.SlimeRancher.World.Gadget>();
        var type = NetworkActorManager.GetPersistentID(gadget.identType);

        GadgetModel? model = null;
        if (GameState.identifiables.TryGetValue(gadget.GetActorId(), out var identModel))
            model = identModel?.TryCast<GadgetModel>();

        var actorId = gadget.GetActorId().Value;
        if (!ActorManager.CanPublishLocalActorSpawn(actorId))
        {
            // Hold (instead of dropping) when only the ownership snapshot is missing.
            var inBlock = Main.Client.IsConnected &&
                          ActorSpawnPublicationPolicy.CanClientPublish(actorId, LocalID, true);
            if (inBlock && !ActorManager.HasAuthoritativeOwnershipSnapshot)
            {
                SrLogger.LogWarning($"Gadget {actorId:X} ({type}) publish held: ownership snapshot not received yet");
                Logger.LogDiagnostic("gadget", "publish-held", $"actorId={actorId:X}; type={type}");
                var waited = 0f;
                while (waited < 30f)
                {
                    yield return null;
                    waited += UnityEngine.Time.unscaledDeltaTime;
                    if (!result || !Main.Client.IsConnected ||
                        !GameState.identifiables.ContainsKey(new ActorId(actorId)))
                    {
                        SrLogger.LogWarning($"Gadget {actorId:X} publish abandoned while held (gadget gone or disconnected)");
                        yield break;
                    }
                    if (ActorManager.HasAuthoritativeOwnershipSnapshot)
                        break;
                }
            }

            if (!ActorManager.CanPublishLocalActorSpawn(actorId))
            {
                var snapshot = ActorManager.HasAuthoritativeOwnershipSnapshot;
                SrLogger.LogWarning(
                    $"Gadget spawn NOT published (will not sync): id={actorId:X} type={type} " +
                    $"snapshot={snapshot} inClientBlock={inBlock} host={Main.Server.IsRunning}");
                Logger.LogDiagnostic("gadget", "publish-suppressed",
                    $"actorId={actorId:X}; type={type}; snapshot={snapshot}; inClientBlock={inBlock}");
                ActorSpawnPublicationPolicy.TraceSuppressed("InstantiateGadget", actorId);
                yield break;
            }
        }

        var packet = new ActorSpawnPacket()
        {
            ActorId = gadget.GetActorId(),
            SceneGroup = (byte)NetworkSceneManager.GetPersistentID(sceneGroup),
            ActorType = type,
            Position = position,
            Rotation = rotation,
            Emotions = float4.zero,
            SpawnType = (byte)ActorSpawnType.Gadget,
            ChargeupTime = model?.waitForChargeupTime ?? 0,
            OwnerId = LocalID
        };

        Main.SendToAllOrServer(packet);

        NetworkGadgetManager.BroadcastGadgetLink(model);
    }

    public static void Postfix(
        GameObject __result,
        SceneGroup sceneGroup,
        Vector3 position,
        Quaternion rotation)
    {
        if (__result)
        {
            var gadget = __result.GetComponent<Il2CppMonomiPark.SlimeRancher.World.Gadget>();
            if (gadget && GameState.TryGetIdentifiableModel(gadget.GetActorId(), out var model))
                NetworkGadgetManager.ApplyPendingCannonFireTime(model?.TryCast<GadgetModel>());
        }

        if (!HandlingPacket && (Main.Server.IsRunning || Main.Client.IsConnected))
            StartCoroutine(SpawnOverNetwork(__result, sceneGroup, position, rotation));
    }
}

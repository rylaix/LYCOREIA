using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Slime.Slumber;
using SR2MP.Components.Actor;
using SR2MP.Packets.Slime.Sloomber;
using SR2MP.Patches.Slime;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Slime.Sloomber;

[HarmonyPatch(typeof(InitiateYawn), nameof(InitiateYawn.Selected))]
internal static class InitiateYawnSelected
{
    public static bool Prefix(InitiateYawn __instance) => SlimeBehaviorAuthority.ShouldRun(__instance);

    public static void Postfix(InitiateYawn __instance) => SloomberYawnSync.Begin(__instance);
}

[HarmonyPatch(typeof(InitiateYawn), nameof(InitiateYawn.Action))]
internal static class InitiateYawnAction
{
    public static bool Prefix(InitiateYawn __instance) => SlimeBehaviorAuthority.ShouldRun(__instance);
}

[HarmonyPatch(typeof(InitiateYawn), nameof(InitiateYawn.Deselected))]
internal static class InitiateYawnDeselected
{
    public static bool Prefix(InitiateYawn __instance) => SlimeBehaviorAuthority.ShouldRun(__instance);

    public static void Postfix(InitiateYawn __instance) => SloomberYawnSync.End(__instance);
}

internal static class SloomberYawnSync
{
    private const float ActiveYawnTimeoutSeconds = NetworkSlimeBehaviorManager.ActiveSloomberTimeoutSeconds;
    private static readonly Dictionary<long, float> ActiveYawns = new();

    internal static void Begin(InitiateYawn behaviour)
    {
        if (!IsMultiplayerActive())
        {
            ActiveYawns.Clear();
            return;
        }

        if (HandlingPacket)
            return;

        var actor = behaviour.GetComponentInParent<NetworkActor>();
        if (!actor || !actor.LocallyOwned)
            return;

        var actorId = actor.ActorId.Value;
        if (actorId == 0)
            return;

        var now = UnityEngine.Time.unscaledTime;
        if (ActiveYawns.TryGetValue(actorId, out var startedAt) &&
            now - startedAt < ActiveYawnTimeoutSeconds)
            return;

        ActiveYawns[actorId] = now;
        NetworkSlimeBehaviorManager.Publish(new SloomberYawnPacket
        {
            ActorId = actor.ActorId,
            Active = true
        });
    }

    internal static void End(InitiateYawn behaviour)
    {
        if (!IsMultiplayerActive())
        {
            ActiveYawns.Clear();
            return;
        }

        if (HandlingPacket)
            return;

        var actor = behaviour.GetComponentInParent<NetworkActor>();
        if (!actor || !actor.LocallyOwned)
            return;

        var actorId = actor.ActorId.Value;
        if (actorId == 0 || !ActiveYawns.Remove(actorId))
            return;

        NetworkSlimeBehaviorManager.Publish(new SloomberYawnPacket
        {
            ActorId = actor.ActorId,
            Active = false
        });
    }

    private static bool IsMultiplayerActive() => Main.Server.IsRunning || Main.Client.IsConnected;
}

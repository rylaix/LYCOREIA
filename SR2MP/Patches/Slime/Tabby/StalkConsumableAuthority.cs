using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Slime;
using SR2MP.Components.Actor;
using SR2MP.Packets.Slime.Stalk;
using SR2MP.Patches.Slime;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Slime.Tabby;

[HarmonyPatch(typeof(StalkConsumable), nameof(StalkConsumable.Selected))]
internal static class StalkConsumableSelected
{
    public static bool Prefix(StalkConsumable __instance) => SlimeBehaviorAuthority.ShouldRun(__instance);
}

[HarmonyPatch(typeof(StalkConsumable), nameof(StalkConsumable.Action))]
internal static class StalkConsumableAction
{
    public static bool Prefix(StalkConsumable __instance) => SlimeBehaviorAuthority.ShouldRun(__instance);
}

[HarmonyPatch(typeof(StalkConsumable), nameof(StalkConsumable.Deselected))]
internal static class StalkConsumableDeselected
{
    public static bool Prefix(StalkConsumable __instance) => SlimeBehaviorAuthority.ShouldRun(__instance);
}

[HarmonyPatch(typeof(StalkConsumable), nameof(StalkConsumable.SetTarget))]
internal static class StalkConsumableSetTarget
{
    public static void Prefix(StalkConsumable __instance, out GameObject __state)
    {
        __state = __instance._target;
    }

    public static void Postfix(StalkConsumable __instance, GameObject newTarget, GameObject __state)
    {
        // Applying an authoritative state calls SetTarget too. Do not turn that
        // application into a second client request if this peer owns the slime.
        if (__state == newTarget)
            return;

        var slimeActor = __instance.GetComponentInParent<NetworkActor>();
        if (!slimeActor || !SlimeBehaviorAuthority.ShouldPublishBehaviorChange(
                Main.Server.IsRunning || Main.Client.IsConnected, HandlingPacket, slimeActor.LocallyOwned))
            return;
        var targetActor = newTarget ? newTarget.GetComponentInParent<NetworkActor>() : null;
        if (slimeActor.ActorId.Value == 0 || (newTarget && !targetActor))
            return;

        if (targetActor && targetActor!.ActorId.Value == 0)
            return;

        if (targetActor)
            SlimeBehaviorAuthority.TransferTargetToLocalOwner(newTarget);

        NetworkSlimeBehaviorManager.Publish(new StalkConsumableTargetPacket
        {
            SlimeActorId = slimeActor.ActorId,
            TargetActorId = targetActor ? targetActor!.ActorId : default
        });
    }
}

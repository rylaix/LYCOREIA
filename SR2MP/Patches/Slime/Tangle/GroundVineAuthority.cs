using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Slime.Tangle;
using SR2MP.Components.Actor;
using SR2MP.Packets.Slime.Tangle;
using SR2MP.Patches.Slime;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Slime.Tangle;

[HarmonyPatch(typeof(GroundVine), nameof(GroundVine.Selected))]
internal static class GroundVineSelected
{
    public static bool Prefix(GroundVine __instance) => SlimeBehaviorAuthority.ShouldRun(__instance);
}

[HarmonyPatch(typeof(GroundVine), nameof(GroundVine.Action))]
internal static class GroundVineAction
{
    public static bool Prefix(GroundVine __instance) => SlimeBehaviorAuthority.ShouldRun(__instance);
}

[HarmonyPatch(typeof(GroundVine), nameof(GroundVine.Deselected))]
internal static class GroundVineDeselected
{
    public static bool Prefix(GroundVine __instance) => SlimeBehaviorAuthority.ShouldRun(__instance);
}

[HarmonyPatch(typeof(GroundVine), nameof(GroundVine.MaybeGrapple))]
internal static class GroundVineMaybeGrapple
{
    public static void Postfix(GroundVine __instance, GameObject target, bool __result)
    {
        if (!__result)
            return;

        var slimeActor = __instance.GetComponentInParent<NetworkActor>();
        var targetActor = target ? target.GetComponentInParent<NetworkActor>() : null;
        if (!slimeActor || !targetActor ||
            !SlimeBehaviorAuthority.ShouldPublishBehaviorChange(
                Main.Server.IsRunning || Main.Client.IsConnected, HandlingPacket, slimeActor!.LocallyOwned) ||
            slimeActor!.ActorId.Value == 0 || targetActor!.ActorId.Value == 0)
            return;

        SlimeBehaviorAuthority.TransferTargetToLocalOwner(target);

        NetworkSlimeBehaviorManager.Publish(new TangleVineGrapplePacket
        {
            TangleActorId = slimeActor.ActorId,
            TargetActorId = targetActor.ActorId,
            Active = true
        });
    }
}

[HarmonyPatch(typeof(GroundVine), nameof(GroundVine.Release))]
internal static class GroundVineRelease
{
    public static void Prefix(GroundVine __instance, out bool __state)
    {
        __state = __instance._target != null;
    }

    public static void Postfix(GroundVine __instance, bool __state)
    {
        if (!__state)
            return;

        var slimeActor = __instance.GetComponentInParent<NetworkActor>();
        if (!slimeActor || !SlimeBehaviorAuthority.ShouldPublishBehaviorChange(
                Main.Server.IsRunning || Main.Client.IsConnected, HandlingPacket, slimeActor.LocallyOwned) ||
            slimeActor.ActorId.Value == 0)
            return;

        NetworkSlimeBehaviorManager.Publish(new TangleVineGrapplePacket
        {
            TangleActorId = slimeActor.ActorId,
            Active = false
        });
    }
}

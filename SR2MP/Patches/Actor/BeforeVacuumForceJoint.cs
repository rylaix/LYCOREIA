using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.Player.PlayerItems;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.Actor;

[HarmonyPatch(typeof(VacuumItem), "ForceJoint", typeof(Vacuumable))]
internal static class BeforeVacuumForceJoint
{
    public static bool Prefix(VacuumItem __instance, Vacuumable __0)
        => NetworkActorCaptureManager.BeforeForceJoint(__instance, __0);
}

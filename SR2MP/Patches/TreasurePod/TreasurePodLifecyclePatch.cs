using System.Reflection;
using HarmonyLib;
using SR2MP.Shared.Managers;

namespace SR2MP.Patches.TreasurePod;

/// <summary>
/// Rechecks pending pod state as scene objects are created and while they load.
/// Update is included because a model or its GameObject may become available after
/// Awake/OnEnable/Start has already run during scene streaming.
/// </summary>
[HarmonyPatch]
internal static class TreasurePodLifecyclePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var lifecycleNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Awake",
            "OnEnable",
            "Start",
            "Update"
        };

        foreach (var method in AccessTools.GetDeclaredMethods(typeof(Il2Cpp.TreasurePod)))
        {
            if (method.GetParameters().Length == 0 && lifecycleNames.Contains(method.Name))
                yield return method;
        }
    }

    public static void Postfix(Il2Cpp.TreasurePod __instance, MethodBase __originalMethod)
    {
        if (__originalMethod.Name == "OnEnable")
            NetworkTreasurePodManager.OnPodEnabled(__instance);
        NetworkTreasurePodManager.ApplyPendingForLoadedPod(__instance);
    }
}

#if SR2MP_DEEPLOG
using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.VFX.EnvironmentInteraction;
using SR2MP.Components.FX;

namespace SR2MP.Patches.FX;

/// <summary>Diagnostics only: logs where the native (local) vac emits foliage ripples, relative to the camera,
/// so RemoteVacuumFoliage can be calibrated against it.</summary>
[HarmonyPatch(typeof(EnvironmentInteractionParticleSpawner), nameof(EnvironmentInteractionParticleSpawner.EmitVacuumRipple))]
internal static class OnEmitVacuumRipple
{
    public static void Postfix(Vector3 emitHere, Vector3 forwardDirection, float strength, float size, float sizeVariation)
    {
        if (RemoteVacuumFoliage.EmittingRemote)
            return;
        var camera = Camera.main;
        if (!camera)
            return;
        var eye = camera.transform;
        var offset = emitHere - eye.position;
        SrLogger.LogDiagnostic("player", "vac-foliage-native",
            $"along={Vector3.Dot(offset, eye.forward):F2}; right={Vector3.Dot(offset, eye.right):F2}; " +
            $"up={Vector3.Dot(offset, eye.up):F2}; fwdDot={Vector3.Dot(forwardDirection.normalized, eye.forward):F2}; " +
            $"strength={strength:F2}; size={size:F2}±{sizeVariation:F2}",
            "vac-foliage-native", 250);
    }
}
#endif

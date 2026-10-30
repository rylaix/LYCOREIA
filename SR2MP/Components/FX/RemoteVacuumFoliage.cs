using Il2CppMonomiPark.SlimeRancher.VFX.EnvironmentInteraction;

namespace SR2MP.Components.FX;

/// <summary>
/// Grass, flowers, tarps and ranch cloth bend through one shared top-down "environment interaction" texture
/// that every client renders around its own camera. The native vac writes ripples into it for the LOCAL player
/// only (VacuumInteractionFX -> EnvironmentInteractionVacuum -> EnvironmentInteractionParticleSpawner).
/// This emits the same ripples along a remote player's active cone, using the local vac's own settings.
/// Visual-only: nothing is sent over the network (the cone pose and on/off state are already synced).
/// </summary>
internal sealed class RemoteVacuumFoliage
{
    private const float SettingsRetryInterval = 2f;

    /// <summary>One native EnvironmentInteractionVacuum: its distance along the cone and its own ripple.</summary>
    private readonly record struct Emitter(float Distance, float Size, float SizeVariation, float Strength, float Rate);

    // Fallbacks until the local vac's settings are readable: the native values measured on 2026-10-30.
    private static readonly Emitter[] FallbackEmitters =
    {
        new(1.82f, 2f, 0.1f, 0.5f, 4f),
        new(3.32f, 5f, 0.1f, 1f, 4f),
        new(5.32f, 8f, 0.1f, 1f, 4f),
        new(8.32f, 11f, 0.1f, 0.6f, 4f),
        new(11.32f, 20f, 0.1f, 0.3f, 4f),
    };
    private const float FallbackMaxDistance = 14f;

    private static EnvironmentInteractionParticleSpawner? spawner;
    private static float nextSpawnerLookupAt;
    private static bool nativeSettingsRead;
    private static float nextSettingsLookupAt;
    private static Emitter[] emitters = FallbackEmitters;
    private static float maxDistance = FallbackMaxDistance;

    internal static bool EmittingRemote { get; private set; }
#if SR2MP_TEST
    internal static float TestStrengthScale = 1f;
#endif

    private static bool loggedFirstUpdate;

    private float[] emissionClocks = Array.Empty<float>();

    /// <summary>Called every frame while the remote cone is visible; <paramref name="aim"/> is the suction direction.</summary>
    internal void Update(Vector3 nozzlePosition, Vector3 aim, float deltaTime)
    {
        if (!loggedFirstUpdate)
        {
            loggedFirstUpdate = true;
            SrLogger.LogDiagnostic("player", "vac-foliage-first-update", $"nozzle={nozzlePosition}; aim={aim}");
        }
        if (!TryGetSpawner(out var particleSpawner))
            return;
        RefreshNativeSettings();

        if (emissionClocks.Length != emitters.Length)
            emissionClocks = new float[emitters.Length];

        var forward = aim.normalized;
        var hitDistance = Physics.Raycast(nozzlePosition, forward, out var hit, maxDistance,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
            ? hit.distance
            : float.PositiveInfinity;
        EmittingRemote = true;
        try
        {
            for (var i = 0; i < emitters.Length; i++)
            {
                var emitter = emitters[i];
                emissionClocks[i] += deltaTime * emitter.Rate;
                if (emissionClocks[i] < 1f)
                    continue;
                // Never burst after a hitch: at most one ripple per emitter per frame.
                emissionClocks[i] = Mathf.Min(emissionClocks[i] - 1f, 1f);

                // Emitters past what the cone hits push at the hit surface instead of inside it.
                var distance = Mathf.Min(emitter.Distance, hitDistance);
                var strength = emitter.Strength;
                var size = emitter.Size;
#if SR2MP_TEST
                strength *= TestStrengthScale;
                size *= Mathf.Max(1f, Mathf.Sqrt(TestStrengthScale));
#endif
                particleSpawner.EmitVacuumRipple(nozzlePosition + forward * distance, forward,
                    strength, size, emitter.SizeVariation);
            }
        }
        catch (Exception ex)
        {
            spawner = null;
            SrLogger.LogDiagnostic("player", "vac-foliage-error", ex.Message, "vac-foliage-error", 10000);
        }
        finally
        {
            EmittingRemote = false;
        }
    }

    internal void Reset() => Array.Clear(emissionClocks);

    private static bool TryGetSpawner(out EnvironmentInteractionParticleSpawner particleSpawner)
    {
        particleSpawner = spawner!;
        if (spawner)
            return true;

        var now = UnityEngine.Time.unscaledTime;
        if (now < nextSpawnerLookupAt)
            return false;
        nextSpawnerLookupAt = now + SettingsRetryInterval;

        var environmentInteraction = Object.FindObjectOfType<EnvironmentInteraction>();
        spawner = environmentInteraction ? environmentInteraction.ParticleSpawner : null;
        particleSpawner = spawner!;
        SrLogger.LogDiagnostic("player", "vac-foliage-spawner", $"found={(bool)spawner}");
        return spawner;
    }

    private static void RefreshNativeSettings()
    {
        if (nativeSettingsRead)
            return;
        var now = UnityEngine.Time.unscaledTime;
        if (now < nextSettingsLookupAt)
            return;
        nextSettingsLookupAt = now + SettingsRetryInterval;

        // The local vac's FX lives on the first-person rig, not under SceneContext.player; remote cone copies
        // never carry this component, so any instance is the local one.
        var found = Object.FindObjectsOfType<VacuumInteractionFX>(true);
        var vacFx = found != null && found.Length > 0 ? found[0] : null;
        if (!vacFx)
            return;

        try
        {
            var nativeEmitters = vacFx!._environmentInteractionVacuumFX;
            var nativeDistances = vacFx._environmentInteractionVacuumDistances;
            if (nativeEmitters == null || nativeDistances == null)
                return;

            // The two arrays are index-aligned: emitter i sits at distance i along the cone.
            var read = new List<Emitter>();
            for (var i = 0; i < Mathf.Min(nativeEmitters.Length, nativeDistances.Length); i++)
            {
                var native = nativeEmitters[i];
                if (!native)
                    continue;
                read.Add(new Emitter(nativeDistances[i], native.vacuumRippleSize, native.vacuumRippleSizeVariation,
                    native.vacuumRippleStrength, native.emissionPerSecond > 0f ? native.emissionPerSecond : 4f));
            }
            if (read.Count == 0)
                return;

            emitters = read.OrderBy(emitter => emitter.Distance).ToArray();
            if (vacFx._maxVacDistance > 0f)
                maxDistance = vacFx._maxVacDistance;
            nativeSettingsRead = true;
            var described = string.Join(" | ", emitters.Select(e =>
                $"d={e.Distance:F2} size={e.Size:F2}±{e.SizeVariation:F2} strength={e.Strength:F2} rate={e.Rate:F1}"));
            SrLogger.LogDiagnostic("player", "vac-foliage-settings", $"max={maxDistance:F2}; emitters=[{described}]");
        }
        catch (Exception ex)
        {
            SrLogger.LogDiagnostic("player", "vac-foliage-error", "settings: " + ex.Message, "vac-foliage-error", 10000);
        }
    }
}

using SR2MP.Shared.Managers;

namespace SR2MP.Components.Actor;

internal sealed partial class NetworkActor
{
    private float nextVisibilityDiagnosticAt;
    private string? lastVisibilityDiagnosticState;

    // Opt-in only: three GetComponentsInChildren scans per actor every 0.5 s cost real frame time with hundreds
    // of actors, and the state changes on every camera move (it flooded diagnostics with ~1000 lines/s).
    private static readonly bool VisibilityTraceEnabled =
        Environment.GetEnvironmentVariable("SR2MP_TRACE_ACTOR_VISIBILITY") == "1";

    private void TraceVisibilityTransition()
    {
        if (!VisibilityTraceEnabled || !Logger.DiagnosticsEnabled ||
            UnityEngine.Time.unscaledTime < nextVisibilityDiagnosticAt)
            return;

        nextVisibilityDiagnosticAt = UnityEngine.Time.unscaledTime + 0.5f;
        try
        {
            var camera = Camera.main;
            var renderers = GetComponentsInChildren<Renderer>(true);
            var enabledRenderers = 0;
            var visibleRenderers = 0;
            var rendererActive = 0;
            foreach (var renderer in renderers)
            {
                if (!renderer || !renderer.enabled)
                    continue;

                enabledRenderers++;
                if (!renderer.gameObject.activeInHierarchy)
                    continue;

                rendererActive++;
                if (renderer.isVisible)
                    visibleRenderers++;
            }

            var lodGroups = GetComponentsInChildren<LODGroup>(true);
            var activeLodGroups = 0;
            var lodBounds = new List<string>(lodGroups.Length);
            foreach (var lod in lodGroups)
            {
                if (!lod)
                    continue;

                if (lod.enabled && lod.gameObject.activeInHierarchy)
                    activeLodGroups++;
                lodBounds.Add($"{lod.name}:enabled={lod.enabled};active={lod.gameObject.activeInHierarchy};size={lod.size:F2}");
            }

            var colliders = GetComponentsInChildren<Collider>(true);
            var activeColliders = 0;
            foreach (var collider in colliders)
            {
                if (collider && collider.enabled && collider.gameObject.activeInHierarchy)
                    activeColliders++;
            }

            var distance = camera
                ? Vector3.Distance(camera.transform.position, transform.position)
                : float.NaN;
            var inFrustum = false;
            if (camera)
            {
                var planes = GeometryUtility.CalculateFrustumPlanes(camera);
                foreach (var renderer in renderers)
                {
                    if (renderer && renderer.enabled && renderer.gameObject.activeInHierarchy &&
                        GeometryUtility.TestPlanesAABB(planes, renderer.bounds))
                    {
                        inFrustum = true;
                        break;
                    }
                }
            }

            var distanceBand = float.IsFinite(distance) ? (int)(distance / 5f) : -1;
            var actorType = identifiable && identifiable.identType
                ? identifiable.identType.name
                : gameObject.name;
            var state = $"active={gameObject.activeInHierarchy};enabledRenderers={enabledRenderers};" +
                        $"activeRenderers={rendererActive};visibleRenderers={visibleRenderers};" +
                        $"inFrustum={inFrustum};lodGroups={activeLodGroups}/{lodGroups.Length};" +
                        $"colliders={activeColliders};distanceBand={distanceBand}";
            if (state == lastVisibilityDiagnosticState)
                return;

            lastVisibilityDiagnosticState = state;
            var actorId = ActorId;
            Logger.LogDiagnostic("actor-visibility", "visibility-state-changed",
                $"actorId={actorId.Value:X}; type={actorType}; owner={CurrentOwnerId}; " +
                $"cameraDistance={distance:F2}; camera={(camera ? camera.name : "<none>")}; {state}; " +
                $"lod=[{string.Join("|", lodBounds)}]");
        }
        catch (Exception exception)
        {
            Logger.LogDiagnostic("actor-visibility", "visibility-sample-failed",
                $"actor={gameObject.name}; reason={exception.GetType().Name}:{exception.Message}",
                $"actor-visibility-error:{GetInstanceID()}", 30_000);
        }
    }
}

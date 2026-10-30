using SR2MP.Components.Actor;

namespace SR2MP.Components.Player;

internal partial class NetworkPlayer
{
    // Nothing applies beyond the largest personal-space radius, so search only that far.
    private const float ForceFieldSearchRadius = ForceFieldSlimeRadius;
    // Il2Cpp array kept alive: a managed Collider[] would be copied into a temporary native array and never
    // receive the results.
    private static readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Collider> ForceFieldHits = new(64);
    private const float ForceFieldSlimeRadius = 4f;
    private const float ForceFieldOtherRadius = 1.2f;
    private const float ForceStrength = 10f;

    /*private void UpdateForceField()
    {
        if (IsLocal) return;

        var position = transform.position;
        var hits = Physics.OverlapSphere(position, ForceFieldSearchRadius);

        foreach (var col in hits)
        {
            if (!col) continue;

            var rb = col.attachedRigidbody;
            if (!rb || rb.isKinematic) continue;

            var actor = col.GetComponent<NetworkActor>();
            if (!actor || !actor.LocallyOwned) continue;

            var offset = col.transform.position - position;
            var distance = offset.magnitude;
            if (distance < 0.001f) continue;

            var radius = actor.isSlime ? ForceFieldSlimeRadius : ForceFieldOtherRadius;
            var falloff = Mathf.Clamp01(1f - Mathf.Pow(distance / radius, ForceFalloff));
            var force = (offset.normalized + Vector3.up * 0.3f)
                        * (ForceStrength * falloff);

            rb.AddForce(force, ForceMode.Force);
        }
    }*/
    
    private void UpdateForceField()
    {
        if (IsLocal) return;

        var position = transform.position;

        var hitCount = Physics.OverlapSphereNonAlloc(position, ForceFieldSearchRadius, ForceFieldHits);

        for (var index = 0; index < hitCount; index++)
        {
            var col = ForceFieldHits[index];
            if (!col) continue;

            var rb = col.attachedRigidbody;
            if (!rb || rb.isKinematic) continue;

            var actor = col.GetComponent<NetworkActor>();
            if (!actor || !actor.LocallyOwned) continue;

            var offset = col.transform.position - position;
            var distance = offset.magnitude;
            if (distance < 0.001f) continue;

            var radius = actor.isSlime ? ForceFieldSlimeRadius : ForceFieldOtherRadius;

            var dir = offset / distance;
            
            // Outside the personal-space radius nothing applies. The tangential damping below used to run for
            // every actor in the 5 m search sphere, which bent passing thrown objects toward this avatar.
            if (distance >= radius) continue;

            var t = distance / radius;

            var smooth = 1f - (t * t * (3f - (2f * t)));
            
            var boost = Mathf.Clamp01(1f - (t * 2f));

            var force = (dir + (Vector3.up * 0.15f)).normalized * (ForceStrength * (smooth + (boost * 1.5f)));
            
            rb.AddForce(force, ForceMode.Acceleration);
            
            var velocity = rb.linearVelocity;

            var radialVel = Vector3.Dot(velocity, dir) * dir;
            var tangentialVel = velocity - radialVel;
            
            rb.AddForce(-tangentialVel * 0.4f, ForceMode.Force);
            
            if (t < 0.25f)
                rb.AddForce(dir * (ForceStrength * 1.5f), ForceMode.Force);
        }
    }
}
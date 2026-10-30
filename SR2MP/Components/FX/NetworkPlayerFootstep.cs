using JetBrains.Annotations;
using Starlight.Storage;

namespace SR2MP.Components.FX;

// Modified version of PlayerFootstepFX (from a restored decomp of 'PlayerFootstepFX' qwq)
[InjectIntoIL]
internal sealed class NetworkPlayerFootstep : MonoBehaviour
{
    public Transform SpawnAtTransform;

    public GameObject FootstepFX;
    public GameObject FootstepFXInstance;
    private ParticleSystem footstepParticles;

    private bool playerGrounded;
    private bool playerInWater;

    private const float GroundCheckDistance = 0.15f;
    private const int GroundedLayer = -1728543467;

    [UsedImplicitly]
    public void Awake()
    {
        SpawnAtTransform = transform.childCount > 2 ? transform.GetChild(2) : transform;
        TryInitializeFootsteps();
    }

    private bool TryInitializeFootsteps()
    {
        if (!SpawnAtTransform)
            return false;

        if (FootstepFXInstance)
            return footstepParticles;

        FootstepFX ??= FXManager.FootstepFX;
        if (!FootstepFX)
            return false;

        FootstepFXInstance = Instantiate(FootstepFX, SpawnAtTransform.position, SpawnAtTransform.rotation);
        if (!FootstepFXInstance)
            return false;

        FootstepFXInstance.transform.SetParent(SpawnAtTransform, false);
        footstepParticles = FootstepFXInstance.GetComponentInChildren<ParticleSystem>();
        return footstepParticles;
    }

    public void UpdateFXState()
    {
        if (!footstepParticles && !TryInitializeFootsteps())
            return;

        if (playerGrounded && !playerInWater)
            footstepParticles.Play(true);
        else
            footstepParticles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
    }

    [UsedImplicitly]
    public void OnTriggerEnter(Collider collider)
    {
        if (!collider.CompareTag("Water") && collider.gameObject.layer != LayerMask.NameToLayer("Water"))
            return;

        playerInWater = true;
        if (footstepParticles)
            footstepParticles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
    }

    [UsedImplicitly]
    public void OnTriggerExit(Collider collider)
    {
        if (!collider.CompareTag("Water") && collider.gameObject.layer != LayerMask.NameToLayer("Water"))
            return;

        playerInWater = false;

        if (playerGrounded && footstepParticles)
            footstepParticles.Play(true);
    }

    private bool CheckGrounded(int layer)
        => Physics.Raycast(transform.position, Vector3.down, GroundCheckDistance, layer);

    public void Update()
    {
        if (!footstepParticles && !TryInitializeFootsteps())
            return;

        var isGrounded = CheckGrounded(GroundedLayer);

        if (isGrounded == playerGrounded)
            return;

        playerGrounded = isGrounded;
        UpdateFXState();
    }
}

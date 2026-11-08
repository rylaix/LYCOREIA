// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Owned.Shared.Utils;

/// <summary>
/// Co-op keeps the ranch (ranch cells per CellDirector.IsOnRanch) safe from native cleanups. Replicas and other
/// peers' actors inflate population counts, and corrected/snapped positions can drop actors through
/// the terrain, so ranch slimes, hens and plorts were culled, timed out or killed by fall triggers.
/// </summary>
internal static class RanchProtection
{
    private const float RescueRayHeight = 300f;

    /// <summary>Ranch actors except Tarr (pests) and produce (keeps its vanilla rot/decay cycle).</summary>
    internal static bool IsProtectedRanchActor(GameObject actorObj)
    {
        var actor = actorObj.GetComponent<IdentifiableActor>();
        if (!actor || !IsInRanch(actor))
            return false;

        var typeName = actor.identType ? actor.identType.name : string.Empty;
        if (typeName.Contains("Tarr", StringComparison.OrdinalIgnoreCase))
            return false;

        return !actorObj.GetComponent<ResourceCycle>();
    }

    /// <summary>
    /// The game's own ranch test (ranch cells only). The ConservatoryFields scene group also holds
    /// Rainbow Fields, so a scene-group check would protect the wilds too.
    /// </summary>
    private static bool IsInRanch(IdentifiableActor actor)
    {
        var member = actor.GetComponent<Il2CppMonomiPark.SlimeRancher.Regions.RegionMember>();
        return member && CellDirector.IsOnRanch(member);
    }

    /// <summary>
    /// Puts an actor that fell through the ranch floor back on the ground straight above it.
    /// Replicas are only kept (their owner rescues and re-syncs them). False => let it be destroyed.
    /// </summary>
    internal static bool TryRescueFall(GameObject actorObj)
    {
        // ownership now lives in the actor table (the NetworkActor component is gone)
        var identifiable = actorObj.GetComponent<IdentifiableActor>();
        if (identifiable && identifiable._model != null && Mod.Actors != null &&
            Mod.Actors.Table.TryGet(identifiable._model.actorId.Value, out var entry) &&
            !string.IsNullOrEmpty(entry.Owner) && !string.Equals(entry.Owner, LocalID, StringComparison.Ordinal))
            return true;

        var position = actorObj.transform.position;
        var origin = new Vector3(position.x, position.y + RescueRayHeight, position.z);
        if (!Physics.Raycast(origin, Vector3.down, out var hit, RescueRayHeight * 2f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            return false;

        actorObj.transform.position = hit.point + Vector3.up * 1f;
        var body = actorObj.GetComponent<Rigidbody>();
        if (body)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.position = actorObj.transform.position;
        }

        return true;
    }
}

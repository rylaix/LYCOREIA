using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.LandPlots;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.LandPlots;

[PacketHandler((byte)PacketType.GardenPlant)]
internal sealed class GardenPlantHandler : BasePacketHandler<GardenPlantPacket>
{
    private const int EmptyCrop = 9;

    protected override bool Handle(GardenPlantPacket packet, IPEndPoint? clientEp)
    {
        // Client-originated requests carry no authoritative revision. The host applies the
        // mutation, assigns a garden revision, and relays that versioned state to peers.
        if (IsServerSide)
        {
            if (string.IsNullOrWhiteSpace(packet.ID) ||
                !GameState.landPlots.TryGetValue(packet.ID, out var model) || model == null)
            {
                SrLogger.LogWarning($"Rejected garden crop change for unknown plot '{packet.ID}'.");
                SendRejectedCropCorrection(packet.ID, clientEp, IsServerSide);
                return GardenCropMutationPolicy.ShouldRelayMutation(mutationAccepted: false);
            }

            if (!ApplyCrop(packet, requirePhysicalSuccess: true))
            {
                SendRejectedCropCorrection(packet.ID, clientEp, IsServerSide);
                return GardenCropMutationPolicy.ShouldRelayMutation(mutationAccepted: false);
            }

            if (!NetworkGardenManager.PrepareLocalCropUpdate(packet))
                return false;

            // The client already applied its local interaction, but it still needs the
            // host revision so delayed older packets cannot roll that interaction back.
            if (clientEp != null)
                Main.Server.SendToClient(packet, clientEp);

            return true;
        }

        if (!NetworkGardenManager.TryAcceptCropUpdate(packet))
        {
            SrLogger.LogGarden(
                $"Ignored stale/invalid crop update: plot='{packet.ID}', garden='{packet.GardenId}', revision={packet.Revision}.");
            return false;
        }

        // If a less advanced local save has not created this plot yet, the cache retains
        // the accepted state. InitialLandPlots consumes it after creating the host plot model.
        // A live host update must also replace any optimistic guest crop before planting the
        // canonical value, since GardenCatcher.CanAccept rejects an already occupied plot.
        return ApplyAuthoritativeCrop(packet);
    }

    private static void SendRejectedCropCorrection(string plotId, IPEndPoint? clientEp, bool isServerSide)
    {
        if (string.IsNullOrWhiteSpace(plotId) || clientEp == null)
            return;

        var canonicalCrop = EmptyCrop;
        if (GameState.landPlots.TryGetValue(plotId, out var model) && model != null)
        {
            // The saved land-plot model is what the initial snapshot serializes. Use the
            // scene component only when the model has no crop definition available.
            var cropType = model.resourceGrowerDefinition?._primaryResourceType;
            if (cropType == null && model.gameObj)
            {
                var spawnResource = model.gameObj.GetComponentInChildren<SpawnResource>(true);
                cropType = spawnResource?._resourceGrowerDefinition?._primaryResourceType;
            }

            if (cropType != null)
            {
                try
                {
                    canonicalCrop = NetworkActorManager.GetPersistentID(cropType);
                }
                catch (Exception exception)
                {
                    SrLogger.LogWarning(
                        $"Could not identify host crop for rejected garden change on '{plotId}': {exception.Message}");
                    return;
                }
            }
        }
        else
        {
            // A host without this plot model has no crop state to preserve. Treat the
            // canonical crop for this rejected request as empty; the guest's plot model
            // is corrected through the same ordered crop-state stream.
            SrLogger.LogGarden($"Rejected garden change for missing host plot '{plotId}'; correcting guest crop to empty.");
        }

        var correction = new GardenPlantPacket
        {
            ID = plotId,
            ActorType = canonicalCrop
        };
        if (!NetworkGardenManager.PrepareLocalCropUpdate(correction) ||
            !GardenCropMutationPolicy.CanPublishRejectedCorrection(
                isServerSide: isServerSide,
                hasRequester: clientEp != null,
                hasCanonicalState: true,
                revision: correction.Revision))
        {
            SrLogger.LogWarning($"Could not prepare rejected garden crop correction for '{plotId}'.");
            return;
        }

        // This is a state correction, not an accepted mutation. Send it only to the
        // requester; the caller returns false so BasePacketHandler will not relay the
        // refused request to other peers.
        Main.Server.SendToClient(correction, clientEp);
        SrLogger.LogGarden(
            $"Sent rejected garden crop correction: plot='{plotId}', crop={canonicalCrop}, revision={correction.Revision}.");
    }

    private static bool ApplyCrop(GardenPlantPacket packet, bool requirePhysicalSuccess = false)
    {
        if (string.IsNullOrWhiteSpace(packet.ID) ||
            !GameState.landPlots.TryGetValue(packet.ID, out var model) || model == null)
            return false;

        if (packet.ActorType == EmptyCrop)
        {
            if (!model.gameObj)
            {
                model.resourceGrowerDefinition = null;
                return true;
            }

            var plot = model.gameObj.GetComponentInChildren<LandPlot>(true);
            if (requirePhysicalSuccess &&
                !GardenCropMutationPolicy.CanCommitClear(modelLoaded: true, hasLandPlot: !!plot))
            {
                SrLogger.LogWarning($"Garden clear was refused because plot '{packet.ID}' has no LandPlot component.");
                return false;
            }

            if (!plot)
            {
                model.resourceGrowerDefinition = null;
                return true;
            }

            var wasHandlingPacket = HandlingPacket;
            HandlingPacket = true;
            try
            {
                plot.DestroyAttached();
            }
            catch (Exception exception)
            {
                SrLogger.LogWarning($"Garden clear was refused for plot '{packet.ID}': {exception.Message}");
                return false;
            }
            finally
            {
                HandlingPacket = wasHandlingPacket;
            }

            model.resourceGrowerDefinition = null;
            return true;
        }

        if (!ActorManager.ActorTypes.TryGetValue(packet.ActorType, out var actor) || actor == null)
        {
            SrLogger.LogWarning($"Garden crop type {packet.ActorType} is unknown for plot '{packet.ID}'.");
            return false;
        }

        var saveReferenceTranslation = GameContext.Instance?.AutoSaveDirector?._saveReferenceTranslation;
        var translation = saveReferenceTranslation?._resourceGrowerTranslation?.RawLookupDictionary;
        if (translation == null)
            return false;

        var growerDefinition = translation._entries
            .FirstOrDefault(entry => entry.value != null && entry.value._primaryResourceType == actor).value;
        if (growerDefinition == null)
        {
            SrLogger.LogWarning($"No garden grower definition for crop {packet.ActorType} in plot '{packet.ID}'.");
            return false;
        }

        var currentCrop = model.resourceGrowerDefinition?._primaryResourceType;
        var existingSpawner = model.gameObj
            ? model.gameObj.GetComponentInChildren<SpawnResource>(true)
            : null;
        var existingCrop = existingSpawner?._resourceGrowerDefinition?._primaryResourceType;
        if (currentCrop == actor && existingCrop == actor)
            return true;

        if (!model.gameObj)
        {
            model.resourceGrowerDefinition = growerDefinition;
            return true;
        }

        var garden = model.gameObj.GetComponentInChildren<GardenCatcher>(true);
        var canAccept = garden && garden.CanAccept(actor);
        if (requirePhysicalSuccess &&
            !GardenCropMutationPolicy.CanBeginPlant(modelLoaded: true, hasGardenCatcher: !!garden, canAccept: canAccept))
        {
            SrLogger.LogWarning($"Garden planting was refused for plot '{packet.ID}' crop {packet.ActorType}.");
            return false;
        }

        if (!requirePhysicalSuccess)
            model.resourceGrowerDefinition = growerDefinition;

        if (!garden || !canAccept)
            return true;

        GameObject? plantedObject;
        var wasHandling = HandlingPacket;
        HandlingPacket = true;
        try
        {
            plantedObject = garden.Plant(actor!, true);
        }
        finally
        {
            HandlingPacket = wasHandling;
        }

        if (!requirePhysicalSuccess)
            return true;

        var plantedSpawner = plantedObject ? plantedObject.GetComponent<SpawnResource>() : null;
        var plantedCrop = plantedSpawner?._resourceGrowerDefinition?._primaryResourceType;
        if (!GardenCropMutationPolicy.CanCommitPlant(
                modelLoaded: true,
                hasGardenCatcher: true,
                canAccept: canAccept,
                plantedCropMatches: plantedCrop == actor))
        {
            SrLogger.LogWarning(
                $"Garden planting did not create the requested crop for plot '{packet.ID}' crop {packet.ActorType}.");
            return false;
        }

        model.resourceGrowerDefinition = growerDefinition;

        return true;
    }

    private static bool ApplyAuthoritativeCrop(GardenPlantPacket packet)
    {
        if (string.IsNullOrWhiteSpace(packet.ID) ||
            !GameState.landPlots.TryGetValue(packet.ID, out var model) || model == null)
            return false;

        if (packet.ActorType == EmptyCrop)
        {
            if (!model.gameObj)
            {
                model.resourceGrowerDefinition = null;
                return true;
            }

            var plot = model.gameObj.GetComponentInChildren<LandPlot>(true);
            if (!plot)
            {
                model.resourceGrowerDefinition = null;
                SrLogger.LogWarning(
                    $"Could not physically clear guest garden '{packet.ID}' because its LandPlot component is missing.");
                return false;
            }

            var wasHandlingPacket = HandlingPacket;
            HandlingPacket = true;
            try
            {
                plot.DestroyAttached();
                model.resourceGrowerDefinition = null;
                return true;
            }
            catch (Exception exception)
            {
                SrLogger.LogWarning($"Could not apply authoritative garden clear for '{packet.ID}': {exception.Message}");
                return false;
            }
            finally
            {
                HandlingPacket = wasHandlingPacket;
            }
        }

        if (!ActorManager.ActorTypes.TryGetValue(packet.ActorType, out var actor) || actor == null)
        {
            SrLogger.LogWarning($"Authoritative garden crop type {packet.ActorType} is unknown for plot '{packet.ID}'.");
            return false;
        }

        var translation = GameContext.Instance?.AutoSaveDirector?._saveReferenceTranslation
            ?._resourceGrowerTranslation?.RawLookupDictionary;
        if (translation == null)
            return false;

        var growerDefinition = translation._entries
            .FirstOrDefault(entry => entry.value != null && entry.value._primaryResourceType == actor).value;
        if (growerDefinition == null)
        {
            SrLogger.LogWarning($"No garden grower definition for authoritative crop {packet.ActorType} on '{packet.ID}'.");
            return false;
        }

        if (!model.gameObj)
        {
            model.resourceGrowerDefinition = growerDefinition;
            return true;
        }

        var garden = model.gameObj.GetComponentInChildren<GardenCatcher>(true);
        var plotComponent = model.gameObj.GetComponentInChildren<LandPlot>(true);
        var existingSpawner = model.gameObj.GetComponentInChildren<SpawnResource>(true);
        var modelCrop = model.resourceGrowerDefinition?._primaryResourceType;
        var physicalCrop = existingSpawner?._resourceGrowerDefinition?._primaryResourceType;
        if (!GardenCropMutationPolicy.ShouldReplacePhysicalCrop(
                modelCropMatches: modelCrop == actor,
                physicalCropMatches: physicalCrop == actor))
            return true;

        if (!plotComponent || !garden)
        {
            model.resourceGrowerDefinition = growerDefinition;
            SrLogger.LogWarning(
                $"Could not fully reconcile guest garden '{packet.ID}' crop {packet.ActorType}: plot or catcher is missing.");
            return false;
        }

        var wasHandling = HandlingPacket;
        HandlingPacket = true;
        try
        {
            // Remove the optimistic local crop first so CanAccept sees the same empty
            // plot state that the original snapshot reconciliation expects.
            plotComponent.DestroyAttached();
            model.resourceGrowerDefinition = growerDefinition;

            if (!garden.CanAccept(actor))
            {
                SrLogger.LogWarning(
                    $"Guest garden '{packet.ID}' still refused authoritative crop {packet.ActorType} after clearing its local crop.");
                return false;
            }

            var plantedObject = garden.Plant(actor, true);
            var plantedSpawner = plantedObject ? plantedObject.GetComponent<SpawnResource>() : null;
            var plantedCrop = plantedSpawner?._resourceGrowerDefinition?._primaryResourceType;
            if (plantedCrop != actor)
            {
                SrLogger.LogWarning(
                    $"Guest garden '{packet.ID}' failed to create authoritative crop {packet.ActorType}.");
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            SrLogger.LogWarning($"Could not apply authoritative garden crop for '{packet.ID}': {exception.Message}");
            return false;
        }
        finally
        {
            HandlingPacket = wasHandling;
        }
    }
}

/// <summary>Pure commit rules for host-authored garden crop mutations.</summary>
internal static class GardenCropMutationPolicy
{
    internal static bool CanBeginPlant(bool modelLoaded, bool hasGardenCatcher, bool canAccept)
        => !modelLoaded || hasGardenCatcher && canAccept;

    internal static bool CanCommitPlant(
        bool modelLoaded,
        bool hasGardenCatcher,
        bool canAccept,
        bool plantedCropMatches)
        => CanBeginPlant(modelLoaded, hasGardenCatcher, canAccept) && (!modelLoaded || plantedCropMatches);

    internal static bool CanCommitClear(bool modelLoaded, bool hasLandPlot)
        => !modelLoaded || hasLandPlot;

    internal static bool CanPublishLocalPlant(bool hasPlotId, bool hasPlantResult, bool returnedCropMatches)
        => hasPlotId && hasPlantResult && returnedCropMatches;

    internal static bool CanPublishRejectedCorrection(
        bool isServerSide,
        bool hasRequester,
        bool hasCanonicalState,
        long revision)
        => isServerSide && hasRequester && hasCanonicalState && revision > 0;

    internal static bool ShouldReplacePhysicalCrop(bool modelCropMatches, bool physicalCropMatches)
        => !modelCropMatches || !physicalCropMatches;

    internal static bool ShouldRelayMutation(bool mutationAccepted)
        => mutationAccepted;

    internal static bool VerifyRejectedCorrectionRules()
        => CanPublishRejectedCorrection(isServerSide: true, hasRequester: true, hasCanonicalState: true, revision: 1)
           && !CanPublishRejectedCorrection(isServerSide: false, hasRequester: true, hasCanonicalState: true, revision: 1)
           && !CanPublishRejectedCorrection(isServerSide: true, hasRequester: false, hasCanonicalState: true, revision: 1)
           && !CanPublishRejectedCorrection(isServerSide: true, hasRequester: true, hasCanonicalState: false, revision: 1)
           && !CanPublishRejectedCorrection(isServerSide: true, hasRequester: true, hasCanonicalState: true, revision: 0)
           && !CanPublishRejectedCorrection(isServerSide: true, hasRequester: true, hasCanonicalState: true, revision: -1)
           && !ShouldReplacePhysicalCrop(modelCropMatches: true, physicalCropMatches: true)
           && ShouldReplacePhysicalCrop(modelCropMatches: false, physicalCropMatches: true)
           && ShouldReplacePhysicalCrop(modelCropMatches: true, physicalCropMatches: false)
           && !ShouldRelayMutation(mutationAccepted: false)
           && ShouldRelayMutation(mutationAccepted: true);

    internal static bool VerifyHostMutationAcceptance()
        => CanBeginPlant(modelLoaded: false, hasGardenCatcher: false, canAccept: false)
           && CanCommitPlant(modelLoaded: false, hasGardenCatcher: false, canAccept: false, plantedCropMatches: false)
           && CanBeginPlant(modelLoaded: true, hasGardenCatcher: true, canAccept: true)
           && CanCommitPlant(modelLoaded: true, hasGardenCatcher: true, canAccept: true, plantedCropMatches: true)
           && !CanBeginPlant(modelLoaded: true, hasGardenCatcher: false, canAccept: false)
           && !CanBeginPlant(modelLoaded: true, hasGardenCatcher: true, canAccept: false)
           && !CanCommitPlant(modelLoaded: true, hasGardenCatcher: false, canAccept: false, plantedCropMatches: false)
           && !CanCommitPlant(modelLoaded: true, hasGardenCatcher: true, canAccept: false, plantedCropMatches: false)
           && !CanCommitPlant(modelLoaded: true, hasGardenCatcher: true, canAccept: true, plantedCropMatches: false)
           && CanCommitClear(modelLoaded: false, hasLandPlot: false)
           && CanCommitClear(modelLoaded: true, hasLandPlot: true)
           && !CanCommitClear(modelLoaded: true, hasLandPlot: false)
           && CanPublishLocalPlant(hasPlotId: true, hasPlantResult: true, returnedCropMatches: true)
           && !CanPublishLocalPlant(hasPlotId: false, hasPlantResult: true, returnedCropMatches: true)
           && !CanPublishLocalPlant(hasPlotId: true, hasPlantResult: false, returnedCropMatches: true)
           && !CanPublishLocalPlant(hasPlotId: true, hasPlantResult: true, returnedCropMatches: false);
}

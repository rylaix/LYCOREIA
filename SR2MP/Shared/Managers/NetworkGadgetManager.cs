using System.Collections;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.SceneManagement;
using SR2MP.Packets.Actor;
using SR2MP.Packets.Gadget;
using SR2MP.Packets.Loading;
using SR2MP.Shared.Utils;

namespace SR2MP.Shared.Managers;

internal static partial class NetworkGadgetManager
{
    internal static GadgetModel? CreateNetworkGadget(
        IdentifiableType type, ActorId actorId, SceneGroup scene, Vector3 position, Quaternion rotation,
        out GameObject gadget, Action<GadgetModel>? configure = null)
    {
        gadget = null!;

        var wasHandlingPacket = HandlingPacket;
        GadgetModel? model;
        HandlingPacket = true;
        try
        {
            model = GameState.CreateGadgetModel(type.Cast<GadgetDefinition>(), actorId, scene, position, false);

            if (model != null)
            {
                model.eulerRotation = rotation.eulerAngles;
                configure?.Invoke(model);

                if (NetworkSceneManager.IsSceneGroupLoaded(scene))
                    gadget = GadgetDirector.InstantiateGadgetFromModel(model);
            }
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        if (gadget)
            gadget.transform.SetPositionAndRotation(position, rotation);

        return model;
    }

    internal static GadgetModel? GetLinkedGadget(GadgetModel model)
    {
        if (model?.ident == null)
            return null;

        var definition = model.ident.Cast<GadgetDefinition>();
        if (!definition.BuyInPairs && !definition.LinkedDefinition && Math.Abs(definition.LinkedGadgetRange) <= 0.0001f)
            return null;

        var candidates = GameState.identifiables._entries
            .Where(x => x.value != null && x.value != model && x.value.ident == model.ident)
            .Select(x => x.value.Cast<GadgetModel>())
            .ToList();

        if (candidates.Count <= 1)
            return candidates.FirstOrDefault();

        var range = definition.LinkedGadgetRange;
        if (Math.Abs(range) <= 0.0001f)
            return candidates.FirstOrDefault();

        var position = model.GetPos();
        return candidates
            .Where(c => Vector3.Distance(c.GetPos(), position) <= range)
            .OrderBy(c => Vector3.Distance(c.GetPos(), position))
            .FirstOrDefault();
    }

    // Every GadgetModel.LinkDestroyer resolves its own partner off the gadget registry.
    // The proximity fallback below picks an arbitrary gadget of the same type once a second pair exists,
    // which for warp depots means picking one up deletes someone else's depot, ammo and all.
    internal static GadgetModel? ResolveLinkedGadget(GadgetModel gadget)
        => gadget.TryCast<TeleporterGadgetModel>(out var teleporter) ? teleporter.GetLinkedGadget()
            : gadget.TryCast<WarpDepotModel>(out var depot) ? depot.GetLinkedGadget()
            : gadget.TryCast<LinkedCannonModel>(out var cannon) ? cannon.GetLinkedGadget()
            : GetLinkedGadget(gadget);

    internal static AmmoModel? GetAmmoFromGadget(GadgetModel model)
    {
        if (model.TryCast(out WarpDepotModel? warp))
            return warp.ammo;

        if (model.TryCast(out LinkedCannonModel? cannon))
            return cannon.Ammo;

        return null;
    }

    internal static void EnsureGadgetLinked(GadgetModel? model)
    {
        if (model == null)
            return;

        if (model.TryCast(out WarpDepotModel? warpDepotModel))
            EnsureWarpDepotLinked(warpDepotModel);
    }

    // Patching TeleporterModel.RemoveDestination to sync the unlink causes a memory access violation.
    internal static void BroadcastLinkedPairDestroy(ActorId actorId)
    {
        try
        {
            if (!GameState.TryGetIdentifiableModel(actorId, out var model)
                || model == null
                || !model.TryCast<GadgetModel>(out var gadget)
                || !gadget.DestroysLinkedPairOnRemoval())
                return;

            var partner = ResolveLinkedGadget(gadget);

            if (partner == null || partner.actorId.Value == actorId.Value)
                return;
            
            // Murderer!!
            SrLogger.LogDebug($"Gadget {actorId.Value} takes its pair {partner.actorId.Value} down with it");

            Main.SendToAllOrServer(new ActorDestroyPacket { ActorId = partner.actorId });
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"Failed to broadcast linked pair destroy for {actorId.Value}: {ex.Message}");
        }
    }

    internal static void BroadcastGadgetLink(GadgetModel? model)
    {
        if (HandlingPacket || model == null) return;
        if (model.TryCast<TeleporterGadgetModel>(out _)) return;

        var partner = ResolveLinkedGadget(model);
        if (partner == null) return;

        if (!RecordGadgetLinkPair(model.actorId.Value, partner.actorId.Value))
            return;

        Main.SendToAllOrServer(new GadgetLinkingPacket
        {
            GadgetId = model.actorId.Value,
            PartnerId = partner.actorId.Value
        });
    }

    private static readonly Dictionary<long, long> PendingGadgetLinks = new();

    private static readonly Dictionary<long, long> GadgetLinkPartners = new();
    private static readonly Dictionary<(long First, long Second, string Sender), PendingAuthorizedGadgetLink>
        PendingAuthorizedGadgetLinks = new();
    private static bool authorizedGadgetLinkPumpRunning;
    private const float AuthorizedGadgetLinkTimeoutSeconds = 15f;
    private const int MaxPendingAuthorizedGadgetLinks = 128;

    private readonly record struct PendingAuthorizedGadgetLink(
        long GadgetId,
        long PartnerId,
        string SenderId,
        float ExpiresAt);

    private enum GadgetLinkValidation
    {
        WaitingForActors,
        Rejected,
        Accepted
    }

    internal static bool TryGetLinkedGadgetId(long gadgetId, out long partnerId)
        => GadgetLinkPartners.TryGetValue(gadgetId, out partnerId);

    private static bool RecordGadgetLinkPair(long gadgetId, long partnerId)
    {
        if (!GadgetLinkPairPolicy.TryRecordPair(
                GadgetLinkPartners,
                gadgetId,
                partnerId,
                out var detachedGadgetPartner,
                out var detachedPartnerPartner))
            return false;

        if (detachedGadgetPartner != 0 && detachedGadgetPartner != partnerId)
            NetworkAmmoManager.OnGadgetLinkResolved(detachedGadgetPartner, detachedGadgetPartner);

        if (detachedPartnerPartner != 0 && detachedPartnerPartner != gadgetId
            && detachedPartnerPartner != detachedGadgetPartner)
            NetworkAmmoManager.OnGadgetLinkResolved(detachedPartnerPartner, detachedPartnerPartner);

        NetworkAmmoManager.OnGadgetLinkResolved(gadgetId, partnerId);
        return true;
    }

    // Both are keyed by actor id and nothing else prunes them,
    // so a destroyed gadget would keep handing NetworkAmmoManager a partner that no longer exists
    internal static void ForgetGadgetLink(ActorId actorId)
    {
        var id = actorId.Value;

        CannonFireTimes.Remove(id);

        if (GadgetLinkPartners.Remove(id, out var partnerId)
            && GadgetLinkPartners.TryGetValue(partnerId, out var backReference)
            && backReference == id)
        {
            GadgetLinkPartners.Remove(partnerId);
            NetworkAmmoManager.OnGadgetLinkResolved(partnerId, partnerId);
        }

        PendingGadgetLinks.Remove(id);

        foreach (var (pendingGadgetId, pendingPartnerId) in PendingGadgetLinks.ToArray())
        {
            if (pendingPartnerId == id)
                PendingGadgetLinks.Remove(pendingGadgetId);
        }
    }

    internal static bool ApplyGadgetLink(long gadgetId, long partnerId)
    {
        if (!RecordGadgetLinkPair(gadgetId, partnerId))
            return false;

        foreach (var (pendingGadgetId, pendingPartnerId) in PendingGadgetLinks.ToArray())
        {
            if (!GadgetLinkPairPolicy.IsCurrentPair(GadgetLinkPartners, pendingGadgetId, pendingPartnerId))
                PendingGadgetLinks.Remove(pendingGadgetId);
        }

        if (TryGetGadgetModel(gadgetId, out var gadgetModel) && TryGetGadgetModel(partnerId, out var partnerModel))
        {
            PendingGadgetLinks.Remove(gadgetId);
            PendingGadgetLinks.Remove(partnerId);
            ApplyResolvedLink(gadgetModel!, partnerModel!);
            return true;
        }

        if (gadgetId < partnerId)
            PendingGadgetLinks[gadgetId] = partnerId;
        else
            PendingGadgetLinks[partnerId] = gadgetId;

        return true;
    }

    /// <summary>
    /// Validates both endpoints before a guest link mutates the host cache. Actor
    /// spawn and link packets can arrive on different channels, so unknown endpoints
    /// are deferred and revalidated after their canonical spawn/owner records arrive.
    /// </summary>
    internal static bool HandleGuestGadgetLink(long gadgetId, long partnerId, string senderId)
    {
        if (!Main.Server.IsRunning || string.IsNullOrWhiteSpace(senderId))
            return false;

        var validation = ValidateGuestGadgetLink(gadgetId, partnerId, senderId);
        if (validation == GadgetLinkValidation.Rejected)
        {
            SrLogger.LogWarning(
                $"Rejected GadgetLinking for {gadgetId}<->{partnerId} from {senderId}: " +
                "both endpoints must exist, be owned by the sender, and form a compatible pair.");
            return false;
        }

        if (validation == GadgetLinkValidation.Accepted)
        {
            CommitGuestGadgetLink(gadgetId, partnerId);
            return false;
        }

        var key = (Math.Min(gadgetId, partnerId), Math.Max(gadgetId, partnerId), senderId);
        if (gadgetId == 0 || partnerId == 0 || gadgetId == partnerId ||
            PendingAuthorizedGadgetLinks.Count >= MaxPendingAuthorizedGadgetLinks)
            return false;

        if (!PendingAuthorizedGadgetLinks.ContainsKey(key))
        {
            PendingAuthorizedGadgetLinks[key] = new PendingAuthorizedGadgetLink(
                gadgetId,
                partnerId,
                senderId,
                UnityEngine.Time.unscaledTime + AuthorizedGadgetLinkTimeoutSeconds);
        }

        StartAuthorizedGadgetLinkPump();
        return false;
    }

    private static GadgetLinkValidation ValidateGuestGadgetLink(long gadgetId, long partnerId, string senderId)
    {
        if (gadgetId == 0 || partnerId == 0 || gadgetId == partnerId ||
            !PlayerManager.CheckPlayerExists(senderId))
            return GadgetLinkValidation.Rejected;

        var hasGadget = TryGetGadgetModel(gadgetId, out var gadgetModel);
        var hasPartner = TryGetGadgetModel(partnerId, out var partnerModel);
        if (!hasGadget || !hasPartner)
        {
            if (HasKnownForeignOwner(gadgetId, senderId) || HasKnownForeignOwner(partnerId, senderId))
                return GadgetLinkValidation.Rejected;
            return GadgetLinkValidation.WaitingForActors;
        }

        if (!ActorManager.OwnershipByActorId.TryGetValue(gadgetId, out var gadgetOwner) ||
            !ActorManager.OwnershipByActorId.TryGetValue(partnerId, out var partnerOwner))
            return GadgetLinkValidation.WaitingForActors;

        var sourceKind = GetGadgetPairKind(gadgetModel!);
        var partnerKind = GetGadgetPairKind(partnerModel!);
        var sameDefinition = gadgetModel!.ident == partnerModel!.ident;
        var compatible = GadgetLinkAuthorizationPolicy.AreCompatible(sourceKind, partnerKind, sameDefinition);
        var accepted = GadgetLinkAuthorizationPolicy.CanAuthorize(
            gadgetId,
            partnerId,
            authenticated: true,
            sourceExists: true,
            sourceOwned: string.Equals(gadgetOwner, senderId, StringComparison.Ordinal),
            partnerExists: true,
            partnerOwned: string.Equals(partnerOwner, senderId, StringComparison.Ordinal),
            compatiblePair: compatible);

        return accepted ? GadgetLinkValidation.Accepted : GadgetLinkValidation.Rejected;
    }

    private static bool HasKnownForeignOwner(long actorId, string senderId)
        => ActorManager.OwnershipByActorId.TryGetValue(actorId, out var ownerId) &&
           !string.Equals(ownerId, senderId, StringComparison.Ordinal);

    private static GadgetPairKind GetGadgetPairKind(GadgetModel model)
    {
        if (model.TryCast<WarpDepotModel>(out _))
            return GadgetPairKind.WarpDepot;
        if (model.TryCast<LinkedCannonModel>(out _))
            return GadgetPairKind.LinkedCannon;
        if (model.TryCast<TeleporterGadgetModel>(out _) ||
            model.TryCast<DroneStationGadgetModel>(out _) || !model.ident)
            return GadgetPairKind.Unsupported;

        var definition = model.ident!.TryCast<GadgetDefinition>();
        if (definition == null ||
            (!definition.BuyInPairs && !definition.LinkedDefinition &&
             Math.Abs(definition.LinkedGadgetRange) <= 0.0001f))
            return GadgetPairKind.Unsupported;

        return GadgetPairKind.GenericLinked;
    }

    private static void CommitGuestGadgetLink(long gadgetId, long partnerId)
    {
        if (!ApplyGadgetLink(gadgetId, partnerId))
            return;

        Main.Server.SendToAll(new GadgetLinkingPacket
        {
            GadgetId = gadgetId,
            PartnerId = partnerId
        });
    }

    private static void StartAuthorizedGadgetLinkPump()
    {
        if (authorizedGadgetLinkPumpRunning || PendingAuthorizedGadgetLinks.Count == 0)
            return;

        authorizedGadgetLinkPumpRunning = true;
        StartCoroutine(PumpAuthorizedGadgetLinks());
    }

    private static IEnumerator PumpAuthorizedGadgetLinks()
    {
        while (PendingAuthorizedGadgetLinks.Count > 0)
        {
            foreach (var (key, pending) in PendingAuthorizedGadgetLinks.ToArray())
            {
                if (!Main.Server.IsRunning ||
                    UnityEngine.Time.unscaledTime >= pending.ExpiresAt ||
                    !PlayerManager.CheckPlayerExists(pending.SenderId))
                {
                    PendingAuthorizedGadgetLinks.Remove(key);
                    continue;
                }

                var validation = ValidateGuestGadgetLink(
                    pending.GadgetId, pending.PartnerId, pending.SenderId);
                if (validation == GadgetLinkValidation.WaitingForActors)
                    continue;

                PendingAuthorizedGadgetLinks.Remove(key);
                if (validation == GadgetLinkValidation.Accepted)
                    CommitGuestGadgetLink(pending.GadgetId, pending.PartnerId);
                else
                    SrLogger.LogWarning(
                        $"Dropped deferred GadgetLinking {pending.GadgetId}<->{pending.PartnerId}: " +
                        "canonical actor ownership or pair type did not validate.");
            }

            if (PendingAuthorizedGadgetLinks.Count > 0)
                yield return new WaitForSecondsRealtime(0.25f);
        }

        authorizedGadgetLinkPumpRunning = false;
    }

    internal static void CheckPendingGadgetLink(GadgetModel? model)
    {
        if (model == null) return;

        ApplyPendingCannonFireTime(model);
        if (PendingGadgetLinks.Count == 0) return;

        var id = model.actorId.Value;

        foreach (var (gadgetId, partnerId) in PendingGadgetLinks.ToArray())
        {
            if (!GadgetLinkPairPolicy.IsCurrentPair(GadgetLinkPartners, gadgetId, partnerId))
            {
                PendingGadgetLinks.Remove(gadgetId);
                continue;
            }

            if (gadgetId != id && partnerId != id)
                continue;

            if (!TryGetGadgetModel(gadgetId, out var gadgetModel) || !TryGetGadgetModel(partnerId, out var partnerModel))
                continue;

            PendingGadgetLinks.Remove(gadgetId);
            ApplyResolvedLink(gadgetModel!, partnerModel!);
        }
    }

    private static bool TryGetGadgetModel(long id, out GadgetModel? model)
    {
        model = null;
        if (!GameState.identifiables.TryGetValue(new ActorId(id), out var identModel))
            return false;

        model = identModel?.TryCast<GadgetModel>();
        return model != null;
    }

    private static void ApplyResolvedLink(GadgetModel gadgetModel, GadgetModel partnerModel)
    {
        if (gadgetModel.TryCast(out WarpDepotModel? warpA) && partnerModel.TryCast(out WarpDepotModel? warpB))
            LinkWarpDepotModels(warpA, warpB);
    }

    internal static List<InitialGadgetLinksPacket.Link> GetAllGadgetLinks()
    {
        var links = new List<InitialGadgetLinksPacket.Link>();
        var seen = new HashSet<long>();

        foreach (var model in GameState.AllGadgets())
        {
            if (!model.TryCast<GadgetModel>(out var gadget) || seen.Contains(gadget.actorId.Value))
                continue;

            if (gadget.TryCast<TeleporterGadgetModel>(out _))
                continue;

            var partner = ResolveLinkedGadget(gadget);
            if (partner == null)
                continue;

            seen.Add(gadget.actorId.Value);
            seen.Add(partner.actorId.Value);

            links.Add(new InitialGadgetLinksPacket.Link
            {
                GadgetId = gadget.actorId.Value,
                PartnerId = partner.actorId.Value
            });
        }

        return links;
    }

    private static void RelinkAllGadgets()
    {
        foreach (var model in GameState.AllGadgets())
        {
            if (model.TryCast<GadgetModel>(out var gadget))
                EnsureGadgetLinked(gadget);
        }
    }

    // Todo: change this if you hate your life
    private static readonly byte[] RelinkRetryFrameDelays = { 2, 10, 30, 60 };

    internal static IEnumerator RelinkGadgets()
    {
        foreach (var delay in RelinkRetryFrameDelays)
        {
            yield return new WaitFrames(delay);
            RelinkAllGadgets();
        }
    }
}

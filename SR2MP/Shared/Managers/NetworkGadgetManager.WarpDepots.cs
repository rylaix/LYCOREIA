using Il2CppMonomiPark.SlimeRancher.DataModel;

namespace SR2MP.Shared.Managers;

internal static partial class NetworkGadgetManager
{
    private static void EnsureWarpDepotLinked(WarpDepotModel? warpDepotModel)
    {
        if (warpDepotModel == null || warpDepotModel.linkedModel != null)
            return;

        try
        {
            var partnerModel = warpDepotModel.GetLinkedGadget()?.TryCast<WarpDepotModel>();
            if (partnerModel != null)
                LinkWarpDepotModels(warpDepotModel, partnerModel);
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"Failed to link warp depot {warpDepotModel.actorId.Value}: {ex.Message}");
        }
    }

    private static void LinkWarpDepotModels(WarpDepotModel a, WarpDepotModel b)
    {
        if (a == null || b == null || a.actorId.Value == b.actorId.Value)
            return;

        // The host's pair mapping is authoritative; repair both directions even if a
        // guest loaded a different stale link from its independent local save.
        DetachReciprocalWarpDepotLink(a);
        DetachReciprocalWarpDepotLink(b);
        a.linkedModel = b;
        b.linkedModel = a;
    }

    private static void DetachReciprocalWarpDepotLink(WarpDepotModel depot)
    {
        var previousPartner = depot.linkedModel;
        depot.linkedModel = null;

        if (previousPartner != null && previousPartner.actorId.Value != depot.actorId.Value &&
            previousPartner.linkedModel != null && previousPartner.linkedModel.actorId.Value == depot.actorId.Value)
            previousPartner.linkedModel = null;
    }
}

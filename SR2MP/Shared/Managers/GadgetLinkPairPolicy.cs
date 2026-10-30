using System.Collections.Generic;
using SR2MP.Packets.Gadget;
using SR2MP.Packets.Loading;
using SR2MP.Packets.Utils;

namespace SR2MP.Shared.Managers;

/// <summary>
/// Keeps the local actor-id cache for paired gadgets reciprocal. This stays pure so
/// pair replacement and malformed packet behavior can be checked without game models.
/// </summary>
internal static class GadgetLinkPairPolicy
{
    internal static bool TryRecordPair(
        IDictionary<long, long> links,
        long gadgetId,
        long partnerId,
        out long detachedGadgetPartner,
        out long detachedPartnerPartner)
    {
        detachedGadgetPartner = 0;
        detachedPartnerPartner = 0;

        if (gadgetId == 0 || partnerId == 0 || gadgetId == partnerId)
            return false;

        if (IsCurrentPair(links, gadgetId, partnerId))
            return true;

        detachedGadgetPartner = Detach(links, gadgetId);
        detachedPartnerPartner = Detach(links, partnerId);

        links[gadgetId] = partnerId;
        links[partnerId] = gadgetId;
        return true;
    }

    internal static bool IsCurrentPair(IDictionary<long, long> links, long gadgetId, long partnerId)
        => links.TryGetValue(gadgetId, out var actualPartner)
           && actualPartner == partnerId
           && links.TryGetValue(partnerId, out var backReference)
           && backReference == gadgetId;

    private static long Detach(IDictionary<long, long> links, long actorId)
    {
        if (!links.TryGetValue(actorId, out var previousPartner))
            return 0;

        links.Remove(actorId);
        if (!links.TryGetValue(previousPartner, out var backReference) || backReference != actorId)
            return 0;

        links.Remove(previousPartner);
        return previousPartner;
    }

    internal static bool VerifyRules()
    {
        var links = new Dictionary<long, long>();
        if (!TryRecordPair(links, 1, 2, out _, out _)
            || !TryRecordPair(links, 3, 4, out _, out _)
            || !TryRecordPair(links, 1, 3, out var detachedA, out var detachedB))
            return false;

        if (detachedA != 2 || detachedB != 4
            || links.Count != 2
            || !IsCurrentPair(links, 1, 3)
            || links.ContainsKey(2)
            || links.ContainsKey(4))
            return false;

        var beforeInvalid = new Dictionary<long, long>(links);
        if (TryRecordPair(links, 0, 5, out _, out _)
            || TryRecordPair(links, 6, 6, out _, out _)
            || links.Count != beforeInvalid.Count)
            return false;

        return IsCurrentPair(links, 1, 3)
               && !IsCurrentPair(links, 1, 2)
#if SR2MP_TEST
               && GadgetLinkAuthorizationPolicy.VerifyRules()
#endif
               && new GadgetLinkingPacket().Reliability == PacketReliability.ReliableOrdered
               && new InitialGadgetLinksPacket().Reliability == PacketReliability.ReliableOrdered
               && new CannonFirePacket().Reliability == PacketReliability.ReliableOrdered;
    }
}

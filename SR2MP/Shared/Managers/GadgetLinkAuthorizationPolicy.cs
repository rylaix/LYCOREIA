namespace SR2MP.Shared.Managers;

internal enum GadgetPairKind
{
    Unsupported,
    WarpDepot,
    LinkedCannon,
    GenericLinked
}

/// <summary>Pure owner and pair-type gates for client-authored gadget links.</summary>
internal static class GadgetLinkAuthorizationPolicy
{
    internal static bool CanAuthorize(long gadgetId, long partnerId, bool authenticated,
        bool sourceExists, bool sourceOwned, bool partnerExists, bool partnerOwned, bool compatiblePair)
        => gadgetId != 0 && partnerId != 0 && gadgetId != partnerId && authenticated &&
           sourceExists && sourceOwned && partnerExists && partnerOwned && compatiblePair;

    internal static bool AreCompatible(GadgetPairKind source, GadgetPairKind partner, bool sameDefinition)
    {
        if (source == GadgetPairKind.WarpDepot || partner == GadgetPairKind.WarpDepot)
            return source == GadgetPairKind.WarpDepot && partner == GadgetPairKind.WarpDepot;
        if (source == GadgetPairKind.LinkedCannon || partner == GadgetPairKind.LinkedCannon)
            return source == GadgetPairKind.LinkedCannon && partner == GadgetPairKind.LinkedCannon;

        return source == GadgetPairKind.GenericLinked && partner == GadgetPairKind.GenericLinked && sameDefinition;
    }

#if SR2MP_TEST
    internal static bool VerifyRules()
        => CanAuthorize(1, 2, true, true, true, true, true, true)
           && !CanAuthorize(1, 2, false, true, true, true, true, true)
           && !CanAuthorize(1, 2, true, true, false, true, true, true)
           && !CanAuthorize(1, 2, true, true, true, true, false, true)
           && !CanAuthorize(1, 2, true, true, true, true, true, false)
           && !CanAuthorize(1, 1, true, true, true, true, true, true)
           && AreCompatible(GadgetPairKind.WarpDepot, GadgetPairKind.WarpDepot, false)
           && AreCompatible(GadgetPairKind.LinkedCannon, GadgetPairKind.LinkedCannon, false)
           && AreCompatible(GadgetPairKind.GenericLinked, GadgetPairKind.GenericLinked, true)
           && !AreCompatible(GadgetPairKind.GenericLinked, GadgetPairKind.GenericLinked, false)
           && !AreCompatible(GadgetPairKind.WarpDepot, GadgetPairKind.LinkedCannon, true)
           && !AreCompatible(GadgetPairKind.Unsupported, GadgetPairKind.Unsupported, true);
#endif
}

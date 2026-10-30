using SR2MP.Packets.Gadget;
using SR2MP.Packets.Loading;
using SR2MP.Packets.Utils;

namespace SR2MP.Shared.Managers;

/// <summary>
/// Cached teleporter edges survive temporary scene/model unloads; unlink tombstones
/// suppress removed edges from snapshots until a new authoritative link is recorded.
/// A destination does not need to also be a source node.
/// </summary>
internal static class TeleporterLinkSnapshotPolicy
{
    internal static bool IsValidCachedLink(string? sourceNodeId, string? destinationNodeId)
        => !string.IsNullOrWhiteSpace(sourceNodeId)
           && !string.IsNullOrWhiteSpace(destinationNodeId)
           && !string.Equals(sourceNodeId, destinationNodeId, StringComparison.Ordinal);

    internal static bool CanAcceptMutation(
        bool authenticatedClient,
        bool sourceExists,
        bool sourceIsTeleporterGadget,
        bool sourceOwnedBySender,
        bool destinationExists,
        bool destinationIsTeleporterNode,
        bool destinationAccessibleBySender)
        => authenticatedClient && sourceExists && sourceIsTeleporterGadget && sourceOwnedBySender &&
           destinationExists && destinationIsTeleporterNode && destinationAccessibleBySender;

    internal static bool ShouldIncludeSnapshotLink(
        string? sourceNodeId,
        string? destinationNodeId,
        bool explicitlyUnlinked)
        => IsValidCachedLink(sourceNodeId, destinationNodeId) && !explicitlyUnlinked;

    internal static void RemoveLink(
        List<(string Source, string Destination)> links,
        string sourceNodeId,
        string destinationNodeId)
        => links.RemoveAll(link => link.Source == sourceNodeId && link.Destination == destinationNodeId);

    internal static void RemoveLink(
        List<(string Source, string Destination, byte SceneGroup)> links,
        string sourceNodeId,
        string destinationNodeId)
        => links.RemoveAll(link => link.Source == sourceNodeId && link.Destination == destinationNodeId);

    internal static bool VerifyRules()
    {
        var known = new List<(string Source, string Destination, byte SceneGroup)>
        {
            ("source", "removed", 1),
            ("source", "kept", 2)
        };
        var cached = new List<(string Source, string Destination)>
        {
            ("source", "removed"),
            ("source", "kept")
        };
        RemoveLink(known, "source", "removed");
        RemoveLink(cached, "source", "removed");

        var passed = IsValidCachedLink("source", "destination")
               && !IsValidCachedLink(null, "destination")
               && !IsValidCachedLink("source", " ")
               && !IsValidCachedLink("node", "node")
               && CanAcceptMutation(true, true, true, true, true, true, true)
               && !CanAcceptMutation(false, true, true, true, true, true, true)
               && !CanAcceptMutation(true, false, true, true, true, true, true)
               && !CanAcceptMutation(true, true, false, true, true, true, true)
               && !CanAcceptMutation(true, true, true, false, true, true, true)
               && !CanAcceptMutation(true, true, true, true, false, true, true)
               && !CanAcceptMutation(true, true, true, true, true, false, true)
               && !CanAcceptMutation(true, true, true, true, true, true, false)
               && ShouldIncludeSnapshotLink("source", "kept", false)
               && !ShouldIncludeSnapshotLink("source", "removed", true)
               && known.Count == 1 && known[0].Destination == "kept"
               && cached.Count == 1 && cached[0].Destination == "kept"
               && new TeleporterLinkPacket().Reliability == PacketReliability.ReliableOrdered
               && new TeleporterUnlinkPacket().Reliability == PacketReliability.ReliableOrdered
               && new TeleporterUnlinkPacket().Type == PacketType.TeleporterUnlink
               && new InitialTeleporterLinksPacket().Reliability == PacketReliability.ReliableOrdered;
#if SR2MP_TEST
        passed &= TeleporterUnlinkPacket.VerifySerializationRoundTrip();
#endif
        return passed;
    }
}

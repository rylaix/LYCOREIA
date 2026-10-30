using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Gadget;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Gadget;

[PacketHandler((byte)PacketType.TeleporterUnlink)]
internal sealed class TeleporterUnlinkHandler : BasePacketHandler<TeleporterUnlinkPacket>
{
    protected override bool Handle(TeleporterUnlinkPacket packet, IPEndPoint? clientEndpoint)
    {
        if (!RequireAuthenticatedClient(clientEndpoint) ||
            !TeleporterLinkSnapshotPolicy.IsValidCachedLink(packet.SourceNodeId, packet.DestinationNodeId))
            return false;

        if (Main.Server.IsRunning &&
            !NetworkGadgetManager.IsAuthorizedTeleporterMutation(
                packet.SourceNodeId, packet.DestinationNodeId, clientEndpoint))
        {
            SrLogger.LogWarning(
                $"Rejected teleporter unlink {packet.SourceNodeId} -/-> {packet.DestinationNodeId}: " +
                "source ownership or destination access check failed.");
            if (clientEndpoint != null)
                NetworkGadgetManager.SendTeleporterLinkSnapshot(clientEndpoint);
            return false;
        }

        return NetworkGadgetManager.ApplyTeleporterUnlink(
            packet.SourceNodeId, packet.DestinationNodeId);
    }
}

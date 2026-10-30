using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Gadget;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Gadget;

[PacketHandler((byte)PacketType.TeleporterLink)]
internal sealed class TeleporterLinkHandler : BasePacketHandler<TeleporterLinkPacket>
{
    protected override bool Handle(TeleporterLinkPacket packet, IPEndPoint? clientEp)
    {
        if (!RequireAuthenticatedClient(clientEp))
            return false;

        if (!TeleporterLinkSnapshotPolicy.IsValidCachedLink(packet.SourceNodeId, packet.DestinationNodeId))
            return false;

        if (Main.Server.IsRunning &&
            !NetworkGadgetManager.IsAuthorizedTeleporterMutation(
                packet.SourceNodeId, packet.DestinationNodeId, clientEp))
        {
            SrLogger.LogWarning(
                $"Rejected teleporter link {packet.SourceNodeId} -> {packet.DestinationNodeId}: " +
                "source ownership or destination access check failed.");
            if (clientEp != null)
                NetworkGadgetManager.SendTeleporterLinkSnapshot(clientEp);
            return false;
        }

        StartCoroutine(NetworkGadgetManager.ApplyTeleporterLink(
            packet.SourceNodeId, packet.DestinationNodeId, packet.DestinationSceneGroup));
        
        return true;
    }
}

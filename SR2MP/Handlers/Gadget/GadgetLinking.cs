using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Gadget;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Gadget;

[PacketHandler((byte)PacketType.GadgetLinking)]
internal sealed class GadgetLinkingHandler : BasePacketHandler<GadgetLinkingPacket>
{
    protected override bool Handle(GadgetLinkingPacket packet, IPEndPoint? clientEndpoint)
    {
        if (!Main.Server.IsRunning)
            return NetworkGadgetManager.ApplyGadgetLink(packet.GadgetId, packet.PartnerId);

        if (!RequireAuthenticatedClient(clientEndpoint) || clientEndpoint == null ||
            !Main.Server.ClientManager.TryGetClient(clientEndpoint, out var sender) || sender == null)
        {
            SrLogger.LogWarning(
                $"Rejected GadgetLinking for gadget {packet.GadgetId}: sender is not authenticated.");
            return false;
        }

        return NetworkGadgetManager.HandleGuestGadgetLink(packet.GadgetId, packet.PartnerId, sender.PlayerId);
    }
}

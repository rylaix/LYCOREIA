using System.Net;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Gadget;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Gadget;

[PacketHandler((byte)PacketType.CannonFire)]
internal sealed class CannonFireHandler : BasePacketHandler<CannonFirePacket>
{
    protected override bool Handle(CannonFirePacket packet, IPEndPoint? clientEndpoint)
    {
        if (Main.Server.IsRunning &&
            (!RequireAuthenticatedClient(clientEndpoint) ||
             !NetworkActorManager.IsCurrentActorOwner(new ActorId(packet.GadgetId), clientEndpoint)))
        {
            SrLogger.LogWarning(
                $"Rejected CannonFire for gadget {packet.GadgetId} from a non-owner or unknown owner.");
            return false;
        }

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            NetworkGadgetManager.ApplyCannonFireTime(packet.GadgetId, packet.NextFireTime);
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        return true;
    }
}

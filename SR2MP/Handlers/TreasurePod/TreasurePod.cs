using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.TreasurePod;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.TreasurePod;

[PacketHandler((byte)PacketType.TreasurePod)]
internal sealed class TreasurePodHandler : BasePacketHandler<TreasurePodPacket>
{
    protected override bool Handle(TreasurePodPacket packet, IPEndPoint? clientEp)
    {
        if (!RequireAuthenticatedClient(clientEp))
            return false;

        if (Main.Server.IsRunning)
        {
            if (packet.IsRejectedResponse)
            {
                if (packet.ClientActivated)
                    NetworkTreasurePodManager.HandleGuestActivationDeclined(packet.ID, clientEp);
                return false;
            }

            if (packet.ClientActivated)
                return NetworkTreasurePodManager.HandleGuestActivationReport(packet.ID, packet.Rewards, clientEp);

            return NetworkTreasurePodManager.HandleGuestOpenRequest(packet.ID, packet.RequestId, clientEp);
        }

        if (packet.IsRejectedResponse)
        {
            NetworkTreasurePodManager.HandleHostOpenRejection(packet.ID, packet.RequestId);
            return false;
        }

        if (packet.ClientActivated)
        {
            NetworkTreasurePodManager.HandleHostActivationGrant(packet.ID, packet.RequestId);
            return false;
        }

        NetworkTreasurePodManager.ShowRewardPopups(packet.ID, packet.Rewards);
        return NetworkTreasurePodManager.ApplyState(
            packet.ID,
            Il2Cpp.TreasurePod.State.OPEN,
            packet.Rewards);
    }
}

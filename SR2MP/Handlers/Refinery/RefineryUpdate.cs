using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Utils;
using SR2MP.Packets.World;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Refinery;

[PacketHandler((byte)PacketType.RefineryUpdate)]
internal sealed class RefineryUpdateHandler : BasePacketHandler<RefineryUpdatePacket>
{
    protected override bool Handle(RefineryUpdatePacket packet, IPEndPoint? clientEp)
    {
        if (!IsServerSide)
            return NetworkRefineryManager.ReceiveUpdate(packet);

        if (clientEp == null ||
            !Main.Server.ClientManager.TryGetClient(clientEp, out var sender) || sender == null)
            return false;

        return NetworkRefineryManager.ReceiveRequest(packet, sender.PlayerId, clientEp);
    }
}

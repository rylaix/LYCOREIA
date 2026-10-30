using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Slime.Sloomber;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Slime.Sloomber;

[PacketHandler((byte)PacketType.SloomberYawn)]
internal sealed class SloomberYawnHandler : BasePacketHandler<SloomberYawnPacket>
{
    protected override bool Handle(SloomberYawnPacket packet, IPEndPoint? clientEp)
    {
        if (packet.ActorId.Value <= 0 || packet.Revision < 0)
            return false;

        if (!NetworkActorManager.IsCurrentActorOwner(packet.ActorId, clientEp))
        {
            if (IsServerSide)
                NetworkSlimeBehaviorManager.TryDeferOwnerRequest(packet, clientEp);
            return false;
        }

        if (IsServerSide)
            return NetworkSlimeBehaviorManager.AcceptClientPacket(packet);

        if (packet.Revision <= 0)
            return false;

        NetworkSlimeBehaviorManager.Receive(packet);
        return false;
    }
}

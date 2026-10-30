using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Slime.Tangle;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Slime.Tangle;

[PacketHandler((byte)PacketType.TangleVineGrapple)]
internal sealed class TangleVineGrappleHandler : BasePacketHandler<TangleVineGrapplePacket>
{
    protected override bool Handle(TangleVineGrapplePacket packet, IPEndPoint? clientEp)
    {
        if (packet.TangleActorId.Value <= 0 || packet.TargetActorId.Value < 0 || packet.Revision < 0 ||
            (packet.Active ? packet.TargetActorId.Value == 0 : packet.TargetActorId.Value != 0))
            return false;

        if (!NetworkActorManager.IsCurrentActorOwner(packet.TangleActorId, clientEp))
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

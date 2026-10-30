using System.Net;
using Il2CppMonomiPark.SlimeRancher.Slime.Dervish;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Slime.Dervish;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Slime.Dervish;

[PacketHandler((byte)PacketType.DervishCyclone)]
internal sealed class DervishCycloneHandler : BasePacketHandler<DervishCyclonePacket>
{
    protected override bool Handle(DervishCyclonePacket packet, IPEndPoint? clientEp)
    {
        if (!packet.IsValid)
            return false;

        if (!NetworkActorManager.IsCurrentActorOwner(packet.ActorId, clientEp))
        {
            if (IsServerSide)
                NetworkSlimeBehaviorManager.TryDeferOwnerRequest(packet, clientEp);
            return false;
        }

        if (IsServerSide)
            return NetworkSlimeBehaviorManager.AcceptClientPacket(packet);

        return packet.Revision > 0 && NetworkSlimeBehaviorManager.Receive(packet);
    }
}

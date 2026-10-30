using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Slime.Stalk;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Slime.Stalk;

[PacketHandler((byte)PacketType.StalkConsumableTarget)]
internal sealed class StalkConsumableTargetHandler : BasePacketHandler<StalkConsumableTargetPacket>
{
    protected override bool Handle(StalkConsumableTargetPacket packet, IPEndPoint? clientEndpoint)
    {
        if (packet.SlimeActorId.Value <= 0 || packet.TargetActorId.Value < 0 || packet.Revision < 0)
            return false;

        if (!NetworkActorManager.IsCurrentActorOwner(packet.SlimeActorId, clientEndpoint))
        {
            if (IsServerSide)
                NetworkSlimeBehaviorManager.TryDeferOwnerRequest(packet, clientEndpoint);
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

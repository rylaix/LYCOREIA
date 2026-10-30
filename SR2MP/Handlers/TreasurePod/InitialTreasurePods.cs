using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.TreasurePod;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.TreasurePod;

[PacketHandler((byte)PacketType.InitialTreasurePods, HandlerType.Client)]
internal sealed class InitialTreasurePodsHandler : BasePacketHandler<InitialTreasurePodsPacket>
{
    protected override bool Handle(InitialTreasurePodsPacket packet, IPEndPoint? _)
    {
        foreach (var (podId, podState) in packet.TreasurePods)
        {
            packet.RewardsByPod.TryGetValue(podId, out var rewards);
            NetworkTreasurePodManager.ApplyState(podId, podState, rewards);
        }

        return false;
    }
}

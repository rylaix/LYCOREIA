#if SR2MP_TEST
using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Actor;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Utils;

namespace SR2MP.Handlers.Actor;

[PacketHandler((byte)PacketType.TestCaptureTarget, HandlerType.Client)]
internal sealed class TestCaptureTargetHandler : BasePacketHandler<TestCaptureTargetPacket>
{
    protected override bool Handle(TestCaptureTargetPacket packet, IPEndPoint? _)
    {
        if (packet.ActorId <= 0 || packet.ActorId >= GlobalVariables.ActorIdOffset)
        {
            SrLogger.LogWarning($"[SR2MP_TEST_BOT] ignored invalid capture target actor={packet.ActorId:X}");
            return false;
        }

        TestGameplayBot.ReceiveCaptureTarget(packet.ActorId);
        return false;
    }
}
#endif

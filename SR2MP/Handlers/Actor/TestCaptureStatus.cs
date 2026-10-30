#if SR2MP_TEST
using System.Net;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Actor;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Utils;

namespace SR2MP.Handlers.Actor;

[PacketHandler((byte)PacketType.TestCaptureStatusRequest, HandlerType.Server)]
internal sealed class TestCaptureStatusRequestHandler : BasePacketHandler<TestCaptureStatusRequestPacket>
{
    protected override bool Handle(TestCaptureStatusRequestPacket packet, IPEndPoint? clientEndpoint)
    {
        if (clientEndpoint == null || packet.ProbeId == Guid.Empty || packet.QueryId <= 0 || packet.ActorId == 0)
            return false;

        var actorId = new ActorId(packet.ActorId);
        var modelPresent = GameState.TryGetIdentifiableModel(actorId, out _);
        var actorPresent = ActorManager.Actors.ContainsKey(packet.ActorId);

        Main.Server.SendToClient(new TestCaptureStatusResultPacket
        {
            ProbeId = packet.ProbeId,
            QueryId = packet.QueryId,
            ActorId = packet.ActorId,
            HostActorPresent = actorPresent,
            HostModelPresent = modelPresent
        }, clientEndpoint);
        return false;
    }
}

[PacketHandler((byte)PacketType.TestCaptureStatusResult, HandlerType.Client)]
internal sealed class TestCaptureStatusResultHandler : BasePacketHandler<TestCaptureStatusResultPacket>
{
    protected override bool Handle(TestCaptureStatusResultPacket packet, IPEndPoint? _)
    {
        TestGameplayBot.ReceiveCaptureStatus(packet);
        return false;
    }
}
#endif

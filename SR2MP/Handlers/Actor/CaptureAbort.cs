using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Actor;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;
using SR2MP.Shared.Utils;

namespace SR2MP.Handlers.Actor;

[PacketHandler((byte)PacketType.CaptureAbort, HandlerType.Server)]
internal sealed class CaptureAbortHandler : BasePacketHandler<CaptureAbortPacket>
{
    protected override bool Handle(CaptureAbortPacket packet, IPEndPoint? clientEndpoint)
    {
        if (clientEndpoint == null ||
            !Main.Server.ClientManager.TryGetClient(clientEndpoint, out var sender) || sender == null)
            return false;

        if (!NetworkActorCaptureManager.AbortServerCapture(
                sender.PlayerId, packet.ActorId.Value, packet.CaptureRequestId))
        {
#if SR2MP_DEEPLOG || SR2MP_TEST
            ActorDiag.Log("capture-abort-ignored", packet.ActorId.Value,
                $"requester={sender.PlayerId}; request={packet.CaptureRequestId}", perActorMilliseconds: 1000);
#endif
#if SR2MP_TEST
            if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") == "1")
                SrLogger.LogWarning(
                    $"[SR2MP_TEST] actor-capture abort ignored actor={packet.ActorId.Value:X} " +
                    $"player={sender.PlayerId} request={packet.CaptureRequestId}");
#endif
        }
        else
        {
#if SR2MP_DEEPLOG || SR2MP_TEST
            ActorDiag.Log("capture-abort", packet.ActorId.Value,
                $"requester={sender.PlayerId}; request={packet.CaptureRequestId}");
#endif
#if SR2MP_TEST
            if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") == "1")
                SrLogger.LogMessage(
                    $"[SR2MP_TEST] actor-capture reservation-aborted actor={packet.ActorId.Value:X} " +
                    $"player={sender.PlayerId} request={packet.CaptureRequestId}");
#endif
        }

        return false;
    }
}

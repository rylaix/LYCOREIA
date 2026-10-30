using System.Net;
using SR2MP.Packets;
using SR2MP.Packets.Utils;

namespace SR2MP.Handlers.Internal;

[PacketHandler((byte)PacketType.Heartbeat, HandlerType.Server)]
internal sealed class HeartbeatHandler : BasePacketHandler<EmptyPacket>
{
    protected override bool Handle(EmptyPacket packet, IPEndPoint? _)
    {
        // Server.OnDataReceived refreshes the authenticated endpoint's timeout
        // before dispatch. BasePacketHandler rejects unregistered endpoints.
        return false;
    }
}

[PacketHandler((byte)PacketType.HeartbeatAck, HandlerType.Client)]
internal sealed class HeartbeatAckHandler : BasePacketHandler<EmptyPacket>
{
    protected override bool Handle(EmptyPacket packet, IPEndPoint? _)
    {
        Main.Client.RecordServerHeartbeat();
        return false;
    }
}

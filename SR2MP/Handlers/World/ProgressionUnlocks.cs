using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Utils;
using SR2MP.Packets.World;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.World;

[PacketHandler((byte)PacketType.ProgressionUnlocks)]
internal sealed class ProgressionUnlocksHandler : BasePacketHandler<ProgressionUnlocksPacket>
{
    protected override bool Handle(ProgressionUnlocksPacket packet, IPEndPoint? clientEp)
    {
        if (!RequireAuthenticatedClient(clientEp))
            return false;

        // Host relays a guest's additions to the other guests.
        NetworkProgressionUnlockManager.Apply(packet);
        return Main.Server.IsRunning;
    }
}

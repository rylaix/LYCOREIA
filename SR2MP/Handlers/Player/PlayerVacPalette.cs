using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Player;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Player;

[PacketHandler((byte)PacketType.PlayerVacPalette)]
internal sealed class PlayerVacPaletteHandler : BasePacketHandler<PlayerVacPalettePacket>
{
    protected override bool Handle(PlayerVacPalettePacket packet, IPEndPoint? clientEp)
    {
        if (!RequireAuthenticatedClient(clientEp))
            return false;

        return NetworkVacPaletteManager.Receive(packet, clientEp);
    }
}

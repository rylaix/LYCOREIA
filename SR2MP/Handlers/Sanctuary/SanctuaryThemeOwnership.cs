using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Utils;
using SR2MP.Packets.World;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Sanctuary;

[PacketHandler((byte)PacketType.SanctuaryThemeOwnership)]
internal sealed class SanctuaryThemeOwnershipHandler : BasePacketHandler<SanctuaryThemeOwnershipPacket>
{
    protected override bool Handle(SanctuaryThemeOwnershipPacket packet, IPEndPoint? clientEp)
    {
        NetworkSanctuaryManager.ApplyThemeOwnership(packet.ThemeReferenceId, packet.IsOwned);
        return true;
    }
}

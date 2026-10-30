using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Ammo;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Ammo;

[PacketHandler((byte)PacketType.AmmoStorageSnapshot, HandlerType.Client)]
internal sealed class AmmoStorageSnapshotHandler : BasePacketHandler<AmmoStorageSnapshotPacket>
{
    protected override bool Handle(AmmoStorageSnapshotPacket packet, IPEndPoint? _)
    {
        if (string.IsNullOrWhiteSpace(packet.ID) || packet.Ammo?.AmmoSlots == null)
            return false;

        var ammo = NetworkAmmoManager.GetAmmo(packet.ID);
        if (ammo == null)
        {
            SrLogger.LogWarning($"AmmoStorageSnapshot: unknown storage '{packet.ID}'.");
            return false;
        }

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            if (!NetworkAmmoManager.TryApplyCanonicalSnapshot(ammo, packet.Ammo, out var error))
                SrLogger.LogWarning($"AmmoStorageSnapshot: could not apply '{packet.ID}': {error}");
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        return false;
    }
}

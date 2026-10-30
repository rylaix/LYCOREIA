using System.Net;
using SR2MP.Components.FX;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.FX;
using SR2MP.Packets.Utils;

namespace SR2MP.Handlers.FX;

[PacketHandler((byte)PacketType.VacuumSuctionPose)]
internal sealed class VacuumSuctionPoseHandler : BasePacketHandler<VacuumSuctionPosePacket>
{
    protected override bool Handle(VacuumSuctionPosePacket packet, IPEndPoint? clientEp)
    {
        if (IsServerSide)
        {
            if (!RequireAuthenticatedPlayer(clientEp, packet.PlayerId))
                return false;
            if (!IsValid(packet))
                return false;

            RemoteVacuumSuctionFX.ApplySuctionPose(packet.PlayerId, packet.Sequence,
                packet.Position, packet.Rotation);
            return true;
        }

        if (packet.PlayerId == LocalID || !IsValid(packet))
            return false;

        RemoteVacuumSuctionFX.ApplySuctionPose(packet.PlayerId, packet.Sequence,
            packet.Position, packet.Rotation);
        return false;
    }

    private static bool IsValid(VacuumSuctionPosePacket packet)
    {
        var rotationNorm = packet.Rotation.x * packet.Rotation.x + packet.Rotation.y * packet.Rotation.y +
                           packet.Rotation.z * packet.Rotation.z + packet.Rotation.w * packet.Rotation.w;
        return !string.IsNullOrWhiteSpace(packet.PlayerId) &&
               float.IsFinite(packet.Position.x) && float.IsFinite(packet.Position.y) &&
               float.IsFinite(packet.Position.z) && float.IsFinite(packet.Rotation.x) &&
               float.IsFinite(packet.Rotation.y) && float.IsFinite(packet.Rotation.z) &&
               float.IsFinite(packet.Rotation.w) && rotationNorm > 0.5f && rotationNorm < 1.5f;
    }
}

using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Player;
using SR2MP.Packets.Utils;

namespace SR2MP.Handlers.Player;

[PacketHandler((byte)PacketType.PlayerUpdate)]
internal sealed class PlayerUpdateHandler : BasePacketHandler<PlayerUpdatePacket>
{
    protected override bool Handle(PlayerUpdatePacket packet, IPEndPoint? clientEp)
    {
        if (IsServerSide && !RequireAuthenticatedPlayer(clientEp, packet.PlayerId))
            return false;

        if (packet.PlayerId == LocalID) return false;

        PlayerManager.UpdatePlayer(
            packet.PlayerId,
            packet.Position,
            packet.Rotation,
            packet.HorizontalMovement,
            packet.ForwardMovement,
            packet.Yaw,
            packet.AirborneState,
            packet.Moving,
            packet.HorizontalSpeed,
            packet.ForwardSpeed,
            packet.Sprinting,
            packet.LookY,
            packet.SceneGroup
        );

        Logger.LogDiagnostic("player", "remote-player-sample",
            $"peer={packet.PlayerId}; sceneGroup={packet.SceneGroup}; position={packet.Position.ToString("F2")}; " +
            $"moving={packet.Moving}; horizontalSpeed={packet.HorizontalSpeed:F2}; forwardSpeed={packet.ForwardSpeed:F2}; " +
            $"airborne={packet.AirborneState}; sprinting={packet.Sprinting}",
            rateKey: $"player-sample:{packet.PlayerId}", minimumIntervalMilliseconds: 3000);

        return true;
    }
}

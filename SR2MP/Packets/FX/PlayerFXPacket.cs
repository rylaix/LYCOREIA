using SR2MP.Packets.Utils;

namespace SR2MP.Packets.FX;

internal sealed class PlayerFXPacket : IPacket
{
    internal enum PlayerFXType : byte
    {
        None,
        VacReject,
        VacHold,
        VacAccept,
        VacShoot,
        VacShootEmpty,
        WaterSplash,
        VacSlotChange,
        VacRunning,
        VacRunningStart,
        VacRunningEnd,
        VacShootSound,
        VacShockwave
    }

    public PlayerFXType FX;
    public Vector3 Position;
    public string Player;

    public PacketType Type => PacketType.PlayerFX;
    public PacketReliability Reliability => RequiresOrderedDelivery(FX)
        ? PacketReliability.ReliableOrdered
        : PacketReliability.Unreliable;
    public NetworkChannel Channel => NetworkChannel.FX;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteEnum(FX);

        if (!IsPlayerSoundDictionary[FX])
            writer.WriteVector3(Position);
        else
            writer.WriteStringWithoutSize(Player);
    }

    public void Deserialise(PacketReader reader)
    {
        FX = reader.ReadEnum<PlayerFXType>();

        if (!IsPlayerSoundDictionary[FX])
            Position = reader.ReadVector3();
        else
            Player = reader.ReadPooledStringOfSize(16)!;
    }

    internal static bool IsSuctionStateTransition(PlayerFXType fx)
        => fx is PlayerFXType.VacRunningStart or PlayerFXType.VacRunningEnd;

    private static bool RequiresOrderedDelivery(PlayerFXType fx)
        => IsSuctionStateTransition(fx) || fx is PlayerFXType.VacRunning or PlayerFXType.VacShockwave;

    internal static bool VerifySuctionStateProtocol()
    {
        var start = new PlayerFXPacket { FX = PlayerFXType.VacRunningStart };
        var end = new PlayerFXPacket { FX = PlayerFXType.VacRunningEnd };
        var running = new PlayerFXPacket { FX = PlayerFXType.VacRunning };
        var shockwave = new PlayerFXPacket { FX = PlayerFXType.VacShockwave };

        return IsSuctionStateTransition(start.FX)
               && IsSuctionStateTransition(end.FX)
               && !IsSuctionStateTransition(running.FX)
               && !IsSuctionStateTransition(shockwave.FX)
               && start.Reliability == PacketReliability.ReliableOrdered
               && end.Reliability == PacketReliability.ReliableOrdered
               && running.Reliability == PacketReliability.ReliableOrdered
               && shockwave.Reliability == PacketReliability.ReliableOrdered
               && IsPlayerSoundDictionary[start.FX]
               && IsPlayerSoundDictionary[end.FX]
               && !IsPlayerSoundDictionary[shockwave.FX];
    }
}

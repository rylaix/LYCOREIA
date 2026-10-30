using System.Net;
using SR2MP.Client.Managers;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Utils;
using SR2MP.Packets.World;

namespace SR2MP.Handlers.Time;

[PacketHandler((byte)PacketType.WorldTime, HandlerType.Client)]
internal sealed class WorldTimeHandler : BasePacketHandler<WorldTimePacket>
{
    private static readonly object RevisionLock = new();
    private static long lastAppliedRevision;

    static WorldTimeHandler()
    {
        Main.Client.OnConnected += _ => ResetRevision();
        Main.Client.OnDisconnected += ResetRevision;
    }

    protected override bool Handle(WorldTimePacket packet, IPEndPoint? _)
    {
        if (!double.IsFinite(packet.Time))
            return false;

        lock (RevisionLock)
        {
            if (!WeatherSyncPolicy.IsNewRevision(packet.Revision, lastAppliedRevision))
                return false;

            var worldModel = SceneContext.Instance.TimeDirector._worldModel;
            if (WeatherSyncPolicy.ShouldCorrectWorldTime(packet.Time, worldModel.worldTime))
                worldModel.worldTime = packet.Time;

            lastAppliedRevision = packet.Revision;
        }

        return false;
    }

    private static void ResetRevision()
    {
        lock (RevisionLock)
            lastAppliedRevision = 0;
    }
}

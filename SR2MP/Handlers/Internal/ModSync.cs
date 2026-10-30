using System.Net;
using SR2MP.Api;
using SR2MP.Packets;
using SR2MP.Packets.Internal;
using SR2MP.Packets.Utils;
#if SR2MP_TEST
using SR2MP.Shared.Utils;
#endif

namespace SR2MP.Handlers.Internal;

[PacketHandler((byte)PacketType.ModSync, HandlerType.Client)]
internal sealed class ModSyncHandler : BasePacketHandler<EmptyPacket>
{
#if SR2MP_TEST
    private static bool abortBeforeModSyncAckAttempted;
#endif

    protected override bool Handle(EmptyPacket packet, IPEndPoint? clientEp)
    {
#if SR2MP_TEST
        if (!abortBeforeModSyncAckAttempted &&
            Environment.GetEnvironmentVariable("SR2MP_TEST_ABORT_BEFORE_MODSYNC_ACK") == "1")
        {
            abortBeforeModSyncAckAttempted = true;
            TestLifecycleControl.SuppressAutoConnectForFiveSeconds();
            SrLogger.LogMessage("[SR2MP_TEST] abort-before-modsync-ack begin");
            Main.Client.Disconnect();
            SrLogger.LogMessage("[SR2MP_TEST] abort-before-modsync-ack complete");
            return false;
        }
#endif

        var dict = new Dictionary<uint, ModData>();

        foreach (var id in ApiHandlers.SharedSideMods)
        {
            var info = ApiHandlers.Holders[id].Mod.Info;
            dict[id] = new ModData
            {
                Name = string.IsNullOrEmpty(info.Name) ? $"Unknown Mod ({id})" : info.Name,
                Version = string.IsNullOrEmpty(info.Version) ? "Unknown Version" : info.Version
            };
        }

        var modSyncPacket = new ModSyncPacket
        {
            PlayerId = LocalID,
            Mods = dict
        };
        Main.Client.SendPacket(modSyncPacket);

        return false;
    }
}

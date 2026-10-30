using System.Net;
using SR2MP.Api;
using SR2MP.Packets.Internal;
using SR2MP.Packets.Loading;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Internal;

[PacketHandler((byte)PacketType.ModSyncAck, HandlerType.Server)]
internal sealed class ModSyncAckHandler : BasePacketHandler<ModSyncPacket>
{
    protected override bool Handle(ModSyncPacket packet, IPEndPoint? clientEp)
    {
        ReSyncManager.TraceJoinSync("mod-sync-ack-enter", packet.PlayerId);
        var clientNeeds = new List<string>();
        var hostNeeds = new List<string>();

        foreach (var (id, data) in packet.Mods)
        {
            if (!ApiHandlers.Holders.ContainsKey(id))
                hostNeeds.Add($"{data.Name} ({data.Version})");
            else if (data.Name == BuildInfo.Name &&
                     ApiHandlers.Holders[id].Mod.Info.Name == BuildInfo.Name &&
                     !string.Equals(data.Version, ApiHandlers.Holders[id].Mod.Info.Version, StringComparison.Ordinal))
                hostNeeds.Add($"{data.Name} has incompatible network version {data.Version}");
        }

        foreach (var (id, holder) in ApiHandlers.Holders)
        {
            if (!packet.Mods.ContainsKey(id))
                clientNeeds.Add($"{holder.Mod.Info.Name} ({holder.Mod.Info.Version})");
            else if (holder.Mod.Info.Name == BuildInfo.Name &&
                     !string.Equals(packet.Mods[id].Version, holder.Mod.Info.Version, StringComparison.Ordinal))
                clientNeeds.Add($"{holder.Mod.Info.Name} has incompatible network version {packet.Mods[id].Version}");
        }

        if (clientNeeds.Count > 0 || hostNeeds.Count > 0)
        {
            SrLogger.LogMessage($"Mods desynchronized!\n\tClient Needs: {string.Join(", ", clientNeeds)}\n\tHost Needs: {string.Join(", ", hostNeeds)}");

            var reason = $"You have incompatible mods!\nMods that are missing or on the wrong version:\n {string.Join("\n", clientNeeds)}";
            var denyPacket = new ConnectionDenyPacket { Reason = reason };
            Main.Server.SendToClient(denyPacket, clientEp!);

            Main.Server.ClientManager.RemoveClient(clientEp!);

            return false;
        }

        ReSyncManager.TraceJoinSync("mod-sync-ack-mods-verified", packet.PlayerId);
        var netData = new List<ModNetData>();

        foreach (var id in ApiHandlers.Holders.Keys)
        {
            netData.Add(new ModNetData
            {
                ModId = id,
                NetId = ApiHandlers.GetOrIncrementNetId(id)
            });
        }

        ReSyncManager.TraceJoinSync("mod-sync-ack-refresh-packet-mapping", packet.PlayerId);
        ApiHandlers.RefreshPacketMapping();
        ReSyncManager.TraceJoinSync("mod-sync-ack-currency-capture", packet.PlayerId);
        var currencyBalances = ConnectionApprovePacket.CaptureCurrencyBalances();
        ReSyncManager.TraceJoinSync($"mod-sync-ack-currency-captured count={currencyBalances.Count}", packet.PlayerId);
        var syncId = ReSyncManager.CreateSyncId();

        var ackPacket = new ConnectionApprovePacket
        {
            InitialJoin = true,
            PlayerId = packet.PlayerId,
            OtherPlayers = Array.ConvertAll(PlayerManager.GetAllPlayers().ToArray(),
                p => (p.PlayerId, p.Username)),
            CurrencyBalances = currencyBalances,
            CurrencyRevisions = ConnectionApprovePacket.CaptureCurrencyRevisions(currencyBalances.Keys),
            AllowCheats = Main.AllowCheats,
            NetData = netData,
            SyncId = syncId,
            HostSessionId = Main.Server.PlayerId
        };

        // The connectAck is different because of initialJoin, otherwise another PlayerJoin request will be sent

        ReSyncManager.TraceJoinSync("mod-sync-ack-approve-send", packet.PlayerId);
        Main.Server.SendToClient(ackPacket, clientEp!);
        ReSyncManager.TraceJoinSync("mod-sync-ack-actor-types", packet.PlayerId);
        ActorManager.SendActorTypeRegistry(clientEp!);

        ReSyncManager.TraceJoinSync("mod-sync-ack-synchronize-client", packet.PlayerId);
        ReSyncManager.SynchronizeClient(packet.PlayerId, clientEp!, syncId);
        ReSyncManager.TraceJoinSync("mod-sync-ack-complete", packet.PlayerId);

        SrLogger.LogMessage(
            $"Player {packet.PlayerId} successfully connected",
            $"Player {packet.PlayerId} successfully connected from {clientEp}");

        return false;
    }
}

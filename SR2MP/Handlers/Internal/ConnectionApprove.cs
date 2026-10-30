using System.Net;
using Il2CppMonomiPark.SlimeRancher.Economy;
using SR2MP.Api;
using SR2MP.Components.Player;
using SR2MP.Packets.Internal;
using SR2MP.Packets.Player;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;
using SR2MP.Shared.Utils;

namespace SR2MP.Handlers.Internal;

[PacketHandler((byte)PacketType.ConnectionApprove, HandlerType.Client)]
internal sealed class ConnectionApproveHandler : BasePacketHandler<ConnectionApprovePacket>
{
    protected override bool Handle(ConnectionApprovePacket packet, IPEndPoint? _)
    {
        var hostSessionId = string.IsNullOrWhiteSpace(packet.HostSessionId) ? packet.PlayerId : packet.HostSessionId;
        Logger.SetDiagnosticHostId(hostSessionId);
        Logger.LogDiagnostic("session", "connection-approved",
            $"hostSession={hostSessionId}; localPeer={packet.PlayerId}; initialJoin={packet.InitialJoin}; syncId={packet.SyncId}; peers={packet.OtherPlayers.Length}; currencyTypes={packet.CurrencyBalances.Count}");
        // Correlate approval with InitialActors by host sync generation. These
        // packets use Important and ActorCritical channels and may be dispatched
        // in either order; a late approval must not reopen a completed barrier.
        NetworkActorCaptureManager.CancelClientCapturesForResync();
        ActorSnapshotDeltaBuffer.BeginSync(packet.SyncId);

        if (packet.InitialJoin)
        {
            var joinPacket = new PlayerJoinPacket
            {
                Type = PacketType.PlayerJoin,
                PlayerId = packet.PlayerId,
                PlayerName = Main.Username
            };

            PacketSender.SendPacket(joinPacket);

            // SR2MPClient.StartHeartbeat();
            Main.Client.NotifyConnected();
            NetworkGardenManager.OnClientConnected();

            SrLogger.LogMessage($"Connection acknowledged by server! (PlayerId: {packet.PlayerId})");

            CheatsEnabled = packet.AllowCheats;

        }

        // Reconcile remote avatars on initial approval and later resyncs.
        foreach (var (id, username) in packet.OtherPlayers)
        {
            if (string.IsNullOrWhiteSpace(id) || id == packet.PlayerId)
                continue;

            EnsurePlayer(id, username);
        }

        var currencyEntries = GameContext.Instance?.LookupDirector?._currencyList?._currencies;
        if (currencyEntries != null && packet.CurrencyBalances != null)
        {
            var wasHandlingPacket = HandlingPacket;
            HandlingPacket = true;
            try
            {
                foreach (var entry in currencyEntries)
                {
                    if (entry == null)
                        continue;

                    var currency = entry.Cast<ICurrency>();
                    if (currency.PersistenceId is < byte.MinValue or > byte.MaxValue ||
                        !packet.CurrencyBalances.TryGetValue((byte)currency.PersistenceId, out var amount) ||
                        amount < 0 ||
                        !packet.CurrencyRevisions.TryGetValue((byte)currency.PersistenceId, out var revision))
                        continue;

                    if (!NetworkCurrencyManager.ApplyClientSnapshot((byte)currency.PersistenceId, revision))
                        continue;

                    SceneContext.Instance.PlayerState._model.SetCurrency(currency, amount);
                }
            }
            finally
            {
                HandlingPacket = wasHandlingPacket;
            }
        }

        foreach (var data in packet.NetData)
        {
            ApiHandlers.SetNetId(data.ModId, data.NetId);
        }

        ApiHandlers.RefreshPacketMapping();

        return false;
    }

    private static void EnsurePlayer(string id, string name)
    {
        var remotePlayer = PlayerManager.GetPlayer(id) ?? PlayerManager.AddPlayer(id);
        remotePlayer.Username = name;

        if (PlayerObjects.TryGetValue(id, out var existingObject) && existingObject)
            return;

        var playerObject = Object.Instantiate(PlayerPrefab).GetComponent<NetworkPlayer>();
        playerObject.gameObject.SetActive(true);
        playerObject.ID = id;
        playerObject.gameObject.name = id;
        PlayerObjects[id] = playerObject.gameObject;
        Object.DontDestroyOnLoad(playerObject);
    }
}

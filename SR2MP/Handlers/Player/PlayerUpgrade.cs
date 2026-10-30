using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Upgrade;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Player;

[PacketHandler((byte)PacketType.PlayerUpgrade)]
internal sealed class PlayerUpgradeHandler : BasePacketHandler<PlayerUpgradePacket>
{
    protected override bool Handle(PlayerUpgradePacket packet, IPEndPoint? _)
    {
        if (IsServerSide)
        {
            if (packet.Kind != PlayerUpgradeMessageKind.Request || !RequireAuthenticatedClient(_))
                return false;

            NetworkUpgradePurchaseManager.HandleHostRequest(packet, _);
            return false;
        }
        else if (packet.Kind == PlayerUpgradeMessageKind.Rejected)
        {
            if (NetworkUpgradeManager.TryAcceptHostRejection(packet.UpgradeID, packet.RequestId))
                SrLogger.LogMessage(
                    $"Host rejected upgrade request={packet.RequestId} upgrade={packet.UpgradeID}.");
            return false;
        }
        else if (packet.Kind != PlayerUpgradeMessageKind.State)
        {
            return false;
        }

        var model = SceneContext.Instance.PlayerState._model.upgradeModel;

        var upgrade = model.upgradeDefinitions.items._items.FirstOrDefault(
            x => x._uniqueId == packet.UpgradeID);
        if (upgrade == null)
        {
            NetworkUpgradeManager.ForgetClientRequest(packet.UpgradeID, packet.RequestId);
            SrLogger.LogWarning($"PlayerUpgrade: unknown upgrade id {packet.UpgradeID}");
            return false;
        }

        if (!NetworkUpgradeManager.TryAcceptHostState(packet.UpgradeID, packet.Revision, packet.RequestId))
            return false;

        var wasHandlingClientState = HandlingPacket;
        HandlingPacket = true;
        try
        {
            model.SetUpgradeLevel(upgrade, packet.Level);
        }
        finally
        {
            HandlingPacket = wasHandlingClientState;
        }

        return false;
    }
}

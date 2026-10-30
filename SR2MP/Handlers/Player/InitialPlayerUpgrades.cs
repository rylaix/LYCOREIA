using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Loading;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Player;

[PacketHandler((byte)PacketType.InitialPlayerUpgrades, HandlerType.Client)]
internal sealed class PlayerUpgradesLoadHandler : BasePacketHandler<InitialUpgradesPacket>
{
    protected override bool Handle(InitialUpgradesPacket packet, IPEndPoint? _)
    {
        var model = SceneContext.Instance.PlayerState._model.upgradeModel;
        var upgradesList = GameContext.Instance.LookupDirector._upgradeDefinitions;
        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;

        try
        {
            foreach (var upgradeLevel in packet.Upgrades)
            {
                var upgrade = upgradesList.items._items.FirstOrDefault(x => x._uniqueId == upgradeLevel.Key);
                if (upgrade == null)
                {
                    SrLogger.LogWarning($"InitialPlayerUpgrades: unknown upgrade id {upgradeLevel.Key}");
                    continue;
                }

                if (!packet.Revisions.TryGetValue(upgradeLevel.Key, out var revision))
                {
                    SrLogger.LogWarning($"InitialPlayerUpgrades: missing revision for upgrade id {upgradeLevel.Key}; skipping unsafe snapshot value");
                    continue;
                }

                if (!NetworkUpgradeManager.TryApplySnapshot(upgradeLevel.Key, revision))
                    continue;

                model.SetUpgradeLevel(upgrade, upgradeLevel.Value);
            }
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        PlayerInventoryManager.NotifyPlayerUpgradeSnapshotApplied();

        return false;
    }
}

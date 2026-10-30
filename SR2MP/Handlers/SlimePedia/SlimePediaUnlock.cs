using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Pedia;
using SR2MP.Packets.Utils;

namespace SR2MP.Handlers.SlimePedia;

[PacketHandler((byte)PacketType.PediaUnlock)]
internal sealed class SlimePediaUnlockHandler : BasePacketHandler<PediaUnlockPacket>
{
    protected override bool Handle(PediaUnlockPacket packet, IPEndPoint? _)
    {
        if (string.IsNullOrWhiteSpace(packet.ID))
            return false;

        var pediaLookup = GameContext.Instance.AutoSaveDirector._saveReferenceTranslation._pediaEntryLookup;
        if (!pediaLookup.TryGetValue(packet.ID, out var pediaEntry) || pediaEntry == null)
        {
            SrLogger.LogWarning($"SlimePediaUnlock: unknown pedia entry '{packet.ID}', ignoring request.");
            return false;
        }

        var pediaDirector = SceneContext.Instance.PediaDirector;
        if (pediaDirector == null)
            return false;

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            if (!pediaDirector.IsUnlocked(pediaEntry))
                pediaDirector.Unlock(pediaEntry, packet.Popup);
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        return true;
    }
}

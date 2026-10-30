using System.Net;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppMonomiPark.SlimeRancher.Pedia;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Loading;
using SR2MP.Packets.Utils;
using Enumerable = Il2CppSystem.Linq.Enumerable;

namespace SR2MP.Handlers.SlimePedia;

[PacketHandler((byte)PacketType.InitialPediaEntries, HandlerType.Client)]
internal sealed class InitialSlimePediaLoadHandler : BasePacketHandler<InitialPediaPacket>
{
    protected override bool Handle(InitialPediaPacket packet, IPEndPoint? _)
    {
        if (packet.Entries == null)
            return false;

        var pediaLookup = GameContext.Instance.AutoSaveDirector._saveReferenceTranslation._pediaEntryLookup;
        var unlocked = new List<PediaEntry>();
        foreach (var entryId in packet.Entries)
        {
            if (string.IsNullOrWhiteSpace(entryId))
                continue;

            if (pediaLookup.TryGetValue(entryId, out var entry) && entry != null)
                unlocked.Add(entry);
        }

        var unlockedCpp = new Il2CppReferenceArray<PediaEntry>(unlocked.ToArray());
        var pediaModel = SceneContext.Instance.PediaDirector._pediaModel;
        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            pediaModel.unlocked = Enumerable.ToHashSet(
                unlockedCpp.Cast<CppCollections.IEnumerable<PediaEntry>>());
            pediaModel.NotifyParticipants();
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        return false;
    }
}

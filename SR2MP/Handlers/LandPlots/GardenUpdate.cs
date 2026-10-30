using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.LandPlots;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.LandPlots;

[PacketHandler((byte)PacketType.GardenUpdate)]
internal sealed class GardenUpdateHandler : BasePacketHandler<GardenUpdatePacket>
{
    protected override bool Handle(GardenUpdatePacket packet, IPEndPoint? clientEp)
    {
        string? senderId = null;
        if (IsServerSide)
        {
            if (clientEp == null || !Main.Server.ClientManager.TryGetClient(clientEp, out var sender) || sender == null)
                return false;

            senderId = sender.PlayerId;
        }

        var accepted = new List<GardenUpdatePacket.Entry>(packet.Entries.Count);
        var ownerCorrections = new Dictionary<string, GardenOwnershipPacket.Entry>(StringComparer.Ordinal);
        for (var i = 0; i < packet.Entries.Count; i++)
        {
            var entry = packet.Entries[i];
            if (!GardenStateCache.HasFiniteState(entry))
            {
                SrLogger.LogWarning(
                    $"Rejected garden state update with non-finite timer/water: garden='{entry.GardenId}'.");
                continue;
            }

            if (Main.Server.IsRunning)
            {
                if (senderId == null || !NetworkGardenManager.IsCurrentOwner(entry.GardenId, senderId))
                {
                    SrLogger.LogWarning(
                        $"Rejected garden state update garden='{entry.GardenId}' from non-owner '{senderId ?? "<null>"}'.");

                    if (senderId != null && NetworkGardenManager.TryCreateOwnerCorrection(
                            entry.GardenId, senderId, senderId, out var correction))
                        ownerCorrections[correction.GardenId] = correction;

                    continue;
                }

                entry.Revision = NetworkGardenManager.AssignServerRevision(entry);
            }

            packet.Entries[i] = entry;
            NetworkGardenManager.ApplyState(entry);
            accepted.Add(entry);
        }

        if (IsServerSide)
        {
            if (clientEp != null && ownerCorrections.Count > 0)
            {
                Main.Server.SendToClient(new GardenOwnershipPacket
                {
                    Entries = ownerCorrections.Values.ToList()
                }, clientEp);
            }

            packet.Entries = accepted;
            return accepted.Count > 0;
        }

        return false;
    }
}

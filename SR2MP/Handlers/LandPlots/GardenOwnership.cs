using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.LandPlots;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.LandPlots;

[PacketHandler((byte)PacketType.GardenOwnership)]
internal sealed class GardenOwnershipHandler : BasePacketHandler<GardenOwnershipPacket>
{
    protected override bool Handle(GardenOwnershipPacket packet, IPEndPoint? clientEp)
    {
        if (!IsServerSide)
        {
            foreach (var entry in packet.Entries)
            {
                if (entry.HasFinalState)
                {
                    if (NetworkGardenManager.IsValidFinalStatePayload(entry.GardenId, entry.FinalState))
                        NetworkGardenManager.ApplyState(entry.FinalState);
                    else
                        SrLogger.LogWarning(
                            $"Ignored invalid final garden state attached to ownership update '{entry.GardenId}'.");
                }

                NetworkGardenManager.ApplyOwnership(entry.GardenId, entry.OwnerId);
            }

            return false;
        }

        if (clientEp == null || !Main.Server.ClientManager.TryGetClient(clientEp, out var sender) || sender == null)
            return false;

        var accepted = new List<GardenOwnershipPacket.Entry>();
        var ownerCorrections = new Dictionary<string, GardenOwnershipPacket.Entry>(StringComparer.Ordinal);
        foreach (var entry in packet.Entries)
        {
            var acceptedEntry = entry;
            if (!NetworkGardenManager.CanAcceptOwnershipChange(acceptedEntry.GardenId, sender.PlayerId, acceptedEntry.OwnerId))
            {
                SrLogger.LogWarning(
                    $"Rejected garden ownership change garden='{acceptedEntry.GardenId}' " +
                    $"sender='{sender.PlayerId}' requestedOwner='{acceptedEntry.OwnerId}'.");

                if (NetworkGardenManager.TryCreateOwnerCorrection(
                        acceptedEntry.GardenId, sender.PlayerId, acceptedEntry.OwnerId, out var correction))
                    ownerCorrections[correction.GardenId] = correction;

                continue;
            }

            if (acceptedEntry.HasFinalState)
            {
                if (!NetworkGardenManager.IsValidFinalStateReleaseEntry(
                        acceptedEntry.GardenId, acceptedEntry.OwnerId, acceptedEntry.FinalState))
                {
                    SrLogger.LogWarning(
                        $"Rejected invalid final garden state in release '{acceptedEntry.GardenId}' " +
                        $"from '{sender.PlayerId}'.");
                    acceptedEntry.HasFinalState = false;
                    acceptedEntry.FinalState = default;
                }
                else
                {
                    var finalState = acceptedEntry.FinalState;
                    finalState.Revision = NetworkGardenManager.AssignServerRevision(finalState);
                    acceptedEntry.FinalState = finalState;
                    NetworkGardenManager.ApplyState(finalState);
                }
            }

            NetworkGardenManager.ApplyOwnership(acceptedEntry.GardenId, acceptedEntry.OwnerId);
            if (string.IsNullOrEmpty(acceptedEntry.OwnerId))
                acceptedEntry.OwnerId = NetworkGardenManager.GetOwnerForSnapshot(acceptedEntry.GardenId, null);
            accepted.Add(acceptedEntry);
        }

        if (ownerCorrections.Count > 0)
        {
            Main.Server.SendToClient(new GardenOwnershipPacket
            {
                Entries = ownerCorrections.Values.ToList()
            }, clientEp);
        }

        packet.Entries = accepted;
        return accepted.Count > 0;
    }
}

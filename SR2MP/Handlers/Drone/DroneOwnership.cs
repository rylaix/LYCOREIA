using System.Net;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Components.Drone;
using SR2MP.Handlers.Internal;
using SR2MP.Shared.Managers;
using SR2MP.Packets.Drone;
using SR2MP.Packets.Utils;

namespace SR2MP.Handlers.Drone;

[PacketHandler((byte)PacketType.DroneOwnership)]
internal sealed class DroneOwnershipHandler : BasePacketHandler<DroneOwnershipPacket>
{
    protected override bool Handle(DroneOwnershipPacket packet, IPEndPoint? clientEndpoint)
    {
#if SR2MP_TEST
        var traceOwnership = Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_DRONE_OWNERSHIP") == "1";
        if (traceOwnership)
            SrLogger.LogMessage(
                $"[SR2MP_TEST] drone-ownership receive station={packet.StationId} " +
                $"claimant={packet.ClaimerID ?? "<null>"} previous={packet.PreviousOwnerID ?? "<null>"} " +
                $"endpoint={clientEndpoint?.ToString() ?? "host/local"}");
#endif

        if (packet.StationId == 0)
            return false;

        var claimantId = packet.ClaimerID ?? string.Empty;
        var previousOwnerId = packet.PreviousOwnerID ?? string.Empty;
        if (string.IsNullOrEmpty(claimantId) && string.IsNullOrEmpty(previousOwnerId))
            return false;

        string? senderId = null;
        if (Main.Server.IsRunning)
        {
            if (clientEndpoint == null ||
                !Main.Server.ClientManager.TryGetClient(clientEndpoint, out var sender) || sender == null)
                return false;

            senderId = sender.PlayerId;

            // A claim must come from the player becoming the owner; a release must
            // come from the owner named by PreviousOwnerID.
            var assertedOwnerId = string.IsNullOrEmpty(claimantId) ? previousOwnerId : claimantId;
            if (senderId != assertedOwnerId)
                return false;
        }

        var hasDrone = NetworkDrone.Drones.TryGetValue(packet.StationId, out var drone) && drone;
        var hasKnownOwner = NetworkDroneManager.TryGetCurrentOwner(packet.StationId, out var currentOwnerId);

        if (Main.Server.IsRunning)
        {
            var stationModel = GameState.GetIdentifiableModel(new ActorId(packet.StationId));
            var isDroneStation = stationModel?.TryCast<DroneStationGadgetModel>() != null;
            if (stationModel == null)
            {
                // The station's ActorCritical spawn can arrive after this WorldState
                // claim. Leave the claimant pending so its normal retry can be checked
                // once the host has canonical station state.
                SrLogger.LogDebug(
                    $"Deferred drone ownership claim for station {packet.StationId}: station model is not loaded yet.");
                return false;
            }

            if (!DroneMutationPolicy.IsValidStationClaimTarget(stationModel != null, isDroneStation))
                return RejectClaim(packet, clientEndpoint, senderId!, hasKnownOwner ? currentOwnerId : string.Empty);
        }

        if (!Main.Server.IsRunning)
        {
            if (!hasKnownOwner && Main.Client.IsConnected)
            {
                // On join/resync, WorldState ownership packets may overtake the
                // ActorCritical station snapshot. Defer this server-authored owner
                // transition until the snapshot has supplied the drone component.
                if (!NetworkDroneManager.PendingOwnerTransitions.TryGetValue(packet.StationId, out var transitions))
                {
                    transitions = new List<(string ClaimerId, string PreviousOwnerId)>();
                    NetworkDroneManager.PendingOwnerTransitions[packet.StationId] = transitions;
                }

                transitions.Add((claimantId, previousOwnerId));
                return true;
            }

            // Only the host can author these packets. Apply its owner state and
            // settle any pending claim on this peer.
            if (!string.IsNullOrEmpty(claimantId))
            {
                NetworkDroneManager.CachedOwners[packet.StationId] = claimantId;
                if (hasDrone)
                    drone!.ApplyServerOwnerState(claimantId);
                else
                    NetworkDroneManager.PendingOwners[packet.StationId] = claimantId;
            }
            else if (string.Equals(previousOwnerId, LocalID, StringComparison.Ordinal))
            {
                // Releases also acknowledge an outstanding claim request from this
                // client. Keep the remembered owner identity for compare-and-swap,
                // but stop retrying and never run the released drone locally.
                drone?.CancelPendingOwnerClaim();
                if (hasDrone)
                    drone!.LocallyOwned = false;
            }

#if SR2MP_TEST
            if (traceOwnership)
                SrLogger.LogMessage(
                    $"[SR2MP_TEST] drone-ownership applied server state station={packet.StationId} " +
                    $"claimant={claimantId} previous={previousOwnerId} hasDrone={hasDrone}");
#endif
            return true;
        }

        if (!hasKnownOwner)
        {
            if (string.IsNullOrEmpty(previousOwnerId))
                currentOwnerId = string.Empty;
            else if (!string.IsNullOrEmpty(claimantId) && claimantId == previousOwnerId)
                currentOwnerId = previousOwnerId;
            else if (!string.IsNullOrEmpty(claimantId) &&
                     DroneMutationPolicy.CanInferUnknownCurrentOwner(
                         claimantId,
                         previousOwnerId,
                         NetworkDroneManager.IsExplicitlyReleasedOwner(packet.StationId, previousOwnerId)))
                currentOwnerId = previousOwnerId;
            else
                return RejectClaim(packet, clientEndpoint, senderId!, string.Empty);
        }

        if (string.IsNullOrEmpty(claimantId))
        {
            if (!DroneMutationPolicy.CanAcceptRelease(senderId!, currentOwnerId, previousOwnerId))
            {
                SrLogger.LogWarning(
                    $"Rejected drone ownership release station={packet.StationId} sender={senderId} " +
                    $"expected={previousOwnerId} current={currentOwnerId}");
                return false;
            }

            NetworkDroneManager.RecordExplicitOwnerRelease(packet.StationId, currentOwnerId);

            // Keep the released owner as the compare-and-swap value. Relay this
            // accepted release before the host claim below: both packets use the
            // reliable-ordered WorldState channel, and the base handler's normal
            // relay runs only after this method returns.
            if (DroneMutationPolicy.ShouldHostTakeOverAfterRelease(
                    Main.Server.IsRunning,
                    releaseAccepted: true,
                    hasDrone: hasDrone,
                    droneHibernated: hasDrone && drone!.IsHibernated))
            {
                Main.Server.SendToAllExcept(packet, clientEndpoint);
                drone!.ClaimOwnership();
                return false; // The release was relayed above; avoid a duplicate.
            }

#if SR2MP_TEST
            if (traceOwnership)
                SrLogger.LogMessage(
                    $"[SR2MP_TEST] drone-ownership accepted release station={packet.StationId} " +
                    $"previous={previousOwnerId} current={currentOwnerId}");
#endif
            return true;
        }

        var currentOwnerConnected = !string.IsNullOrEmpty(currentOwnerId) &&
                                    PlayerManager.CheckPlayerExists(currentOwnerId);
        var explicitlyReleased = NetworkDroneManager.IsExplicitlyReleasedOwner(packet.StationId, currentOwnerId);
        if (!DroneMutationPolicy.CanAcceptClaim(
                currentOwnerId, claimantId, previousOwnerId, currentOwnerConnected, explicitlyReleased))
        {
            SrLogger.LogWarning(
                $"Rejected drone ownership claim station={packet.StationId} sender={senderId} " +
                $"expected={previousOwnerId} current={currentOwnerId} released={explicitlyReleased}");
            return RejectClaim(packet, clientEndpoint, senderId!, currentOwnerId);
        }

        NetworkDroneManager.CachedOwners[packet.StationId] = claimantId;
        NetworkDroneManager.ClearExplicitOwnerRelease(packet.StationId);
        if (hasDrone)
        {
            drone!.CurrentOwnerId = claimantId;
            drone.LocallyOwned = claimantId == LocalID;
            NetworkDroneManager.PendingOwners.Remove(packet.StationId);
        }
        else
        {
            NetworkDroneManager.PendingOwners[packet.StationId] = claimantId;
        }

        // The requester waits in replica mode until this direct host ACK arrives.
        // The BasePacketHandler will also relay the accepted transition to the
        // other clients after this handler returns.
        Main.Server.SendToClient(packet, clientEndpoint!);

#if SR2MP_TEST
        if (traceOwnership)
            SrLogger.LogMessage(
                $"[SR2MP_TEST] drone-ownership accepted claim station={packet.StationId} " +
                $"claimant={claimantId} previous={previousOwnerId} current={currentOwnerId} hasDrone={hasDrone}");
#endif
        return true;
    }

    private static bool RejectClaim(
        DroneOwnershipPacket packet,
        IPEndPoint? clientEndpoint,
        string senderId,
        string currentOwnerId)
    {
        if (clientEndpoint != null)
        {
            // The client stays in replica mode until an owner transition is
            // accepted. Return the authoritative owner to close any stale request.
            Main.Server.SendToClient(new DroneOwnershipPacket
            {
                StationId = packet.StationId,
                ClaimerID = currentOwnerId,
                PreviousOwnerID = senderId
            }, clientEndpoint);
        }

        return false;
    }
}

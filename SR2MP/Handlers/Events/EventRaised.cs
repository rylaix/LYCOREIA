using System.Net;
using SR2MP.Handlers.Internal;
using SR2MP.Packets.Utils;
using SR2MP.Packets.World;
using SR2MP.Shared.Managers;

namespace SR2MP.Handlers.Events;

[PacketHandler((byte)PacketType.EventRaised)]
internal sealed class EventRaisedHandler : BasePacketHandler<EventRaisedPacket>
{
    protected override bool Handle(EventRaisedPacket packet, IPEndPoint? clientEp)
    {
        if (!RequireAuthenticatedClient(clientEp))
            return false;

        if (IsServerSide && EventSyncPolicy.IsZoneUnlockedEvent(packet.EventKey))
        {
            if (!NetworkEventManager.ApplyEventRaised(packet.EventKey, packet.DataKey, packet.Count) ||
                clientEp == null)
                return false;

            // Zone unlock requests are deferred by the guest's Harmony prefix.
            // Echo the accepted canonical state to that guest; BasePacketHandler
            // relays the same packet to all other authenticated peers.
            packet.Count = EventSyncPolicy.UnlockedZoneCount;
            Main.Server.SendToClient(packet, clientEp);
            return true;
        }

        var accepted = NetworkEventManager.ApplyEventRaised(packet.EventKey, packet.DataKey, packet.Count);
        Logger.LogDiagnostic("world-event", accepted ? "event-applied" : "event-rejected",
            $"eventKey={packet.EventKey}; dataKey={packet.DataKey}; requestedCount={packet.Count}; " +
            $"canonicalCount={NetworkEventManager.GetEventCount(packet.EventKey, packet.DataKey)}; hostSide={IsServerSide}");
        if (IsServerSide && accepted)
        {
            var canonicalCount = NetworkEventManager.GetEventCount(packet.EventKey, packet.DataKey);
            if (EventCanonicalReconciliationPolicy.ShouldEchoCanonicalCount(
                    accepted: accepted, requestedCount: packet.Count, canonicalCount: canonicalCount))
            {
                // Repair a sender whose absolute count was stale or whose local
                // increment was clamped by the host. The same canonical count is
                // relayed to other peers below instead of the stale request.
                packet.Count = canonicalCount;
                if (clientEp != null)
                    Main.Server.SendToClient(packet, clientEp);
            }
        }

        return accepted;
    }
}

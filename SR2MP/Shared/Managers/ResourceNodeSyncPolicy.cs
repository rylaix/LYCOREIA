using SR2MP.Packets.World;
using SR2MP.Packets.Utils;

namespace SR2MP.Shared.Managers;

/// <summary>
/// Pure invariants shared by resource-node packet handling and the in-process test harness.
/// </summary>
internal static class ResourceNodeSyncPolicy
{
    internal static bool IsValidNodeId(string? nodeId)
        => !string.IsNullOrWhiteSpace(nodeId);

    internal static bool VerifyRules()
        => IsValidNodeId("resource-node")
           && !IsValidNodeId(null)
           && !IsValidNodeId(string.Empty)
           && !IsValidNodeId("   ")
           && new ResourceNodePacket().Reliability == PacketReliability.ReliableOrdered
           && new ResourceNodePlacementPacket().Reliability == PacketReliability.ReliableOrdered
           && new InitialResourceNodesPacket().Reliability == PacketReliability.ReliableOrdered;
}

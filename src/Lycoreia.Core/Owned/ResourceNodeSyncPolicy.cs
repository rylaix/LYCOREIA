// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Owned;

/// <summary>
/// Pure invariants shared by resource-node packet handling and the in-process test harness.
/// </summary>
public static class ResourceNodeSyncPolicy
{
    internal static bool IsValidNodeId(string? nodeId)
        => !string.IsNullOrWhiteSpace(nodeId);

    internal static bool VerifyRules()
        => IsValidNodeId("resource-node")
           && !IsValidNodeId(null)
           && !IsValidNodeId(string.Empty)
           && !IsValidNodeId("   ");
}

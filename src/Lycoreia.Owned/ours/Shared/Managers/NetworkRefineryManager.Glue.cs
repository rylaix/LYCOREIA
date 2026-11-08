// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Lycoreia.Net;
using Lycoreia.Net.Messages;

namespace Lycoreia.Owned.Shared.Managers;

internal static partial class NetworkRefineryManager
{
    internal static void Register()
    {
        // Host: a guest's delta. A canonicalised request is relayed to the other peers (the sender got its acknowledgement).
        Mod.Node.On<RefineryUpdate>(Side.Host, (c, m) => ReceiveRequest(m, c.SenderId, c.Sender) ? Verdict.Relay : Verdict.Reject);
        // Received messages are pooled after the handler: the queue keeps copies.
        Mod.Node.On<RefineryUpdate>(Side.Guest, (c, m) => { ReceiveUpdate(Mod.Node.Clone(m)); });
        Mod.Node.On<InitialRefinery>(Side.Guest, (c, m) => ReceiveSnapshot(Mod.Node.Clone(m)));
        Mod.Snapshots.Register(Lycoreia.Snapshot.SnapStep.Refinery, ctx =>
        {
            var msg = new InitialRefinery { Revision = CurrentHostRevision };
            var counts = SceneContext.Instance ? SceneContext.Instance.GadgetDirector?._model?._itemCounts : null;
            if (counts != null)
                foreach (var pair in counts)
                {
                    if (!pair.Key) continue;
                    int id = NetworkActorManager.GetPersistentID(pair.Key);
                    if (id != 0 && RefineryMutationPolicy.IsRepresentableCount(pair.Value)) { msg.Items[id] = pair.Value; RecordSyncedCount(id, pair.Value); }
                }
            ctx.Send(msg);
        });
    }
}

/// <summary>Publishes refinery item-count changes made through the setter; the reconcile pump catches native changes that bypass it.</summary>
[HarmonyPatch(typeof(GadgetsModel), nameof(GadgetsModel.SetCount))]
internal static class RefinerySetCountPatch
{
    public static void Prefix(GadgetsModel __instance, IdentifiableType type, out int __state)
    {
        __state = 0;
        try { if (type) __state = __instance.GetCount(type); } catch { }
    }
    public static void Postfix(GadgetsModel __instance, IdentifiableType type, int __state)
    {
        try
        {
            if (!ModFacade.IsNetworkActive || HandlingPacket || !type) return;
            var now = __instance.GetCount(type); long delta = (long)now - __state; if (delta == 0) return;
            int id = NetworkActorManager.GetPersistentID(type); if (id == 0) return;
            NetworkRefineryManager.PublishLocalChange(id, delta, now);
        }
        catch (Exception e) { OwnedLog.LogWarning("refinery SetCount publish: " + e.Message); }
    }
}

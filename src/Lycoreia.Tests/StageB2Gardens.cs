// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.Snapshot;
using Lycoreia.World;

namespace Lycoreia.Tests
{
    static partial class StageB2Tests
    {
        static partial void RegisterGardensImpl()
        {
            T.Test("gardens/two guests plant and clear: host revision order wins, a late timer never removes the crop, only the owner publishes timers", () =>
            {
                var (w, h, g) = Rig(2); var lh = new KeyLeases(h, null); var gh = new GardenSync(h) { Leases = lh }; var g0 = new GardenSync(g[0]) { Leases = new KeyLeases(g[0], null) }; var g1 = new GardenSync(g[1]) { Leases = new KeyLeases(g[1], null) };
                int timers1 = 0; g1.OnTimer = m => timers1++;
                lh.HostClaimAll(new[] { "plot1" });
                g0.LocalCrop("plot1", "plot1", 7); g1.LocalCrop("plot1", "plot1", 0); w.Net.Step(400);
                T.Eq(gh.CropOf("plot1"), g0.CropOf("plot1")); T.Eq(gh.CropOf("plot1"), g1.CropOf("plot1"), "everyone converges on the host order");
                g1.LocalCrop("plot1", "plot1", 9); w.Net.Step(300); T.Eq(9, g0.CropOf("plot1")); T.Eq(9, g1.CropOf("plot1"), "the originator gets its own result back");
                g[0].Node.SendToHost(new GardenUpdate { GardenId = "plot1", NextSpawnTime = 5, StoredWater = 1 }); w.Net.Step(300); T.Eq(0, timers1, "a non-owner's timer is rejected");
                gh.LocalTimer("plot1", 10, 2, true); w.Net.Step(300); T.Eq(1, timers1); T.Eq(9, g1.CropOf("plot1"), "a timer never touches the crop");
                h.Node.SendToAll(new GardenPlant { GardenId = "plot1", ActorType = 0, Revision = 2 }); w.Net.Step(300); T.Eq(9, g1.CropOf("plot1"), "an older delayed clear cannot roll the crop back");
                g[0].Node.SendToHost(new GardenUpdate { GardenId = "plot1", NextSpawnTime = double.NaN, StoredWater = 1 }); w.Net.Step(200); T.Eq(1, timers1, "non-finite timers rejected");
            });
            T.Test("gardens/snapshot crop: stale only against a newer crop event; timer revisions never supersede it; empty clears", () =>
            {
                var (w, h, g) = Rig(1); var gg = new GardenSync(g[0]); var applied = new List<(string, int)>(); gg.OnCrop = (id, t) => applied.Add((id, t));
                h.Node.SendToAll(new GardenPlant { GardenId = "a", ActorType = 4, Revision = 5 }); w.Net.Step(300); T.Eq(4, gg.CropOf("a"));
                T.Eq((int?)4, gg.ApplySnapshotCrop("a", 3, 2), "an older snapshot keeps the newer live crop"); T.Eq(4, gg.CropOf("a"));
                h.Node.SendToAll(new GardenUpdate { GardenId = "b", NextSpawnTime = 1, StoredWater = 0, Revision = 9 }); w.Net.Step(300);
                T.Eq(6, gg.ApplySnapshotCrop("b", 4, 6), "a timer-only revision does not make the snapshot crop stale"); T.Eq(6, gg.CropOf("b"));
                T.Eq(0, gg.ApplySnapshotCrop("b", 12, 0), "an empty-plot snapshot clears"); T.Eq(0, gg.CropOf("b"));
                h.Node.SendToAll(new GardenPlant { GardenId = "b", ActorType = 3, Revision = 11 }); w.Net.Step(300); T.Eq(0, gg.CropOf("b"), "a live crop older than the snapshot is ignored");
                T.Eq(null, gg.ApplySnapshotCrop("c", 0, 5), "revision 0 snapshots are ignored");
            });
            T.Test("gardens/leases: refused over a connected owner, taken after a release or when the owner leaves, the host counts as connected, late joiners learn owners", () =>
            {
                var (w, h, g) = Rig(2); var snap = new SnapshotHost(h); var lh = new KeyLeases(h, snap); var l0 = new KeyLeases(g[0], null); var l1 = new KeyLeases(g[1], null);
                lh.HostClaimAll(new[] { "gA", "gB" }); T.True(lh.ShouldRun("gA")); T.True(lh.ShouldRun("never-seen"), "unknown gardens run on the host"); T.False(l0.ShouldRun("never-seen"));
                l0.Claim("gA"); w.Net.Step(300); T.Eq(h.LocalId, lh.OwnerOf("gA"), "the host counts as connected");
                lh.Release("gA"); w.Net.Step(300); T.Eq("", l0.OwnerOf("gA")); T.False(lh.ShouldRun("gA"), "released: nobody runs it until a claim");
                l0.Claim("gA"); w.Net.Step(300); T.Eq(g[0].LocalId, lh.OwnerOf("gA")); T.True(l0.ShouldRun("gA")); T.Eq(g[0].LocalId, l1.OwnerOf("gA"));
                l1.Claim("gA"); w.Net.Step(300); T.Eq(g[0].LocalId, lh.OwnerOf("gA"), "refused while the owner is connected");
                g[1].Node.SendToHost(new GardenOwnership { GardenId = "gA", OwnerId = "" }); w.Net.Step(300); T.Eq(g[0].LocalId, lh.OwnerOf("gA"), "only the owner releases");
                g[1].Node.SendToHost(new GardenOwnership { GardenId = "gB", OwnerId = g[0].LocalId }); w.Net.Step(300); T.Eq(h.LocalId, lh.OwnerOf("gB"), "claims are only for oneself");
                var late = Late(w); var ll = new KeyLeases(late, null); JoinLate(w, late); T.Eq(g[0].LocalId, ll.OwnerOf("gA")); T.Eq(h.LocalId, ll.OwnerOf("gB"));
                g[0].Disconnect(); w.Net.Step(800); l1.Claim("gA"); w.Net.Step(300); T.Eq(g[1].LocalId, lh.OwnerOf("gA"), "a disconnected owner's garden can be taken");
            });
        }
    }
}

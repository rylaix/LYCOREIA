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
        static partial void RegisterTeleportersImpl()
        {
            T.Test("teleporters/replacing a destination drops the old edge; tombstones keep unlinked edges out; a guest publishes nothing before its snapshot", () =>
            {
                var (w, h, g) = Rig(1); var snap = new SnapshotHost(h); var th = new TeleporterLinks(h, snap); var t0 = new TeleporterLinks(g[0], null);
                var unlinked = new List<(string, string)>(); t0.OnUnlink = (s, d) => unlinked.Add((s, d));
                t0.LocalLink("A", "X", 1); w.Net.Step(300); T.Eq(0, th.Links.Count, "no publishing before the first snapshot");
                th.LocalLink("A", "X", 1); w.Net.Step(300); T.Eq("X", t0.Links["A"].dest);
                th.LocalLink("A", "Y", 1); w.Net.Step(300); T.Eq("Y", t0.Links["A"].dest); T.True(unlinked.Contains(("A", "X")), "old edge removed on the guest");
                th.LocalLink("B", "B", 1); T.False(th.Links.ContainsKey("B"), "self links are invalid");
                th.LocalUnlink("A", null); w.Net.Step(300); T.False(t0.Links.ContainsKey("A")); T.True(th.IsTombstoned("A", "Y"));
                var late = Late(w); var tl = new TeleporterLinks(late, null); JoinLate(w, late); T.False(tl.Links.ContainsKey("A"), "an unlinked edge never reappears for a joiner"); T.True(tl.SnapshotApplied);
                th.LocalLink("A", "Y", 1); w.Net.Step(300); T.False(th.IsTombstoned("A", "Y"), "a new authoritative link clears the tombstone"); T.Eq("Y", tl.Links["A"].dest);
                tl.LocalLink("C", "Z", 2); w.Net.Step(300); T.Eq("Z", th.Links["C"].dest, "after the snapshot a guest's link reaches the host");
            });
            T.Test("teleporters/the snapshot is authoritative: guest links the host does not have are removed", () =>
            {
                var w = new World(); var h = w.StartHost(); var snap = new SnapshotHost(h); var th = new TeleporterLinks(h, snap); th.LocalLink("A", "X", 1);
                var gs = w.AddGuest("G"); var tg = new TeleporterLinks(gs, null); var removed = new List<string>(); tg.OnUnlink = (s, d) => removed.Add(s + d);
                w.Join(gs); T.True(w.Net.RunUntil(() => gs.Joined, 4000)); w.Net.Step(500); T.Eq("X", tg.Links["A"].dest); T.True(tg.SnapshotApplied);
            });
            T.Test("gadget links/two warp depot pairs: picking one up removes only its own partner; pairs reciprocal; unknown endpoints deferred up to 15 s", () =>
            {
                var (w, h, g) = Rig(1); var snap = new SnapshotHost(h); long clock = 0; var known = new HashSet<long> { 1, 2, 3, 4 };
                var lh = new GadgetLinks(h, snap, () => clock) { Authorize = (a, b, s) => !known.Contains(a) || !known.Contains(b) ? (bool?)null : true };
                var l0 = new GadgetLinks(g[0], null); var unpaired0 = new List<long>(); l0.OnUnpair = a => unpaired0.Add(a);
                lh.LocalPair(1, 2); l0.LocalPair(3, 4); w.Net.Step(300);
                T.Eq(2L, l0.PartnerOf(1)); T.Eq(1L, l0.PartnerOf(2)); T.Eq(4L, lh.PartnerOf(3)); T.Eq(3L, lh.PartnerOf(4));
                lh.GadgetDestroyed(1); w.Net.Step(300); T.Eq(0L, l0.PartnerOf(1)); T.Eq(0L, l0.PartnerOf(2)); T.Eq(4L, l0.PartnerOf(3), "the other pair is untouched"); T.Eq(3L, l0.PartnerOf(4));
                l0.LocalPair(7, 8); w.Net.Step(300); T.Eq(1, lh.PendingCount, "unknown endpoints are deferred"); T.Eq(0L, lh.PartnerOf(7));
                known.Add(7); known.Add(8); clock += 5000; lh.Revalidate(); w.Net.Step(300); T.Eq(8L, l0.PartnerOf(7), "resolved when the spawn records arrived");
                l0.LocalPair(9, 10); w.Net.Step(300); clock += 16_000; known.Add(9); known.Add(10); lh.Revalidate(); T.Eq(0L, lh.PartnerOf(9), "expired after 15 s");
                lh.LocalPair(3, 7); w.Net.Step(300); T.Eq(7L, l0.PartnerOf(3)); T.Eq(0L, l0.PartnerOf(4), "re-pairing detaches the old partners"); T.Eq(0L, l0.PartnerOf(8));
                var late = Late(w); var ll = new GadgetLinks(late, null); JoinLate(w, late); T.Eq(7L, ll.PartnerOf(3)); T.Eq(3L, ll.PartnerOf(7));
                g[0].Node.SendToHost(new GadgetLinking { GadgetId = 5, PartnerId = 5 }); w.Net.Step(200); T.Eq(0L, lh.PartnerOf(5), "self pair rejected");
                T.True(Lycoreia.Owned.GadgetLinkPairPolicy.VerifyRules()); T.True(Lycoreia.Owned.TeleporterLinkSnapshotPolicy.VerifyRules());
            });
        }
    }
}

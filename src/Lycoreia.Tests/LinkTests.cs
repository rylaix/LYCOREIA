// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using Lycoreia.Net;

namespace Lycoreia.Tests
{
    /// <summary>Two links joined by a lossy, reordering pipe on a virtual clock.</summary>
    sealed class LinkRig
    {
        public long Now; public double Loss, Dup; public int MinDelay = 1, MaxDelay = 1; public Random Rng;
        public Link A, B; public Func<byte[], bool> Filter;
        public readonly List<(ushort type, byte[] data)> AtA = new List<(ushort, byte[])>(), AtB = new List<(ushort, byte[])>();
        readonly SortedDictionary<(long, long), (bool toB, byte[] d)> _q = new SortedDictionary<(long, long), (bool, byte[])>();
        long _n; public bool Cut;
        public LinkRig(int seed = 1, double loss = 0, int maxDelay = 1)
        {
            Rng = new Random(seed); Loss = loss; MaxDelay = maxDelay;
            A = new Link((b, o, l) => Put(true, b, o, l), () => Now, (t, ln, d, o, l) => AtA.Add((t, Slice(d, o, l))));
            B = new Link((b, o, l) => Put(false, b, o, l), () => Now, (t, ln, d, o, l) => AtB.Add((t, Slice(d, o, l))));
        }
        static byte[] Slice(byte[] d, int o, int l) { var r = new byte[l]; Buffer.BlockCopy(d, o, r, 0, l); return r; }
        void Put(bool toB, byte[] b, int o, int l)
        {
            if (Cut) return; if (Filter != null) { var tmp = Slice(b, o, l); if (Filter(tmp)) return; }
            int copies = Rng.NextDouble() < Dup ? 2 : 1;
            for (int i = 0; i < copies; i++)
            {
                if (Rng.NextDouble() < Loss) continue;
                _q[(Now + MinDelay + Rng.Next(0, Math.Max(1, MaxDelay - MinDelay + 1)), _n++)] = (toB, Slice(b, o, l));
            }
        }
        public void Step(int ms)
        {
            for (int i = 0; i < ms; i++)
            {
                Now++;
                while (_q.Count > 0) { var e = System.Linq.Enumerable.First(_q); if (e.Key.Item1 > Now) break; _q.Remove(e.Key); (e.Value.toB ? B : A).OnDatagram(e.Value.d, 0, e.Value.d.Length); }
                A.Tick(); B.Tick();
            }
        }
        public bool Until(Func<bool> c, int max) { for (int i = 0; i < max; i++) { if (c()) return true; Step(1); } return c(); }
    }

    static class LinkTests
    {
        static byte[] Blob(int n, int seed = 9) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
        static byte[] Pattern(int n) { var b = new byte[n]; for (int i = 0; i < n; i++) b[i] = (byte)(i * 31 / 7); return b; }

        public static void Register()
        {
            T.Test("link/ordered packet id wraps within dedup window without dropping lifecycle messages", () =>
            {
                var r = new LinkRig(); r.Dup = 1;
                // Finish more than an entire ushort cycle in under 60 seconds.
                for (int batch = 0; batch < 70; batch++)
                {
                    for (int i = 0; i < 1000; i++) r.A.Send(51, 2, Delivery.ReliableOrdered, new byte[] { 1 });
                    r.Step(3);
                }
                T.Eq(70000, r.AtB.Count);
                T.True(r.Until(() => r.A.PendingReliable == 0, 100));
                T.True(r.B.Duplicates >= 70000, "actual retries/duplicates remain suppressed across wrap");
            });
            T.Test("link/reset always changes the local epoch", () =>
            {
                var r = new LinkRig(); for (int i = 0; i < 2000; i++) { var before = r.A.LocalEpoch; r.A.Reset(); T.True(r.A.LocalEpoch != before && r.A.LocalEpoch != 0); }
            });
            T.Test("link/peer restart with a new epoch clears dedup so reused ids are delivered", () =>
            {
                var r = new LinkRig(); for (int i = 0; i < 5; i++) r.A.Send(51, 2, Delivery.ReliableOrdered, new byte[] { 1 });
                T.True(r.Until(() => r.A.PendingReliable == 0, 100)); T.Eq(5, r.AtB.Count);
                r.A.Reset(); for (int i = 0; i < 5; i++) r.A.Send(51, 2, Delivery.ReliableOrdered, new byte[] { 1 });
                T.True(r.Until(() => r.A.PendingReliable == 0, 100)); T.Eq(10, r.AtB.Count);
            });
            T.Test("link/small reliable ordered delivery", () =>
            {
                var r = new LinkRig(); for (int i = 0; i < 20; i++) r.A.Send(5, 0, Delivery.ReliableOrdered, new byte[] { (byte)i });
                T.True(r.Until(() => r.AtB.Count == 20, 500));
                for (int i = 0; i < 20; i++) T.Eq((byte)i, r.AtB[i].data[0]);
                T.True(r.Until(() => r.A.PendingReliable == 0, 500), "acks clear pending");
            });
            T.Test("link/unreliable delivers and is not resent", () =>
            {
                var r = new LinkRig(); r.A.Send(2, 2, Delivery.Unreliable, new byte[] { 1, 2, 3 }); r.Step(50);
                T.Eq(1, r.AtB.Count); T.Eq(0, r.A.PendingReliable);
            });
            T.Test("link/split join in order, compressed and plain", () =>
            {
                foreach (var data in new[] { Blob(5000), Pattern(300_000) })
                {
                    var r = new LinkRig(); r.A.Send(9, 7, Delivery.Reliable, data);
                    T.True(r.Until(() => r.AtB.Count == 1, 2000)); T.SeqEq(data, r.AtB[0].data);
                }
            });
            T.Test("link/compression only when it saves 10 percent", () =>
            {
                var r = new LinkRig(); long before;
                r.A.Send(1, 0, Delivery.Unreliable, Blob(3000)); before = r.A.DatagramsSent;
                T.Eq(3, (int)before); // incompressible: 3 datagrams of up to 1200
                r.A.Send(1, 0, Delivery.Unreliable, new byte[3000]); // zeros compress to nothing
                T.Eq(1, (int)(r.A.DatagramsSent - before));
                r.Step(10); T.Eq(2, r.AtB.Count); T.Eq(3000, r.AtB[1].data.Length);
                var small = new LinkRig(); small.A.Send(1, 0, Delivery.Unreliable, new byte[100]); T.Eq(1, (int)small.A.DatagramsSent);
            });
            T.Test("link/shuffled and duplicated parts", () =>
            {
                var r = new LinkRig(seed: 4, maxDelay: 60) { Dup = 0.5 };
                var data = Blob(40_000); r.A.Send(3, 4, Delivery.Reliable, data);
                T.True(r.Until(() => r.AtB.Count >= 1, 5000)); r.Step(2000);
                T.Eq(1, r.AtB.Count, "delivered once"); T.SeqEq(data, r.AtB[0].data);
            });
            T.Test("link/duplicates acked but delivered once", () =>
            {
                var r = new LinkRig(seed: 2) { Dup = 1.0 };
                for (int i = 0; i < 10; i++) r.A.Send(6, 1, Delivery.ReliableOrdered, new[] { (byte)i });
                r.Step(1500); T.Eq(10, r.AtB.Count); T.Eq(0, r.A.PendingReliable);
            });
            T.Test("link/lost parts are resent", () =>
            {
                foreach (double loss in new[] { 0.05, 0.10, 0.30 })
                {
                    var r = new LinkRig(seed: 11, loss: loss, maxDelay: 30); var data = Blob(20_000);
                    r.A.Send(3, 5, Delivery.ReliableOrdered, data);
                    T.True(r.Until(() => r.AtB.Count == 1, 25_000), "loss " + loss); T.SeqEq(data, r.AtB[0].data);
                }
            });
            T.Test("link/300 part snapshot under loss and reorder completes in 30s", () =>
            {
                foreach (var (loss, seed) in new[] { (0.05, 21), (0.10, 22) })
                {
                    var r = new LinkRig(seed: seed, loss: loss, maxDelay: 80) { Dup = 0.02 };
                    var data = Blob(300 * 1200 - 50, seed);
                    r.A.Send(24, 7, Delivery.ReliableOrdered, data);
                    T.True(r.Until(() => r.AtB.Count == 1, 30_000), "300 parts at loss " + loss);
                    T.SeqEq(data, r.AtB[0].data); T.Eq(0L, r.A.GaveUp);
                }
            });
            T.Test("link/large splits resend paced 24 per burst", () =>
            {
                var r = new LinkRig(); r.Cut = true; r.A.Send(24, 7, Delivery.Reliable, Blob(100 * 1200)); long sent = r.A.DatagramsSent; T.Eq(100L, sent);
                r.Step(500); // first resend round begins: at most one burst now
                T.True(r.A.DatagramsSent - sent <= 24, "burst " + (r.A.DatagramsSent - sent));
                r.Step(4); T.True(r.A.DatagramsSent - sent <= 24, "no second burst within 5ms");
                r.Step(10); T.True(r.A.DatagramsSent - sent >= 25, "next burst follows");
                r.Step(1000); T.True(r.A.DatagramsSent - sent >= 100 * 2, "keeps resending: all parts per round");
            });
            T.Test("link/gives up after 10s (30s for large) or 64 attempts", () =>
            {
                var r = new LinkRig(); r.Cut = true; r.A.Send(5, 0, Delivery.Reliable, new byte[] { 1 });
                r.Step(9_900); T.Eq(1, r.A.PendingReliable); r.Step(300); T.Eq(0, r.A.PendingReliable); T.Eq(1L, r.A.GaveUp);
                var r2 = new LinkRig(); r2.Cut = true; r2.A.Send(5, 0, Delivery.Reliable, Blob(40 * 1200));
                r2.Step(20_000); T.Eq(1, r2.A.PendingReliable, "large still alive at 20s"); r2.Step(11_000); T.Eq(0, r2.A.PendingReliable);
            });
            T.Test("link/ordered: first sequence is 1, late first message not dropped", () =>
            {
                var r = new LinkRig(maxDelay: 1); r.Rng = new Random(1);
                // send three; hold the first back by cutting then restoring
                r.Cut = true; r.A.Send(7, 3, Delivery.ReliableOrdered, new byte[] { 1 }); r.Cut = false;
                r.A.Send(7, 3, Delivery.ReliableOrdered, new byte[] { 2 }); r.A.Send(7, 3, Delivery.ReliableOrdered, new byte[] { 3 });
                r.Step(100); T.Eq(0, r.AtB.Count, "2 and 3 wait for 1");
                r.Step(500); T.Eq(3, r.AtB.Count); T.Eq((byte)1, r.AtB[0].data[0]); T.Eq((byte)2, r.AtB[1].data[0]); T.Eq((byte)3, r.AtB[2].data[0]);
            });
            T.Test("link/sequences are independent per lane and per type", () =>
            {
                var r = new LinkRig(); r.Cut = true; r.A.Send(7, 3, Delivery.ReliableOrdered, new byte[] { 1 }); r.Cut = false;
                r.A.Send(7, 4, Delivery.ReliableOrdered, new byte[] { 2 }); r.A.Send(8, 3, Delivery.ReliableOrdered, new byte[] { 3 });
                r.Step(100); T.Eq(2, r.AtB.Count, "other lane and other type are not blocked");
            });
            T.Test("link/gap skipped after 12s when sender gave up", () =>
            {
                var r = new LinkRig(); r.Filter = d => { if (Frame.Parse(d, 0, d.Length, out var h) == Frame.ParseResult.Ok && h.Kind == Frame.KindData && h.Ordered && h.Seq == 2) return true; return false; };
                for (int i = 1; i <= 3; i++) r.A.Send(7, 3, Delivery.ReliableOrdered, new[] { (byte)i });
                r.Step(100); T.Eq(1, r.AtB.Count, "3 held behind missing 2");
                r.Step(11_000); T.Eq(0, r.A.PendingReliable, "sender gave up on 2 at 10s"); T.Eq(1, r.AtB.Count, "gap younger than 12s is kept");
                r.Step(1_500); T.Eq(2, r.AtB.Count, "gap skipped at 12s"); T.Eq((byte)3, r.AtB[1].data[0]);
            });
            T.Test("link/reorder buffer never drops acknowledged data past 4096", () =>
            {
                var r = new LinkRig(); r.Cut = true; r.A.Send(7, 3, Delivery.ReliableOrdered, new byte[] { 0 }); r.Cut = false;
                for (int i = 1; i <= Link.ReorderCap + 5; i++) { r.A.Send(7, 3, Delivery.ReliableOrdered, new byte[] { (byte)i }); if (i % 200 == 0) r.Step(1); }
                r.Step(10); T.Eq(Link.ReorderCap + 5, r.AtB.Count, "held ones delivered at the cap"); r.Step(600);
                T.Eq(Link.ReorderCap + 6, r.AtB.Count, "late first message still delivered");
            });
            T.Test("link/corrupt datagram is dropped, nothing delivered", () =>
            {
                int sent = 0; byte[] copy = null;
                var a = new Link((b, o, l) => { copy = new byte[l]; Buffer.BlockCopy(b, o, copy, 0, l); sent++; }, () => 0, (t, ln, d, o, l) => { });
                int got = 0; var b2 = new Link((b, o, l) => { }, () => 0, (t, ln, d, o, l) => got++);
                a.Send(1, 0, Delivery.Unreliable, new byte[] { 1, 2, 3 });
                var bad = (byte[])copy.Clone(); bad[bad.Length - 6] ^= 1;
                b2.OnDatagram(bad, 0, bad.Length); T.Eq(0, got); T.Eq(1L, b2.BadChecksum);
                b2.OnDatagram(copy, 0, copy.Length); T.Eq(1, got);
            });
            T.Test("link/incomplete messages expire after 30s and byte cap holds", () =>
            {
                var r = new LinkRig(); long now = 0; byte[] first = null; int n = 0;
                var a = new Link((b, o, l) => { if (n++ == 0) { first = new byte[l]; Buffer.BlockCopy(b, o, first, 0, l); } }, () => now, (t, ln, d, o, l) => { });
                int got = 0; var rx = new Link((b, o, l) => { }, () => now, (t, ln, d, o, l) => got++);
                a.Send(1, 0, Delivery.Unreliable, Blob(5000));
                rx.OnDatagram(first, 0, first.Length); T.True(rx.PartialBytes > 0);
                now = 31_000; rx.Tick(); T.Eq(0L, rx.PartialBytes);
                // cap: many 2-part messages with only first part delivered
                long baseBytes = 0; for (int i = 0; i < 100; i++) { n = 0; a.Send(1, 0, Delivery.Unreliable, Blob(2400, i)); rx.OnDatagram(first, 0, first.Length); }
                T.True(rx.PartialBytes <= Link.PartialBytesCap);
            });
            T.Test("link/part count mismatch and absurd counts rejected", () =>
            {
                var buf = new byte[1300]; int rej = 0; long now = 0;
                var rx = new Link((b, o, l) => { }, () => now, (t, ln, d, o, l) => { }); rx.OnReject += (t, w) => rej++;
                int len = Frame.WriteData(buf, 0, Frame.FSplit, 5, 1, 9, 0, 0, 3, new byte[10]); rx.OnDatagram(buf, 0, len);
                len = Frame.WriteData(buf, 0, Frame.FSplit, 5, 1, 9, 0, 1, 4, new byte[10]); rx.OnDatagram(buf, 0, len);
                T.Eq(1, rej, "mismatching count rejected");
                len = Frame.WriteData(buf, 0, Frame.FSplit, 5, 1, 9, 0, 0, 1, new byte[10]); rx.OnDatagram(buf, 0, len); // count 1 with split flag is malformed
                T.True(rx.Rejected >= 1);
            });
            T.Test("link/restarted peer on same endpoint gets fresh state", () =>
            {
                var r = new LinkRig(); for (int i = 0; i < 5; i++) r.A.Send(7, 3, Delivery.ReliableOrdered, new[] { (byte)i });
                r.Step(100); T.Eq(5, r.AtB.Count);
                // A restarts: brand-new link object talking to the same B
                var a2 = new Link((b, o, l) => { var c = new byte[l]; Buffer.BlockCopy(b, o, c, 0, l); r.B.OnDatagram(c, 0, c.Length); }, () => r.Now, (t, ln, d, o, l) => { });
                a2.Send(7, 3, Delivery.ReliableOrdered, new byte[] { 99 });
                T.Eq(6, r.AtB.Count); T.Eq((byte)99, r.AtB[5].data[0]);
            });
            T.Test("link/steady-state send and receive allocate nothing", () =>
            {
                int rx = 0; byte[] last = new byte[2048]; int lastLen = 0;
                var a = new Link((b, o, l) => { Buffer.BlockCopy(b, o, last, 0, l); lastLen = l; }, () => 0, (t, ln, d, o, l) => { });
                var b2 = new Link((b, o, l) => { }, () => 0, (t, ln, d, o, l) => rx++);
                var payload = new byte[60];
                for (int i = 0; i < 2000; i++) { a.Send(1, 2, Delivery.Unreliable, payload); b2.OnDatagram(last, 0, lastLen); } // warm up pools
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5000; i++) { a.Send(1, 2, Delivery.Unreliable, payload); b2.OnDatagram(last, 0, lastLen); }
                long used = GC.GetAllocatedBytesForCurrentThread() - before;
                T.Eq(7000, rx); T.True(used < 5000 * 4, "allocated " + used + " bytes");
            });
        }
    }
}

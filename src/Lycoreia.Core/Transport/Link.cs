// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Lycoreia.Net
{
    /// <summary>Called when a complete message has arrived and passed ordering. The buffer is only valid during the call.</summary>
    public delegate void MessageSink(ushort type, byte lane, byte[] data, int offset, int length);

    /// <summary>
    /// One remote endpoint's reliability state: framing, split/join, acknowledgements, resends, dedup and ordering.
    /// Pure logic over a raw-send callback and a millisecond clock, so tests drive it with virtual time.
    /// </summary>
    public sealed class Link
    {
        // Reliable means reliable while the peer is alive. Until 2.0.1 a message gave up after 10 s and was silently dropped while the
        // session stayed up (the host only drops a silent guest after 30 s): on lossy or filtered paths (TSPU, VPN proxies) any blackout
        // of 10-30 s lost state changes for good (issue 2026-11-09). Now sending outlasts the session's own drop timeout, resends back
        // off after the first rounds so a blackout does not flood the path, and the receiver waits as long before skipping a gap.
        public const int ResendIntervalMs = 500, BackoffResendIntervalMs = 1000, FastResendRounds = 6;
        public const int GiveUpMs = 32_000, GiveUpLargeMs = 45_000, MaxAttempts = 96;
        public const int LargeParts = 32, BurstParts = 24, BurstGapMs = 5;
        public const int ReorderCap = 4096, GapSkipMs = 34_000, PartialTtlMs = 50_000;
        public const long PartialBytesCap = 32L * 1024 * 1024;
        const int DedupWindowMs = 60_000;

        public delegate void RawSend(byte[] buf, int offset, int length);
        readonly RawSend _send; readonly Func<long> _clock; readonly MessageSink _sink;
        readonly object _gate = new object();

        // counters for diagnostics and tests
        public long DatagramsSent, DatagramsReceived, BadChecksum, Duplicates, GaveUp, Skipped, Resent, Rejected;
        public event Action<ushort, string> OnReject;      // (type, reason) - already rate-limit friendly: raised per event
        public event Action<ushort> OnGiveUp;

        // ---- send state
        ushort _relId, _unrelId;
        readonly Dictionary<int, uint> _sendSeq = new Dictionary<int, uint>();
        readonly Dictionary<ushort, Outgoing> _out = new Dictionary<ushort, Outgoing>();
        readonly List<ushort> _outKeys = new List<ushort>();
        readonly byte[] _scratch = new byte[Frame.MaxPayload + Frame.MaxHeader + 8];

        sealed class Outgoing
        {
            public ushort Id, Type; public byte[][] Parts; public int[] Lens; public bool[] Acked; public int AckedCount;
            public long First, RoundStart, NextBurst; public int Attempts; public bool Large;
            public int Cursor = -1, Sent, Offset; // Cursor<0: idle; else position inside current round
        }

        // ---- receive state
        readonly List<(ushort id, ushort part)> _acksOut = new List<(ushort, ushort)>();
        readonly Dictionary<ushort, Partial> _partials = new Dictionary<ushort, Partial>();
        long _partialBytes;
        // Ordered streams outlive the 16-bit packet id cycle at normal actor rates.
        readonly Dictionary<(ushort id, ushort type, byte lane, uint seq), long> _done = new();
        long _lastPurge;
        readonly Dictionary<int, Order> _order = new Dictionary<int, Order>();

        sealed class Partial { public ushort Type; public byte Lane, Flags; public uint Seq; public byte[][] Parts; public int Got, Count; public long Bytes, First; }
        sealed class Order { public uint Next = 1; public SortedDictionary<uint, Held> Held = new SortedDictionary<uint, Held>(Cmp.I); public long GapSince; }
        sealed class Held { public ushort Type; public byte Lane; public byte[] Data; public int Len; }
        sealed class Cmp : IComparer<uint> { public static readonly Cmp I = new Cmp(); public int Compare(uint a, uint b) => (int)(a - b); }

        /// <summary>System.Random per-link-lifetime tag so a peer that restarted on the same endpoint is recognised and its old state dropped.</summary>
        public ushort LocalEpoch;
        ushort _remoteEpoch;
        static readonly System.Random EpochRng = new System.Random();
        static ushort NewEpoch(ushort avoid = 0) { lock (EpochRng) { ushort e; do e = (ushort)EpochRng.Next(1, 65536); while (e == avoid); return e; } }   // avoid: a reset must never reuse the previous epoch or the peer keeps stale dedup state

        public Link(RawSend send, Func<long> clock, MessageSink sink) { _send = send; _clock = clock; _sink = sink; LocalEpoch = NewEpoch(); }

        public int PendingReliable { get { lock (_gate) return _out.Count; } }
        public long PartialBytes { get { lock (_gate) return _partialBytes; } }

        /// <summary>Forget everything (new connection from the same endpoint).</summary>
        public void Reset()
        {
            lock (_gate)
            {
                LocalEpoch = NewEpoch(LocalEpoch); _remoteEpoch = 0; _relId = 0; _unrelId = 0; _sendSeq.Clear(); _out.Clear(); _acksOut.Clear(); _partials.Clear(); _partialBytes = 0;
                _done.Clear(); _order.Clear();
            }
        }

        // =============================================================== send
        public void Send(ushort type, byte lane, Delivery mode, ReadOnlySpan<byte> payload)
        {
            lock (_gate)
            {
                long now = _clock();
                byte flags = 0; ReadOnlySpan<byte> body = payload; byte[] comp = null;
                if (payload.Length > Frame.CompressMinBytes)
                {
                    comp = ArrayPool<byte>.Shared.Rent(Lz4.MaxCompressedSize(payload.Length) + 4);
                    int n = Lz4.Compress(payload, comp.AsSpan(4));
                    if (n > 0 && n + 4 <= payload.Length - payload.Length / 10)
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(comp, payload.Length);
                        body = new ReadOnlySpan<byte>(comp, 0, n + 4); flags |= Frame.FCompressed;
                    }
                }
                try
                {
                    bool reliable = mode != Delivery.Unreliable;
                    if (reliable) flags |= Frame.FReliable;
                    uint seq = 0;
                    if (mode == Delivery.ReliableOrdered)
                    {
                        flags |= Frame.FOrdered;
                        int key = (lane << 16) | type;
                        _sendSeq.TryGetValue(key, out uint s); s = s == uint.MaxValue ? 1 : s + 1; _sendSeq[key] = s; seq = s;
                    }
                    ushort id;
                    if (reliable)
                    {
                        int scanned = 0;
                        do { _relId = _relId == ushort.MaxValue ? (ushort)1 : (ushort)(_relId + 1); }
                        while (_out.ContainsKey(_relId) && ++scanned < ushort.MaxValue);
                        if (_out.ContainsKey(_relId)) throw new WireException("reliable packet slots exhausted");
                        id = _relId;
                    }
                    else { _unrelId = _unrelId == ushort.MaxValue ? (ushort)1 : (ushort)(_unrelId + 1); id = _unrelId; }

                    int count = Math.Max(1, (body.Length + Frame.MaxPayload - 1) / Frame.MaxPayload);
                    if (count > Frame.MaxParts) throw new WireException("message too large to send");
                    if (count > 1) flags |= Frame.FSplit;
                    Outgoing o = null;
                    if (reliable)
                    {
                        o = new Outgoing { Id = id, Type = type, Parts = new byte[count][], Lens = new int[count], Acked = new bool[count], First = now, RoundStart = now, Large = count >= LargeParts };
                    }
                    for (int i = 0; i < count; i++)
                    {
                        var chunk = body.Slice(i * Frame.MaxPayload, Math.Min(Frame.MaxPayload, body.Length - i * Frame.MaxPayload));
                        if (reliable)
                        {
                            var d = new byte[Frame.MaxHeader + chunk.Length + 4];
                            int len = Frame.WriteData(d, lane, flags, LocalEpoch, type, id, seq, (ushort)i, (ushort)count, chunk);
                            o.Parts[i] = d; o.Lens[i] = len;
                            Raw(d, len);
                        }
                        else
                        {
                            int len = Frame.WriteData(_scratch, lane, flags, LocalEpoch, type, id, seq, (ushort)i, (ushort)count, chunk);
                            Raw(_scratch, len);
                        }
                    }
                    if (reliable) { _out[id] = o; o.Attempts = 1; }
                }
                finally { if (comp != null) ArrayPool<byte>.Shared.Return(comp); }
            }
        }

        void Raw(byte[] b, int len) { DatagramsSent++; _send(b, 0, len); }

        // =============================================================== receive
        public void OnDatagram(byte[] buf, int off, int len)
        {
            lock (_gate)
            {
                DatagramsReceived++;
                var pr = Frame.Parse(buf, off, len, out var h);
                if (pr == Frame.ParseResult.BadChecksum) { BadChecksum++; return; }
                if (pr != Frame.ParseResult.Ok) { Rejected++; return; }
                long now = _clock();
                if (h.Kind == Frame.KindAck) { if (h.Epoch == LocalEpoch) HandleAcks(buf, h); return; }
                if (h.Epoch != _remoteEpoch)
                {
                    bool restarted = _remoteEpoch != 0;
                    _remoteEpoch = h.Epoch;
                    _acksOut.Clear(); _partials.Clear(); _partialBytes = 0; _done.Clear(); _order.Clear();
                    if (restarted) { _out.Clear(); _sendSeq.Clear(); }
                }
                if (h.Reliable) { _acksOut.Add((h.MsgId, h.Part)); if (_acksOut.Count >= 200) FlushAcksLocked(); }
                if (h.Reliable && _done.ContainsKey((h.MsgId, h.Type, h.Lane, h.Seq))) { Duplicates++; return; }
                var payload = new ReadOnlySpan<byte>(buf, h.PayloadOffset, h.PayloadLength);
                if (!h.Split) { Complete(h.Type, h.Lane, h.Flags, h.MsgId, h.Seq, payload, now); return; }
                // split
                if (!_partials.TryGetValue(h.MsgId, out var p))
                {
                    if (h.Count > Frame.MaxParts) { Reject(h.Type, "absurd part count"); return; }
                    p = new Partial { Type = h.Type, Lane = h.Lane, Flags = h.Flags, Seq = h.Seq, Parts = new byte[h.Count][], Count = h.Count, First = now };
                    _partials[h.MsgId] = p;
                }
                else if (p.Count != h.Count || p.Type != h.Type) { Reject(h.Type, "part count mismatch"); return; }
                if (p.Parts[h.Part] != null) { Duplicates++; return; }
                p.Parts[h.Part] = payload.ToArray(); p.Got++; p.Bytes += payload.Length; _partialBytes += payload.Length;
                EnforcePartialCap(h.MsgId);
                if (p.Got == p.Count && _partials.Remove(h.MsgId))
                {
                    _partialBytes -= p.Bytes;
                    byte[] whole = ArrayPool<byte>.Shared.Rent((int)Math.Min(p.Bytes, int.MaxValue));
                    try
                    {
                        int pos = 0; foreach (var part in p.Parts) { part.CopyTo(whole, pos); pos += part.Length; }
                        Complete(p.Type, p.Lane, p.Flags, h.MsgId, p.Seq, new ReadOnlySpan<byte>(whole, 0, pos), now);
                    }
                    finally { ArrayPool<byte>.Shared.Return(whole); }
                }
            }
        }

        void EnforcePartialCap(ushort keep)
        {
            while (_partialBytes > Link.PartialBytesCap)
            {
                ushort oldest = 0; long t = long.MaxValue; bool any = false;
                foreach (var kv in _partials) if (kv.Key != keep && kv.Value.First < t) { t = kv.Value.First; oldest = kv.Key; any = true; }
                if (!any) { var self = _partials[keep]; _partialBytes -= self.Bytes; _partials.Remove(keep); return; }
                _partialBytes -= _partials[oldest].Bytes; _partials.Remove(oldest);
            }
        }

        void Reject(ushort type, string why) { Rejected++; OnReject?.Invoke(type, why); }

        void Complete(ushort type, byte lane, byte flags, ushort id, uint seq, ReadOnlySpan<byte> payload, long now)
        {
            bool reliable = (flags & Frame.FReliable) != 0;
            if (reliable) _done[(id, type, lane, seq)] = now;
            byte[] data; int len; bool pooled = false;
            if ((flags & Frame.FCompressed) != 0)
            {
                if (payload.Length < 4) { Reject(type, "short compressed"); return; }
                int orig = BinaryPrimitives.ReadInt32LittleEndian(payload);
                if (orig < 0 || orig > Frame.MaxMessageBytes) { Reject(type, "decompressed size over cap"); return; }
                data = ArrayPool<byte>.Shared.Rent(orig); pooled = true; len = orig;
                try { Lz4.Decompress(payload.Slice(4), data.AsSpan(0, orig)); }
                catch (WireException e) { ArrayPool<byte>.Shared.Return(data); Reject(type, e.Message); return; }
            }
            else { data = ArrayPool<byte>.Shared.Rent(Math.Max(1, payload.Length)); pooled = true; payload.CopyTo(data); len = payload.Length; }
            if ((flags & Frame.FOrdered) == 0) { try { _sink(type, lane, data, 0, len); } finally { if (pooled) ArrayPool<byte>.Shared.Return(data); } return; }

            int key = (lane << 16) | type;
            if (!_order.TryGetValue(key, out var st)) _order[key] = st = new Order();
            int d = (int)(seq - st.Next);
            if (d < 0)
            {
                // genuinely new (duplicates were filtered by id) but its slot was already skipped: deliver late rather than lose it
                try { _sink(type, lane, data, 0, len); } finally { ArrayPool<byte>.Shared.Return(data); }
                return;
            }
            if (d == 0)
            {
                try { _sink(type, lane, data, 0, len); } finally { ArrayPool<byte>.Shared.Return(data); }
                st.Next = Inc(st.Next); st.GapSince = 0; Drain(st, now);
                return;
            }
            st.Held[seq] = new Held { Type = type, Lane = lane, Data = data, Len = len };
            if (st.GapSince == 0) st.GapSince = now == 0 ? 1 : now;
            if (st.Held.Count > ReorderCap) SkipToLowest(st, now);
        }

        static uint Inc(uint s) => s == uint.MaxValue ? 1 : s + 1;

        void Drain(Order st, long now)
        {
            while (st.Held.Count > 0)
            {
                uint first = default; foreach (var k in st.Held.Keys) { first = k; break; }
                if (first != st.Next) { if (st.GapSince == 0) st.GapSince = now == 0 ? 1 : now; return; }
                var h = st.Held[first]; st.Held.Remove(first);
                try { _sink(h.Type, h.Lane, h.Data, 0, h.Len); } finally { ArrayPool<byte>.Shared.Return(h.Data); }
                st.Next = Inc(st.Next); st.GapSince = 0;
            }
        }

        /// <summary>Gives up on the missing sequences before the lowest held one, then drains. Acknowledged data is never dropped.</summary>
        void SkipToLowest(Order st, long now)
        {
            if (st.Held.Count == 0) return;
            uint first = default; foreach (var k in st.Held.Keys) { first = k; break; }
            Skipped++; st.Next = first; st.GapSince = 0; Drain(st, now);
        }

        // =============================================================== acks
        void HandleAcks(byte[] buf, Frame.Header h)
        {
            int p = h.PayloadOffset, end = h.PayloadOffset + h.PayloadLength;
            while (p + 4 <= end)
            {
                ushort id = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(p)); ushort part = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(p + 2)); p += 4;
                if (_out.TryGetValue(id, out var o) && part < o.Acked.Length && !o.Acked[part])
                {
                    o.Acked[part] = true; o.AckedCount++;
                    if (o.AckedCount == o.Acked.Length) _out.Remove(id);
                }
            }
        }

        void FlushAcksLocked()
        {
            int i = 0; var buf = _scratch;
            while (i < _acksOut.Count)
            {
                int p = Frame.WriteAckHeader(buf, _remoteEpoch);
                while (i < _acksOut.Count && p + 4 + 4 <= Frame.MaxPayload)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(p), _acksOut[i].id);
                    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(p + 2), _acksOut[i].part); p += 4; i++;
                }
                int len = Frame.Seal(buf, p); Raw(buf, len);
            }
            _acksOut.Clear();
        }

        // =============================================================== time
        /// <summary>Drive resends, ack flushing and expiry. Call every few milliseconds.</summary>
        public void Tick()
        {
            lock (_gate)
            {
                long now = _clock();
                if (_acksOut.Count > 0) FlushAcksLocked();
                if (_out.Count > 0)
                {
                    _outKeys.Clear(); foreach (var k in _out.Keys) _outKeys.Add(k);
                    foreach (var id in _outKeys)
                    {
                        var o = _out[id];
                        int limit = o.Large ? GiveUpLargeMs : GiveUpMs;
                        if (now - o.First > limit || o.Attempts > MaxAttempts) { _out.Remove(id); GaveUp++; OnGiveUp?.Invoke(o.Type); continue; }
                        ResendTick(o, now);
                    }
                }
                foreach (var st in _order.Values)
                    if (st.Held.Count > 0 && st.GapSince != 0 && now - st.GapSince >= GapSkipMs) SkipToLowest(st, now);
                if (now - _lastPurge >= 1000)
                {
                    _lastPurge = now;
                    List<(ushort id, ushort type, byte lane, uint seq)> dead = null;
                    foreach (var kv in _done) if (now - kv.Value > DedupWindowMs) (dead ??= new List<(ushort, ushort, byte, uint)>()).Add(kv.Key);
                    if (dead != null) foreach (var k in dead) _done.Remove(k);
                    List<ushort> exp = null;
                    foreach (var kv in _partials) if (now - kv.Value.First > PartialTtlMs) (exp ??= new List<ushort>()).Add(kv.Key);
                    if (exp != null) foreach (var k in exp) { _partialBytes -= _partials[k].Bytes; _partials.Remove(k); }
                }
            }
        }

        void ResendTick(Outgoing o, long now)
        {
            int n = o.Parts.Length;
            if (o.Cursor < 0)
            {
                if (now - o.RoundStart < (o.Attempts < FastResendRounds ? ResendIntervalMs : BackoffResendIntervalMs)) return;
                o.RoundStart = now; o.Cursor = 0; o.Attempts++; o.NextBurst = now;
                if (o.Large) o.Offset = (o.Offset + BurstParts * 7 + 1) % n; // different starting part each attempt
            }
            if (!o.Large)
            {
                for (int i = 0; i < n; i++) if (!o.Acked[i]) { Raw(o.Parts[i], o.Lens[i]); Resent++; }
                o.Cursor = -1; return;
            }
            if (now < o.NextBurst) return;
            int sent = 0;
            while (o.Cursor < n && sent < BurstParts)
            {
                int idx = (o.Offset + o.Cursor) % n; o.Cursor++;
                if (o.Acked[idx]) continue;
                Raw(o.Parts[idx], o.Lens[idx]); Resent++; sent++;
            }
            o.NextBurst = now + BurstGapMs;
            if (o.Cursor >= n) o.Cursor = -1;
        }
    }
}

// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Net;
using Lycoreia.Net;

namespace Lycoreia.Tests
{
    /// <summary>Deterministic in-memory UDP with virtual time, loss, duplication and reordering.</summary>
    public sealed class SimNet
    {
        public long Now;
        public double Loss, Dup; public int MaxDelay = 1, MinDelay = 1;
        public readonly Random Rng;
        public long Delivered, Dropped;
        readonly SortedDictionary<(long at, long n), (SimTransport to, EndPoint from, byte[] data)> _q = new SortedDictionary<(long, long), (SimTransport, EndPoint, byte[])>();
        readonly Dictionary<EndPoint, SimTransport> _eps = new Dictionary<EndPoint, SimTransport>();
        public readonly List<Node> Nodes = new List<Node>();
        long _n;
        public Func<EndPoint, EndPoint, bool> Blackhole; // (from,to) -> drop everything
        public SimNet(int seed = 1) { Rng = new Random(seed); }

        public SimTransport Open(int port) { var t = new SimTransport(this, new IPEndPoint(IPAddress.Loopback, port)); _eps[t.Local] = t; return t; }

        internal void Enqueue(SimTransport from, EndPoint to, byte[] data)
        {
            if (!_eps.TryGetValue(to, out var dest)) return;
            if (Blackhole != null && Blackhole(from.Local, to)) { Dropped++; return; }
            int copies = Rng.NextDouble() < Dup ? 2 : 1;
            for (int i = 0; i < copies; i++)
            {
                if (Rng.NextDouble() < Loss) { Dropped++; continue; }
                long at = Now + MinDelay + (MaxDelay > MinDelay ? Rng.Next(0, MaxDelay - MinDelay + 1) : 0);
                _q[(at, _n++)] = (dest, from.Local, data);
            }
        }

        /// <summary>Advance virtual time by ms (1 ms steps): deliver due datagrams, tick links, pump main-thread queues.</summary>
        public void Step(int ms, Action perMs = null)
        {
            for (int i = 0; i < ms; i++)
            {
                Now++;
                while (_q.Count > 0)
                {
                    var e = System.Linq.Enumerable.First(_q);
                    if (e.Key.at > Now) break;
                    _q.Remove(e.Key); Delivered++;
                    e.Value.to.Deliver(e.Value.from, e.Value.data);
                }
                foreach (var n in Nodes) { n.Tick(); n.Pump(); }
                perMs?.Invoke();
            }
        }
        public bool RunUntil(Func<bool> cond, int maxMs) { for (int i = 0; i < maxMs; i++) { if (cond()) return true; Step(1); } return cond(); }
    }

    public sealed class SimTransport : IDatagramTransport
    {
        readonly SimNet _net; public readonly EndPoint Local;
        public event DatagramHandler Received;
        public event Action<System.Net.Sockets.SocketError, string> Faulted;
        public bool Disposed;
        public SimTransport(SimNet n, EndPoint ep) { _net = n; Local = ep; }
        public void Send(EndPoint to, byte[] buf, int off, int len)
        {
            if (Disposed) return;
            var copy = new byte[len]; Buffer.BlockCopy(buf, off, copy, 0, len);
            _net.Enqueue(this, to, copy);
        }
        internal void Deliver(EndPoint from, byte[] data) { if (!Disposed) Received?.Invoke(from, data, 0, data.Length); }
        public void Start() { }
        public void Dispose() { Disposed = true; }
    }
}

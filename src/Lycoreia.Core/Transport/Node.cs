// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Diagnostics;

namespace Lycoreia.Net
{
    public enum Role { Offline, Host, Guest }
    public enum Side { Host = 1, Guest = 2, Both = 3 }
    /// <summary>What a handler wants done with the message after processing.</summary>
    public enum Verdict { Reject, Accept, Relay }

    /// <summary>Per-endpoint state.</summary>
    public sealed class Peer
    {
        public EndPoint EP; public Link Link;
        public string PlayerId; public string Name; public bool Registered; public bool IsHost;
        public long LastHeardMs; public long CreatedMs; public ushort ShortId; public long SyncId;
        public volatile bool Acknowledged;
        public override string ToString() => PlayerId ?? EP?.ToString();
    }

    /// <summary>What a handler receives about the message origin. The sender id comes from the endpoint, never from payload.</summary>
    public readonly struct Ctx
    {
        public readonly Node Node; public readonly Peer Sender; public readonly bool OnHost;
        public Ctx(Node n, Peer s, bool onHost) { Node = n; Sender = s; OnHost = onHost; }
        public string SenderId => Sender?.PlayerId;
    }

    public delegate Verdict Handler(in Ctx ctx, Msg msg);

    /// <summary>Scoped "applying a received message" flag; hooks check it to avoid echoing network changes back out.</summary>
    public static class Applying
    {
        [ThreadStatic] static int _depth;
        public static bool Active => _depth > 0;
        public readonly struct Scope : IDisposable { public void Dispose() { if (_depth > 0) _depth--; } }
        public static Scope Enter() { _depth++; return new Scope(); }
        /// <summary>Restores the previous depth when disposed.</summary>
        public readonly struct Saved : IDisposable { readonly int _d; internal Saved(int d) { _d = d; } public void Dispose() { _depth = _d; } }
        /// <summary>
        /// Lifts the flag for a host-run native transaction started from a handler (treasure pod, shop or upgrade purchase): its own hooks
        /// must see the native commit and publish it.
        /// </summary>
        public static Saved Suspend() { var d = _depth; _depth = 0; return new Saved(d); }
        /// <summary>Sets the flag on (depth at least 1) or off (0); used by owned code that saves and restores a bool.</summary>
        public static void Force(bool on) { _depth = on ? Math.Max(_depth, 1) : 0; }
    }

    /// <summary>
    /// Network node: owns the transport, peers, message queues and dispatch. Host and guest share it.
    /// Network threads decode messages; handlers run in Pump() on the main thread.
    /// </summary>
    public sealed class Node
    {
        public readonly MessageRegistry Registry;
        public readonly Func<long> Clock;
        public Role Role { get; private set; }
        public string LocalId;
        public IDatagramTransport Transport { get; private set; }
        public Peer HostPeer { get; private set; }
        public int MaxUnregistered = 64;

        readonly ConcurrentDictionary<EndPoint, Peer> _peers = new ConcurrentDictionary<EndPoint, Peer>();
        readonly Dictionary<ushort, List<(Side side, Handler h)>> _handlers = new Dictionary<ushort, List<(Side, Handler)>>();
        readonly ConcurrentQueue<Action> _general = new ConcurrentQueue<Action>();
        readonly ConcurrentQueue<Work> _guestQ = new ConcurrentQueue<Work>(), _hostQ = new ConcurrentQueue<Work>();
        readonly RateLimitedLog _rl;
        readonly HashSet<ushort> _unknownLogged = new HashSet<ushort>();
        struct Work { public Msg M; public MessageInfo I; public Peer P; }

        public event Action<Peer> PeerAdded, PeerRemoved;
        public event Action<ushort, string> Rejected;       // (type, reason)
        public event Action<string, double, int> SlowDispatch;
        readonly Dictionary<ushort, long> _nextSlowDispatch = new();
        public event Action<ushort, int, int, bool> PacketSeen; // (type, bytes, parts?, sent)

        public Node(MessageRegistry reg, Func<long> clock = null)
        {
            Registry = reg; Clock = clock ?? (() => Environment.TickCount64); _rl = new RateLimitedLog(Clock);
        }

        public IEnumerable<Peer> Peers => _peers.Values;
        public IEnumerable<Peer> RegisteredGuests { get { foreach (var p in _peers.Values) if (p.Registered && !p.IsHost) yield return p; } }
        public Peer FindByPlayer(string id) { foreach (var p in _peers.Values) if (p.Registered && p.PlayerId == id) return p; return null; }
        public int GuestCount { get { int n = 0; foreach (var p in _peers.Values) if (p.Registered && !p.IsHost) n++; return n; } }

        // ------------------------------------------------------------ handlers
        public void On<T>(Side side, Func<Ctx, T, Verdict> h) where T : Msg
        {
            var id = Registry.Get<T>().Id;
            AddHandler(id, side, (in Ctx c, Msg m) => h(c, (T)m));
        }
        public void On<T>(Side side, Action<Ctx, T> h) where T : Msg => On<T>(side, (c, m) => { h(c, m); return Verdict.Accept; });
        public void AddHandler(ushort id, Side side, Handler h)
        {
            lock (_handlers) { if (!_handlers.TryGetValue(id, out var l)) _handlers[id] = l = new List<(Side, Handler)>(); l.Add((side, h)); }
        }
        public bool HasHandler(ushort id, Side side)
        {
            lock (_handlers) { if (!_handlers.TryGetValue(id, out var l)) return false; foreach (var e in l) if ((e.side & side) != 0) return true; return false; }
        }

        // ------------------------------------------------------------ lifecycle
        public void Attach(Role role, IDatagramTransport t, string localId)
        {
            Detach();
            Role = role; Transport = t; LocalId = localId;
            t.Received += OnDatagram;
            t.Start();
        }
        public void Detach()
        {
            var t = Transport; Transport = null;
            if (t != null) { t.Received -= OnDatagram; t.Dispose(); }
            _peers.Clear(); HostPeer = null; Role = Role.Offline;
            while (_hostQ.TryDequeue(out _)) { } while (_guestQ.TryDequeue(out _)) { }
        }

        public Peer AddPeer(EndPoint ep, bool isHost)
        {
            Peer p = null;
            p = new Peer { EP = ep, IsHost = isHost, CreatedMs = Clock(), LastHeardMs = Clock() };
            p.Link = new Link((b, o, l) => RawSend(p, b, o, l), Clock, (type, lane, d, off, len) => OnMessage(p, type, lane, d, off, len));
            p.Link.OnReject += (t, why) => ReportReject(t, why, p);
            if (isHost) HostPeer = p;
            _peers[ep] = p;
            PeerAdded?.Invoke(p);
            return p;
        }
        public void RemovePeer(Peer p)
        {
            if (_peers.TryRemove(p.EP, out _)) PeerRemoved?.Invoke(p);
            p.Registered = false;
        }
        void RawSend(Peer p, byte[] b, int o, int l) { var t = Transport; t?.Send(p.EP, b, o, l); }

        /// <summary>Drive resends and liveness. Call from a timer thread every few ms.</summary>
        public event Action Ticked;
        public void Tick()
        {
            foreach (var p in _peers.Values) p.Link.Tick();
            Ticked?.Invoke();
        }

        // ------------------------------------------------------------ receive
        void OnDatagram(EndPoint from, byte[] buf, int off, int len)
        {
            if (!_peers.TryGetValue(from, out var p))
            {
                if (Role != Role.Host) return; // guests only talk to the host
                int un = 0; foreach (var q in _peers.Values) if (!q.Registered) un++;
                if (un >= MaxUnregistered) return;
                p = AddPeer(from, false);
            }
            p.LastHeardMs = Clock();
            p.Link.OnDatagram(buf, off, len);
        }

        void ReportReject(ushort type, string why, Peer p)
        {
            Rejected?.Invoke(type, why);
            var info = Registry.Get(type);
            _rl.Warn($"reject:{type}:{p.EP}", $"dropped {(info?.Name ?? "type " + type)} from {(p.PlayerId ?? "unregistered peer")}: {why}");
        }

        void OnMessage(Peer p, ushort type, byte lane, byte[] data, int off, int len)
        {
            var info = Registry.Get(type);
            if (info == null)
            {
                bool first; lock (_unknownLogged) first = _unknownLogged.Add(type);
                if (first) Log.Warn($"ignored message of unknown type {type} (peer build mismatch?)");
                return;
            }
            PacketSeen?.Invoke(type, len, 1, false);
            bool onHost = Role == Role.Host;
            if (onHost)
            {
                if ((info.Dir & Direction.GuestToHost) == 0) { ReportReject(type, "wrong direction", p); return; }
                if (!p.Registered && !info.AllowUnregistered) { ReportReject(type, "sender is not registered", p); return; }
            }
            else
            {
                if ((info.Dir & Direction.HostToGuest) == 0) { ReportReject(type, "wrong direction", p); return; }
                if (!p.IsHost) return;
            }
            var m = info.Rent();
            var r = NetReader.Rent(data, off, len);
            try
            {
                m.Describe(new Codec(r));
                if (!r.AtEnd) throw new WireException($"{r.Remaining} unread bytes");
            }
            catch (WireException e) { ReportReject(type, e.Message, p); r.Return(); info.Recycle(m); return; }
            catch (Exception e) { Log.Error("decode " + info.Name, e); r.Return(); info.Recycle(m); return; }
            r.Return();
            m.From = p.PlayerId;
            if (onHost && info.PlayerScoped && p.Registered && ((IPlayerScoped)m).ScopedPlayerId != p.PlayerId)
            {
                ReportReject(type, "player id does not match endpoint", p); info.Recycle(m); return;
            }
            (onHost ? _hostQ : _guestQ).Enqueue(new Work { M = m, I = info, P = p });
        }

        // ------------------------------------------------------------ main thread
        public void Post(Action a) => _general.Enqueue(a);

        /// <summary>Per-frame pump: general actions, then client handlers, then server handlers.</summary>
        public void Pump()
        {
            while (_general.TryDequeue(out var a)) { try { a(); } catch (Exception e) { Log.Error("queued action", e); } }
            Drain(_guestQ, false);
            Drain(_hostQ, true);
        }

        void Drain(ConcurrentQueue<Work> q, bool onHost)
        {
            while (q.TryDequeue(out var w))
            {
                long started = SlowDispatch != null ? Stopwatch.GetTimestamp() : 0;
                try
                {
                    List<(Side, Handler)> list; lock (_handlers) _handlers.TryGetValue(w.I.Id, out list);
                    if (list == null) { _rl.Warn("nohandler:" + w.I.Id, "no handler registered for " + w.I.Name); continue; }
                    var ctx = new Ctx(this, w.P, onHost);
                    Side me = onHost ? Side.Host : Side.Guest;
                    Handler[] snap; lock (_handlers) { snap = new Handler[list.Count]; int n = 0; foreach (var e in list) if ((e.Item1 & me) != 0) snap[n++] = e.Item2; if (n != snap.Length) Array.Resize(ref snap, n); }
                    Verdict v = Verdict.Accept;
                    foreach (var h in snap)
                    {
                        try { using (Applying.Enter()) { var r = h(ctx, w.M); if (r == Verdict.Reject) { v = Verdict.Reject; break; } if (r == Verdict.Relay) v = Verdict.Relay; } }
                        catch (Exception e) { Log.Error("handler for " + w.I.Name, e); }
                    }
                    if (v == Verdict.Relay && onHost) SendToAllExcept(w.M, w.P);
                }
                finally
                {
                    w.I.Recycle(w.M);
                    if (started != 0)
                    {
                        double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                        long now = Clock();
                        if (ms >= 20 && (!_nextSlowDispatch.TryGetValue(w.I.Id, out var next) || now >= next))
                        {
                            _nextSlowDispatch[w.I.Id] = now + 5000;
                            try { SlowDispatch?.Invoke(w.I.Name, ms, q.Count); } catch { }
                        }
                    }
                }
            }
        }

        /// <summary>Deep copy through the wire format; used when a handler must keep a message beyond the handler call.</summary>
        public T Clone<T>(T m) where T : Msg
        {
            var info = Registry.Get(m.GetType()); var w = NetWriter.Rent();
            try { m.Describe(new Codec(w)); var copy = (T)info.Factory(); var r = NetReader.Rent(w.Buffer, 0, w.Length); try { copy.Describe(new Codec(r)); } finally { r.Return(); } copy.From = m.From; return copy; }
            finally { w.Return(); }
        }

        // ------------------------------------------------------------ send
        public void SendTo(Peer p, Msg m, Delivery? mode = null, Lane? lane = null)
        {
            if (p == null || Transport == null) return;
            var info = Registry.Get(m.GetType());
            var w = NetWriter.Rent();
            try
            {
                m.Describe(new Codec(w));
                p.Link.Send(info.Id, (byte)(lane ?? info.Lane), mode ?? info.Mode, w.Span);
                PacketSeen?.Invoke(info.Id, w.Length, 1, true);
            }
            catch (WireException e) { Log.Error($"could not encode {info.Name}: {e.Message}"); }
            finally { w.Return(); }
        }
        public void SendToHost(Msg m, Delivery? mode = null, Lane? lane = null) { if (Role == Role.Guest) SendTo(HostPeer, m, mode, lane); }
        public void SendToAll(Msg m, Delivery? mode = null, Lane? lane = null) { foreach (var p in _peers.Values) if (p.Registered && !p.IsHost) SendTo(p, m, mode, lane); }
        public void SendToAllExcept(Msg m, Peer except, Delivery? mode = null, Lane? lane = null) { foreach (var p in _peers.Values) if (p.Registered && !p.IsHost && p != except) SendTo(p, m, mode, lane); }
        public void SendToPlayer(string id, Msg m) => SendTo(FindByPlayer(id), m);
        /// <summary>Host: to everyone. Guest: to the host.</summary>
        public void SendAuto(Msg m, Delivery? mode = null, Lane? lane = null) { if (Role == Role.Host) SendToAll(m, mode, lane); else SendToHost(m, mode, lane); }
    }
}

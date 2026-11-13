// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Lycoreia.Net;
using Lycoreia.Net.Messages;

namespace Lycoreia
{
    public sealed class PlayerInfo
    {
        public string Id, Name; public ushort ShortId; public bool IsHost;
        /// <summary>The player's selected name colour ("#RRGGBB"), null until their profile arrives.</summary>
        public string ColourHex;
        /// <summary>Display name (the owned UI code calls it Username).</summary>
        public string Username => Name;
        public override string ToString() => Name + " (" + Id + ")";
    }

    /// <summary>
    /// Connection and session state for host and guest: connect, mod sync, approve/deny, liveness, players, chat.
    /// Pure logic over Node; the game layer subscribes to the events. Everything time related runs from Tick() on the clock.
    /// </summary>
    public sealed class Session
    {
        public const int HeartbeatMs = 5000, HostDropMs = 30_000, GuestLossMs = 20_000, AckWithinMs = 10_000, ClosingGraceMs = 3000;

        public readonly Node Node; public readonly ModRegistry Mods; readonly Func<long> _clock;
        /// <summary>Non-null: this install may not host or join (unsupported game build); the text is shown to the player.</summary>
        public string BlockedReason;
        public bool DevMode; public string ForcedPlayerId; public string LocalName = "Player"; public string Build = ModVersion.Value;
        public bool AllowCheats;                 // host setting, announced in Approve
        public string LocalId { get; private set; }
        public string HostId { get; private set; }
        public long SyncId { get; private set; }
        public bool HostAllowsCheats { get; private set; }
        public bool Connecting { get; private set; }
        public bool Connected => Node.Role == Role.Guest && Node.HostPeer != null && Node.HostPeer.Acknowledged;
        public bool Joined { get; private set; }
        public bool IsHost => Node.Role == Role.Host;
        public bool IsGuest => Node.Role == Role.Guest;
        public bool Active => Node.Role != Role.Offline;
        public int Generation => _gen;
        public readonly Dictionary<string, PlayerInfo> Players = new Dictionary<string, PlayerInfo>();

        // transports (replaceable in tests)
        public Func<int, bool, IDatagramTransport> Listen = (port, v6) => UdpTransport.Listen(port, v6);
        public Func<AddressFamily, IDatagramTransport> Open = f => UdpTransport.Open(f);

        // events
        public event Action ServerStarted, ServerStopped;
        public event Action ClientJoined;                       // guest: initial approve applied
        public event Action<string> ClientDisconnected;         // guest: reason shown (or null)
        public event Action<PlayerInfo> PlayerAdded, PlayerRemoved;
        public event Action<Peer> ClientAdded, ClientRemoved;   // host side registrations
        public event Action<Peer, Approve> BuildApprove;        // host: let domains fill currencies etc.
        public event Action<Peer, bool> Approved;               // host: approve sent (peer, initial) -> run the snapshot
        public event Action<Approve> ApproveApplied;            // guest: every approve
        public event Action<Peer, bool> ResyncRequested;              // host
        public event Action<ChatMessage> ChatReceived;
        /// <summary>A player's profile (colour) arrived or changed.</summary>
        public event Action<PlayerInfo> ProfileChanged;
        public event Action<string> Notice;                     // system line for the player
        /// <summary>Shows a system line to the local player (e.g. the update notice).</summary>
        public void RaiseNotice(string text) => Notice?.Invoke(text);
        public event Action<string> ErrorShown;                // error text for the player (box / chat)
        public event Action<string> OutboundSystemChat;        // host broadcast mirror

        bool _timeoutPosted, _lossPosted; int _gen; long _connectAt, _lastHb, _lastHostBeat; bool _errorShown, _hintShown, _faultScheduled;
        readonly List<(Peer p, long at)> _closing = new List<(Peer, long)>();
        readonly RateLimitedLog _rl;

        public Session(Node node, ModRegistry mods = null)
        {
            Node = node; _clock = node.Clock; _rl = new RateLimitedLog(_clock);
            Mods = mods ?? new ModRegistry(); Mods.Node = node; Mods.Session = this; node.Ticked += Tick;
            Node.On<Connect>(Side.Host, OnConnect);
            Node.On<ModSyncRequest>(Side.Guest, OnModSyncRequest);
            Node.On<ModSyncReply>(Side.Host, OnModSyncReply);
            Node.On<Approve>(Side.Guest, OnApprove);
            Node.On<Deny>(Side.Guest, OnDeny);
            Node.On<Close>(Side.Guest, (c, m) => { Notice?.Invoke("Server closed!"); Disconnect("Server closed!", false); });
            Node.On<Heartbeat>(Side.Host, (c, m) => { });
            Node.On<HeartbeatAck>(Side.Guest, (c, m) => { });
            Node.On<ResyncRequest>(Side.Host, (c, m) => SafeInvoke2(ResyncRequested, c.Sender, m.Automatic));
            Node.On<PlayerJoin>(Side.Host, OnPlayerJoin);
            Node.On<PlayerJoined>(Side.Guest, (c, m) => AddPlayer(m.Player.Id, m.Player.Name, false));
            Node.On<PlayerLeave>(Side.Host, (c, m) => { RemoveClient(c.Sender, "left"); });
            Node.On<PlayerLeft>(Side.Guest, (c, m) => RemovePlayer(m.PlayerId));
            Node.On<PlayerProfile>(Side.Both, (c, m) =>
            {
                if (c.OnHost) m.PlayerId = c.SenderId;                 // the sender comes from the connection
                var colour = ProfileColour.Normalize(m.Colour);
                if (colour == null || string.IsNullOrEmpty(m.PlayerId) || m.PlayerId == LocalId) return c.OnHost ? Verdict.Reject : Verdict.Accept;
                m.Colour = colour; SetProfileColour(m.PlayerId, colour);
                return c.OnHost ? Verdict.Relay : Verdict.Accept;
            });
            Node.On<ChatMessage>(Side.Both, (c, m) => { SafeInvoke(ChatReceived, m); return c.OnHost ? Verdict.Relay : Verdict.Accept; });
            Node.On<ApiCall>(Side.Both, (c, m) => Mods.Dispatch(c, m));
        }

        static void SafeInvoke<T>(Action<T> a, T arg) { if (a == null) return; foreach (Action<T> d in a.GetInvocationList()) { try { d(arg); } catch (Exception e) { Log.Error("event subscriber failed", e); } } }
        static void SafeInvoke(Action a) { if (a == null) return; foreach (Action d in a.GetInvocationList()) { try { d(); } catch (Exception e) { Log.Error("event subscriber failed", e); } } }

        // ================================================================ host
        /// <summary>Starts hosting. Returns null on success or the refusal text.</summary>
        public string StartServer(int port, bool ipv6 = true)
        {
            if (IsGuest) return "Cannot host while connected to another world.";
            if (IsHost) return "A server is already running.";
            IDatagramTransport t;
            try { t = Listen(port, ipv6); }
            catch (Exception e) { return DescribeSocketError(e); }
            return StartServer(t);
        }
        public string StartServer(IDatagramTransport t)
        {
            if (BlockedReason != null) return BlockedReason;
            if (IsGuest) return "Cannot host while connected to another world.";
            if (IsHost) return "A server is already running.";
            LocalId = Identity.ForHost(DevMode); HostId = LocalId; _gen++;
            Players.Clear(); _colours.Clear(); Players[LocalId] = new PlayerInfo { Id = LocalId, Name = LocalName, ShortId = Identity.ShortId(LocalId), IsHost = true };
            t.Faulted += OnFault;
            Node.Attach(Role.Host, t, LocalId);
            _lastHostBeat = _clock();
            SafeInvoke(ServerStarted);
            Notice?.Invoke("The world is now open to others!");
            return null;
        }

        public void StopServer()
        {
            if (!IsHost) return;
            Notice?.Invoke("Server closed!");
            Node.SendToAll(new Close());
            SafeInvoke(ServerStopped);
            _closing.Clear(); Players.Clear(); _colours.Clear(); Mods.ClearNetIds(); Node.Detach(); Joined = false;
        }

        static string DescribeSocketError(Exception e)
        {
            if (e is SocketException se && se.SocketErrorCode == SocketError.AddressAlreadyInUse) return "That port is already in use. Pick another port.";
            return "Could not start the server: " + e.Message;
        }

        void OnConnect(Ctx c, Connect m)
        {
            var peer = c.Sender;
            if (string.IsNullOrEmpty(m.PlayerId) || !m.PlayerId.StartsWith(Identity.Prefix, StringComparison.Ordinal)) { Deny(peer, "Invalid player id."); return; }
            var mismatch = VersionCheck.DescribeBuildMismatch(Build, m.Build);
            if (mismatch != null) { Deny(peer, mismatch); return; }
            if (peer.Registered) Log.Warn($"{m.PlayerId} sent Connect again from the same endpoint; keeping its entry");
            var other = Node.FindByPlayer(m.PlayerId);
            if (other != null && other != peer) { Log.Warn($"replacing stale connection of {m.PlayerId}", LogTarget.Main); RemoveClient(other, "replaced"); }
            peer.PlayerId = m.PlayerId; peer.Name = string.IsNullOrWhiteSpace(m.Name) ? "Player" : m.Name; peer.Registered = true; peer.Acknowledged = false;
            peer.ShortId = Identity.ShortId(m.PlayerId); peer.LastHeardMs = _clock();
            Log.Split(LogLevel.Message, $"Client connecting: {peer.Name}", $"Client {peer.Name} {peer.PlayerId} from {peer.EP}");
            SafeInvoke(ClientAdded, peer);
            Node.SendTo(peer, new ModSyncRequest());
        }

        void OnModSyncReply(Ctx c, ModSyncReply m)
        {
            var peer = c.Sender;
            var diffs = Mods.Compare(m.Mods);
            if (diffs.Count > 0)
            {
                string why = "Mod mismatch: " + string.Join("; ", diffs);
                Log.Warn($"Denying {peer.Name}: {why}");
                Deny(peer, why); return;
            }
            var ids = Mods.BuildNetIds();
            var a = new Approve
            {
                InitialJoin = true, AllowCheats = AllowCheats, YourId = peer.PlayerId, HostId = HostId, SyncId = NextSyncId(), Mods = ids
            };
            foreach (var p in Players.Values) if (p.Id != peer.PlayerId) a.Players.Add(new PlayerEntry { Id = p.Id, Name = p.Name });
            SafeInvoke2(BuildApprove, peer, a);
            peer.SyncId = a.SyncId; peer.Acknowledged = true;
            Node.SendTo(peer, a);
            SafeInvoke2(Approved, peer, true);
        }
        static void SafeInvoke2<A, B>(Action<A, B> ev, A a, B b) { if (ev == null) return; foreach (Action<A, B> d in ev.GetInvocationList()) { try { d(a, b); } catch (Exception e) { Log.Error("event subscriber failed", e); } } }

        static long _syncCounter;
        public static long NextSyncId() => System.Threading.Interlocked.Increment(ref _syncCounter);

        /// <summary>Sends a fresh Approve (initial false) used by a resync, with a new sync id.</summary>
        public Approve NewResyncApprove(Peer peer, long reuseSyncId = 0)
        {
            var a = new Approve { InitialJoin = false, AllowCheats = AllowCheats, YourId = peer.PlayerId, HostId = HostId, SyncId = reuseSyncId > 0 ? reuseSyncId : NextSyncId(), Mods = Mods.BuildNetIds() };
            foreach (var p in Players.Values) if (p.Id != peer.PlayerId) a.Players.Add(new PlayerEntry { Id = p.Id, Name = p.Name });
            SafeInvoke2(BuildApprove, peer, a); peer.SyncId = a.SyncId;
            return a;
        }

        void Deny(Peer peer, string reason)
        {
            Node.SendTo(peer, new Deny { Reason = reason });
            peer.Registered = false; // closing: lingers briefly so the deny is acknowledged
            _closing.Add((peer, _clock() + ClosingGraceMs));
        }

        void OnPlayerJoin(Ctx c, PlayerJoin m)
        {
            var p = c.Sender;
            AddPlayer(p.PlayerId, p.Name, true);
            Node.SendToAllExcept(new PlayerJoined { Player = new PlayerEntry { Id = p.PlayerId, Name = p.Name } }, p);
            BroadcastSystem(p.Name + " joined the world!");
        }

        /// <summary>Host removes a client: avatar and player, broadcast, ownership reassignment on the main thread, reset reliability.</summary>
        public void RemoveClient(Peer p, string why)
        {
            if (p == null) return;
            bool was = p.Registered; string id = p.PlayerId, name = p.Name;
            p.Registered = false;
            if (was && id != null)
            {
                RemovePlayer(id);
                Node.SendToAll(new PlayerLeft { PlayerId = id });
                BroadcastSystem((name ?? "A player") + " left the world!");
                Node.Post(() => SafeInvoke(ClientRemoved, p));
            }
            Node.RemovePeer(p); p.Link.Reset();
            Log.Split(LogLevel.Message, $"Client removed ({why}): {name}", $"Client {name} {id} {p.EP} removed: {why}");
        }

        public void BroadcastSystem(string text)
        {
            Notice?.Invoke(text);
            Node.SendToAll(new ChatMessage { Username = "", Text = text, MessageId = Guid.NewGuid().ToString("N"), Kind = ChatKind.System });
        }

        void AddPlayer(string id, string name, bool notify)
        {
            if (string.IsNullOrEmpty(id) || id == LocalId) return;
            if (Players.ContainsKey(id)) return;
            var p = new PlayerInfo { Id = id, Name = name, ShortId = Identity.ShortId(id) };
            if (_colours.TryGetValue(id, out var known)) p.ColourHex = known;
            Players[id] = p; SafeInvoke(PlayerAdded, p);
        }
        readonly Dictionary<string, string> _colours = new Dictionary<string, string>();
        /// <summary>Stored even before the roster entry exists (a profile can beat the PlayerJoined message).</summary>
        void SetProfileColour(string id, string colour)
        {
            _colours[id] = colour;
            if (Players.TryGetValue(id, out var p) && p.ColourHex != colour) { p.ColourHex = colour; SafeInvoke(ProfileChanged, p); }
        }
        public string ColourOf(string id) => id != null && _colours.TryGetValue(id, out var c) ? c : null;
        /// <summary>Sends the local colour to everyone (host broadcasts, a guest sends to the host which relays).</summary>
        public void SendProfile(string colour)
        {
            colour = ProfileColour.Normalize(colour); if (colour == null || LocalId == null) return;
            _colours[LocalId] = colour; if (Players.TryGetValue(LocalId, out var p)) p.ColourHex = colour;
            Node.SendAuto(new PlayerProfile { PlayerId = LocalId, Colour = colour });
        }
        /// <summary>Host: every known colour for a joining client.</summary>
        public IEnumerable<PlayerProfile> KnownProfiles() { foreach (var kv in _colours) yield return new PlayerProfile { PlayerId = kv.Key, Colour = kv.Value }; }
        void RemovePlayer(string id)
        {
            if (id != null && Players.TryGetValue(id, out var p)) { Players.Remove(id); SafeInvoke(PlayerRemoved, p); }
        }

        // ================================================================ guest
        public static bool ParseTarget(string text, out string host, out int port, int defaultPort = 1919)
        {
            host = null; port = defaultPort;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if (text.StartsWith("["))
            {
                int end = text.IndexOf(']'); if (end < 0) return false;
                host = text.Substring(1, end - 1); string rest = text.Substring(end + 1).Trim().TrimStart(':').Trim();
                if (rest.Length > 0 && !int.TryParse(rest, out port)) return false;
                return host.Length > 0 && port > 0 && port < 65536;
            }
            var parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2) { host = parts[0]; return int.TryParse(parts[1], out port) && port > 0 && port < 65536; }
            if (parts.Length != 1) return false;
            int colons = parts[0].Count(ch => ch == ':');
            if (colons == 1) { int i = parts[0].IndexOf(':'); host = parts[0].Substring(0, i); return int.TryParse(parts[0].Substring(i + 1), out port) && port > 0 && port < 65536 && host.Length > 0; }
            host = parts[0]; return true; // bare host or bare IPv6 literal
        }

        /// <summary>Resolves "host:port" or "host port" and connects. Returns null on success or the refusal text.</summary>
        public string Connect(string target)
        {
            if (!ParseTarget(target, out var host, out int port)) return "Usage: connect <host[:port]>";
            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip))
            {
                try { var all = Dns.GetHostAddresses(host); if (all.Length == 0) return "Could not resolve " + host; ip = all[0]; }
                catch (Exception e) { return "Could not resolve " + host + ": " + e.Message; }
            }
            return Connect(new IPEndPoint(ip, port));
        }

        public string Connect(IPEndPoint ep)
        {
            string refusal = CheckConnectable(ep); if (refusal != null) return refusal;
            IDatagramTransport t;
            try { t = Open(ep.AddressFamily); }
            catch (Exception e) { return "Could not connect: " + e.Message; }
            return ConnectVia(t, ep);
        }

        string CheckConnectable(IPEndPoint ep)
        {
            if (IsHost) return "You are hosting a world; stop it before connecting.";
            if (Connected) return "Already connected.";
            if (Connecting) return "Already connecting.";
            if (!DevMode && IPAddress.IsLoopback(ep.Address)) return "127.0.0.1 only works on the same machine setup used for development. Use the host's LAN IP instead (find it with ipconfig or on your router page).";
            return null;
        }

        /// <summary>Connect through a ready transport (also used by tests).</summary>
        public string ConnectVia(IDatagramTransport t, EndPoint hostEp)
        {
            if (BlockedReason != null) return BlockedReason;
            if (IsHost) return "You are hosting a world; stop it before connecting.";
            if (Connecting || Connected) return "Already connecting or connected.";
            int gen = ++_gen; _errorShown = false; _faultScheduled = false; _timeoutPosted = false; _lossPosted = false;
            try
            {
                Connecting = true; Joined = false; LocalId = ForcedPlayerId ?? Identity.ProcessPlayerId; SyncId = 0; HostAllowsCheats = false;
                t.Faulted += OnFault;
                Node.Attach(Role.Guest, t, LocalId);
                var hp = Node.AddPeer(hostEp, true); hp.PlayerId = null; hp.Registered = true;
                _connectAt = _clock(); _lastHb = _connectAt;
                Node.SendTo(hp, new Connect { PlayerId = LocalId, Name = LocalName, Build = Build });
                return null;
            }
            catch (Exception e)
            {
                Reject();
                return "Could not connect: " + e.Message;
            }
        }

        void OnModSyncRequest(Ctx c, ModSyncRequest m)
        {
            Node.SendToHost(new ModSyncReply { PlayerId = LocalId, Mods = Mods.LocalSharedList() });
        }

        void OnApprove(Ctx c, Approve m)
        {
            var hp = Node.HostPeer; if (hp == null) return;
            HostId = m.HostId; SyncId = m.SyncId; hp.PlayerId = m.HostId; hp.Name = hp.Name ?? "Host";
            Mods.ApplyNetIds(m.Mods);
            foreach (var p in m.Players) AddPlayer(p.Id, p.Name, false);
            if (!Players.ContainsKey(m.HostId)) { Players[m.HostId] = new PlayerInfo { Id = m.HostId, Name = "Host", ShortId = Identity.ShortId(m.HostId), IsHost = true }; SafeInvoke(PlayerAdded, Players[m.HostId]); }
            if (m.InitialJoin)
            {
                HostAllowsCheats = m.AllowCheats; Connecting = false; Joined = true;
                Players[LocalId] = new PlayerInfo { Id = LocalId, Name = LocalName, ShortId = Identity.ShortId(LocalId) };
                hp.Acknowledged = true; _lastHb = _clock();
                Node.SendToHost(new PlayerJoin());
            }
            SafeInvoke(ApproveApplied, m);
            if (m.InitialJoin) SafeInvoke(ClientJoined);
        }

        void OnDeny(Ctx c, Deny m)
        {
            string text = "Connection was denied! " + m.Reason;
            ShowError(text); Reject();
            SafeInvoke(ClientDisconnected, text);
        }

        void ShowError(string text) { _errorShown = true; ErrorShown?.Invoke(text); }

        /// <summary>Reject path: bump generation, clear flags, stop reliability, close the socket, clear mod ids.</summary>
        public void Reject()
        {
            _gen++; Connecting = false; Joined = false; _faultScheduled = false;
            Players.Clear(); _colours.Clear(); Mods.ClearNetIds(); Node.Detach();
        }

        /// <summary>Client disconnect. Tries to send PlayerLeave, then tears down. No automatic reconnect.</summary>
        public void Disconnect(string reason = null, bool sendLeave = true)
        {
            if (!IsGuest) return;
            _gen++;
            if (!_errorShown && reason == null) Notice?.Invoke("You disconnected from the world!");
            else if (reason != null) { if (!_errorShown) ErrorShown?.Invoke(reason); }
            if (sendLeave) { try { Node.SendToHost(new PlayerLeave()); } catch { } }
            Connecting = false; Joined = false; Players.Clear(); _colours.Clear(); Mods.ClearNetIds(); Node.Detach();
            SafeInvoke(ClientDisconnected, reason);
        }

        // ================================================================ liveness
        void OnFault(SocketError code, string message)
        {
            int n = (int)code;
            if (n == 10054)
            {
                if (!_hintShown && IsGuest) { _hintShown = true; Notice?.Invoke("No server answered. If you connect over the internet, check that the host's tunnel or port forward is up and that the firewall allows UDP."); }
                return;
            }
            if (n == 10004) return;
            Log.Error("socket error " + code + ": " + message);
            if (IsGuest && !_faultScheduled)
            {
                _faultScheduled = true; int gen = _gen;
                Node.Post(() => { if (gen == _gen && IsGuest) { ShowError("Could not join the world, check the MelonLoader console for details"); Disconnect("Could not join the world, check the MelonLoader console for details", false); } });
            }
        }

        /// <summary>Heartbeats, timeouts and cleanup. Call every ~100 ms or faster (network timer thread or sim loop).</summary>
        public void Tick()
        {
            long now = _clock();
            if (IsHost)
            {
                if (now - _lastHostBeat >= HeartbeatMs)
                {
                    _lastHostBeat = now;
                    var beat = new HeartbeatAck();
                    foreach (var p in Node.RegisteredGuests.ToList())
                    {
                        if (now - p.LastHeardMs > HostDropMs) { Log.Warn($"{p.Name} stopped responding"); int g = _gen; var pp = p; Node.Post(() => { if (pp.Registered) RemoveClient(pp, "timeout"); }); continue; }
                        Node.SendTo(p, beat);
                    }
                }
                for (int i = _closing.Count - 1; i >= 0; i--) if (now >= _closing[i].at) { var p = _closing[i].p; _closing.RemoveAt(i); Node.RemovePeer(p); p.Link.Reset(); }
                foreach (var p in Node.Peers.ToList()) if (!p.Registered && now - p.LastHeardMs > 30_000 && !_closing.Any(x => x.p == p)) Node.RemovePeer(p);
            }
            else if (IsGuest)
            {
                var hp = Node.HostPeer; if (hp == null) return;
                if (!hp.Acknowledged)
                {
                    if (Connecting && !_timeoutPosted && now - _connectAt > AckWithinMs)
                    {
                        int gen = _gen; _timeoutPosted = true;
                        Node.Post(() => { if (gen == _gen && Connecting) { _timeoutPosted = false; const string t = "The host did not answer in time."; ShowError(t); Reject(); SafeInvoke(ClientDisconnected, t); } });
                    }
                    return;
                }
                if (now - _lastHb >= HeartbeatMs) { _lastHb = now; Node.SendToHost(new Heartbeat(), Delivery.Unreliable, Lane.Important); }
                if (now - hp.LastHeardMs > GuestLossMs && !_lossPosted)
                {
                    int gen = _gen; _lossPosted = true;
                    Node.Post(() => { _lossPosted = false; if (gen == _gen && IsGuest && _clock() - Node.HostPeer.LastHeardMs > GuestLossMs) { ShowError("Lost connection to the host."); Disconnect("Lost connection to the host.", false); } });
                }
            }
        }

        /// <summary>Quit: close the server or leave, without waiting for anything.</summary>
        public void Shutdown() { if (IsHost) StopServer(); else if (IsGuest) Disconnect(null, true); }

        public void SendChat(string text)
        {
            text = (text ?? "").Trim(); if (text.Length == 0) return; if (text.Length > 1000) text = text.Substring(0, 1000);
            var m = new ChatMessage { Username = LocalName, Text = text, MessageId = Guid.NewGuid().ToString("N"), Kind = ChatKind.Player };
            Node.SendAuto(m);
        }
    }
}

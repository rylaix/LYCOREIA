// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Lycoreia.Net;

namespace Lycoreia
{
    /// <summary>A peer addressed by Steam id. Logged masked.</summary>
    public sealed class SteamEndPoint : EndPoint
    {
        public readonly ulong Id;
        public SteamEndPoint(ulong id) { Id = id; }
        public override AddressFamily AddressFamily => AddressFamily.Unspecified;
        public override bool Equals(object obj) => obj is SteamEndPoint o && o.Id == Id;
        public override int GetHashCode() => Id.GetHashCode();
        public override string ToString() => "steam:..." + (Id % 10000).ToString("D4");
    }

    /// <summary>Raw flat-API imports. Only call through SteamNet, which checks that Steam is running first.</summary>
    internal static unsafe class SteamApi
    {
        const string Dll = "steam_api64";
        [DllImport(Dll)] public static extern int SteamAPI_GetHSteamUser();
        [DllImport(Dll)] public static extern IntPtr SteamAPI_SteamUser_v021();
        [DllImport(Dll)] public static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);
        [DllImport(Dll)] public static extern IntPtr SteamAPI_SteamUtils_v010();
        [DllImport(Dll)] public static extern uint SteamAPI_ISteamUtils_GetAppID(IntPtr self);
        [DllImport(Dll)] public static extern IntPtr SteamAPI_SteamFriends_v017();
        [DllImport(Dll)] public static extern int SteamAPI_ISteamFriends_GetFriendCount(IntPtr self, int flags);
        [DllImport(Dll)] public static extern ulong SteamAPI_ISteamFriends_GetFriendByIndex(IntPtr self, int index, int flags);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool SteamAPI_ISteamFriends_GetFriendGamePlayed(IntPtr self, ulong friend, byte* info);
        [DllImport(Dll)] public static extern IntPtr SteamAPI_ISteamFriends_GetFriendRichPresence(IntPtr self, ulong friend, byte* key);
        [DllImport(Dll)] public static extern IntPtr SteamAPI_ISteamFriends_GetFriendPersonaName(IntPtr self, ulong friend);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool SteamAPI_ISteamFriends_SetRichPresence(IntPtr self, byte* key, byte* value);
        [DllImport(Dll)] public static extern IntPtr SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
        [DllImport(Dll)] public static extern int SteamAPI_ISteamNetworkingMessages_SendMessageToUser(IntPtr self, byte* identity, byte* data, uint size, int flags, int channel);
        [DllImport(Dll)] public static extern int SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(IntPtr self, int channel, IntPtr* messages, int max);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(IntPtr self, byte* identity);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(IntPtr self, byte* identity);
        [DllImport(Dll)] public static extern IntPtr SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
        [DllImport(Dll)] public static extern void SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(IntPtr self);
        [DllImport(Dll)] public static extern int SteamAPI_ISteamNetworkingUtils_GetRelayNetworkStatus(IntPtr self, byte* details);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool SteamAPI_ISteamNetworkingUtils_SetGlobalCallback_MessagesSessionRequest(IntPtr self, IntPtr fn);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool SteamAPI_ISteamNetworkingUtils_SetGlobalCallback_MessagesSessionFailed(IntPtr self, IntPtr fn);
        [DllImport(Dll)] public static extern void SteamAPI_SteamNetworkingIdentity_SetSteamID64(byte* identity, ulong id);
        [DllImport(Dll)] public static extern ulong SteamAPI_SteamNetworkingIdentity_GetSteamID64(byte* identity);
        [DllImport(Dll)] public static extern void SteamAPI_SteamNetworkingMessage_t_Release(IntPtr message);
    }

    public sealed class FriendHost { public ulong Id; public string Name, Code, Version; }

    /// <summary>
    /// Steam availability, incoming-session policy and rich presence. Everything degrades to "not available" (UDP only) when the
    /// game runs without Steam (Epic, Game Pass, tests) or an import is missing.
    /// </summary>
    public static unsafe class SteamNet
    {
        public const int Channel = 0x4C59;          // our own channel; the game itself does not use Steam networking
        public const int SendUnreliableNoNagle = 1, SendAutoRestart = 32;
        public const string PresenceKey = "lycoreia_join", PresenceVersionKey = "lycoreia_ver";
        public const int IdentitySize = 136, MessagePeerOffset = 16, MessageDataOffset = 0, MessageSizeOffset = 8;

        static int _state;                         // 0 untried, 1 ready, -1 unavailable
        static IntPtr _msgs, _utils, _friends;
        public static ulong LocalId { get; private set; }
        public static uint AppId { get; private set; }
        public static string Unavailable { get; private set; } = "not checked";
        /// <summary>Host mode: accept sessions from anyone (they still need the right build and mods to be approved).</summary>
        public static volatile bool AcceptIncoming;
        public static Action<string, string> Trace = (e, d) => { };
        static void Tr(string e, string d) { try { Trace(e, d); } catch { } }

        public static bool Ready => _state == 1 || TryInit();

        /// <summary>Main thread. Safe to call often; only the first successful call does work.</summary>
        public static bool TryInit()
        {
            if (_state == 1) return true;
            if (_state == -1) return false;
            try
            {
                if (!OperatingSystem.IsWindows()) { Fail("not Windows (tests)"); return false; }
                if (!EnsureLibrary()) return false;
                if (SteamApi.SteamAPI_GetHSteamUser() == 0) { Unavailable = "Steam is not initialised (non-Steam copy or Steam not running)"; return false; } // may come later
                var user = SteamApi.SteamAPI_SteamUser_v021(); if (user == IntPtr.Zero) { Unavailable = "no ISteamUser"; return false; }
                LocalId = SteamApi.SteamAPI_ISteamUser_GetSteamID(user); if (LocalId == 0) { Unavailable = "no Steam id"; return false; }
                var u = SteamApi.SteamAPI_SteamUtils_v010(); AppId = u == IntPtr.Zero ? 0 : SteamApi.SteamAPI_ISteamUtils_GetAppID(u);
                _msgs = SteamApi.SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
                _utils = SteamApi.SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
                _friends = SteamApi.SteamAPI_SteamFriends_v017();
                if (_msgs == IntPtr.Zero || _utils == IntPtr.Zero) { Fail("Steam networking interfaces missing"); return false; }
                SteamApi.SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(_utils);
                // Delegates kept in static fields so the GC never collects what Steam holds a pointer to.
                _onRequest = OnSessionRequest; _onFailed = OnSessionFailed;
                bool cb1 = SteamApi.SteamAPI_ISteamNetworkingUtils_SetGlobalCallback_MessagesSessionRequest(_utils, Marshal.GetFunctionPointerForDelegate(_onRequest));
                bool cb2 = SteamApi.SteamAPI_ISteamNetworkingUtils_SetGlobalCallback_MessagesSessionFailed(_utils, Marshal.GetFunctionPointerForDelegate(_onFailed));
                _state = 1; Unavailable = null;
                Log.Info($"Steam networking ready (app {AppId}, session callbacks {cb1}/{cb2}, relay {RelayStatus()})");
                Tr("steam-ready", $"app={AppId}; callbacks={cb1}/{cb2}");
                return true;
            }
            catch (DllNotFoundException e) { Unavailable = "Steam library not loaded yet: " + e.Message; return false; }   // not final: retried every 5 s
            catch (Exception e) when (e is EntryPointNotFoundException || e is BadImageFormatException) { Fail(e.GetType().Name + ": " + e.Message); return false; }
            catch (Exception e) { Fail(e.Message); return false; }
        }
        [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);
        static bool _libLogged;
        /// <summary>
        /// The game keeps steam_api64.dll in SlimeRancher2_Data/Plugins/x86_64, which is not on the DLL search path, and loads it only
        /// during start-up. rc5 looked on the first frame and gave up for the whole session. Load it from the game folder ourselves
        /// (the game then gets the same module); a missing file means a non-Steam copy.
        /// </summary>
        static bool EnsureLibrary()
        {
            if (GetModuleHandleW("steam_api64.dll") != IntPtr.Zero) return true;
            string dir = System.IO.Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
            string path = System.IO.Path.Combine(dir, "SlimeRancher2_Data", "Plugins", "x86_64", "steam_api64.dll");
            if (!System.IO.File.Exists(path)) { Fail("no steam_api64.dll in the game folder (non-Steam copy)"); return false; }
            try { System.Runtime.InteropServices.NativeLibrary.Load(path); if (!_libLogged) { _libLogged = true; Log.Info("Steam library loaded from the game folder"); } return true; }
            catch (Exception e) { Unavailable = "could not load steam_api64.dll: " + e.Message; return false; }   // retried by Pump
        }
        static void Fail(string why) { _state = -1; Unavailable = why; Log.Info("Steam networking unavailable: " + why); Tr("steam-unavailable", why); }

        /// <summary>Relay network availability (100 = current); a few seconds after start-up it should be ready.</summary>
        public static string RelayStatus()
        {
            if (_utils == IntPtr.Zero) return "n/a";
            byte* buf = stackalloc byte[1024];
            int a = SteamApi.SteamAPI_ISteamNetworkingUtils_GetRelayNetworkStatus(_utils, buf);
            return a switch { 100 => "current", 3 => "attempting", 2 => "waiting", 1 => "never tried", -100 => "previously worked, failing", -101 => "failed", -102 => "cannot try", _ => a.ToString() };
        }

        static void Identity(byte* id, ulong steamId) { new Span<byte>(id, IdentitySize).Clear(); SteamApi.SteamAPI_SteamNetworkingIdentity_SetSteamID64(id, steamId); }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SessionCallback(IntPtr data);
        static SessionCallback _onRequest, _onFailed;

        static void OnSessionRequest(IntPtr request)
        {
            try
            {
                var id = (byte*)request; ulong who = SteamApi.SteamAPI_SteamNetworkingIdentity_GetSteamID64(id);
                bool ok = AcceptIncoming && SteamApi.SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(_msgs, id);
                Tr("steam-session-request", $"from={new SteamEndPoint(who)}; accepted={ok}; hosting={AcceptIncoming}");
            }
            catch { }
        }

        static void OnSessionFailed(IntPtr failed)
        {
            try
            {
                // SteamNetworkingMessagesSessionFailed_t { SteamNetConnectionInfo_t m_info }: identity first; state/end reason after it.
                var p = (byte*)failed; ulong who = SteamApi.SteamAPI_SteamNetworkingIdentity_GetSteamID64(p);
                int state = *(int*)(p + 176), reason = *(int*)(p + 180);
                Tr("steam-session-failed", $"peer={new SteamEndPoint(who)}; state={state}; reason={reason}");
                Log.Warn($"Steam session with {new SteamEndPoint(who)} failed (state {state}, reason {reason})");
            }
            catch { }
        }

        /// <summary>Main thread, while hosting: accept pending sessions of friends playing this game (in case the session-request callback is not dispatched).</summary>
        static long _nextAcceptPoll;
        static void AcceptFriendsPending()
        {
            if (!AcceptIncoming || _friends == IntPtr.Zero) return;
            byte* id = stackalloc byte[IdentitySize];
            foreach (var f in FriendsInThisGame())
            {
                Identity(id, f);
                if (SteamApi.SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(_msgs, id)) Tr("steam-session-accepted-poll", "from=" + new SteamEndPoint(f));
            }
        }

        static IEnumerable<ulong> FriendsInThisGame()
        {
            var list = new List<ulong>(); if (_friends == IntPtr.Zero) return list;
            const int Immediate = 4; int n = SteamApi.SteamAPI_ISteamFriends_GetFriendCount(_friends, Immediate);
            byte* info = stackalloc byte[64];
            for (int i = 0; i < n && i < 2000; i++)
            {
                ulong f = SteamApi.SteamAPI_ISteamFriends_GetFriendByIndex(_friends, i, Immediate); if (f == 0) continue;
                new Span<byte>(info, 64).Clear();
                if (!SteamApi.SteamAPI_ISteamFriends_GetFriendGamePlayed(_friends, f, info)) continue;
                ulong game = *(ulong*)info; if (AppId != 0 && (uint)(game & 0xFFFFFF) != AppId) continue;
                list.Add(f);
            }
            return list;
        }

        // ---- rich presence: lets friends see the hosted world in their Join page (no public IP in it: Steam id + LAN only)
        static string _presence = "";
        public static void SetHostedCode(string code)
        {
            code ??= "";
            if (!Ready || _friends == IntPtr.Zero || code == _presence) return;
            if (SetPresence(PresenceKey, code) && SetPresence(PresenceVersionKey, code.Length == 0 ? "" : ModVersion.Value)) { _presence = code; Tr("steam-presence", code.Length == 0 ? "cleared" : "set"); }
        }
        static bool SetPresence(string key, string value)
        {
            var k = Encoding.UTF8.GetBytes(key + "\0"); var v = Encoding.UTF8.GetBytes(value + "\0");
            fixed (byte* kp = k) fixed (byte* vp = v) return SteamApi.SteamAPI_ISteamFriends_SetRichPresence(_friends, kp, vp);
        }

        static List<FriendHost> _hosts = new List<FriendHost>(); static long _nextHostsScan;
        static readonly HashSet<ulong> _announced = new HashSet<ulong>();
        /// <summary>Main thread: a Steam friend opened a world (raised once per friend per hosting run).</summary>
        public static event Action<FriendHost> FriendStartedHosting;
        /// <summary>Friends currently hosting a Lycoreia world (refreshed every few seconds on the main thread).</summary>
        public static IReadOnlyList<FriendHost> FriendsHosting => _hosts;

        static void ScanFriendsHosting()
        {
            var found = new List<FriendHost>();
            var k1 = Encoding.UTF8.GetBytes(PresenceKey + "\0"); var k2 = Encoding.UTF8.GetBytes(PresenceVersionKey + "\0");
            foreach (var f in FriendsInThisGame())
            {
                string code, ver;
                fixed (byte* kp = k1) code = Utf8(SteamApi.SteamAPI_ISteamFriends_GetFriendRichPresence(_friends, f, kp));
                if (string.IsNullOrEmpty(code) || !JoinCode.TryDecode(code, out _)) continue;
                fixed (byte* kp = k2) ver = Utf8(SteamApi.SteamAPI_ISteamFriends_GetFriendRichPresence(_friends, f, kp));
                found.Add(new FriendHost { Id = f, Code = code, Version = ver ?? "", Name = Utf8(SteamApi.SteamAPI_ISteamFriends_GetFriendPersonaName(_friends, f)) ?? "?" });
            }
            found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            _hosts = found;
            _announced.RemoveWhere(id => !found.Exists(f => f.Id == id));
            foreach (var f in found) if (_announced.Add(f.Id)) { Tr("steam-friend-hosting", "friend=" + new SteamEndPoint(f.Id) + "; version=" + f.Version); try { FriendStartedHosting?.Invoke(f); } catch (Exception e) { Log.Error("friend hosting subscriber", e); } }
        }
        static string Utf8(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

        /// <summary>Main thread, every frame: cheap; does its work once a second / every 3 s.</summary>
        public static void Pump(long nowMs)
        {
            if (_state == -1) return;
            if (_state == 0) { if (nowMs < _nextAcceptPoll) return; _nextAcceptPoll = nowMs + 5000; TryInit(); return; }
            try
            {
                if (nowMs >= _nextAcceptPoll) { _nextAcceptPoll = nowMs + 1000; AcceptFriendsPending(); }
                if (nowMs >= _nextHostsScan) { _nextHostsScan = nowMs + 3000; ScanFriendsHosting(); }
            }
            catch (Exception e) { Log.Warn("steam pump: " + e.Message); _nextHostsScan = nowMs + 30000; }
        }

        // ---- datagrams (thread safe on Steam's side)
        public static int Send(ulong to, byte[] buf, int off, int len)
        {
            byte* id = stackalloc byte[IdentitySize]; Identity(id, to);
            fixed (byte* p = &buf[off]) return SteamApi.SteamAPI_ISteamNetworkingMessages_SendMessageToUser(_msgs, id, p, (uint)len, SendUnreliableNoNagle | SendAutoRestart, Channel);
        }
        public static int Receive(IntPtr* batch, int max) => SteamApi.SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(_msgs, Channel, batch, max);
        public static void Close(ulong peer) { try { byte* id = stackalloc byte[IdentitySize]; Identity(id, peer); SteamApi.SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(_msgs, id); } catch { } }
        public static void Release(IntPtr m) => SteamApi.SteamAPI_SteamNetworkingMessage_t_Release(m);
        public static ulong PeerOf(IntPtr m) => SteamApi.SteamAPI_SteamNetworkingIdentity_GetSteamID64((byte*)m + MessagePeerOffset);
    }

    /// <summary>Datagrams over Steam (ISteamNetworkingMessages, our channel). Our Link layer keeps doing reliability on top.</summary>
    public sealed unsafe class SteamTransport : IDatagramTransport
    {
        static int _live;
        readonly Thread _rx; volatile bool _stop; int _sendFailures;
        readonly ConcurrentDictionary<ulong, byte> _peers = new ConcurrentDictionary<ulong, byte>();
        public event DatagramHandler Received;
        /// <summary>Steam reports failures per session (SteamNet session-failed trace), never as socket errors.</summary>
        public event Action<SocketError, string> Faulted { add { } remove { } }

        public SteamTransport()
        {
            if (!SteamNet.Ready) throw new InvalidOperationException("Steam is not available: " + SteamNet.Unavailable);
            if (Interlocked.Exchange(ref _live, 1) == 1) Log.Warn("a second Steam transport was opened; the first one stops receiving");
            _rx = new Thread(RxLoop) { IsBackground = true, Name = "Lycoreia-steam-rx" };
        }
        public void Start() => _rx.Start();

        public void Send(EndPoint to, byte[] buf, int off, int len)
        {
            if (_stop || !(to is SteamEndPoint s) || len <= 0) return;
            _peers[s.Id] = 0;
            int r = SteamNet.Send(s.Id, buf, off, len);
            if (r != 1 && Interlocked.Increment(ref _sendFailures) % 200 == 1) Log.Warn($"Steam send to {s} failed (EResult {r})");
        }

        void RxLoop()
        {
            const int Max = 64; var batch = new IntPtr[Max]; var buf = new byte[65536];
            while (!_stop)
            {
                int n;
                try { fixed (IntPtr* b = batch) n = SteamNet.Receive(b, Max); }
                catch (Exception e) { Log.Error("steam receive", e); Thread.Sleep(100); continue; }
                for (int i = 0; i < n; i++)
                {
                    var m = batch[i];
                    try
                    {
                        var data = *(IntPtr*)((byte*)m + SteamNet.MessageDataOffset); int size = *(int*)((byte*)m + SteamNet.MessageSizeOffset);
                        ulong from = SteamNet.PeerOf(m);
                        if (size > 0 && size <= buf.Length && from != 0) { Marshal.Copy(data, buf, 0, size); _peers[from] = 0; Received?.Invoke(new SteamEndPoint(from), buf, 0, size); }
                    }
                    catch (Exception e) { Log.Error("steam message", e); }
                    finally { SteamNet.Release(m); }
                }
                if (n < Max) Thread.Sleep(1);
            }
        }

        public void Dispose()
        {
            if (_stop) return;
            _stop = true;
            foreach (var p in _peers.Keys) SteamNet.Close(p);
            Interlocked.Exchange(ref _live, 0);
        }
    }

    /// <summary>Host: one node listening on UDP and Steam at once; sends go out on the transport matching the endpoint.</summary>
    public sealed class CompositeTransport : IDatagramTransport
    {
        readonly IDatagramTransport _udp, _steam;
        public event DatagramHandler Received;
        public event Action<SocketError, string> Faulted;
        public IDatagramTransport Udp => _udp;
        public CompositeTransport(IDatagramTransport udp, IDatagramTransport steam)
        {
            _udp = udp; _steam = steam;
            DatagramHandler fwd = (from, b, o, l) => Received?.Invoke(from, b, o, l);
            _udp.Received += fwd; _steam.Received += fwd;
            _udp.Faulted += (c, m) => Faulted?.Invoke(c, m);
            _steam.Faulted += (c, m) => Faulted?.Invoke(c, m);
        }
        public void Send(EndPoint to, byte[] buf, int off, int len) { if (to is SteamEndPoint) _steam.Send(to, buf, off, len); else _udp.Send(to, buf, off, len); }
        public void Start() { _udp.Start(); _steam.Start(); }
        public void Dispose() { try { _steam.Dispose(); } finally { _udp.Dispose(); } }
    }
}

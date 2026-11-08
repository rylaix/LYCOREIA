// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lycoreia
{
    /// <summary>Immutable desired presence. Equality decides whether an update is needed.</summary>
    public sealed class PresenceActivity : IEquatable<PresenceActivity>
    {
        public readonly string Details, State, PartyId; public readonly long StartUnix; public readonly int PartySize, PartyMax;
        public PresenceActivity(string details, string state, long startUnix, string partyId = null, int partySize = 0, int partyMax = 0)
        { Details = details; State = state; StartUnix = startUnix; PartyId = partyId; PartySize = partySize; PartyMax = partyMax; }
        public bool Equals(PresenceActivity o) => o != null && Details == o.Details && State == o.State && StartUnix == o.StartUnix && PartyId == o.PartyId && PartySize == o.PartySize && PartyMax == o.PartyMax;
        public override bool Equals(object o) => Equals(o as PresenceActivity);
        public override int GetHashCode() => (Details ?? "").GetHashCode() ^ (State ?? "").GetHashCode() ^ StartUnix.GetHashCode();
    }

    public sealed class DiscordPresence
    {
        public const int OpHandshake = 0, OpFrame = 1, OpClose = 2, OpPing = 3, OpPong = 4;
        public const int MinUpdateMs = 15_000, MaxFrame = 64 * 1024;

        // ---------- pure helpers (tested) ----------
        public static byte[] EncodeFrame(int opcode, string json)
        {
            var body = Encoding.UTF8.GetBytes(json ?? "");
            var buf = new byte[8 + body.Length];
            WriteLE(buf, 0, opcode); WriteLE(buf, 4, body.Length); Buffer.BlockCopy(body, 0, buf, 8, body.Length);
            return buf;
        }
        /// <summary>Decodes one frame from the start of buf[0..len). False when incomplete or invalid (consumed 0).</summary>
        public static bool TryDecodeFrame(byte[] buf, int len, out int opcode, out string json, out int consumed)
        {
            opcode = 0; json = null; consumed = 0;
            if (buf == null || len < 8) return false;
            opcode = ReadLE(buf, 0); int n = ReadLE(buf, 4);
            if (n < 0 || n > MaxFrame || len < 8 + n) return false;
            json = Encoding.UTF8.GetString(buf, 8, n); consumed = 8 + n; return true;
        }
        static void WriteLE(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
        static int ReadLE(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);

        public static string HandshakeJson(string clientId) => "{\"v\":1,\"client_id\":" + Q(clientId) + "}";

        public static string ActivityJson(PresenceActivity a, string version, string discordUrl)
        {
            if (a == null) return "null";
            var sb = new StringBuilder("{");
            sb.Append("\"details\":").Append(Q(a.Details)).Append(",\"state\":").Append(Q(a.State));
            sb.Append(",\"timestamps\":{\"start\":").Append(a.StartUnix.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('}');
            if (!string.IsNullOrEmpty(a.PartyId) && a.PartySize > 0)
            {
                sb.Append(",\"party\":{\"id\":").Append(Q(a.PartyId));
                if (a.PartyMax > 0) sb.Append(",\"size\":[").Append(a.PartySize).Append(',').Append(Math.Max(a.PartySize, a.PartyMax)).Append(']');   // no cap: no "n of max"
                sb.Append('}');
            }
            sb.Append(",\"assets\":{\"large_image\":\"lycoreia\",\"large_text\":").Append(Q("Lycoreia " + version)).Append('}');
            if (!string.IsNullOrEmpty(discordUrl)) sb.Append(",\"buttons\":[{\"label\":\"Discord\",\"url\":").Append(Q(discordUrl)).Append("}]");
            return sb.Append('}').ToString();
        }

        public static string SetActivityJson(int pid, PresenceActivity a, string version, string discordUrl, string nonce) =>
            "{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":" + pid + ",\"activity\":" + ActivityJson(a, version, discordUrl) + "},\"nonce\":" + Q(nonce) + "}";

        /// <summary>JSON string literal with escaping.</summary>
        public static string Q(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break; case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break; case '\r': sb.Append("\\r"); break; case '\t': sb.Append("\\t"); break;
                    default: if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); break;
                }
            }
            return sb.Append('"').ToString();
        }

        public static bool IsReady(string json) => json != null && json.Contains("\"evt\"") && json.Contains("\"READY\"");

        // ---------- client ----------
        readonly string _clientId, _version, _url;
        readonly object _lock = new object();
        PresenceActivity _desired; bool _enabled = true; volatile bool _stop; Thread _thread; bool _loggedFail;
        public DiscordPresence(string clientId, string version, string discordUrl) { _clientId = clientId; _version = version; _url = discordUrl; }

        /// <summary>Main thread: post the desired state. Cheap, never blocks on IO.</summary>
        public void Set(PresenceActivity activity, bool enabled)
        {
            lock (_lock) { _desired = activity; _enabled = enabled; }
            if (_thread == null && enabled && !_stop)
                lock (_lock) { if (_thread == null) { _thread = new Thread(Run) { IsBackground = true, Name = "Lycoreia-discord" }; _thread.Start(); } }
        }

        /// <summary>Best effort: stops the worker (it sends CLOSE) and waits at most 500 ms.</summary>
        public void Shutdown()
        {
            _stop = true; Thread t; lock (_lock) t = _thread;
            try { t?.Join(500); } catch { }
        }

        void Run()
        {
            try
            {
                int backoff = 5000; Stream s = null; IDisposable owner = null; Task<(int op, string json)> pending = null;
                PresenceActivity sent = null; bool sentOnce = false; long lastSend = 0; var clock = Stopwatch.StartNew();
                while (!_stop)
                {
                    PresenceActivity want; bool en; lock (_lock) { want = _desired; en = _enabled; }
                    try
                    {
                        if (s != null && !en) { SendClear(s); Close(ref s, ref owner, true); pending = null; sent = null; sentOnce = false; }
                        if (s == null)
                        {
                            if (!en) { SleepStop(500); continue; }
                            s = Connect(out owner);
                            if (s == null)
                            {
                                if (!_loggedFail) { _loggedFail = true; Log.Info($"Discord not reachable (wine={IsWine()}); presence retries in the background"); }
                                SleepStop(backoff); backoff = Math.Min(backoff * 2, 60_000); continue;
                            }
                            if (!Handshake(s)) { Close(ref s, ref owner, false); SleepStop(backoff); backoff = Math.Min(backoff * 2, 60_000); continue; }
                            backoff = 5000; _loggedFail = false; sent = null; sentOnce = false; pending = null; Log.Info("Discord presence connected: " + LastEndpoint);
                        }
                        pending = pending ?? ReadFrameAsync(s);
                        if (pending.Wait(100))
                        {
                            var f = pending.Result; pending = null;
                            if (f.op == OpPing) Write(s, OpPong, f.json);
                            else if (f.op == OpClose) throw new IOException("closed by Discord");
                        }
                        if (en && (want == null ? sent != null : !want.Equals(sent)) && (!sentOnce || clock.ElapsedMilliseconds - lastSend >= MinUpdateMs))
                        {
                            Write(s, OpFrame, SetActivityJson(PresencePid(), want, _version, _url, Guid.NewGuid().ToString()));
                            sent = want; sentOnce = true; lastSend = clock.ElapsedMilliseconds;
                        }
                    }
                    catch (Exception)
                    {
                        Close(ref s, ref owner, false); pending = null; SleepStop(backoff); backoff = Math.Min(backoff * 2, 60_000);
                    }
                }
                if (s != null) { try { Write(s, OpClose, "{}"); } catch { } Close(ref s, ref owner, false); }
            }
            catch (Exception e) { try { Log.Warn("Discord presence stopped: " + e.Message); } catch { } }
        }

        void SleepStop(int ms) { for (int i = 0; i < ms && !_stop; i += 50) Thread.Sleep(50); }
        void SendClear(Stream s) { try { Write(s, OpFrame, SetActivityJson(PresencePid(), null, _version, _url, Guid.NewGuid().ToString())); } catch { } }
        static void Close(ref Stream s, ref IDisposable owner, bool _) { try { s?.Dispose(); } catch { } try { owner?.Dispose(); } catch { } s = null; owner = null; }

        static void Write(Stream s, int op, string json)
        {
            var b = EncodeFrame(op, json);
            if (!s.WriteAsync(b, 0, b.Length).Wait(2000)) throw new IOException("write timeout");
            s.Flush();
        }

        bool Handshake(Stream s)
        {
            Write(s, OpHandshake, HandshakeJson(_clientId));
            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 5000 && !_stop)
            {
                var t = ReadFrameAsync(s); if (!t.Wait(5000)) return false;
                var f = t.Result;
                if (f.op == OpPing) Write(s, OpPong, f.json);
                else if (f.op == OpClose) return false;
                else if (f.op == OpFrame && IsReady(f.json)) return true;
            }
            return false;
        }

        static async Task<(int op, string json)> ReadFrameAsync(Stream s)
        {
            var head = new byte[8]; await ReadExact(s, head, 8).ConfigureAwait(false);
            int op = ReadLE(head, 0), n = ReadLE(head, 4);
            if (n < 0 || n > MaxFrame) throw new IOException("bad frame length");
            var body = new byte[n]; await ReadExact(s, body, n).ConfigureAwait(false);
            return (op, Encoding.UTF8.GetString(body));
        }
        static async Task ReadExact(Stream s, byte[] b, int n)
        {
            int got = 0; while (got < n) { int r = await s.ReadAsync(b, got, n - got).ConfigureAwait(false); if (r <= 0) throw new EndOfStreamException(); got += r; }
        }

        /// <summary>Discord matches the activity to a running process. Under Wine the .NET process id is Wine's, not the Linux pid of
        /// the game Discord detected, so the Linux pid is asked from the kernel (getpid) to let our presence replace the detected game.</summary>
        static int PresencePid() { try { if (IsWine() && WineUnixSocket.Available) { long p = WineUnixSocket.GetPid(); if (p > 0) return (int)p; } } catch { } return Environment.ProcessId; }

        /// <summary>Endpoint of the last successful connect (for the log).</summary>
        public static string LastEndpoint;

        /// <summary>
        /// Windows: named pipes. Native Linux/macOS: the unix socket. Under Wine/Proton (the usual Linux setup) the game is a Windows
        /// process but Discord runs natively and only listens on a unix socket: WineUnixSocket reaches it with raw Linux syscalls;
        /// winsock AF_UNIX (Z: and raw path) stays as a fallback for Wine builds that support it. Native, Flatpak, Snap and Vesktop.
        /// </summary>
        static Stream Connect(out IDisposable owner)
        {
            owner = null;
            bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            if (windows)
                for (int n = 0; n < 10; n++)
                {
                    try
                    {
                        var p = new NamedPipeClientStream(".", "discord-ipc-" + n, PipeDirection.InOut, PipeOptions.Asynchronous);
                        try { p.Connect(200); LastEndpoint = @"\\.\pipe\discord-ipc-" + n; return p; } catch { p.Dispose(); }
                    }
                    catch { }
                }
            if (windows && !IsWine()) return null;
            if (windows && WineUnixSocket.Available)
            {
                // Proton's Wine has no AF_UNIX in winsock (verified GE-Proton10-34: WSAEAFNOSUPPORT): talk to the host kernel directly.
                foreach (var path in UnixCandidates(false))
                {
                    var ws = WineUnixSocket.TryConnect(path);
                    if (ws != null) { LastEndpoint = path + " (wine syscall)"; return ws; }
                }
            }
            foreach (var path in UnixCandidates(windows))
            {
                try
                {
                    var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try { sock.Connect(new UnixDomainSocketEndPoint(path)); owner = sock; LastEndpoint = path; return new NetworkStream(sock, true); }
                    catch { sock.Dispose(); owner = null; }
                }
                catch { }
            }
            return null;
        }

        static readonly string[] DiscordSubdirs = { "", "app/com.discordapp.Discord", "app/com.discordapp.DiscordCanary", "app/com.discordapp.DiscordPTB",
            "snap.discord", "snap.discord-canary", "app/dev.vencord.Vesktop", ".flatpak/dev.vencord.Vesktop/xdg-run" };

        /// <summary>Unix socket paths to try; under Wine each host path is offered as Z:\... first, then raw.</summary>
        public static System.Collections.Generic.List<string> UnixCandidates(bool wine)
        {
            var dirs = new System.Collections.Generic.List<string>();
            void AddDir(string d) { if (!string.IsNullOrEmpty(d) && d.StartsWith("/") && !dirs.Contains(d.TrimEnd('/'))) dirs.Add(d.TrimEnd('/')); }
            foreach (var v in new[] { "XDG_RUNTIME_DIR", "TMPDIR", "TMP", "TEMP" }) AddDir(Environment.GetEnvironmentVariable(v));
            if (wine)
            {
                // Wine may not pass XDG_RUNTIME_DIR through: look at /run/user/<uid> via the Z: drive.
                try { foreach (var d in Directory.GetDirectories(@"Z:\run\user")) AddDir("/run/user/" + Path.GetFileName(d)); } catch { }
            }
            try { if (IsWine() && WineUnixSocket.Available) { long uid = WineUnixSocket.GetUid(); if (uid >= 0) AddDir("/run/user/" + uid); } } catch { }
            AddDir("/tmp");
            var list = new System.Collections.Generic.List<string>();
            foreach (var d in dirs)
                foreach (var sub in DiscordSubdirs)
                    for (int n = 0; n < 10; n++)
                    {
                        string unix = d + (sub.Length > 0 ? "/" + sub : "") + "/discord-ipc-" + n;
                        if (wine) list.Add("Z:" + unix.Replace('/', '\\'));
                        list.Add(unix);
                    }
            return list;
        }

        static int _wine = -1;
        /// <summary>Wine/Proton exports wine_get_version from ntdll.</summary>
        public static bool IsWine()
        {
            if (_wine >= 0) return _wine == 1;
            bool w = false;
            try { if (System.Runtime.InteropServices.NativeLibrary.TryLoad("ntdll.dll", out var h)) w = System.Runtime.InteropServices.NativeLibrary.TryGetExport(h, "wine_get_version", out _); } catch { }
            _wine = w ? 1 : 0; return w;
        }
    }

    /// <summary>
    /// A unix-domain stream socket for a Windows process running under Wine/Proton, made with raw Linux system calls.
    /// Proton's seccomp filter (GE-Proton10-34 ntdll.so, decoded 2026-11-08) traps syscalls issued from low (PE) addresses and from
    /// 0x7fff_fe000000..0x7fff_ffff0000, and allows them from the Linux-library range (instruction pointer high word 0x7001..0x7ffe).
    /// So the 29-byte syscall stub is placed with VirtualAlloc at 0x71000000_0000 (or the next free 1 TiB step) and every call is
    /// verified once (getuid must look like a uid, not an NTSTATUS). Blocking is avoided: reads poll with a 250 ms timeout.
    /// </summary>
    public sealed unsafe class WineUnixSocket : Stream
    {
        [DllImport("kernel32")] static extern IntPtr VirtualAlloc(IntPtr a, UIntPtr s, uint t, uint p);
        [DllImport("kernel32")] static extern bool VirtualProtect(IntPtr a, UIntPtr s, uint p, out uint o);
        // Windows x64 (rcx=nr, rdx, r8, r9, [rsp+40], [rsp+48]) -> Linux syscall (rax, rdi, rsi, rdx, r10, r8); rsi/rdi preserved.
        static readonly byte[] Stub = { 0x56, 0x57, 0x48,0x89,0xC8, 0x48,0x89,0xD7, 0x4C,0x89,0xC6, 0x4C,0x89,0xCA, 0x4C,0x8B,0x54,0x24,0x38, 0x4C,0x8B,0x44,0x24,0x40, 0x0F,0x05, 0x5F, 0x5E, 0xC3 };
        const long SysRead = 0, SysWrite = 1, SysClose = 3, SysPoll = 7, SysSocket = 41, SysConnect = 42, SysShutdown = 48, SysGetUid = 102;
        const long EINTR = -4, EAGAIN = -11;
        static delegate* unmanaged<long, long, long, long, long, long, long> _sys;
        static int _state;   // 0 unknown, 1 ok, -1 unavailable

        public static bool Available
        {
            get
            {
                if (_state != 0) return _state > 0;
                try
                {
                    IntPtr mem = IntPtr.Zero;
                    for (long hi = 0x7100; hi <= 0x7f00 && mem == IntPtr.Zero; hi += 0x100) mem = VirtualAlloc((IntPtr)(hi << 32), (UIntPtr)4096, 0x3000, 0x04);
                    if (mem == IntPtr.Zero) { _state = -1; return false; }
                    Marshal.Copy(Stub, 0, mem, Stub.Length);
                    if (!VirtualProtect(mem, (UIntPtr)4096, 0x20, out _)) { _state = -1; return false; }
                    _sys = (delegate* unmanaged<long, long, long, long, long, long, long>)mem;
                    long uid = _sys(SysGetUid, 0, 0, 0, 0, 0);
                    _state = uid >= 0 && uid < 0x40000000 ? 1 : -1;
                    Log.Info($"wine unix socket: stub at 0x{mem.ToInt64():x}, getuid={uid}, {(_state > 0 ? "usable" : "trapped")}");
                }
                catch (Exception e) { _state = -1; try { Log.Info("wine unix socket unavailable: " + e.Message); } catch { } }
                return _state > 0;
            }
        }

        public static long GetUid() => Available ? _sys(SysGetUid, 0, 0, 0, 0, 0) : -1;
        public static long GetPid() => Available ? _sys(39 /*getpid*/, 0, 0, 0, 0, 0) : -1;

        public static WineUnixSocket TryConnect(string path)
        {
            if (!Available) return null;
            var bytes = Encoding.UTF8.GetBytes(path); if (bytes.Length >= 108) return null;
            long fd = _sys(SysSocket, 1 /*AF_UNIX*/, 1 | 0x80000 /*SOCK_STREAM|SOCK_CLOEXEC*/, 0, 0, 0);
            if (fd < 0) return null;
            var sa = new byte[110]; sa[0] = 1; Array.Copy(bytes, 0, sa, 2, bytes.Length);
            long r; fixed (byte* p = sa) r = _sys(SysConnect, fd, (long)p, 110, 0, 0);
            if (r != 0) { _sys(SysClose, fd, 0, 0, 0, 0); return null; }
            return new WineUnixSocket(fd);
        }

        long _fd; volatile bool _closed; int _users;
        WineUnixSocket(long fd) { _fd = fd; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null || offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
            Interlocked.Increment(ref _users);
            try
            {
                var pfd = stackalloc long[1];
                while (true)
                {
                    if (_closed) return 0;
                    ((int*)pfd)[0] = (int)_fd; ((short*)pfd)[2] = 1 /*POLLIN*/; ((short*)pfd)[3] = 0;
                    long pr = _sys(SysPoll, (long)pfd, 1, 250, 0, 0);
                    if (pr == 0 || pr == EINTR) continue;
                    if (pr < 0) throw new IOException("poll " + pr);
                    long n; fixed (byte* b = buffer) n = _sys(SysRead, _fd, (long)(b + offset), count, 0, 0);
                    if (n == EINTR || n == EAGAIN) continue;
                    if (n < 0) throw new IOException("read " + n);
                    return (int)n;
                }
            }
            finally { if (Interlocked.Decrement(ref _users) == 0 && _closed) CloseFd(); }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null || offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
            int done = 0;
            while (done < count)
            {
                if (_closed) throw new IOException("closed");
                long n; fixed (byte* b = buffer) n = _sys(SysWrite, _fd, (long)(b + offset + done), count - done, 0, 0);
                if (n == EINTR || n == EAGAIN) continue;
                if (n <= 0) throw new IOException("write " + n);
                done += (int)n;
            }
        }

        // Base Stream async serializes reads and writes on one semaphore: a pending read would block every write. Bypass it.
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.Run(() => Read(buffer, offset, count), ct);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) { Write(buffer, offset, count); return Task.CompletedTask; }

        void CloseFd() { long fd = Interlocked.Exchange(ref _fd, -1); if (fd >= 0) _sys(SysClose, fd, 0, 0, 0, 0); }
        protected override void Dispose(bool disposing)
        {
            if (!_closed) { _closed = true; long fd = _fd; if (fd >= 0) _sys(SysShutdown, fd, 2 /*SHUT_RDWR*/, 0, 0, 0); if (Volatile.Read(ref _users) == 0) CloseFd(); }
            base.Dispose(disposing);
        }

        public override bool CanRead => !_closed; public override bool CanWrite => !_closed; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
    }
}

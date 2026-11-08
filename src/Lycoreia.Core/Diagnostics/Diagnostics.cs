// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Lycoreia.Net;

namespace Lycoreia
{
    /// <summary>
    /// diagnostics.jsonl: bounded queue, one writer thread, rotation at 8 MiB with 7 backups, dropped records counted into the
    /// next record. Off unless enabled (development setting, deeplog and test builds).
    /// </summary>
    public sealed class DiagnosticsWriter : IDisposable
    {
        public const int QueueLimit = 8192, DetailLimit = 4096, Backups = 7;
        public const long RotateBytes = 8L * 1024 * 1024;
        public const int WindowSeconds = 15;

        readonly string _path; readonly long _rotate; readonly Func<DateTime> _utcNow;
        readonly BlockingCollection<string> _queue = new BlockingCollection<string>(QueueLimit);
        Thread _thread; volatile bool _failed; long _seq; int _dropped; long _written;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly string _session = Guid.NewGuid().ToString("N").Substring(0, 8);
        StreamWriter _w;

        public bool Enabled { get; private set; }
        public Func<string> HostId = () => ""; public Func<string> LocalId = () => ""; public Func<string> Role = () => "offline";

        public DiagnosticsWriter(string folder, long rotateBytes = RotateBytes, Func<DateTime> utcNow = null)
        { Directory.CreateDirectory(folder); _path = Path.Combine(folder, "diagnostics.jsonl"); _rotate = rotateBytes; _utcNow = utcNow ?? (() => DateTime.UtcNow); }

        public void Start(string version)
        {
            if (Enabled) return;
            // Optional diagnostics must not abort the expansion's remaining initialization.
            try { Rotate(startup: true); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Log.Warn("diagnostics could not start: " + e.Message);
                return;
            }
            Enabled = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "Lycoreia-diag" }; _thread.Start();
            Record("diagnostics", "diagnostics-started", $"version={version}; flavour={BuildInfo.Flavour}; mvid={typeof(DiagnosticsWriter).Assembly.ManifestModule.ModuleVersionId}; pid={Environment.ProcessId}; session={_session}");
#if DEEPLOG
            Record("diagnostics", "deeplog-started", "flags=DEEPLOG");
#endif
        }

        public static string Escape(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\""); else if (c == '\\') sb.Append("\\\\"); else if (c == '\n') sb.Append("\\n"); else if (c == '\r') sb.Append("\\r"); else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Queue a record. Never blocks the caller; a full queue drops and counts.</summary>
        public void Record(string category, string ev, string details = "")
        {
            if (!Enabled || _failed) return;
            if (details != null && details.Length > DetailLimit) details = details.Substring(0, DetailLimit);
            int dropped = Interlocked.Exchange(ref _dropped, 0);
            long seq = Interlocked.Increment(ref _seq);
            string line = "{\"utc\":\"" + _utcNow().ToString("O") + "\",\"ms\":" + _clock.ElapsedMilliseconds + ",\"seq\":" + seq +
                          ",\"host\":\"" + Escape(HostId()) + "\",\"role\":\"" + Escape(Role()) + "\",\"local\":\"" + Escape(LocalId()) +
                          "\",\"category\":\"" + Escape(category) + "\",\"event\":\"" + Escape(ev) + "\",\"details\":\"" + Escape(details) + "\",\"dropped\":" + dropped + "}";
            if (!_queue.TryAdd(line)) Interlocked.Add(ref _dropped, dropped + 1);
        }

        void Loop()
        {
            try
            {
                foreach (var line in _queue.GetConsumingEnumerable())
                {
                    if (_w == null) Open();
                    _w.WriteLine(line); _written += line.Length + 1;
                    if (_queue.Count == 0) _w.Flush();
                    if (_written >= _rotate) Rotate(false);
                }
            }
            catch (Exception e)
            {
                _failed = true; Log.Warn("diagnostics stopped: " + e.Message);
            }
            finally { try { _w?.Flush(); _w?.Dispose(); } catch { } }
        }

        void Open() { _w = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)); _written = new FileInfo(_path).Length; }

        void Rotate(bool startup)
        {
            try { _w?.Dispose(); _w = null; } catch { }
            if (!File.Exists(_path) || new FileInfo(_path).Length == 0) return;
            for (int i = Backups; i >= 1; i--)
            {
                string from = i == 1 ? _path : _path + "." + (i - 1), to = _path + "." + i;
                if (!File.Exists(from)) continue; if (File.Exists(to)) File.Delete(to); File.Move(from, to);
            }
        }

        /// <summary>Joins the writer for up to 3 seconds and closes the file.</summary>
        public void Flush()
        {
            if (!Enabled) return;
            Enabled = false; _queue.CompleteAdding();
            _thread?.Join(3000);
        }
        public void Dispose() => Flush();
    }

    /// <summary>Per-type packet counters turned into a summary record every 15 s; rejected packets are rate limited.</summary>
    public sealed class PacketWindow
    {
        readonly DiagnosticsWriter _d; readonly Func<ushort, string> _name; readonly Func<long> _clock;
        readonly Dictionary<(ushort, bool), (int count, long bytes, int parts)> _w = new Dictionary<(ushort, bool), (int, long, int)>();
        long _start; readonly RateLimitedLog _rej;
        public PacketWindow(DiagnosticsWriter d, Func<ushort, string> name, Func<long> clock) { _d = d; _name = name; _clock = clock; _start = clock(); _rej = new RateLimitedLog(clock, 10_000); }

        public void Seen(ushort type, int bytes, int parts, bool sent)
        {
            lock (_w)
            {
                _w.TryGetValue((type, sent), out var e); _w[(type, sent)] = (e.count + 1, e.bytes + bytes, Math.Max(e.parts, parts));
                if (_clock() - _start >= DiagnosticsWriter.WindowSeconds * 1000L) Emit();
            }
        }
        public void Rejected(ushort type, string why)
        {
            if (_rej.ShouldEmit("rej:" + type, out int dropped)) _d.Record("net", "packet-rejected", $"type={_name(type)}; reason={why}; suppressed={dropped}");
        }
        void Emit()
        {
            foreach (var kv in _w) _d.Record("net", "packet-window", $"type={_name(kv.Key.Item1)}; dir={(kv.Key.Item2 ? "out" : "in")}; count={kv.Value.count}; bytes={kv.Value.bytes}; maxParts={kv.Value.parts}");
            _w.Clear(); _start = _clock();
        }
        public void FlushNow() { lock (_w) Emit(); }
    }
}

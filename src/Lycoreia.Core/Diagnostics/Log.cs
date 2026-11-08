// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Lycoreia
{
    public enum LogLevel { Debug, Message, Warning, Error }
    public enum LogTarget { Main, Sensitive, Both, Neither }

    /// <summary>Static logger. Public log is safe to share; the sensitive log may contain addresses.</summary>
    public static class Log
    {
        /// <summary>Console sink, set by the entry point (MelonLogger in game; stdout in tests).</summary>
        public static Action<LogLevel, string> Console;
        public static bool PacketSizes;
        static LogFile _main, _sensitive;
        static readonly object Gate = new object();

        public static void Open(string folder, long maxBytes = 8L * 1024 * 1024, int backups = 2)
        {
            lock (Gate)
            {
                _main?.Dispose(); _sensitive?.Dispose();
                Directory.CreateDirectory(folder);
                _main = new LogFile(Path.Combine(folder, "latest.log"), maxBytes, backups);
                _sensitive = new LogFile(Path.Combine(folder, "sensitive.log"), maxBytes, backups);
            }
        }
        public static void Close() { lock (Gate) { _main?.Dispose(); _sensitive?.Dispose(); _main = _sensitive = null; } }

        public static void Debug(string m, LogTarget t = LogTarget.Main) => Write(LogLevel.Debug, m, t);
        public static void Info(string m, LogTarget t = LogTarget.Main) => Write(LogLevel.Message, m, t);
        public static void Warn(string m, LogTarget t = LogTarget.Main) => Write(LogLevel.Warning, m, t);
        public static void Error(string m, LogTarget t = LogTarget.Main) => Write(LogLevel.Error, m, t);
        public static void Error(string m, Exception e, LogTarget t = LogTarget.Main) => Write(LogLevel.Error, m + ": " + e, t);

        /// <summary>Redacted line to the public log and console, detail to the sensitive log only.</summary>
        public static void Split(LogLevel level, string redacted, string detail)
        {
            Write(level, redacted, LogTarget.Main);
            Write(level, detail, LogTarget.Sensitive, quietConsole: true);
        }

        public static string Format(LogLevel level, string m)
        {
            if (m.StartsWith("[")) return m;
            string lv = level == LogLevel.Message ? "INFO" : level.ToString().ToUpperInvariant();
            return $"[{DateTime.Now:HH:mm:ss}] [{lv}] {m}";
        }

        static void Write(LogLevel level, string m, LogTarget t, bool quietConsole = false)
        {
            if (t == LogTarget.Neither) return;
            string line = Format(level, m);
            lock (Gate)
            {
                if (t == LogTarget.Main || t == LogTarget.Both) _main?.WriteLine(line);
                if (t == LogTarget.Sensitive || t == LogTarget.Both) _sensitive?.WriteLine(line);
            }
            try
            {
                if (t == LogTarget.Sensitive) { if (!quietConsole) Console?.Invoke(level, "A sensitive message was logged!"); }
                else Console?.Invoke(level, m);
            }
            catch { }
        }

        sealed class LogFile : IDisposable
        {
            readonly string _path; readonly long _max; readonly int _backups; StreamWriter _w; long _size;
            public LogFile(string path, long max, int backups)
            {
                _path = path; _max = max; _backups = backups;
                Rotate(); // once at startup
            }
            void Rotate()
            {
                _w?.Dispose(); _w = null;
                try
                {
                    if (File.Exists(_path) && new FileInfo(_path).Length > 0)
                    {
                        for (int i = _backups; i >= 1; i--)
                        {
                            string from = i == 1 ? _path : _path + "." + (i - 1), to = _path + "." + i;
                            if (!File.Exists(from)) continue;
                            if (File.Exists(to)) File.Delete(to);
                            File.Move(from, to);
                        }
                    }
                    _w = new StreamWriter(new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
                    _size = 0;
                }
                catch { _w = null; }
            }
            public void WriteLine(string s)
            {
                if (_w == null) return;
                try { _w.WriteLine(s); _size += s.Length + 1; if (_size >= _max) Rotate(); } catch { }
            }
            public void Dispose() { try { _w?.Dispose(); } catch { } _w = null; }
        }
    }

    /// <summary>Logs a message at most once per interval per key; counts the suppressed ones.</summary>
    public sealed class RateLimitedLog
    {
        readonly Func<long> _clock; readonly long _intervalMs; readonly Dictionary<string, (long at, int dropped)> _last = new Dictionary<string, (long, int)>();
        public RateLimitedLog(Func<long> clock = null, long intervalMs = 5000) { _clock = clock ?? (() => Environment.TickCount64); _intervalMs = intervalMs; }
        /// <summary>Returns true if the caller should emit now; dropped receives the number suppressed since the last emit.</summary>
        public bool ShouldEmit(string key, out int dropped)
        {
            lock (_last)
            {
                long now = _clock(); dropped = 0;
                if (_last.TryGetValue(key, out var e) && now - e.at < _intervalMs) { _last[key] = (e.at, e.dropped + 1); return false; }
                dropped = e.dropped; _last[key] = (now, 0); return true;
            }
        }
        public void Warn(string key, string msg)
        {
            if (ShouldEmit(key, out int d)) Log.Warn(d > 0 ? $"{msg} (+{d} suppressed)" : msg);
        }
        public void Info(string key, string msg)
        {
            if (ShouldEmit(key, out int d)) Log.Info(d > 0 ? $"{msg} (+{d} suppressed)" : msg);
        }
    }
}

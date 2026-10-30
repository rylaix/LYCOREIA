using SR2MP.Shared.Utils;
using System.Text;
using System.Diagnostics;
using System.Text.Json;
using JetBrains.Annotations;
using MelonLoader;
using MelonLoader.Logging;
using MelonLoader.Utils;
using Starlight.Managers;
// ReSharper disable InconsistentNaming

namespace SR2MP;

/// <summary>
/// A utility class for handling formatted logging across file outputs, MelonLoader consoles, and Starlight management.
/// </summary>
[PublicApi]
public static class Logger
{
    private enum LogLevel : byte
    {
        Debug,
        Message,
        Warning,
        Error
    }

    /// <summary>
    /// Specifies the file destination(s) for a logged message.
    /// </summary>
    [Flags]
    public enum LogTarget : byte
    {
        /// <summary>
        /// Do not log to any file target.
        /// </summary>
        /// <remarks>Required to follow the standard but PLEASE don't use this value!</remarks>
        Neither = 0,

        /// <summary>
        /// Log to the main public log file.
        /// </summary>
        Main = 1 << 0,

        /// <summary>
        /// Log to the sensitive log file.
        /// </summary>
        Sensitive = 1 << 1,

        /// <summary>
        /// Log to both the main and sensitive log files.
        /// </summary>
        Both = Main | Sensitive
    }

    private static readonly MelonLogger.Instance _melonLogger;
    private static readonly LogHandler _logHandler;
    private static readonly LogHandler _sensitiveLogHandler;
    private static DiagnosticLogHandler? _diagnosticLogHandler;
    private static readonly Stopwatch ProcessClock = Stopwatch.StartNew();
    private static long _diagnosticSequence;
    private static string _diagnosticHostId = string.Empty;
    private static bool _diagnosticsEnabled;
    private static readonly object PacketWindowLock = new();
    private static readonly Dictionary<string, PacketWindow> PacketWindows = new();
    private static long NextPacketWindowAtMs = 15_000;

    static Logger()
    {
        _melonLogger = new MelonLogger.Instance(BuildInfo.Name, ColorARGB.FromArgb(77, 149, 203));

        var folderPath = ModPaths.DataDirectory;

        if (!Directory.Exists(folderPath))
            Directory.CreateDirectory(folderPath);

        _logHandler = new LogHandler(Path.Combine(folderPath, "latest.log"), 8 * 1024 * 1024, 2);
        _sensitiveLogHandler = new LogHandler(Path.Combine(folderPath, "sensitive.log"), 8 * 1024 * 1024, 2);
    }

    internal static bool DiagnosticsEnabled => _diagnosticsEnabled;

    internal static void ConfigureDiagnostics(bool enabled)
    {
#if SR2MP_DEEPLOG
        // Deep-log builds always record diagnostics, whatever the preference says.
        enabled = true;
#endif
        _diagnosticsEnabled = enabled;
        if (!enabled || _diagnosticLogHandler != null)
            return;

        var folderPath = ModPaths.DataDirectory;
        Directory.CreateDirectory(folderPath);
        _diagnosticLogHandler = new DiagnosticLogHandler(
            Path.Combine(folderPath, "diagnostics.jsonl"), 8 * 1024 * 1024, 7);
        LogDiagnostic("runtime", "diagnostics-started",
            $"version={BuildInfo.Version}; processId={Environment.ProcessId}; processSessionId={Guid.NewGuid():N}");
#if SR2MP_DEEPLOG
        LogDiagnostic("session", "deeplog-started", DescribeDeepLogBuild());
#endif
    }

#if SR2MP_DEEPLOG
    private static string DescribeDeepLogBuild()
    {
        var flags = "DEEPLOG"
#if SR2MP_TEST
                    + ",TEST"
#endif
            ;
        try
        {
            var path = typeof(Logger).Assembly.Location;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return $"version={BuildInfo.Version}; flags={flags}; dll=<unavailable>";

            var info = new FileInfo(path);
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.Create().ComputeHash(stream));
            return $"version={BuildInfo.Version}; flags={flags}; dll={info.Name}; bytes={info.Length}; " +
                   $"written={info.LastWriteTimeUtc:O}; sha256={hash}";
        }
        catch (Exception exception)
        {
            return $"version={BuildInfo.Version}; flags={flags}; dll=<error {exception.GetType().Name}>";
        }
    }
#endif

    internal static void SetDiagnosticHostId(string hostId)
        => _diagnosticHostId = string.IsNullOrWhiteSpace(hostId) ? string.Empty : hostId;

    internal static void RecordPacket(string direction, byte packetType, int bytes, ushort chunks)
    {
        if (!_diagnosticsEnabled)
            return;

        List<(string Key, PacketWindow Window)>? report = null;
        var now = ProcessClock.ElapsedMilliseconds;
        var typeName = Enum.GetName(typeof(SR2MP.Packets.Utils.PacketType), packetType) ?? $"Unknown_{packetType}";
        lock (PacketWindowLock)
        {
            var key = $"{direction}:{typeName}";
            if (!PacketWindows.TryGetValue(key, out var window))
                window = new PacketWindow(direction, typeName);
            window.Count++;
            window.Bytes += Math.Max(0, bytes);
            window.MaxChunks = Math.Max(window.MaxChunks, chunks);
            PacketWindows[key] = window;

            if (now >= NextPacketWindowAtMs)
            {
                report = PacketWindows.Select(pair => (pair.Key, pair.Value)).ToList();
                PacketWindows.Clear();
                NextPacketWindowAtMs = now + 15_000;
            }
        }

        if (report == null)
            return;
        foreach (var (key, window) in report)
            LogDiagnostic("network", "packet-window",
                $"direction={window.Direction}; type={window.Type}; count={window.Count}; bytes={window.Bytes}; maxChunks={window.MaxChunks}");
    }

    internal static void RecordNetworkProblem(string direction, byte packetType, string reason)
    {
        var typeName = Enum.GetName(typeof(SR2MP.Packets.Utils.PacketType), packetType) ?? $"Unknown_{packetType}";
        LogDiagnostic("network", "packet-rejected",
            $"direction={direction}; type={typeName}; reason={reason}",
            $"network-reject:{direction}:{packetType}:{reason}", 1000);
    }

    internal static void LogDiagnostic(string category, string eventName, string details,
        string? rateKey = null, int minimumIntervalMilliseconds = 0)
    {
        if (!_diagnosticsEnabled || _diagnosticLogHandler == null)
            return;

        if (minimumIntervalMilliseconds > 0 && !DiagnosticRateLimit.TryAcquire(
                rateKey ?? $"{category}:{eventName}", minimumIntervalMilliseconds))
            return;

        try
        {
            var server = Main.Server;
            var client = Main.Client;
            var role = server?.IsRunning == true ? "host" : client?.IsConnected == true ? "client" : "offline";
            var localId = server?.IsRunning == true ? server.PlayerId : client?.PlayerId ?? string.Empty;
            var hostId = server?.IsRunning == true ? server.PlayerId : _diagnosticHostId;
            var sequence = Interlocked.Increment(ref _diagnosticSequence);
            var dropped = _diagnosticLogHandler.TakeDroppedCount();
            var record = new DiagnosticRecord(
                DateTimeOffset.UtcNow.ToString("O"),
                ProcessClock.ElapsedMilliseconds,
                sequence,
                hostId,
                role,
                localId,
                category,
                eventName,
                details.Length <= 4096 ? details : details[..4096],
                dropped);
            _diagnosticLogHandler.TryWrite(JsonSerializer.Serialize(record));
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _diagnosticFailureReported, 1) == 0)
                MelonLogger.Warning($"Development diagnostics disabled after write error: {exception.GetType().Name}");
        }
    }

    private static int _diagnosticFailureReported;

    internal static void FlushDiagnostics()
    {
        _diagnosticLogHandler?.Dispose();
        _diagnosticLogHandler = null;
        _diagnosticsEnabled = false;
    }

    /// <summary>
    /// Logs a standard informational message.
    /// </summary>
    /// <inheritdoc cref="LogInternal"/>
    public static void LogMessage(object? message, SrLogTarget target = SrLogTarget.Both)
        => LogInternal(message, LogLevel.Message, target, StarlightLogManager.SendMessage, _melonLogger.Msg);

    /// <summary>
    /// Logs a warning message.
    /// </summary>
    /// <inheritdoc cref="LogInternal"/>
    public static void LogWarning(object? message, SrLogTarget target = SrLogTarget.Both)
        => LogInternal(message, LogLevel.Warning, target, StarlightLogManager.SendWarning, _melonLogger.Warning);

    /// <summary>
    /// Logs an error message.
    /// </summary>
    /// <inheritdoc cref="LogInternal"/>
    public static void LogError(object? message, SrLogTarget target = SrLogTarget.Both)
        => LogInternal(message, LogLevel.Error, target, null, _melonLogger.Error);

    /// <summary>
    /// Logs a debug message, which bypasses Starlight and MelonLoader outputs.
    /// </summary>
    /// <inheritdoc cref="LogInternal"/>
    public static void LogDebug(object? message, SrLogTarget target = SrLogTarget.Both)
    {
        if (DevMode)
            LogInternal(message, LogLevel.Debug, target, null, _melonLogger.Msg);
    }

    /// <summary>
    /// Logs a garden-related message, if garden logging is globally enabled.
    /// </summary>
    /// <inheritdoc cref="LogInternal"/>
    public static void LogGarden(object? message, SrLogTarget target = SrLogTarget.Both)
    {
        if (GardenLogging)
            LogInternal(message, LogLevel.Debug, target, null, _melonLogger.Msg);
    }

    /// <summary>
    /// Logs packet size information, if packet size logging is globally enabled.
    /// </summary>
    /// <inheritdoc cref="LogInternal"/>
    public static void LogPacketSize(object? message, SrLogTarget target = SrLogTarget.Both)
    {
        if (Main.PacketSizeLogging)
            LogInternal(message, LogLevel.Message, target, null, _melonLogger.Msg);
    }

    /// <summary>
    /// Logs packet acknowledgment information, if packet acknowledgment logging is globally enabled.
    /// </summary>
    /// <inheritdoc cref="LogInternal"/>
    public static void LogPacketAcknowledge(object? message, SrLogTarget target = SrLogTarget.Both)
    {
        if (Main.PacketAcknowledgeLogging)
            LogInternal(message, LogLevel.Warning, target, null, _melonLogger.Msg);
    }

    /// <summary>
    /// Backing method for logging messages of various levels.
    /// </summary>
    /// <param name="message">The message to log.</param>
    /// <param name="level">The message level.</param>
    /// <param name="target">The intended log file targets.</param>
    /// <param name="sr2EAction">The Starlight logging action.</param>
    /// <param name="melonAction">The MelonLoader logging action.</param>
    private static void LogInternal(object? message, LogLevel level, SrLogTarget target, Action<string>? sr2EAction, Action<string>? melonAction)
    {
        if (target == SrLogTarget.Neither)
            return;

        var msgString = message?.ToString() ?? "message was null!";
        var formattedLine = Format(msgString, level);

        if (target.HasFlag(SrLogTarget.Main))
            _logHandler.Write(formattedLine);

        if (target.HasFlag(SrLogTarget.Sensitive))
            _sensitiveLogHandler.Write(formattedLine);

        if (target == SrLogTarget.Sensitive)
            msgString = $"A sensitive [{level}] message was logged!";

        sr2EAction?.Invoke(msgString);
        melonAction?.Invoke(msgString);
    }

    /// <summary>
    /// Logs a standard informational message, separating public output from sensitive file output.
    /// </summary>
    /// <inheritdoc cref="LogSplit"/>
    public static void LogMessage(object? publicMsg, object? sensitiveMsg)
        => LogSplit(publicMsg, sensitiveMsg, LogLevel.Message, StarlightLogManager.SendMessage, _melonLogger.Msg);

    /// <summary>
    /// Logs a warning message, separating public output from sensitive file output.
    /// </summary>
    /// <inheritdoc cref="LogSplit"/>
    public static void LogWarning(object? publicMsg, object? sensitiveMsg)
        => LogSplit(publicMsg, sensitiveMsg, LogLevel.Warning, StarlightLogManager.SendWarning, _melonLogger.Warning);

    /// <summary>
    /// Logs an error message, separating public output from sensitive file output.
    /// </summary>
    /// <inheritdoc cref="LogSplit"/>
    public static void LogError(object? publicMsg, object? sensitiveMsg)
        => LogSplit(publicMsg, sensitiveMsg, LogLevel.Error, StarlightLogManager.SendError, _melonLogger.Error);

    /// <summary>
    /// Logs a debug message, separating public output from sensitive file output.
    /// </summary>
    /// <inheritdoc cref="LogSplit"/>
    public static void LogDebug(object? publicMsg, object? sensitiveMsg)
        => LogSplit(publicMsg, sensitiveMsg, LogLevel.Debug, null, null);

    /// <summary>
    /// Logs packet size information if globally enabled, separating public output from sensitive file output.
    /// </summary>
    /// <inheritdoc cref="LogSplit"/>
    public static void LogPacketSize(object? publicMsg, object? sensitiveMsg)
    {
        if (Main.PacketSizeLogging)
            LogSplit(publicMsg, sensitiveMsg, LogLevel.Message, null, _melonLogger.Msg);
    }

    /// <summary>
    /// Backing method for logging separate messages of separate importance.
    /// </summary>
    /// <param name="publicMsg">The message sent to public logs and MelonLoader.</param>
    /// <param name="sensitiveMsg">The detailed message sent only to the sensitive log file.</param>
    /// <param name="level">The message level.</param>
    /// <param name="sr2EAction">The Starlight logging action.</param>
    /// <param name="melonAction">The MelonLoader logging action.</param>
    private static void LogSplit(object? publicMsg, object? sensitiveMsg, LogLevel level, Action<string>? sr2EAction, Action<string>? melonAction)
    {
        var publicStr = publicMsg?.ToString() ?? "public message was null!";
        var sensitiveStr = sensitiveMsg?.ToString() ?? "sensitive message was null!";

        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var levelStr = level.ToString().ToUpperInvariant();

        _logHandler.Write(FormatLocal(publicStr));
        _sensitiveLogHandler.Write(FormatLocal(sensitiveStr));

        sr2EAction?.Invoke(publicStr);
        melonAction?.Invoke(publicStr);

        string FormatLocal(string msg) => msg.StartsWith('[') ? msg : $"[{timestamp}] [{levelStr}] {msg}";
    }

    private static string Format(string message, LogLevel level) => message.StartsWith('[')
        ? message // Assumed that the message is already formatted
        : $"[{DateTime.Now:HH:mm:ss}] [{level.ToString().ToUpperInvariant()}] {message}";

    private sealed class LogHandler : IDisposable
    {
        private StreamWriter? _writer;
        private readonly object _lock = new();
        private readonly string _path;
        private readonly long _maxBytes;
        private readonly int _backupCount;
        private long _currentBytes;

        public LogHandler(string path, long maxBytes, int backupCount)
        {
            try
            {
                _path = path;
                _maxBytes = maxBytes;
                _backupCount = backupCount;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                RotateAtStartup();
                OpenWriter();
            }
            catch (Exception ex)
            {
                _path = path;
                _maxBytes = maxBytes;
                _backupCount = backupCount;
                MelonLogger.Error($"Failed to initialize log file at {path}: {ex.Message}");
            }
        }

        public void Write(string line)
        {
            if (_writer == null)
                return;

            lock (_lock)
            {
                try
                {
                    var lineBytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                    if (_currentBytes + lineBytes > _maxBytes)
                    {
                        Rotate();
                        OpenWriter();
                    }

                    if (_writer == null)
                        return;
                    _writer.WriteLine(line);
                    _writer.Flush();
                    _currentBytes += lineBytes;
                }
                catch
                {
                    // ignored
                }
            }
        }

        private void RotateAtStartup()
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length == 0)
                return;
            RotateFiles();
        }

        private void Rotate()
        {
            _writer?.Dispose();
            _writer = null;
            RotateFiles();
        }

        private void RotateFiles()
        {
            for (var index = _backupCount; index >= 1; index--)
            {
                var source = index == 1 ? _path : $"{_path}.{index - 1}";
                var destination = $"{_path}.{index}";
                if (File.Exists(destination))
                    File.Delete(destination);
                if (File.Exists(source))
                    File.Move(source, destination);
            }
        }

        private void OpenWriter()
        {
            _writer = new StreamWriter(_path, false, new UTF8Encoding(false));
            _currentBytes = 0;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    private sealed record DiagnosticRecord(string Utc, long UptimeMs, long Sequence, string SessionId,
        string Role, string LocalPeerId, string Category, string Event, string Details, int DroppedBefore);

    private sealed class PacketWindow
    {
        internal PacketWindow(string direction, string type)
        {
            Direction = direction;
            Type = type;
        }

        internal string Direction { get; }
        internal string Type { get; }
        internal long Count;
        internal long Bytes;
        internal ushort MaxChunks;
    }

    private sealed class DiagnosticLogHandler : IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<string> _queue = new(2048);
        private readonly LogHandler _writer;
        private readonly System.Threading.Thread _thread;
        private long _dropped;

        internal DiagnosticLogHandler(string path, long maxBytes, int backupCount)
        {
            _writer = new LogHandler(path, maxBytes, backupCount);
            _thread = new System.Threading.Thread(WriteLoop)
            {
                IsBackground = true,
                Name = "Lycoreia diagnostics writer"
            };
            _thread.Start();
        }

        internal void TryWrite(string line)
        {
            if (!_queue.TryAdd(line))
                Interlocked.Increment(ref _dropped);
        }

        internal int TakeDroppedCount() => (int)Math.Min(int.MaxValue, Interlocked.Exchange(ref _dropped, 0));

        private void WriteLoop()
        {
            foreach (var line in _queue.GetConsumingEnumerable())
                _writer.Write(line);
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            if (!_thread.Join(TimeSpan.FromSeconds(3)))
                _thread.Interrupt();
            _writer.Dispose();
            _queue.Dispose();
        }
    }

    private static class DiagnosticRateLimit
    {
        private static readonly object Lock = new();
        private static readonly Dictionary<string, long> LastTimestamp = new();

        internal static bool TryAcquire(string key, int minimumIntervalMilliseconds)
        {
            var now = ProcessClock.ElapsedMilliseconds;
            lock (Lock)
            {
                if (LastTimestamp.TryGetValue(key, out var last) &&
                    now - last < minimumIntervalMilliseconds)
                    return false;
                LastTimestamp[key] = now;
                if (LastTimestamp.Count > 4096)
                    LastTimestamp.Clear();
                return true;
            }
        }
    }
}

// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Owned;

/// <summary>
/// A warning that may fire very often: logs the first one, then at most one per interval with the number of
/// repeats it swallowed since, so the log keeps the signal without hundreds of identical lines.
/// </summary>
public sealed class ThrottledWarning
{
    private readonly TimeSpan interval;
    private readonly object gate = new();
    private DateTime lastLogged = DateTime.MinValue;
    private int suppressed;

    internal ThrottledWarning(TimeSpan interval) => this.interval = interval;

    internal void Log(string message)
    {
        int repeats;
        lock (gate)
        {
            var now = DateTime.UtcNow;
            if (now - lastLogged < interval)
            {
                suppressed++;
                return;
            }

            repeats = suppressed;
            suppressed = 0;
            lastLogged = now;
        }

        Lycoreia.Log.Warn(repeats > 0 ? $"{message} (+{repeats} similar in the last {interval.TotalSeconds:0} s)" : message);
    }
}

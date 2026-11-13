// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Threading;

namespace Lycoreia
{
    public static class Watchdog
    {
        public const int StallSeconds = 5;
        static long _beat; static volatile string _step = "start"; static long _frame;
        static Thread _thread; static volatile bool _stop;

        /// <summary>Main thread: entering a named step (cheap: one interlocked add and a reference write).</summary>
        public static void Step(string name) { _step = name; Interlocked.Increment(ref _beat); }
        public static void Frame() { Interlocked.Increment(ref _frame); Step("frame start"); }

        /// <summary>Pure: which stall lengths get a report (5 s, 15 s, 60 s, then every 5 min).</summary>
        public static bool ShouldReport(int stalledSeconds, int lastReported)
        {
            int[] marks = { StallSeconds, 15, 60 };
            foreach (var m in marks) if (stalledSeconds >= m && lastReported < m) return true;
            return stalledSeconds >= 300 && stalledSeconds - lastReported >= 300;
        }

        public static void Start()
        {
            if (_thread != null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "Lycoreia watchdog", Priority = System.Threading.ThreadPriority.BelowNormal };
            _thread.Start();
        }
        public static void Stop() => _stop = true;

        static void Run()
        {
            long seen = Interlocked.Read(ref _beat); int stalled = 0, reported = 0;
            while (!_stop)
            {
                Thread.Sleep(1000);
                long now = Interlocked.Read(ref _beat);
                if (now != seen)
                {
                    if (reported > 0) { try { Log.Warn($"[watchdog] main thread moving again after {stalled} s (was stuck in '{_step}')"); } catch { } }
                    seen = now; stalled = 0; reported = 0; continue;
                }
                stalled++;
                if (!ShouldReport(stalled, reported)) continue;
                reported = stalled;
                try
                {
                    string step = _step;
                    Log.Warn($"[watchdog] main thread has not moved for {stalled} s; last step: '{step}' (frame {Interlocked.Read(ref _frame)}). " +
                             (step == "between frames" ? "Our code had finished this frame: the stall is in the game or Unity." : "This Lycoreia step is the suspect."));
                    Mod.Diagnostics?.Record("runtime", "watchdog-stall", $"seconds={stalled}; step={step}; frame={Interlocked.Read(ref _frame)}");
                }
                catch { }
            }
        }
    }
}

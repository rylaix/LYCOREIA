// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Lycoreia.Game
{
    static class LagProbe
    {
        static readonly Dictionary<string, long> NextReport = new Dictionary<string, long>();
        static long _lastFrame, _nextSettings;
        public static long Start() => Mod.IsNetworkActive && Mod.Diagnostics?.Enabled == true ? Stopwatch.GetTimestamp() : 0;
        public static void Frame()
        {
            long now = Start();
            if (now == 0) { _lastFrame = 0; NextReport.Clear(); return; }
            if (now >= _nextSettings)
            {
                _nextSettings = now + 30 * Stopwatch.Frequency;
                Mod.Diagnostics?.Record("performance", "runtime-frame-settings", $"target-fps={UnityEngine.Application.targetFrameRate}; vsync={UnityEngine.QualitySettings.vSyncCount}; render-interval={UnityEngine.Rendering.OnDemandRendering.renderFrameInterval}; loading={GameState.Loading}; time-scale={UnityEngine.Time.timeScale}; pausers={Il2Cpp.SceneContext.Instance?.TimeDirector?._pauserCount}");
            }
            if (_lastFrame != 0) Report("frame-gap", _lastFrame, now, 100);
            _lastFrame = now;
        }
        public static void End(string name, long started)
        {
            if (started != 0) Report(name, started, Stopwatch.GetTimestamp(), 20);
        }
        static void Report(string name, long started, long now, double thresholdMs)
        {
            double elapsed = (now - started) * 1000.0 / Stopwatch.Frequency;
            if (elapsed < thresholdMs || NextReport.TryGetValue(name, out var next) && now < next) return;
            NextReport[name] = now + 5 * Stopwatch.Frequency;
            try { Mod.Diagnostics?.Record("performance", "slow-frame-work", $"part={name}; ms={elapsed:0.00}; gc0={GC.CollectionCount(0)}; gc1={GC.CollectionCount(1)}; gc2={GC.CollectionCount(2)}"); }
            catch { } // optional diagnostics must not interrupt the frame loop
        }
    }
}

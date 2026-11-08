// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
#if DEEPLOG
// Lycoreia 2 core - deeplog-only pause / held-tablet tracing. Compiled out of release builds. Observes only; every hook is exception-safe and never alters behaviour.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace Lycoreia.Game.Hooks
{
    static class PauseTrace
    {
        const string Cat = "pause-trace";
        const int MaxEntries = 800;       // per-process cap on individual lines; summaries still flush
        const double FlushEvery = 5.0;
        static int _written;
        static double _nextFlush;
        static int _lastCount = int.MinValue;
        static readonly Dictionary<string, int> StackCounts = new Dictionary<string, int>();
        static readonly Dictionary<string, int> PendingByStack = new Dictionary<string, int>();
        static readonly Queue<string> Trail = new Queue<string>();   // recent hooked native callers (fallback when stacks are useless)
        static readonly object Gate = new object();
        static bool _installed;

        /// <summary>Per-frame poll (from the Pump list, only once in-game): installs the hooks on the first in-game frame, then watches the pauser count and flushes summaries.</summary>
        public sealed class Poll : IFramePump
        {
            public string Name => "pause-trace";
            public void Pump()
            {
                if (!_installed) { Install(); return; }
                try { Tick(); } catch { }
            }
        }

        // Only explicitly named methods are patched. No type scan, and no hooks on hot or ICF-shared bodies
        // (TimeDirector.LateUpdate, UIInputLocker.OnEnable/OnDisable, DelayedUnpause, ApplyUIState, OnSetHeld): the first deeplog build died natively right after UIInputLocker.OnEnable fired.
        static void Install()
        {
            if (_installed) return; _installed = true;
            try
            {
                var h = new HarmonyLib.Harmony("lycoreia.pause-trace");
                var enter = Method(nameof(Enter)); var post = Method(nameof(PausePost)); var tPre = Method(nameof(ToggleEnter)); var tPost = Method(nameof(TogglePost));
                int n = 0;
                n += Patch(h, typeof(TimeDirector), nameof(TimeDirector.Pause), post: post);
                n += Patch(h, typeof(TimeDirector), nameof(TimeDirector.Unpause), post: post);
                n += Patch(h, typeof(PediaOpener), "ShowPedia", pre: enter);
                n += Patch(h, typeof(PediaOpener), "ShowVac", pre: enter);
                n += Patch(h, typeof(Il2CppMonomiPark.SlimeRancher.UI.Framework.Displays.UIDisplayManager), "PauseGame", pre: enter);
                n += Patch(h, typeof(Il2CppMonomiPark.SlimeRancher.UI.Framework.Displays.UIDisplayManager), "UnpauseGame", pre: enter);
                n += Patch(h, typeof(Il2CppMonomiPark.SlimeRancher.Script.UI.Pause.PauseMenuDirector), "TogglePause", pre: tPre, post: tPost);
                n += Patch(h, typeof(Il2CppMonomiPark.SlimeRancher.UI.Pedia.PediaRoot), "ClosePedia", pre: enter);
                n += Patch(h, typeof(Il2CppMonomiPark.SlimeRancher.Player.PlayerItems.GadgetItem), "ClearHeldGadget", pre: enter);
                n += Patch(h, typeof(Il2CppMonomiPark.SlimeRancher.Player.PlayerItems.GadgetItem), "SetHeldGadget", pre: enter);
                Mod.Diagnostics?.Record(Cat, "installed", $"patched={n}; keep-sim={Lycoreia.Actors.PausePolicy.MenusKeepSimulating}");
            }
            catch (Exception e) { Log.Warn("pause-trace install failed: " + e.Message); }
        }

        static MethodInfo Method(string name) => typeof(PauseTrace).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        // Null-safe: a missing or ambiguous method is skipped and never breaks the patch run.
        static int Patch(HarmonyLib.Harmony h, Type t, string name, MethodInfo pre = null, MethodInfo post = null)
        {
            try
            {
                if (t == null) return 0;
                var m = t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                         .FirstOrDefault(x => x.Name == name && !x.IsGenericMethodDefinition);
                if (m == null) { Mod.Diagnostics?.Record(Cat, "hook-missing", $"{t.Name}.{name}"); return 0; }
                h.Patch(m, pre != null ? new HarmonyMethod(pre) : null, post != null ? new HarmonyMethod(post) : null);
                return 1;
            }
            catch (Exception e) { Mod.Diagnostics?.Record(Cat, "hook-failed", $"{t?.Name}.{name}: {e.Message}"); return 0; }
        }

        static string PathOf(object o)
        {
            try
            {
                if (o is Component c && c != null)
                {
                    var sb = new StringBuilder(c.gameObject.name); var p = c.transform.parent;
                    for (int i = 0; p != null && i < 8; i++, p = p.parent) sb.Insert(0, p.name + "/");
                    return sb.ToString();
                }
            }
            catch { }
            return "";
        }

        static string State()
        {
            var td = SceneContext.Instance?.TimeDirector;
            return $"frame={Time.frameCount}; pausers={(td != null ? td._pauserCount : -1)}; timeScale={Time.timeScale:0.##}";
        }

        static void AddTrail(string s) { lock (Gate) { Trail.Enqueue($"{Time.frameCount}:{s}"); while (Trail.Count > 8) Trail.Dequeue(); } }
        static string TrailText() { lock (Gate) return string.Join(" < ", Trail.Reverse()); }

        static bool Allow() { lock (Gate) { if (_written >= MaxEntries) return false; _written++; return true; } }

        static void Enter(object __instance, MethodBase __originalMethod)
        {
            try
            {
                var name = $"{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}";
                AddTrail(name);
                if (!Allow()) return;
                Mod.Diagnostics?.Record(Cat, "enter", $"{name}; {State()}; obj={PathOf(__instance)}");
            }
            catch { }
        }

        static void ToggleEnter(object __instance)
        {
            try
            {
                var td = SceneContext.Instance?.TimeDirector;
                AddTrail("PauseMenuDirector.TogglePause");
                if (!Allow()) return;
                Mod.Diagnostics?.Record(Cat, "toggle-pause-enter", State());
            }
            catch { }
        }

        static void TogglePost()
        {
            try
            {
                var td = SceneContext.Instance?.TimeDirector;
                if (!Allow()) return;
                Mod.Diagnostics?.Record(Cat, "toggle-pause-result", State());
            }
            catch { }
        }

        static string Stack()
        {
            string s = null;
            try { s = new Il2CppSystem.Diagnostics.StackTrace(2, false).ToString(); } catch { }
            if (string.IsNullOrWhiteSpace(s))
            {
                try { s = new System.Diagnostics.StackTrace(2, false).ToString(); } catch { }
            }
            if (string.IsNullOrWhiteSpace(s)) return "<no-stack>";
            var lines = s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(14);
            return string.Join(" | ", lines);
        }

        static void PausePost(object __instance, MethodBase __originalMethod)
        {
            try
            {
                var name = __originalMethod.Name;
                AddTrail("TimeDirector." + name);
                string key = name + "|" + Stack() + "|trail=" + TrailText();
                bool first;
                lock (Gate)
                {
                    StackCounts.TryGetValue(key, out int c); StackCounts[key] = c + 1; first = c == 0;
                    if (!first) { PendingByStack.TryGetValue(key, out int p); PendingByStack[key] = p + 1; }
                }
                if (first && Allow())
                    Mod.Diagnostics?.Record(Cat, "td-" + name.ToLowerInvariant(), $"{State()}; stack={key}");
            }
            catch { }
        }

        static void Tick()
        {
            var td = SceneContext.Instance?.TimeDirector;
            if (td == null) return;
            int cnt = td._pauserCount;
            if (cnt != _lastCount)
            {
                int prev = _lastCount; _lastCount = cnt;
                if (Allow()) Mod.Diagnostics?.Record(Cat, "count-changed", $"{State()}; from={prev}; trail={TrailText()}");
            }
            double now = Time.unscaledTimeAsDouble;
            if (now < _nextFlush) return;
            _nextFlush = now + FlushEvery;
            List<KeyValuePair<string, int>> pend;
            lock (Gate) { pend = PendingByStack.ToList(); PendingByStack.Clear(); }
            foreach (var kv in pend)
                Mod.Diagnostics?.Record(Cat, "td-summary", $"repeats={kv.Value}; total={StackCounts.GetValueOrDefault(kv.Key)}; {kv.Key}");
        }
    }
}
#endif

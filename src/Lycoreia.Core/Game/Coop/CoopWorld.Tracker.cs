// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Runtime.CompilerServices;
using Il2CppTMPro;
using Il2CppMonomiPark.SlimeRancher.UI;
using Il2CppMonomiPark.SlimeRancher.UI.HUD.States;
using Lycoreia.Coop;
using Lycoreia.Owned.Components.UI;
using Lycoreia.Text;
using UnityEngine;

namespace Lycoreia.Game.Coop
{
    /// <summary>Pure tracker maths (tested).</summary>
    public static class TrackerMath
    {
        public const float UrgentFraction = 0.2f;
        public static float Progress(int progress, int target) => target <= 0 ? 0f : Math.Min(1f, Math.Max(0f, progress / (float)target));
        /// <summary>Share of the time window still left, 0..1 (0 for an empty window).</summary>
        public static float TimeLeftFraction(long nowUtc, long startUtc, long endUtc)
        {
            long total = endUtc - startUtc; if (total <= 0) return 0f;
            return Math.Min(1f, Math.Max(0f, (endUtc - nowUtc) / (float)total));
        }
        public static bool Urgent(bool running, long nowUtc, long startUtc, long endUtc) => running && endUtc > startUtc && TimeLeftFraction(nowUtc, startUtc, endUtc) <= UrgentFraction;
        public static string Clock(long seconds) { if (seconds < 0) seconds = 0; return (seconds / 60) + ":" + (seconds % 60).ToString("00"); }
        /// <summary>The panel shows only while the native HUD does.</summary>
        public static bool Visible(bool runActive, bool hudObjectActive, bool hideTagSet) => runActive && hudObjectActive && !hideTagSet;
        /// <summary>0 none, 1 small (progress went up), 2 big (finished): what the panel should do after the run changed.</summary>
        public static int Pulse(bool sameRun, bool sawRunning, ExpeditionOutcome was, ExpeditionOutcome now, int oldProgress, int newProgress)
        {
            if (!sameRun) return 0;
            if (was == ExpeditionOutcome.Running && now != ExpeditionOutcome.Running && now != ExpeditionOutcome.Abandoned && sawRunning) return 2;
            if (now == ExpeditionOutcome.Running && newProgress > oldProgress) return 1;
            return 0;
        }
    }

    static partial class CoopWorld
    {
        static GameObject _hudRoot; static RectTransform _panel, _fill; static TextMeshProUGUI _title, _count, _clock, _goal;
        static UnityEngine.UI.Image _fillImg; static UiFade _fade; static bool _shown;
        static long _evKey = -1; static ExpeditionOutcome _evOutcome; static int _evProgress; static bool _evSawRunning; static int _pulse;
        static bool _hudGateLogged; static string _lastGate; static int _gateLogs;
        static readonly Color Accent = new Color(0.50f, 0.82f, 1f), Good = new Color(0.55f, 1f, 0.55f), Bad = new Color(1f, 0.35f, 0.30f);

        /// <summary>Expedition tracker under the top of the screen while one runs (and for a few seconds after it ends). Never throws.</summary>
        static void Tracker(double now)
        {
            try
            {
                var run = CoopRuntime.Board.Expedition; var def = run != null ? Expeditions.Find(run.DefId) : null;
                long utc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                bool active = def != null && (run.Outcome == ExpeditionOutcome.Running || utc - run.EndsUtc < 8 && run.Outcome != ExpeditionOutcome.Abandoned);
                if (!active) { _evKey = -1; SetShown(false); return; }
                TrackEvents(run);
                bool hudActive = true, tagSet = false; HudGate(out hudActive, out tagSet);
                if (!TrackerMath.Visible(true, hudActive, tagSet)) { SetShown(false); return; }
                EnsurePanel(); if (_panel == null) return;
                bool running = run.Outcome == ExpeditionOutcome.Running; long left = Math.Max(0, run.EndsUtc - utc);
                bool urgent = TrackerMath.Urgent(running, utc, run.StartedUtc, run.EndsUtc);
                // name and goal in the player's language (the menu already did this), goal and clock on their own lines.
                SetText(_title, "<b>" + MenuText.ExpName(def) + "</b>");
                SetText(_count, run.Progress + "/" + run.Target);
                string clock = run.Outcome switch { ExpeditionOutcome.Succeeded => CoopWorldText.Complete, ExpeditionOutcome.Failed => CoopWorldText.TimeUp, _ => CoopWorldText.Left(TrackerMath.Clock(left)) };
                SetText(_clock, clock); _clock.color = run.Outcome == ExpeditionOutcome.Succeeded ? Good : (urgent || run.Outcome == ExpeditionOutcome.Failed) ? Bad : new Color(1f, 0.97f, 0.88f, 0.9f);
                SetText(_goal, MenuText.ExpGoal(def));
                float f = TrackerMath.Progress(run.Progress, run.Target); _fill.anchorMax = new Vector2(f, 1f);
                _fillImg.color = run.Outcome == ExpeditionOutcome.Succeeded ? Good : (urgent || run.Outcome == ExpeditionOutcome.Failed) ? Bad : Accent;
                SetShown(true);
                if (_pulse > 0 && _fade != null && _shown) { float big = _pulse == 2 ? 1.1f : 1.04f; _pulse = 0; _fade.Play(_panel, 1f, big, 0.12f, false, () => { try { _fade.Play(_panel, 1f, 1f, 0.3f, false); } catch { } }); }
            }
            catch (Exception e) { Rl.Warn("tracker", "expedition tracker: " + e.Message); }
        }

        /// <summary>Chime + pulse when the run we watched finished, small pulse on progress. Runs even while the HUD is hidden so the sound is not lost.</summary>
        static void TrackEvents(ExpeditionRun run)
        {
            long key = run.StartedUtc * 100 + run.DefId; bool same = key == _evKey;
            int pulse = TrackerMath.Pulse(same, _evSawRunning, _evOutcome, run.Outcome, _evProgress, run.Progress);
            if (!same) { _evKey = key; _evSawRunning = false; }
            if (run.Outcome == ExpeditionOutcome.Running) _evSawRunning = true;
            _evOutcome = run.Outcome; _evProgress = run.Progress;
            if (pulse == 2) { try { UiFx.Play(run.Outcome == ExpeditionOutcome.Succeeded ? UiCue.Success : UiCue.Fail); } catch { } }
            if (pulse > _pulse) _pulse = pulse;
        }

        /// <summary>Native HUD gate. Falls back to "visible" if the HUD types cannot be read (logged once).</summary>
        static void HudGate(out bool hudActive, out bool tagSet)
        {
            hudActive = true; tagSet = false; string why = "visible";
            try { ReadHud(out hudActive, out tagSet, out why); }
            catch (Exception e) { if (!_hudGateLogged) { _hudGateLogged = true; Log.Warn("expedition HUD gate unavailable, always showing: " + e.Message); } hudActive = true; tagSet = false; return; }
            if (why != _lastGate && _gateLogs < 40) { _gateLogs++; Log.Info("HUD gate (expedition panel, world labels): native HUD " + (why == "visible" ? "visible" : "hidden (" + why + ")")); }
            _lastGate = why;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ReadHud(out bool hudActive, out bool tagSet, out string why)
        {
            var hud = HudUI.Instance; why = "visible"; tagSet = false;
            hudActive = hud != null && hud.gameObject != null && hud.gameObject.activeInHierarchy;
            if (!hudActive) { why = hud == null ? "no HudUI" : "HudUI object inactive"; return; }
            var tag = hud._hideHudTag; var state = HudState.Instance;
            if (tag != null && state != null) { try { tagSet = state.HasStateTag(tag.Cast<IHudStateTag>()); } catch { tagSet = false; } if (tagSet) why = "hide-HUD tag '" + tag.Identifier + "'"; }
        }

        static void SetText(TextMeshProUGUI t, string s) { if (t != null && t.text != s) t.text = s; }

        static void SetShown(bool on)
        {
            if (on == _shown) return;
            _shown = on;
            if (_hudRoot == null) return;
            if (on) { _hudRoot.SetActive(true); _fade?.Play(_panel, 1f, 1f, 0.22f, false, null, 0f, 0.9f); }
            else _fade?.Play(_panel, 0f, 0.96f, 0.18f, false, () => { try { if (!_shown && _hudRoot != null) _hudRoot.SetActive(false); } catch { } });
        }

        static void Box(RectTransform rt, float ax0, float ax1, float top, float bottom, float l, float r)
        {
            rt.anchorMin = new Vector2(ax0, 1f); rt.anchorMax = new Vector2(ax1, 1f); rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(l, -bottom); rt.offsetMax = new Vector2(-r, -top);
        }

        static TextMeshProUGUI Txt(string name, Transform parent, float size, TextAlignmentOptions align, Color colour)
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false); go.AddComponent<RectTransform>();
            var t = go.AddComponent<TextMeshProUGUI>(); t.alignment = align; t.fontSize = size; t.richText = true; t.color = colour; t.enableWordWrapping = false; t.overflowMode = TextOverflowModes.Ellipsis;
            var font = UiFonts.Default; if (font != null) t.font = font;
            TmpOutline.Apply(t, 0.16f, new Color32(30, 20, 40, 220)); t.raycastTarget = false;
            return t;
        }

        static UnityEngine.UI.Image Rect(string name, Transform parent, Color c)
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false); go.AddComponent<RectTransform>();
            var img = go.AddComponent<UnityEngine.UI.Image>(); img.color = c; img.raycastTarget = false; return img;
        }

        static void EnsurePanel()
        {
            if (_hudRoot != null && _panel != null) return;
            try
            {
                _hudRoot = new GameObject("Lycoreia.ExpeditionHud"); UnityEngine.Object.DontDestroyOnLoad(_hudRoot);
                var canvas = _hudRoot.AddComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 50;
                // smaller and in the upper-left corner (was 560x100 at the top centre, scaled by screen width). Every text box is at least
                // 1.6x its font size tall: TMP's Ellipsis mode hides a line that does not fit vertically, and the Cyrillic fallback font has taller
                // lines than Lexend, so the Russian goal and clock lines used to vanish.
                var scaler = _hudRoot.AddComponent<UnityEngine.UI.CanvasScaler>(); scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = 1f;
                var bg = Rect("Panel", _hudRoot.transform, Color.white); _panel = bg.rectTransform;
                _panel.anchorMin = _panel.anchorMax = _panel.pivot = new Vector2(0f, 1f); _panel.anchoredPosition = new Vector2(24, -24); _panel.sizeDelta = new Vector2(380, 112); UiPlates.Apply(bg, PlateKind.Card, height: 112f);
                _fade = UiFade.Ensure(_panel.gameObject);
                var edge = Rect("Edge", _panel, Accent); var er = edge.rectTransform; er.anchorMin = new Vector2(0, 0); er.anchorMax = new Vector2(0, 1); er.pivot = new Vector2(0, 0.5f); er.sizeDelta = new Vector2(4, -24); er.anchoredPosition = new Vector2(8, 0); edge.sprite = UiSprites.Rounded(2); edge.type = UnityEngine.UI.Image.Type.Sliced;
                var cream = new Color(1f, 0.97f, 0.88f);
                _title = Txt("Title", _panel, 19, TextAlignmentOptions.Left, cream); Box(_title.rectTransform, 0f, 0.72f, 7, 37, 20, 0);
                _count = Txt("Count", _panel, 19, TextAlignmentOptions.Right, cream); Box(_count.rectTransform, 0.72f, 1f, 7, 37, 0, 14);
                var track = Rect("Track", _panel, new Color(1f, 1f, 1f, 0.14f)); Box(track.rectTransform, 0f, 1f, 40, 48, 20, 14); track.sprite = UiSprites.Rounded(4); track.type = UnityEngine.UI.Image.Type.Sliced;
                _fillImg = Rect("Fill", track.transform, Accent); _fillImg.sprite = UiSprites.Rounded(4); _fillImg.type = UnityEngine.UI.Image.Type.Sliced; _fill = _fillImg.rectTransform; _fill.anchorMin = Vector2.zero; _fill.anchorMax = new Vector2(0, 1); _fill.offsetMin = _fill.offsetMax = Vector2.zero;
                _goal = Txt("Goal", _panel, 15, TextAlignmentOptions.Left, new Color(1f, 0.97f, 0.88f, 0.78f)); Box(_goal.rectTransform, 0f, 1f, 52, 80, 20, 14);
                _clock = Txt("Clock", _panel, 15, TextAlignmentOptions.Left, cream); Box(_clock.rectTransform, 0f, 1f, 80, 106, 20, 14);
                _fade.Play(_panel, 0f, 1f, 0.01f, false);   // start transparent until SetShown fades it in
                Log.Info("expedition HUD panel built (native HUD gate: HudUI active + hide-HUD state tag)");
            }
            catch (Exception e) { Rl.Warn("hud", "expedition HUD: " + e.Message); _panel = null; if (_hudRoot != null) { Destroy(_hudRoot); _hudRoot = null; } }
        }

        static void HideTracker()
        {
            _shown = false; if (_hudRoot != null) _hudRoot.SetActive(false);
        }
    }
}

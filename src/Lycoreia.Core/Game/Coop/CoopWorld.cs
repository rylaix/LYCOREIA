// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppTMPro;
using Il2CppMonomiPark.SlimeRancher.UI;
using Il2CppMonomiPark.SlimeRancher.UI.HUD;
using Il2CppMonomiPark.SlimeRancher.UI.Map;
using Lycoreia.Coop;
using UnityEngine;

namespace Lycoreia.Game.Coop
{
    static class CoopWorld
    {
        const float NoteRange = 30f, JobLabelRange = 60f;
        static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);
        sealed class Label { public GameObject Go; public TextMesh Text; public string Shown; }
        static readonly Dictionary<string, Label> _labels = new Dictionary<string, Label>();
        static readonly Dictionary<int, (GameObject marker, int scene)> _compass = new Dictionary<int, (GameObject, int)>();
        static int _seenRevision = -1; static double _next;
        static GameObject _hudRoot; static TextMeshProUGUI _hud;

        public static void Pump()
        {
            double now = Time.unscaledTimeAsDouble; if (now < _next) return; _next = now + 0.1;
            try
            {
                var cam = Camera.main; var pl = SceneContext.Instance?.Player;
                if (cam == null || pl == null || !GameState.Ready) { Hide(); return; }
                int scene = Avatars.RemoteAvatars.LocalSceneGroup(); var me = pl.transform.position;
                var wanted = new HashSet<string>();
                foreach (var n in CoopRuntime.Board.Notes)
                {
                    if (n.Scene != scene) continue; var at = new Vector3(n.Position.X, n.Position.Y, n.Position.Z); if (Vector3.Distance(me, at) > NoteRange) continue;
                    var key = "n" + n.Id; wanted.Add(key); Show(key, at, $"Note: {n.Text}\n<size=70%>{CoopRuntime.NameOf(n.Author)}</size>", new Color(1f, 0.92f, 0.45f), cam);
                }
                foreach (var j in CoopRuntime.Board.Jobs)
                {
                    if (j.Done || !j.HasPosition || j.Scene != scene) continue; var at = new Vector3(j.Position.X, j.Position.Y, j.Position.Z); if (Vector3.Distance(me, at) > JobLabelRange) continue;
                    var key = "j" + j.Id; wanted.Add(key);
                    string who = j.ClaimedBy.Length > 0 ? CoopRuntime.NameOf(j.ClaimedBy) + " is on it" : "open job";
                    Show(key, at + Vector3.up * 0.8f, $"Job: {j.Text}\n<size=70%>{who}</size>", new Color(1f, 0.62f, 0.25f), cam);
                }
                foreach (var key in _labels.Keys.Where(k => !wanted.Contains(k)).ToList()) { Destroy(_labels[key].Go); _labels.Remove(key); }
                if (_seenRevision != CoopRuntime.Revision) { _seenRevision = CoopRuntime.Revision; SyncCompass(); }
                Hud(now);
            }
            catch (Exception e) { Rl.Warn("world", "co-op world: " + e.Message); }
        }

        static void Show(string key, Vector3 at, string text, Color colour, Camera cam)
        {
            if (!_labels.TryGetValue(key, out var l) || l.Go == null)
            {
                var go = new GameObject("Lycoreia.CoopLabel." + key); UnityEngine.Object.DontDestroyOnLoad(go);
                var tm = go.AddComponent<TextMesh>(); tm.anchor = TextAnchor.LowerCenter; tm.alignment = TextAlignment.Center; tm.characterSize = 0.05f; tm.fontSize = 48; tm.richText = true;
                var font = Avatars.AvatarTemplate.LabelFont(); if (font != null) { tm.font = font; var mr = go.GetComponent<MeshRenderer>(); if (mr != null) mr.sharedMaterial = font.material; }   // as the nametags: a bare TextMesh draws nothing
                _labels[key] = l = new Label { Go = go, Text = tm };
            }
            if (l.Shown != text) { l.Text.text = text; l.Shown = text; }
            l.Text.color = colour; l.Go.transform.position = at;
            l.Go.transform.rotation = Quaternion.LookRotation(at - cam.transform.position);
            float d = Vector3.Distance(cam.transform.position, at); l.Go.transform.localScale = Vector3.one * Mathf.Clamp(d / 12f, 0.6f, 2.2f);
        }

        /// <summary>Open jobs with a place get a native compass marker (Unity objects at the job's position, registered like the game's own).</summary>
        static void SyncCompass()
        {
            var open = CoopRuntime.Board.Jobs.Where(j => !j.Done && j.HasPosition && j.Scene >= 0).ToDictionary(j => j.Id);
            foreach (var id in _compass.Keys.ToList())
                if (!open.TryGetValue(id, out var j) || j.Scene != _compass[id].scene)
                {
                    try { RadarRegistry.UnregisterTrackedGameObjectAdvanced(_compass[id].marker); } catch { }
                    Destroy(_compass[id].marker); _compass.Remove(id);
                }
            var icon = JobIcon.Get(); if (icon == null) return;
            foreach (var j in open.Values)
            {
                if (_compass.ContainsKey(j.Id)) continue; var group = SceneGroups.Get(j.Scene); if (group == null) continue;
                try
                {
                    var marker = new GameObject("Lycoreia.JobMarker." + j.Id); UnityEngine.Object.DontDestroyOnLoad(marker);
                    marker.transform.position = new Vector3(j.Position.X, j.Position.Y, j.Position.Z);
                    RadarRegistry.RegisterTrackedGameObjectAdvanced(marker, group, icon, false);
                    _compass[j.Id] = (marker, j.Scene);
                }
                catch (Exception e) { Rl.Warn("compass", "job compass marker: " + e.Message); }
            }
        }

        /// <summary>Expedition tracker under the top of the screen while one runs (and for a few seconds after it ends).</summary>
        static void Hud(double now)
        {
            var run = CoopRuntime.Board.Expedition; var def = run != null ? Expeditions.Find(run.DefId) : null;
            long utc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool show = def != null && (run.Outcome == ExpeditionOutcome.Running || utc - run.EndsUtc < 8 && run.Outcome != ExpeditionOutcome.Abandoned);
            if (!show) { if (_hudRoot != null) _hudRoot.SetActive(false); return; }
            EnsureHud(); if (_hud == null) return;
            _hudRoot.SetActive(true);
            long left = Math.Max(0, run.EndsUtc - utc);
            string state = run.Outcome switch { ExpeditionOutcome.Succeeded => "<color=#8CFF8C>complete!</color>", ExpeditionOutcome.Failed => "<color=#FF9C8C>time is up</color>", _ => $"{left / 60}:{left % 60:00} left" };
            _hud.text = $"<b>{def.Name}</b>  {run.Progress}/{run.Target}  ·  {state}\n<size=75%>{def.Goal}</size>";
        }

        static void EnsureHud()
        {
            if (_hudRoot != null) return;
            try
            {
                _hudRoot = new GameObject("Lycoreia.ExpeditionHud"); UnityEngine.Object.DontDestroyOnLoad(_hudRoot);
                var canvas = _hudRoot.AddComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 50;
                var scaler = _hudRoot.AddComponent<UnityEngine.UI.CanvasScaler>(); scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080);
                var go = new GameObject("Text"); go.transform.SetParent(_hudRoot.transform, false);
                var rt = go.AddComponent<RectTransform>(); rt.anchorMin = new Vector2(0.5f, 1f); rt.anchorMax = new Vector2(0.5f, 1f); rt.pivot = new Vector2(0.5f, 1f); rt.anchoredPosition = new Vector2(0, -96); rt.sizeDelta = new Vector2(900, 90);
                _hud = go.AddComponent<TextMeshProUGUI>(); _hud.alignment = TextAlignmentOptions.Top; _hud.fontSize = 30; _hud.richText = true; _hud.color = new Color(1f, 0.97f, 0.88f);
                var font = Lycoreia.Owned.Components.UI.UiFonts.Default; if (font != null) _hud.font = font;
                _hud.outlineWidth = 0.18f; _hud.outlineColor = new Color32(30, 20, 40, 220);
            }
            catch (Exception e) { Rl.Warn("hud", "expedition HUD: " + e.Message); _hud = null; }
        }

        static void Hide()
        {
            foreach (var l in _labels.Values) Destroy(l.Go); _labels.Clear();
            if (_hudRoot != null) _hudRoot.SetActive(false);
        }

        public static void Reset()
        {
            Hide(); foreach (var c in _compass.Values) { try { RadarRegistry.UnregisterTrackedGameObjectAdvanced(c.marker); } catch { } Destroy(c.marker); } _compass.Clear(); _seenRevision = -1;
        }

        static void Destroy(GameObject go) { try { if (go != null) UnityEngine.Object.Destroy(go); } catch { } }
    }

    /// <summary>The compass icon for jobs: a native flag/marker sprite when the game has one, else a generated flag.</summary>
    static class JobIcon
    {
        static readonly string[] NativeNames = { "iconMapMarker", "icon_map_marker", "iconWaypoint", "icon_waypoint", "iconFlag", "icon_flag", "MapMarker" };
        static Sprite _sprite;
        public static Sprite Get()
        {
            if (_sprite != null) return _sprite; string source = "generated flag";
            try { foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>()) if (s != null && Array.IndexOf(NativeNames, s.name) >= 0) { _sprite = s; source = "native sprite '" + s.name + "'"; break; } } catch { }
            if (_sprite == null) _sprite = Flag(64);
            Log.Info("job compass icon: " + source);
            return _sprite;
        }
        static Sprite Flag(int n)
        {
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "Lycoreia.JobIcon", wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
                {
                    float u = x / (float)n, v = y / (float)n;
                    bool pole = u > 0.22f && u < 0.30f && v > 0.08f && v < 0.92f;
                    bool cloth = u >= 0.30f && v > 0.50f && v < 0.90f && u < 0.30f + 0.55f * (1f - Math.Abs(v - 0.70f) / 0.20f * 0.35f);
                    tex.SetPixel(x, y, pole || cloth ? Color.white : new Color(1, 1, 1, 0));
                }
            tex.Apply(false, false); UnityEngine.Object.DontDestroyOnLoad(tex); tex.hideFlags = HideFlags.HideAndDontSave;
            var s = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f); s.name = "Lycoreia.JobIcon"; s.hideFlags = HideFlags.HideAndDontSave; return s;
        }
    }
}

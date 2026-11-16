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
using Lycoreia.Text;
using UnityEngine;

namespace Lycoreia.Game.Coop
{
    static partial class CoopWorld
    {
        const float NoteRange = 30f, JobLabelRange = 60f, JobFadeStart = 40f, JobShortFrom = 25f;
        static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);
        static readonly HashSet<string> _labelKeys = new HashSet<string>();   // world labels we own (drawn by Owned/Ui/WorldLabels on an overlay canvas)
        static bool _gateHooked;
        static readonly Dictionary<int, (GameObject marker, int scene)> _compass = new Dictionary<int, (GameObject, int)>();
        static int _seenRevision = -1; static double _next;

        public static void Pump()
        {
            double now = Time.unscaledTimeAsDouble; if (now < _next) return; _next = now + 0.1;
            try
            {
                var cam = Camera.main; var pl = SceneContext.Instance?.Player;
                if (cam == null || pl == null || !GameState.Ready) { Hide(); return; }
                int scene = Avatars.RemoteAvatars.LocalSceneGroup(); var me = pl.transform.position;
                var wanted = new HashSet<string>(); if (!_gateHooked) { _gateHooked = true; Lycoreia.Owned.Components.UI.WorldLabels.HudVisible = HudVisibleNow; }
                foreach (var n in CoopRuntime.Board.Notes)
                {
                    if (n.Scene != scene) continue; var at = new Vector3(n.Position.X, n.Position.Y, n.Position.Z); if (Vector3.Distance(me, at) > NoteRange) continue;
                    var key = "n" + n.Id; wanted.Add(key); Show(key, at, CoopWorldText.Note(n.Text, CoopRuntime.NameOf(n.Author)), null, new Color(1f, 0.92f, 0.45f), 18f, NoteRange, 0f);
                }
                foreach (var j in CoopRuntime.Board.Jobs)
                {
                    if (j.Done || !j.HasPosition || j.Scene != scene) continue; var at = new Vector3(j.Position.X, j.Position.Y, j.Position.Z); if (Vector3.Distance(me, at) > JobLabelRange) continue;
                    var key = "j" + j.Id; wanted.Add(key);
                    string who = j.ClaimedBy.Length > 0 ? CoopWorldText.OnIt(CoopRuntime.NameOf(j.ClaimedBy)) : CoopWorldText.OpenJob;
                    Show(key, at + Vector3.up * 0.8f, CoopWorldText.Job(j.Text, who), CoopWorldText.JobShort(j.Text), new Color(1f, 0.62f, 0.25f), JobFadeStart, JobLabelRange, JobShortFrom);
                }
                foreach (var key in _labelKeys.Where(k => !wanted.Contains(k)).ToList()) { Lycoreia.Owned.Components.UI.WorldLabels.Remove(key); _labelKeys.Remove(key); }
                if (_seenRevision != CoopRuntime.Revision || MapDirectorChanged()) { _seenRevision = CoopRuntime.Revision; SyncCompass(); SyncMapMarkers(); }
                Tracker(now);
            }
            catch (Exception e) { Rl.Warn("world", "co-op world: " + e.Message); }
        }

        /// <summary>Overlay label (constant pixel size, distance fade, plate + outline); refreshed every pump so it expires on its own if we stop.</summary>
        static void Show(string key, Vector3 at, string text, string shortText, Color colour, float fadeStart, float fadeEnd, float shortFrom)
        {
            _labelKeys.Add(key);
            Lycoreia.Owned.Components.UI.WorldLabels.Set(key, at, text, shortText, colour, fadeStart, fadeEnd, shortFrom, 0.7f, 1f, 1f, true);
        }

        /// <summary>Native HUD gate for the overlay labels (same read as the expedition tracker).</summary>
        static bool HudVisibleNow() { HudGate(out bool hudActive, out bool tagSet); return hudActive && !tagSet; }

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

        static void Hide()
        {
            _labelKeys.Clear(); Lycoreia.Owned.Components.UI.WorldLabels.Clear();
            HideTracker();
        }

        public static void Reset()
        {
            Hide(); ResetMapMarkers(); foreach (var c in _compass.Values) { try { RadarRegistry.UnregisterTrackedGameObjectAdvanced(c.marker); } catch { } Destroy(c.marker); } _compass.Clear(); _seenRevision = -1;
        }

        static void Destroy(GameObject go) { try { if (go != null) UnityEngine.Object.Destroy(go); } catch { } }
    }
}

// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Il2CppMonomiPark.SlimeRancher.Map;
using Lycoreia.Coop;
using UnityEngine;

namespace Lycoreia.Game.Coop
{
    static partial class CoopWorld
    {
        sealed class MapEntry { public string Id; public MapNavigationMarkerData Data; public int Scene; }
        static readonly Dictionary<int, MapEntry> _mapMarkers = new Dictionary<int, MapEntry>();
        static MapDirector _mapDir; static bool _mapUnavailable;

        static MapDirector CurrentDirector()
        {
            try { var d = SceneContext.Instance?.MapDirector; return d != null ? d : null; } catch { return null; }
        }

        /// <summary>True when the scene's map director is not the one our markers were registered on (new world load).</summary>
        static bool MapDirectorChanged()
        {
            if (_mapUnavailable) return false;
            var d = CurrentDirector(); return d != null && !ReferenceEquals(d, _mapDir) && (_mapDir == null ? _mapMarkers.Count > 0 || CoopRuntime.Board.Jobs.Count > 0 : true);
        }

        /// <summary>The map whose related scenes contain the group, else the director's default map.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static MapDefinition MapFor(MapDirector dir, Il2CppMonomiPark.SlimeRancher.SceneManagement.SceneGroup group)
        {
            try
            {
                var maps = dir._mapList?._maps;
                if (maps != null && group != null)
                    for (int i = 0; i < maps.Length; i++)
                    {
                        var m = maps[i]; var scenes = m?.RelatedScenes?._gameplaySceneGroups; if (scenes == null) continue;
                        for (int k = 0; k < scenes.Length; k++) { var g = scenes[k]; if (g != null && g.ReferenceId == group.ReferenceId) return m; }
                    }
            }
            catch { }
            return dir.DefaultMap;
        }

        /// <summary>Open jobs with a place get a native map marker; finished/removed jobs lose it. Isolated: a failure disables map markers, not the game.</summary>
        static void SyncMapMarkers()
        {
            if (_mapUnavailable) return;
            try
            {
                var dir = CurrentDirector();
                if (dir == null) return;
                if (!ReferenceEquals(dir, _mapDir))
                {   // a new director: markers of the old one died with it
                    _mapMarkers.Clear(); _mapDir = dir; Log.Info("job map markers: using MapDirector '" + dir.name + "' (native MapDirector.RegisterMarker)");
                }
                var open = CoopRuntime.Board.Jobs.Where(j => !j.Done && j.HasPosition && j.Scene >= 0).ToDictionary(j => j.Id);
                foreach (var id in _mapMarkers.Keys.ToList())
                    if (!open.TryGetValue(id, out var j) || j.Scene != _mapMarkers[id].Scene)
                    {
                        try { dir.DeregisterMarker(_mapMarkers[id].Id); } catch (Exception e) { Rl.Warn("mapdereg", "job map marker remove: " + e.Message); }
                        _mapMarkers.Remove(id);
                    }
                foreach (var j in open.Values)
                {
                    if (_mapMarkers.ContainsKey(j.Id)) continue;
                    var group = SceneGroups.Get(j.Scene); if (group == null) continue;
                    var map = MapFor(dir, group); if (map == null) { Rl.Warn("mapdef", "job map marker: no map definition for scene " + j.Scene); continue; }
                    string id = "Lycoreia.Job." + j.Id;
                    try
                    {
                        try { dir.DeregisterMarker(id); } catch { }   // a stale entry of an earlier session (Dictionary.Add would throw)
                        var data = new MapNavigationMarkerData();
                        if (!data.SetPosition(new Vector3(j.Position.X, j.Position.Y, j.Position.Z), map)) { Rl.Warn("mappos", "job map marker: SetPosition refused for job " + j.Id); continue; }
                        dir.RegisterMarker(id, data.Cast<IMapMarkerSource>());
                        _mapMarkers[j.Id] = new MapEntry { Id = id, Data = data, Scene = j.Scene };
                        Log.Info($"job map marker: job {j.Id} registered on map '{map.name}' (native descriptor icon {(data.GetMapMarkerDescriptor()?.MapIcon?.name ?? "none")}; the map UI draws the Lycoreia job flag instead)");
                        Lycoreia.Owned.Components.UI.MapLabels.Ensure();
                    }
                    catch (Exception e) { Rl.Warn("mapreg", "job map marker: " + e.Message); }
                }
            }
            catch (Exception e)
            {
                _mapUnavailable = true;
                Log.Warn("job map markers disabled: " + e.Message);
            }
        }

        /// <summary>The registered job markers as (source object pointer, job id), for the map hover label to match the map UI's MapMarker components.</summary>
        internal static List<(IntPtr ptr, int job)> MapMarkerSnapshot()
        {
            var l = new List<(IntPtr, int)>(_mapMarkers.Count);
            foreach (var kv in _mapMarkers) { try { if (kv.Value.Data != null) l.Add((kv.Value.Data.Pointer, kv.Key)); } catch { } }
            return l;
        }

        static void ResetMapMarkers()
        {
            try
            {
                var dir = _mapDir;
                if (dir != null) foreach (var m in _mapMarkers.Values) { try { dir.DeregisterMarker(m.Id); } catch { } }
            }
            catch { }
            _mapMarkers.Clear(); _mapDir = null;
        }
    }

    /// <summary>The compass icon for jobs: the game's own navigation-waypoint sprite (taken from the native map marker descriptor), else a named native sprite, else a generated flag.</summary>
    static class JobIcon
    {
        static readonly string[] NativeNames = { "iconMapMarker", "icon_map_marker", "iconWaypoint", "icon_waypoint", "iconFlag", "icon_flag", "MapMarker" };
        static Sprite _sprite, _flag;
        public static Sprite Get()
        {
            if (_sprite != null) return _sprite;
            string source = null;
            try { _sprite = FromMapDescriptor(out bool directorReady, out source); if (_sprite == null && !directorReady) return _flag != null ? _flag : (_flag = Flag(64)); }
            catch (Exception e) { Log.Warn("job compass icon: native waypoint lookup failed: " + e.Message); }
            if (_sprite == null)
            {
                try { foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>()) if (s != null && Array.IndexOf(NativeNames, s.name) >= 0) { _sprite = s; source = "native sprite '" + s.name + "' (by name)"; break; } } catch { }
            }
            if (_sprite == null) { _sprite = _flag != null ? _flag : (_flag = Flag(64)); source = "generated flag (no native waypoint sprite found)"; }
            Log.Info("job compass icon: " + source);
            return _sprite;
        }

        /// <summary>MapNavigationMarkerData.SetPosition copies the map's navigation-marker descriptor; its MapIcon is the game's waypoint sprite.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static Sprite FromMapDescriptor(out bool directorReady, out string source)
        {
            source = null; var dir = SceneContext.Instance?.MapDirector; directorReady = dir != null; if (dir == null) return null;
            var map = dir.DefaultMap; if (map == null) return null;
            var probe = new MapNavigationMarkerData();
            if (!probe.SetPosition(Vector3.zero, map)) return null;
            var sprite = probe.GetMapMarkerDescriptor()?.MapIcon; if (sprite == null) return null;
            source = "native waypoint sprite '" + sprite.name + "' (map navigation marker descriptor of '" + map.name + "')";
            return sprite;
        }

        /// <summary>White flag sprite for the map UI (tinted gold there); distinct from the native waypoint.</summary>
        public static Sprite MapFlag() => _flag != null ? _flag : (_flag = Flag(64));

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

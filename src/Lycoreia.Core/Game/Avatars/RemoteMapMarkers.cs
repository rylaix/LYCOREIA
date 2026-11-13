// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppMonomiPark.SlimeRancher.UI.Map;
using Lycoreia.Game.Hooks;
using UnityEngine;
using UnityEngine.UI;
using Il2CppTMPro;
using Il2CppMonomiPark.SlimeRancher.UI;
using Il2CppMonomiPark.SlimeRancher.UI.HUD;

namespace Lycoreia.Game.Avatars
{
    static class RemoteMapMarkers
    {
        sealed class Entry { public GameObject Root; public RectTransform Rect; public Image Image; public TextMeshProUGUI Label; }
        static readonly Dictionary<int, Dictionary<string, Entry>> Views = new Dictionary<int, Dictionary<string, Entry>>();
        static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);

        static readonly Dictionary<string, (GameObject root, int scene)> Radar = new Dictionary<string, (GameObject, int)>();
        static double _nextRadar;
        public static void UpdateRadar()
        {
            double now = Time.unscaledTimeAsDouble; if (now < _nextRadar) return; _nextRadar = now + 0.5;
            try
            {
                foreach (var id in new List<string>(Radar.Keys))
                    if (!Mod.IsNetworkActive || !RemoteAvatars.All.TryGetValue(id, out var current) ||
                        current.Root != Radar[id].root || current.SceneGroup != Radar[id].scene)
                    {
                        if (Radar[id].root != null) RadarRegistry.UnregisterTrackedGameObjectAdvanced(Radar[id].root);
                        Radar.Remove(id);
                    }
                if (!Mod.IsNetworkActive) return;
                Sprite icon = PlayerIcon.Get();
                if (icon == null) return;
                foreach (var pair in RemoteAvatars.All)
                {
                    var avatar = pair.Value;
                    if (Radar.ContainsKey(pair.Key) || !avatar.HasState || avatar.Root == null) continue;
                    var scene = SceneGroups.Get(avatar.SceneGroup); if (scene == null) continue;
                    RadarRegistry.RegisterTrackedGameObjectAdvanced(avatar.Root, scene, icon, false);
                    Radar[pair.Key] = (avatar.Root, avatar.SceneGroup);
                    Mod.Diagnostics?.Record("players", "gps-player-registered", $"player={pair.Key}; scene={avatar.SceneGroup}");
                }
                TintCompass();
            }
            catch (Exception e) { Rl.Warn("gps-players", "remote GPS entries: " + e.Message); }
        }

        /// <summary>Colours each remote player's compass marker with their selected colour (markers are matched to avatars by world position).</summary>
        static void TintCompass()
        {
            foreach (var compass in SceneObjectCache.All<CompassBarUI>(true))
            {
                if (compass == null || compass._radarInstances == null) continue;
                foreach (var pair in compass._radarInstances)
                {
                    try
                    {
                        if (pair.Key == null || pair.Value == null) continue;
                        var at = pair.Key.WorldPosition; RemoteAvatar best = null; float bestSq = 4f;     // within 2 m
                        foreach (var a in RemoteAvatars.All.Values)
                        {
                            if (a.Root == null) continue; float d = (a.Root.transform.position - at).sqrMagnitude;
                            if (d < bestSq) { bestSq = d; best = a; }
                        }
                        if (best == null) continue;
                        foreach (var image in pair.Value.GetComponentsInChildren<Image>(true)) if (image != null) image.color = best.Colour;
                    }
                    // a radar entry whose tracked object was destroyed throws inside its own WorldPosition (DOTween DOMoveZ): skip it quietly
                    catch (Exception e) { if ((e.Message ?? "").Contains("NullReference")) continue; Rl.Warn("gps-tint", "compass tint: " + e.Message); }
                }
            }
        }

        public static void Clear(MapUI ui)
        {
            if (ui == null || !Views.Remove(ui.GetInstanceID(), out var entries)) return;
            foreach (var item in entries.Values) if (item.Root != null) UnityEngine.Object.Destroy(item.Root);
        }

        public static void Draw(MapUI ui)
        {
            if (ui == null) return;
            if (!Mod.IsNetworkActive) { Clear(ui); return; }
            if (ui._map == null || ui._playerMarker == null || ui.CurrentMapDefinition == null) return;
            try
            {
                int id = ui.GetInstanceID();
                if (!Views.TryGetValue(id, out var entries)) Views[id] = entries = new Dictionary<string, Entry>();
                foreach (var key in new List<string>(entries.Keys))
                    if (!RemoteAvatars.All.ContainsKey(key))
                    { if (entries[key].Root != null) UnityEngine.Object.Destroy(entries[key].Root); entries.Remove(key); }
                foreach (var pair in RemoteAvatars.All)
                {
                    var avatar = pair.Value; bool visible = false;
                    var scene = SceneGroups.Get(avatar.SceneGroup);
                    var related = ui.CurrentMapDefinition.RelatedScenes;
                    if (avatar.HasState && scene != null && related?._gameplaySceneGroups != null)
                        foreach (var member in related._gameplaySceneGroups) if (member != null && member.Pointer == scene.Pointer) { visible = true; break; }
                    if (!visible) { if (entries.TryGetValue(pair.Key, out var hidden) && hidden.Root != null) hidden.Root.SetActive(false); continue; }
                    if (!entries.TryGetValue(pair.Key, out var entry) || entry.Root == null)
                    {
                        var source = ui._playerMarker._arrowRect != null ? ui._playerMarker._arrowRect.GetComponentInChildren<Image>() : null;
                        if (source == null) source = ui._playerMarker.GetComponentInChildren<Image>();
                        if (source == null) continue;
                        var go = new GameObject("Lycoreia.MapPlayer." + pair.Key);
                        go.AddComponent<RectTransform>(); go.transform.SetParent(ui._playerMarker.transform.parent, false);
                        entry = new Entry { Root = go, Rect = go.GetComponent<RectTransform>(), Image = go.AddComponent<Image>() };
                        entry.Image.sprite = source.sprite; entry.Image.raycastTarget = false;
                        entry.Rect.sizeDelta = source.rectTransform.sizeDelta;
                        var label = new GameObject("Name"); label.transform.SetParent(go.transform, false);
                        entry.Label = label.AddComponent<TextMeshProUGUI>(); entry.Label.fontSize = 15; entry.Label.alignment = TextAlignmentOptions.Center;
                        entry.Label.raycastTarget = false;
                        var rect = entry.Label.rectTransform; rect.sizeDelta = new Vector2(180, 28); rect.anchoredPosition = new Vector2(0, 24);
                        entries[pair.Key] = entry;
                        Mod.Diagnostics?.Record("players", "map-player-created", $"player={pair.Key}; scene={avatar.SceneGroup}");
                    }
                    entry.Root.SetActive(true);
                    entry.Rect.anchoredPosition = ui._map.GetMapPosition(avatar.MapPosition);
                    entry.Rect.localRotation = Quaternion.Euler(0, 0, -avatar.transform.eulerAngles.y + ui._playerMarker._rotationOffset);
                    entry.Image.color = avatar.Colour;
                    var upright = Quaternion.Inverse(entry.Rect.localRotation);
                    entry.Label.rectTransform.localRotation = upright;
                    entry.Label.rectTransform.anchoredPosition = upright * new Vector3(0, 24, 0);
                    entry.Label.text = Prefs.StreamerMode ? "Player" : avatar.Name; entry.Label.color = avatar.Colour;
                }
            }
            catch (Exception e) { Rl.Warn("map-players", "remote map markers: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(MapUI), nameof(MapUI.LateUpdate))]
    static class RemoteMapMarkersHook { static void Postfix(MapUI __instance) => RemoteMapMarkers.Draw(__instance); }
    [HarmonyPatch(typeof(MapUI), nameof(MapUI.OnDestroy))]
    static class RemoteMapMarkersDestroyHook { static void Prefix(MapUI __instance) => RemoteMapMarkers.Clear(__instance); }
}

namespace Lycoreia.Game.Avatars
{
    /// <summary>The player marker icon: a native game sprite when one with an exact known name is loaded, otherwise a generated white head-and-shoulders silhouette (tinted per player).</summary>
    static class PlayerIcon
    {
        // Exact sprite names tried against the loaded sprites (first match wins); none is assumed to exist.
        static readonly string[] NativeNames = { "icon_player", "Icon_Player", "player_marker", "PlayerMarker", "icon_rancher", "Icon_Rancher" };
        static Sprite _sprite; static bool _logged;

        public static Sprite Get()
        {
            if (_sprite != null) return _sprite;
            string source = "generated head-and-shoulders silhouette";
            try
            {
                foreach (var candidate in Resources.FindObjectsOfTypeAll<Sprite>())
                {
                    if (candidate == null) continue;
                    if (Array.IndexOf(NativeNames, candidate.name) >= 0) { _sprite = candidate; source = "native sprite '" + candidate.name + "'"; break; }
                }
            }
            catch (Exception e) { Mod.Diagnostics?.Record("players", "gps-icon-scan-failed", e.Message); }
            if (_sprite == null) _sprite = Generate(64);
            if (!_logged) { _logged = true; Log.Info("player compass icon: " + source); Mod.Diagnostics?.Record("players", "gps-icon", source); }
            return _sprite;
        }

        /// <summary>Anti-aliased (4x4 supersampled) white silhouette; alpha carries the shape so Image.color tints it.</summary>
        static Sprite Generate(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Lycoreia.PlayerIcon", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            const int ss = 4;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int hits = 0;
                    for (int sy = 0; sy < ss; sy++) for (int sx = 0; sx < ss; sx++)
                        if (Inside((x + (sx + 0.5f) / ss) / size, (y + (sy + 0.5f) / ss) / size)) hits++;
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, hits / (float)(ss * ss)));
                }
            tex.Apply(false, false);
            UnityEngine.Object.DontDestroyOnLoad(tex); tex.hideFlags = HideFlags.HideAndDontSave;
            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = "Lycoreia.PlayerIcon"; sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        /// <summary>Head: circle; shoulders: half-ellipse below it. Unit square, y up.</summary>
        internal static bool Inside(float u, float v)
        {
            float hx = u - 0.5f, hy = v - 0.70f; if (hx * hx + hy * hy <= 0.2f * 0.2f) return true;
            float sx = (u - 0.5f) / 0.40f, sy = (v - 0.04f) / 0.50f; return v >= 0.04f && sx * sx + sy * sy <= 1f;
        }
    }
}

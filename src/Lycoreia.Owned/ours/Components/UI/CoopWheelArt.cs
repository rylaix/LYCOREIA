// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Lycoreia.Coop;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UI;

namespace Lycoreia.Owned.Components.UI;

internal static class CoopWheelArt
{
    // "radial menu is a bit pixelated": the ring was a 160 px texture stretched to 500 reference px (650+ px at 1440p);
    // 512 px keeps its anti-aliased edge about one screen pixel wide up to 4K. Icons 64 -> 128 for the same reason.
    private const int RingSize = 512, IconSize = 128;
    internal const float RingInnerRatio = 0.58f;
    private static Sprite? ring;
    private static readonly System.Collections.Generic.Dictionary<string, (Sprite? sprite, bool native)> icons = new();
    private static bool scanned;
    private static readonly System.Collections.Generic.Dictionary<string, string[]> NativeNames = new()
    {
        ["done"] = new[] { "icon_check", "iconcheck", "checkmark", "icon_checkmark", "iconconfirm" },
        ["remove"] = new[] { "icon_close", "iconclose", "icon_cross", "iconcross", "icon_x", "iconx" },
        ["claim"] = new[] { "iconflag", "icon_flag", "iconwaypoint", "icon_waypoint" },
        ["job"] = new[] { "iconmapmarker", "icon_map_marker", "iconpin", "icon_pin", "mapmarker" },
        ["note"] = new[] { "iconnote", "icon_note", "iconnotes" },
        ["write"] = new[] { "iconedit", "icon_edit", "iconpencil", "icon_pencil", "iconpen" },
        ["ping"] = new[] { "iconping", "icon_ping" },
    };

    /// <summary>The curved wedge sprite (a ring of RingInnerRatio..1 of the half size). Never null unless texture creation failed.</summary>
    internal static Sprite? Ring()
    {
        if (ring) return ring;
        try
        {
            var n = RingSize; var half = n / 2f;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, true) { name = "Lycoreia.WheelRing", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            var px = new Color32[n * n];
            for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                {
                    var d = Mathf.Sqrt((x + 0.5f - half) * (x + 0.5f - half) + (y + 0.5f - half) * (y + 0.5f - half));
                    var a = Mathf.Clamp01((half - 1f - d) / 1.5f + 0.5f) * Mathf.Clamp01((d - half * RingInnerRatio) / 1.5f + 0.5f);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply(true, false); tex.hideFlags = HideFlags.HideAndDontSave;
            ring = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
            ring.name = "Lycoreia.WheelRing"; ring.hideFlags = HideFlags.HideAndDontSave;
            OwnedLog.LogMessage("wheel art: generated ring sprite (radial-filled curved wedges)");
        }
        catch (Exception ex) { OwnedLog.LogWarning("wheel art: ring failed: " + ex.Message); }
        return ring;
    }

    /// <summary>Icon for a CoopWheelLayout.IconKey. native = a game sprite (draw it untinted); otherwise a white glyph to tint.</summary>
    internal static (Sprite? sprite, bool native) Icon(string key)
    {
        if (icons.TryGetValue(key, out var hit)) return hit;
        (Sprite?, bool) res = (null, false); string source;
        try
        {
            ScanNative();
            Sprite? found = null;
            if (NativeNames.TryGetValue(key, out var names))
                foreach (var n in names) if (nativeFound.TryGetValue(n, out var s) && s) { found = s; break; }
            if (found) { res = (found, true); source = "native sprite '" + found!.name + "'"; }
            else { res = (Generated(key), false); source = "generated glyph (no native sprite named " + string.Join("/", NativeNames.TryGetValue(key, out var nn) ? nn : System.Array.Empty<string>()) + ")"; }
        }
        catch (Exception ex) { source = "failed: " + ex.Message; }
        icons[key] = res;
        OwnedLog.LogMessage("wheel icon " + key + ": " + source);
        return res;
    }

    private static readonly System.Collections.Generic.Dictionary<string, Sprite> nativeFound = new();

    private static void ScanNative()
    {
        if (scanned) return;
        scanned = true;
        var wanted = new System.Collections.Generic.HashSet<string>();
        foreach (var list in NativeNames.Values) foreach (var n in list) wanted.Add(n);
        var hints = new System.Collections.Generic.List<string>();
        try
        {
            foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>())
            {
                if (!s) continue;
                var name = (s.name ?? string.Empty).ToLowerInvariant();
                if (wanted.Contains(name) && !nativeFound.ContainsKey(name)) nativeFound[name] = s;
                if (hints.Count < 40 && (name.Contains("check") || name.Contains("flag") || name.Contains("waypoint") || name.Contains("marker") || name.Contains("pencil") || name.Contains("icon_close")))
                    hints.Add(s.name!);
            }
        }
        catch (Exception ex) { OwnedLog.LogWarning("wheel icons: sprite scan failed: " + ex.Message); }
        OwnedLog.LogMessage("wheel icons: exact names found " + nativeFound.Count + "/" + wanted.Count + "; similar sprite names for tuning: " + string.Join(", ", hints));
    }

    // ------------------------------------------------------------ generated glyphs (white, antialiased, unit square, y up)
    private static float Seg(float px, float py, float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay, t = Mathf.Clamp01(((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy));
        float cx = ax + t * dx - px, cy = ay + t * dy - py;
        return Mathf.Sqrt(cx * cx + cy * cy);
    }

    private static bool InTri(float px, float py, float ax, float ay, float bx, float by, float cx, float cy)
    {
        float d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by), d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy), d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
        var neg = d1 < 0 || d2 < 0 || d3 < 0; var pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    private static float Glyph(string key, float x, float y, float aa)
    {
        const float w = 0.055f;                                              // half stroke width
        float C(float d, float half) => Mathf.Clamp01((half - d) / aa + 0.5f);
        float RingAt(float cx, float cy, float r) => C(Mathf.Abs(Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r), w);
        float Line(float ax, float ay, float bx, float by) => C(Seg(x, y, ax, ay, bx, by), w);
        switch (key)
        {
            case "done": return Mathf.Max(Line(0.2f, 0.5f, 0.4f, 0.3f), Line(0.4f, 0.3f, 0.8f, 0.74f));
            case "remove": return Mathf.Max(Line(0.25f, 0.25f, 0.75f, 0.75f), Line(0.25f, 0.75f, 0.75f, 0.25f));
            case "claim":
                return Mathf.Max(Line(0.3f, 0.15f, 0.3f, 0.85f), InTri(x, y, 0.3f, 0.84f, 0.8f, 0.68f, 0.3f, 0.52f) ? 1f : 0f);
            case "job": return Mathf.Max(Mathf.Max(RingAt(0.5f, 0.62f, 0.2f), Line(0.5f, 0.42f, 0.5f, 0.12f)), C(Mathf.Sqrt((x - 0.5f) * (x - 0.5f) + (y - 0.62f) * (y - 0.62f)), 0.07f));
            case "note":
                return Mathf.Max(Mathf.Max(Line(0.22f, 0.2f, 0.78f, 0.2f), Line(0.22f, 0.8f, 0.78f, 0.8f)), Mathf.Max(Mathf.Max(Line(0.22f, 0.2f, 0.22f, 0.8f), Line(0.78f, 0.2f, 0.78f, 0.8f)),
                    Mathf.Max(Line(0.34f, 0.6f, 0.66f, 0.6f), Line(0.34f, 0.42f, 0.58f, 0.42f))));
            case "write": return Mathf.Max(C(Seg(x, y, 0.3f, 0.3f, 0.72f, 0.72f), 0.08f), Mathf.Max(Line(0.2f, 0.15f, 0.8f, 0.15f), InTri(x, y, 0.2f, 0.2f, 0.34f, 0.26f, 0.26f, 0.34f) ? 1f : 0f));
            default: return Mathf.Max(RingAt(0.5f, 0.5f, 0.28f), C(Mathf.Sqrt((x - 0.5f) * (x - 0.5f) + (y - 0.5f) * (y - 0.5f)), 0.09f));
        }
    }

    private static Sprite? Generated(string key)
    {
        const int n = IconSize;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, true) { name = "Lycoreia.WheelIcon." + key, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
        var px = new Color32[n * n];
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
                px[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Glyph(key, (x + 0.5f) / n, (y + 0.5f) / n, 2f / n) * 255f));
        tex.SetPixels32(px);
        tex.Apply(true, false); tex.hideFlags = HideFlags.HideAndDontSave;
        var s = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f); s.name = "Lycoreia.WheelIcon." + key; s.hideFlags = HideFlags.HideAndDontSave;
        return s;
    }
}

/// <summary>Which gamepad button holds the wheel. Resolved once from the game's own input asset.</summary>
internal static class CoopWheelPad
{
    private static bool resolved;
    private static string? name;

    /// <summary>Binding path of the hold button ("&lt;Gamepad&gt;/dpad/down"), null when every candidate is taken or the asset is not ready.</summary>
    internal static string? Path { get { Resolve(); return name == null ? null : "<Gamepad>/" + name; } }

    internal static ButtonControl? Control(Gamepad pad)
    {
        Resolve();
        switch (name)
        {
            case "dpad/down": return pad.dpad.down;
            case "dpad/up": return pad.dpad.up;
            case "dpad/left": return pad.dpad.left;
            case "dpad/right": return pad.dpad.right;
            case "select": return pad.selectButton;
            case "rightStickPress": return pad.rightStickButton;
            case "leftStickPress": return pad.leftStickButton;
            case "start": return pad.startButton;
            default: return null;
        }
    }

    private static void Resolve()
    {
        if (resolved) return;
        try
        {
            var asset = GameContext.Instance ? GameContext.Instance.InputDirector?._inputActions : null;
            if (!asset) return;                                              // not ready yet: try again later
            resolved = true;
            var bound = new System.Collections.Generic.List<string>(); var skipped = new System.Collections.Generic.List<string>();
            var maps = asset!.actionMaps;
            for (var m = 0; m < maps.Count; m++)
            {
                var map = maps[m]; var mn = (map.name ?? string.Empty).ToLowerInvariant();
                if (mn.Contains("ui") || mn.Contains("menu") || mn.Contains("pause") || mn.Contains("debug") || mn.Contains("cheat")) { skipped.Add(map.name!); continue; }
                var actions = map.actions;
                for (var a = 0; a < actions.Count; a++)
                {
                    var binds = actions[a].bindings;
                    for (var b = 0; b < binds.Count; b++)
                    {
                        var p = binds[b].effectivePath;
                        if (!string.IsNullOrEmpty(p) && p.StartsWith("<Gamepad>", System.StringComparison.OrdinalIgnoreCase)) bound.Add(p);
                    }
                }
            }
            name = CoopWheelLayout.PickPadButton(bound);
            OwnedLog.LogMessage("wheel gamepad: hold button = " + (name ?? "none (every candidate is used by gameplay)") + "; gameplay gamepad paths seen: " + bound.Count + "; maps ignored: " + string.Join(",", skipped));
        }
        catch (Exception ex) { resolved = true; name = null; OwnedLog.LogWarning("wheel gamepad: resolve failed, no gamepad hold button: " + ex.Message); }
    }
}

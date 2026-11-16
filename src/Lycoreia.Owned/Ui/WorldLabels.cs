// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System.Collections.Generic;
using Il2CppTMPro;
using UnityEngine.UI;

namespace Lycoreia.Owned.Components.UI;

/// <summary>Pure label maths (tested).</summary>
public static class WorldLabelMath
{
    public const float MinLuminance = 0.62f;
    /// <summary>1 up to fadeStart, 0 from fadeEnd, smooth in between.</summary>
    public static float Fade(float distance, float fadeStart, float fadeEnd)
    {
        if (!float.IsFinite(distance)) return 0f;
        if (distance <= fadeStart) return 1f; if (distance >= fadeEnd || fadeEnd <= fadeStart) return 0f;
        float t = (distance - fadeStart) / (fadeEnd - fadeStart); return 1f - t * t * (3f - 2f * t);
    }
    /// <summary>Gentle size: 1.1 up close shrinking to 0.75 at 100 m.</summary>
    public static float Scale(float distance)
    {
        if (!float.IsFinite(distance) || distance <= 10f) return 1.1f; if (distance >= 100f) return 0.75f;
        return 1.1f + (0.75f - 1.1f) * ((distance - 10f) / 90f);
    }
    /// <summary>Which text to show: the short one (name only) from shortFrom on, with a little hysteresis so it does not flicker at the border.</summary>
    public static bool UseShort(float distance, float shortFrom, bool wasShort, bool hasShort)
    {
        if (!hasShort || shortFrom <= 0f) return false;
        return wasShort ? distance > shortFrom * 0.92f : distance >= shortFrom;
    }
    /// <summary>How far to blend a colour toward white so its luminance reaches MinLuminance (0 when bright enough); dark player colours stay readable.</summary>
    public static float LightenToward(float r, float g, float b)
    {
        float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b; if (lum >= MinLuminance) return 0f;
        return (MinLuminance - lum) / (1f - lum);
    }
    /// <summary>On screen (with a margin in pixels) and in front of the camera.</summary>
    public static bool OnScreen(float x, float y, float depth, float width, float height, float margin = 80f)
        => depth > 0.1f && x >= -margin && y >= -margin && x <= width + margin && y <= height + margin;
}

/// <summary>Overlay world labels. Main thread only. Never throws.</summary>
internal static class WorldLabels
{
    private sealed class Entry
    {
        public string Key = "", Full = "", Short = "", Shown = "";
        public Vector3 World; public Color Colour = Color.white, ShownColour = new Color(-1, 0, 0, 0);
        public float FadeStart, FadeEnd, ShortFrom, Expires, Alpha = 1f, Scale = 1f; public bool Rich, ShownShort, Active, Dirty = true;
        public GameObject Go = null!; public RectTransform Rt = null!; public CanvasGroup Group = null!; public Image Plate = null!; public TextMeshProUGUI Text = null!;
    }

    private const float MaxWidth = 420f, PadX = 12f, PadY = 6f, GateEvery = 0.15f;
    private static readonly Dictionary<string, Entry> Live = new();
    private static readonly Stack<Entry> Pool = new();
    private static readonly List<Entry> Gone = new();
    private static GameObject? root; private static Canvas? canvas;
    private static bool installed, logged, rootShown; private static float nextGate, nextSummary; private static bool gateOpen = true;
    /// <summary>True while the native HUD is shown (set by the co-op world; null = always).</summary>
    internal static System.Func<bool>? HudVisible;

    internal static int Count => Live.Count;

    /// <summary>Adds or refreshes a label; keep calling it while it should show (it disappears ttl seconds after the last call).</summary>
    internal static void Set(string key, Vector3 world, string full, string? shortText, Color colour, float fadeStart, float fadeEnd, float shortFrom = 0f, float ttl = 0.5f, float alpha = 1f, float scale = 1f, bool rich = false)
    {
        try
        {
            if (!Live.TryGetValue(key, out var e))
            {
                if (!Ensure()) return;
                e = Pool.Count > 0 ? Pool.Pop() : Create(); e.Key = key; e.Shown = ""; e.ShownColour = new Color(-1, 0, 0, 0); e.Dirty = true; e.Active = false; Live[key] = e;
            }
            e.World = world; e.Colour = colour; e.FadeStart = fadeStart; e.FadeEnd = fadeEnd; e.ShortFrom = shortFrom; e.Alpha = alpha; e.Scale = scale;
            e.Expires = Time.unscaledTime + ttl;
            if (e.Full != full || e.Rich != rich) { e.Full = full; e.Rich = rich; e.Dirty = true; }
            string s = shortText ?? ""; if (e.Short != s) { e.Short = s; e.Dirty = true; }
        }
        catch (System.Exception ex) { DiagLog.LogDiagnostic("world-labels", "set-error", ex.Message); }
    }

    internal static void Remove(string key) { try { if (Live.TryGetValue(key, out var e)) Release(e); } catch { } }

    internal static void Clear() { try { Gone.Clear(); Gone.AddRange(Live.Values); foreach (var e in Gone) Release(e); Gone.Clear(); if (root) root!.SetActive(false); rootShown = false; } catch { } }

    private static void Release(Entry e)
    {
        Live.Remove(e.Key); e.Active = false;
        if (e.Go) { e.Go.SetActive(false); Pool.Push(e); }
    }

    private static bool Ensure()
    {
        if (root && canvas) return true;
        try
        {
            root = new GameObject("Lycoreia.WorldLabels"); Object.DontDestroyOnLoad(root);
            canvas = root.AddComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 20;
            var scaler = root.AddComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = 0.5f;
            rootShown = true;
            Pool.Clear(); Live.Clear();   // pooled entries belonged to a destroyed root
            if (!installed) { installed = true; Lycoreia.Game.LycoreiaMod.LatePumps.Add(new FramePump()); }
            if (!logged) { logged = true; OwnedLog.LogMessage("world labels: overlay canvas (nametags, markers, ping labels; post-processing free)"); }
            return true;
        }
        catch (System.Exception ex) { DiagLog.LogDiagnostic("world-labels", "build-error", ex.Message); root = null; canvas = null; return false; }
    }

    private static Entry Create()
    {
        var go = new GameObject("Label"); go.transform.SetParent(root!.transform, false);
        var rt = go.AddComponent<RectTransform>(); rt.anchorMin = rt.anchorMax = Vector2.zero; rt.pivot = new Vector2(0.5f, 0f);
        var group = go.AddComponent<CanvasGroup>(); group.interactable = false; group.blocksRaycasts = false;
        var plate = go.AddComponent<Image>(); plate.color = new Color(0.05f, 0.04f, 0.10f, 0.58f); plate.raycastTarget = false;
        var tgo = new GameObject("Text"); tgo.transform.SetParent(go.transform, false);
        var trt = tgo.AddComponent<RectTransform>(); trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = new Vector2(PadX, PadY); trt.offsetMax = new Vector2(-PadX, -PadY);
        var t = tgo.AddComponent<TextMeshProUGUI>(); t.alignment = TextAlignmentOptions.Center; t.fontSize = 24f; t.enableWordWrapping = true; t.raycastTarget = false;
        var font = UiFonts.Default; if (font) t.font = font;
        TmpOutline.Apply(t, 0.22f, new Color32(12, 8, 22, 235));
        go.SetActive(false);
        return new Entry { Go = go, Rt = rt, Group = group, Plate = plate, Text = t };
    }

    private static void Apply(Entry e, bool useShort)
    {
        string text = useShort ? e.Short : e.Full;
        e.Text.richText = e.Rich; e.Text.text = text; e.Shown = text; e.ShownShort = useShort;
        var size = e.Text.GetPreferredValues(text, MaxWidth - 2 * PadX, 0f);
        e.Rt.sizeDelta = new Vector2(Mathf.Min(size.x, MaxWidth - 2 * PadX) + 2 * PadX, size.y + 2 * PadY);
        e.Dirty = false;
    }

    private static void Frame()
    {
        try
        {
            if (Live.Count == 0) { if (rootShown && root) { root!.SetActive(false); rootShown = false; } return; }
            float now = Time.unscaledTime;
            if (now >= nextGate) { nextGate = now + GateEvery; try { gateOpen = HudVisible == null || HudVisible(); } catch { gateOpen = true; } }
            var cam = Camera.main;
            if (!gateOpen || !cam || !root || !canvas) { if (rootShown && root) { root!.SetActive(false); rootShown = false; } ExpireOnly(now); return; }
            if (!rootShown) { root!.SetActive(true); rootShown = true; }
            float sf = Mathf.Max(0.01f, canvas!.scaleFactor), w = Screen.width, h = Screen.height; var cpos = cam.transform.position;
            Gone.Clear();
            foreach (var e in Live.Values)
            {
                if (now > e.Expires) { Gone.Add(e); continue; }
                var sp = cam.WorldToScreenPoint(e.World); float d = Vector3.Distance(cpos, e.World);
                float a = e.Alpha * WorldLabelMath.Fade(d, e.FadeStart, e.FadeEnd);
                bool show = a > 0.02f && WorldLabelMath.OnScreen(sp.x, sp.y, sp.z, w, h);
                if (!show) { if (e.Active) { e.Go.SetActive(false); e.Active = false; } continue; }
                bool useShort = WorldLabelMath.UseShort(d, e.ShortFrom, e.ShownShort, e.Short.Length > 0);
                if (e.Dirty || useShort != e.ShownShort) Apply(e, useShort);
                if (e.Colour != e.ShownColour)
                {
                    float t = WorldLabelMath.LightenToward(e.Colour.r, e.Colour.g, e.Colour.b); var c = Color.Lerp(e.Colour, Color.white, t); c.a = 1f;
                    e.Text.color = c; e.ShownColour = e.Colour;
                }
                if (!e.Active) { e.Go.SetActive(true); e.Active = true; }
                e.Group.alpha = Mathf.Clamp01(a);
                e.Rt.anchoredPosition = new Vector2(sp.x / sf, sp.y / sf);
                float s = WorldLabelMath.Scale(d) * e.Scale; e.Rt.localScale = new Vector3(s, s, 1f);
            }
            foreach (var e in Gone) Release(e); Gone.Clear();
            if (now >= nextSummary) { nextSummary = now + 30f; int act = 0; foreach (var e in Live.Values) if (e.Active) act++; DiagLog.LogDiagnostic("world-labels", "summary", $"live={Live.Count} active={act} hudGate={gateOpen} screen={Screen.width}x{Screen.height} scale={sf:0.00}"); }
        }
        catch (System.Exception ex) { DiagLog.LogDiagnostic("world-labels", "frame-error", ex.Message); }
    }

    private static void ExpireOnly(float now)
    {
        Gone.Clear(); foreach (var e in Live.Values) if (now > e.Expires) Gone.Add(e);
        foreach (var e in Gone) Release(e); Gone.Clear();
    }

    private sealed class FramePump : Lycoreia.Game.IFramePump { public string Name => "world-labels"; void Lycoreia.Game.IFramePump.Pump() => Frame(); }
}

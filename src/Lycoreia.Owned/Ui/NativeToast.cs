// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Il2CppInterop.Runtime.Attributes;
using Il2CppTMPro;
using UnityEngine.UI;

namespace Lycoreia.Owned.Components.UI;

/// <summary>Top-right toast stack (max 3, ~4 s). Main thread only. Never throws.</summary>
internal static class NativeToast
{
    private static bool pathLogged, stackLogged;
    private static ToastHost? host;
    /// <summary>Chat line used when no world is loaded (set by MultiplayerUI).</summary>
    internal static Action<string>? ChatFallback;

    /// <summary>True when a world is loaded and toasts can be shown.</summary>
    internal static bool WorldLoaded
    {
        get { try { var s = SceneContext.Instance; return s != null && s.Player != null; } catch { return false; } }
    }

    internal static void Show(string title, string? body, Sprite? icon = null, UiCue cue = UiCue.Notify)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body)) return;
            if (!WorldLoaded)
            {
                LogPath("chat");
                ChatFallback?.Invoke(string.IsNullOrWhiteSpace(body) ? title : title + ": " + body);
                return;
            }
            if (!host) host = ToastHost.Create();
            if (!host) { LogPath("chat"); ChatFallback?.Invoke(string.IsNullOrWhiteSpace(body) ? title : title + ": " + body); return; }
            LogPath("own");
            host!.Add(title ?? string.Empty, body, icon, cue);
            UiFx.Play(cue);
        }
        catch (Exception e)
        {
            try { OwnedLog.LogWarning("toast failed: " + e.Message + (stackLogged ? "" : "\n" + e.StackTrace)); stackLogged = true; } catch { }
        }
    }

    private static void LogPath(string p)
    {
        if (pathLogged) return;
        pathLogged = true;
        try { OwnedLog.LogMessage("toast path: " + p); } catch { }
    }
}

internal sealed class ToastHost : MonoBehaviour
{
    public ToastHost(IntPtr ptr) : base(ptr) { }

    private const int MaxToasts = 3;
    private const float Width = 440f, Height = 86f, Gap = 10f, Margin = 24f, Life = 4f, FadeIn = 0.25f, FadeOut = 0.4f;

    private sealed class Entry { internal RectTransform Rt = null!; internal CanvasGroup Group = null!; internal float Born, Y, Dying = -1f; }
    private readonly List<Entry> entries = new();
    private RectTransform? rect;
    private RectTransform Rect => rect ? rect! : (rect = GetComponent<RectTransform>())!;   // never (RectTransform)transform: the cached wrapper can be a plain Transform

    internal static ToastHost? Create()
    {
        try
        {
            if (!Il2CppInterop.Runtime.Injection.ClassInjector.IsTypeRegisteredInIl2Cpp<ToastHost>())
                Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<ToastHost>();
            // RectTransform first: go.transform would otherwise cache a plain Transform wrapper that outlives the AddComponent swap (rc3 "Transform to RectTransform" cast failure)
            var go = new GameObject("LycoreiaToasts");
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(UiCanvas.Root, false);
            UiNode.Stretch(rt);
            return go.AddComponent<ToastHost>();
        }
        catch (Exception e) { OwnedLog.LogWarning("toast host: " + e.Message); return null; }
    }

    [HideFromIl2Cpp]
    internal void Add(string title, string? body, Sprite? icon, UiCue cue)
    {
        // drop the oldest when full
        while (entries.Count >= MaxToasts) { Destroy(entries[0].Rt.gameObject); entries.RemoveAt(0); }
        var accent = cue == UiCue.Fail ? UiTheme.WarningText : cue == UiCue.Success ? new Color(0.45f, 0.85f, 0.5f, 1f) : UiTheme.AccentColor;
        var rt = UiNode.AddChild(Rect, "Toast");
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 1f);
        rt.sizeDelta = new Vector2(Width, Height);
        UiPlates.Apply(UiNode.AddImage(rt, Color.white, 0, false), PlateKind.Card);
        var bar = UiNode.AddChild(rt, "Accent");
        bar.anchorMin = new Vector2(0f, 0f); bar.anchorMax = new Vector2(0f, 1f); bar.pivot = new Vector2(0f, 0.5f);
        bar.sizeDelta = new Vector2(6f, -16f); bar.anchoredPosition = new Vector2(8f, 0f);
        UiNode.AddImage(bar, accent, 3, false);
        float left = 26f;
        if (icon)
        {
            var ic = UiNode.AddChild(rt, "Icon");
            ic.anchorMin = ic.anchorMax = ic.pivot = new Vector2(0f, 0.5f);
            ic.sizeDelta = new Vector2(52f, 52f); ic.anchoredPosition = new Vector2(24f, 0f);
            var img = UiNode.AddImage(ic, Color.white, 0, false); img.sprite = icon; img.preserveAspect = true;
            left = 90f;
        }
        bool hasBody = !string.IsNullOrWhiteSpace(body);
        AddText(rt, "Title", title, 24f, UiTheme.AccentColor == accent ? UiTheme.GeneralText : accent, left, hasBody ? 0.5f : 0f, 1f, FontStyles.Bold);
        if (hasBody) AddText(rt, "Body", body!, 20f, UiTheme.GeneralText, left, 0f, 0.55f, FontStyles.Normal);
        var group = rt.gameObject.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false; group.interactable = false;
        var e = new Entry { Rt = rt, Group = group, Born = Time.unscaledTime, Y = -Margin };
        entries.Add(e);
        rt.anchoredPosition = new Vector2(Width + Margin, e.Y);   // starts off-screen right, slides in
        group.alpha = 0f;
    }

    private static void AddText(RectTransform parent, string name, string text, float size, Color color, float left, float yMin, float yMax, FontStyles style)
    {
        var rt = UiNode.AddChild(parent, name);
        rt.anchorMin = new Vector2(0f, yMin); rt.anchorMax = new Vector2(1f, yMax);
        rt.offsetMin = new Vector2(left, 6f); rt.offsetMax = new Vector2(-UiPlates.PadCard.x, -6f);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        var font = UiFonts.Default; if (font) t.font = font;
        t.text = text; t.fontSize = size; t.fontStyle = style; t.color = color; t.raycastTarget = false;
        t.alignment = TextAlignmentOptions.MidlineLeft; t.enableAutoSizing = true; t.fontSizeMin = 12f; t.fontSizeMax = size;
        t.overflowMode = TextOverflowModes.Ellipsis;
    }

    private void Update()
    {
        try
        {
            float now = Time.unscaledTime, dt = Time.unscaledDeltaTime;
            float y = -Margin;
            for (int i = entries.Count - 1; i >= 0; i--)   // newest at the top
            {
                var e = entries[i];
                float age = now - e.Born;
                if (e.Dying < 0f && age > Life) e.Dying = now;
                float a = Mathf.Clamp01(age / FadeIn);
                if (e.Dying >= 0f) a = Mathf.Min(a, 1f - Mathf.Clamp01((now - e.Dying) / FadeOut));
                e.Group.alpha = a;
                e.Y = Mathf.Lerp(e.Y, y, Mathf.Clamp01(dt * 12f));
                float slide = (1f - (1f - Mathf.Pow(1f - Mathf.Clamp01(age / FadeIn), 3f))) * (Width + Margin);
                e.Rt.anchoredPosition = new Vector2(-Margin + slide, e.Y);
                y -= Height + Gap;
                if (e.Dying >= 0f && now - e.Dying >= FadeOut) { Destroy(e.Rt.gameObject); entries.RemoveAt(i); }
            }
        }
        catch { /* cosmetic */ }
    }
}

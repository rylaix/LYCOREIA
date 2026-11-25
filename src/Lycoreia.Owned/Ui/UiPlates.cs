// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
// Lycoreia - shared text backgrounds ("plates"): one look for every label, toast, card and pill.
// Industry-standard game UI plate: antialiased rounded 9-sliced fill, a 1 px light edge, a soft ambient shadow (no offset,
// so it stays correct at any size), fixed design tokens for radius/padding/colour, and a fill dark enough for 4.5:1 text contrast.
#nullable enable
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Lycoreia.Owned.Components.UI;

internal enum PlateKind
{
    /// <summary>Small floating label (nametag, job/note/ping label, map hover).</summary>
    Label,
    /// <summary>Rounded pill (wheel action labels, hold prompts, compact badges).</summary>
    Pill,
    /// <summary>Panel with a title and body (toast, tracker, hint card, popup, banner).</summary>
    Card,
}

internal static class UiPlates
{
    // ---- design tokens (keep every plate on these) ----
    internal static readonly Color Fill = new(0.055f, 0.050f, 0.110f, 0.80f);       // deep blue-violet, ~4.5:1+ for white text over any scene
    internal static readonly Color FillStrong = new(0.050f, 0.045f, 0.100f, 0.92f); // cards with long body text
    internal static readonly Color Edge = new(1f, 1f, 1f, 0.12f);                    // 1 px inner edge
    internal static readonly Color ShadowColour = new(0f, 0f, 0f, 0.38f);
    internal const int RadiusLabel = 8, RadiusCard = 14, ShadowBlur = 10;
    /// <summary>Text padding inside a plate (x = left/right, y = top/bottom), in canvas units.</summary>
    internal static readonly Vector2 PadLabel = new(12f, 5f), PadPill = new(16f, 6f), PadCard = new(18f, 14f);

    internal static int RadiusOf(PlateKind k, float height) => k switch
    {
        PlateKind.Pill => Mathf.Clamp(Mathf.RoundToInt(height * 0.5f), 6, 32),
        PlateKind.Card => RadiusCard,
        _ => RadiusLabel,
    };

    internal static Vector2 PaddingOf(PlateKind k) => k switch { PlateKind.Pill => PadPill, PlateKind.Card => PadCard, _ => PadLabel };

    /// <summary>
    /// Turns an existing Image into a plate: rounded sliced fill (keeps the Image, so callers may still fade/tint it), plus an edge ring and
    /// a soft shadow added as the first children (both hollow, so drawing them after the fill only shows outside/on the rim; text children stay on top).
    /// Idempotent: calling it again only refreshes the sprites.
    /// </summary>
    internal static Image Apply(Image img, PlateKind kind = PlateKind.Label, Color? fill = null, bool edge = true, bool shadow = true, float height = 0f)
    {
        int radius = RadiusOf(kind, height > 0 ? height : (img.rectTransform ? img.rectTransform.rect.height : 0f));
        img.sprite = UiSprites.Rounded(radius);
        img.type = Image.Type.Sliced;
        img.pixelsPerUnitMultiplier = 1f;
        img.color = fill ?? (kind == PlateKind.Card ? FillStrong : Fill);
        var rt = img.rectTransform;
        if (shadow) Layer(rt, "PlateShadow", ShadowSprite(radius, ShadowBlur), ShadowColour, -ShadowBlur);
        if (edge) Layer(rt, "PlateEdge", EdgeSprite(radius), Edge, 0f);
        return img;
    }

    /// <summary>Creates a plate Image on a new child stretched under the given rect (for texts that have no background object yet).</summary>
    internal static Image AddBehind(RectTransform textRect, PlateKind kind = PlateKind.Label, Color? fill = null)
    {
        var parent = textRect.parent ? textRect.parent.TryCast<RectTransform>() : null;
        var go = new GameObject("Plate");
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent != null ? parent : textRect, false);
        rt.SetSiblingIndex(parent != null ? textRect.GetSiblingIndex() : 0);
        var pad = PaddingOf(kind);
        rt.anchorMin = textRect.anchorMin; rt.anchorMax = textRect.anchorMax; rt.pivot = textRect.pivot;
        rt.anchoredPosition = textRect.anchoredPosition; rt.sizeDelta = textRect.sizeDelta + pad * 2f;
        var img = go.AddComponent<Image>(); img.raycastTarget = false;
        return Apply(img, kind, fill, height: rt.sizeDelta.y);
    }

    /// <summary>Size of a plate that wraps a text of the given preferred size (padding from the tokens; pill height rounded up to even).</summary>
    internal static Vector2 Wrap(Vector2 textSize, PlateKind kind)
    {
        var p = PaddingOf(kind); var s = textSize + p * 2f;
        return new Vector2(Mathf.Ceil(s.x), Mathf.Ceil(s.y / 2f) * 2f);
    }

    static void Layer(RectTransform parent, string name, Sprite sprite, Color colour, float inset)
    {
        var found = parent.Find(name); var t = found ? found.TryCast<RectTransform>() : null;
        Image img;
        if (t == null)
        {
            var go = new GameObject(name);
            t = go.AddComponent<RectTransform>(); t.SetParent(parent, false);
            img = go.AddComponent<Image>(); img.raycastTarget = false;
        }
        else img = t.GetComponent<Image>();
        t.SetAsFirstSibling();
        t.anchorMin = Vector2.zero; t.anchorMax = Vector2.one; t.pivot = new Vector2(0.5f, 0.5f);
        t.offsetMin = new Vector2(inset, inset); t.offsetMax = new Vector2(-inset, -inset);
        img.sprite = sprite; img.type = Image.Type.Sliced; img.color = colour;
    }

    // ---- sprites (white, tinted by Image.color; cached per radius) ----
    static readonly Dictionary<int, Sprite> EdgeCache = new(), ShadowCache = new();

    /// <summary>1 px ring on the inside of the rounded rectangle.</summary>
    internal static Sprite EdgeSprite(int radius)
    {
        if (EdgeCache.TryGetValue(radius, out var s) && s) return s;
        int size = Mathf.Max(64, radius * 2 + 8);
        s = Build("Lycoreia.PlateEdge." + radius, size, radius + 1, (x, y) =>
        {
            float d = RoundedDistance(x, y, size, size, radius);
            float outer = Mathf.Clamp01(0.5f - d), inner = Mathf.Clamp01(0.5f - (d + 1.25f));
            return outer - inner;
        });
        EdgeCache[radius] = s; return s;
    }

    /// <summary>Soft shadow OUTSIDE the rounded rectangle only (hollow inside), blur px wide.</summary>
    internal static Sprite ShadowSprite(int radius, int blur)
    {
        int key = radius * 1000 + blur;
        if (ShadowCache.TryGetValue(key, out var s) && s) return s;
        int size = Mathf.Max(64, (radius + blur) * 2 + 8);
        s = Build("Lycoreia.PlateShadow." + key, size, radius + blur + 1, (x, y) =>
        {
            float d = RoundedDistance(x - blur, y - blur, size - 2 * blur, size - 2 * blur, radius);
            if (d <= -0.5f) return 0f;                                                             // hollow: the fill covers the inside
            float t = Mathf.Clamp01(1f - Mathf.Max(d, 0f) / blur);
            return t * t * (3f - 2f * t) * Mathf.Clamp01(d + 0.5f);                                // smoothstep falloff, antialiased at the rim
        });
        ShadowCache[key] = s; return s;
    }

    /// <summary>Signed distance (px) from a pixel centre to a rounded rectangle of w x h at the origin; negative inside.</summary>
    static float RoundedDistance(float x, float y, float w, float h, float r)
    {
        float px = x + 0.5f, py = y + 0.5f;
        float qx = Mathf.Abs(px - w * 0.5f) - (w * 0.5f - r), qy = Mathf.Abs(py - h * 0.5f) - (h * 0.5f - r);
        float ox = Mathf.Max(qx, 0f), oy = Mathf.Max(qy, 0f);
        return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
    }

    static Sprite Build(string name, int size, int border, System.Func<int, int, float> alpha)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
        var px = new Color32[size * size];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) px[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(alpha(x, y)) * 255f));
        tex.SetPixels32(px); tex.Apply(false, true);
        var s = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0u, SpriteMeshType.FullRect, new Vector4(border, border, border, border));
        s.name = name; s.hideFlags = HideFlags.HideAndDontSave; return s;
    }
}

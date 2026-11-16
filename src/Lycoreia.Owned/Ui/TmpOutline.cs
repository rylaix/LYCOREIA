// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Il2CppTMPro;

namespace Lycoreia.Owned.Components.UI;

internal static class TmpOutline
{
    private struct Pending { public TMP_Text Text; public float Width; public Color32 Color; public int Tries; }
    private static readonly List<Pending> pending = new();
    private static bool warned;

    /// <summary>Applies the outline now if the text is active, else queues it. Never throws.</summary>
    internal static void Apply(TMP_Text? text, float width, Color32 color)
    {
        if (!text)
            return;
        try
        {
            if (!text!.gameObject.activeInHierarchy) { pending.Add(new Pending { Text = text, Width = width, Color = color }); return; }
            Set(text, width, color);
        }
        catch (Exception ex) { Warn(ex); }
    }

    /// <summary>Call once per frame (MenuPolish.Tick): applies queued outlines whose texts became active; drops destroyed ones.</summary>
    internal static void Pump()
    {
        if (pending.Count == 0)
            return;
        for (var i = pending.Count - 1; i >= 0; i--)
        {
            var p = pending[i];
            try
            {
                if (!p.Text) { pending.RemoveAt(i); continue; }
                if (!p.Text.gameObject.activeInHierarchy)
                {
                    if (++p.Tries > 36000) pending.RemoveAt(i); else pending[i] = p;   // give up after ~10 min inactive
                    continue;
                }
                pending.RemoveAt(i);
                Set(p.Text, p.Width, p.Color);
            }
            catch (Exception ex) { pending.RemoveAt(i); Warn(ex); }
        }
    }

    private static void Set(TMP_Text text, float width, Color32 color)
    {
        if (!text.font) { var f = UiFonts.Default; if (f) text.font = f; }
        try { text.outlineColor = color; text.outlineWidth = width; }
        catch (Exception ex) { Warn(ex); }
    }

    private static void Warn(Exception ex)
    {
        if (warned) return;
        warned = true;
        OwnedLog.LogWarning("text outline unavailable (text kept without outline): " + ex.GetType().Name + ": " + ex.Message);
    }
}

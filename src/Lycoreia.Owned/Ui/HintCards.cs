// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Il2CppTMPro;
using Lycoreia.Text;

namespace Lycoreia.Owned.Components.UI;

internal static class HintCards
{
    private const string WheelKey = "hint.wheel", BoostKey = "hint.boost";
    private const float WheelDelay = 8f, NearDelay = 3f, NearRange = 12f, ShowSeconds = 9f, Gap = 4f;

    private static RectTransform? card;
    private static float shownAt = -100f, hiddenAt = -100f, inSessionSince = -1f, nearSince = -1f;
    private static bool fading;

    internal static void Tick()
    {
        var now = UnityEngine.Time.unscaledTime;
        if (card)
        {
            if (!fading && now - shownAt > ShowSeconds) Fade();
            return;
        }
        if (!Lycoreia.Mod.IsNetworkActive || !NativeToast.WorldLoaded || Lycoreia.Game.GameState.Loading) { inSessionSince = nearSince = -1f; return; }
        bool wheelOpen = false;
        try { wheelOpen = CoopWheel.Busy; } catch { }
        if (LycoreiaMenu.IsOpen || wheelOpen || UnityEngine.Time.timeScale <= 0f) { return; }   // paused or busy: the timers wait, nothing is shown
        if (now - hiddenAt < Gap) return;

        if (inSessionSince < 0f) inSessionSince = now;
        if (!SeenFlags.Has(WheelKey) && !InputGlyphs.UsingGamepad && now - inSessionSince >= WheelDelay)
        {
            Show(WheelKey, MenuText.HintWheel(InputGlyphs.Label("MMB")));
            return;
        }
        if (SeenFlags.Has(BoostKey)) return;
        if (AnotherPlayerNear()) { if (nearSince < 0f) nearSince = now; } else nearSince = -1f;
        if (nearSince >= 0f && now - nearSince >= NearDelay) Show(BoostKey, MenuText.HintBoost);
    }

    private static bool AnotherPlayerNear()
    {
        var me = SceneContext.Instance ? SceneContext.Instance.Player : null;
        if (!me) return false;
        var here = me!.transform.position;
        foreach (var pair in Lycoreia.Game.Players.Avatars)
        {
            var t = pair.Value;
            if (!t || pair.Key == Lycoreia.Mod.LocalId || !t.gameObject.activeInHierarchy) continue;
            if ((t.position - here).sqrMagnitude <= NearRange * NearRange) return true;
        }
        return false;
    }

    private static void Show(string key, string body)
    {
        SeenFlags.Set(key, "1");   // once: it counts as seen the moment it is shown
        const float w = 760f, h = 104f;
        var panel = new UiPanel { Color = UiColor.Primary, CornerRadius = 16, Size = new Vector2(w, h) };
        panel.Children.Add(new UiPanel { CustomColor = UiTheme.AccentColor, CornerRadius = 3, Size = new Vector2(6f, h - 28f), Position = new Vector2(-w / 2f + 18f, 0f) });
        panel.Children.Add(MenuPolish.Label("<b>" + MenuText.HintTitle + "</b>", w - 70f, 30f, 22f, UiColor.TextCategory, TextAlignmentOptions.Left, new Vector2(18f, h / 2f - 12f - 15f)));
        panel.Children.Add(MenuPolish.Label(body, w - 70f, 56f, 26f, UiColor.TextGeneral, TextAlignmentOptions.TopLeft, new Vector2(18f, -8f)));
        var rt = panel.Render(UiCanvas.Root);
        if (panel.Picture) UiPlates.Apply(panel.Picture!, PlateKind.Card, height: h);
        rt.gameObject.name = "LycoreiaHint_" + key;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.anchoredPosition = new Vector2(0f, 210f);
        MenuPolish.NoRaycast(rt);
        card = rt; shownAt = UnityEngine.Time.unscaledTime; fading = false;
        try { UiFade.Ensure(rt.gameObject).Play(rt, 1f, 1f, 0.3f, false, null, 0f, 0.94f); } catch (Exception e) { OwnedLog.LogWarning("hint fade: " + e.Message); }
        UiFx.Play(UiCue.Notify);
        OwnedLog.LogMessage($"hint card shown: {key} (lang={CoopText.RenderLang()}, font={(UiFonts.Default ? UiFonts.Default!.name : "TMP default")})");
    }

    private static void Fade()
    {
        fading = true;
        var go = card ? card!.gameObject : null;
        try
        {
            if (go == null) { card = null; return; }
            UiFade.Ensure(go).Play(card, 0f, 0.96f, 0.4f, true, () => { try { UnityEngine.Object.Destroy(go); } catch { } hiddenAt = UnityEngine.Time.unscaledTime; });
        }
        catch { try { if (go) UnityEngine.Object.Destroy(go); } catch { } hiddenAt = UnityEngine.Time.unscaledTime; }
        // the reference is dropped when the fade ends (the object is destroyed, so the Unity null check turns true)
    }
}

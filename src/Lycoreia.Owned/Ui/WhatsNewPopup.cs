// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Il2CppTMPro;
using Lycoreia.Text;
using UnityEngine.InputSystem;

namespace Lycoreia.Owned.Components.UI;

internal static class WhatsNewPopup
{
    internal const string Key = "whatsnew.version";
    private const float SettleSeconds = 1.5f, PanelW = 1040f, Pad = 40f;
    private const int MaxItems = 4;

    private static RectTransform? root, panel;
    private static bool finished;
    private static float usableSince = -1f;

    internal static bool IsOpen => root && root!.gameObject.activeSelf;

    internal static void Tick()
    {
        if (IsOpen)
        {
            var kb = Keyboard.current;
            if (Lycoreia.Game.GameState.Ready || Lycoreia.Mod.IsNetworkActive) Close(false);
            else if (kb != null && kb.escapeKey.wasPressedThisFrame) Close(true);
            return;
        }
        if (finished || !Lycoreia.Mod.Initialised) return;

        if (!MenuPolish.MainMenuUsable || LycoreiaMenu.IsOpen) { usableSince = -1f; return; }
        var now = UnityEngine.Time.unscaledTime;
        if (usableSince < 0f) usableSince = now;
        if (now - usableSince < SettleSeconds) return;

        finished = true;   // decided once per game start
        bool fresh = false;
        try { fresh = Lycoreia.Game.Prefs.Get<bool>(Settings.UiFirstRun); } catch { }
        var last = SeenFlags.Get(Key);
        var decision = WhatsNewGate.Decide(last, BuildInfo.Version, fresh);
        OwnedLog.LogMessage($"whats-new popup: last seen={last ?? "none"}, current={BuildInfo.Version}, fresh install={fresh}, decision={decision}");
        if (decision == WhatsNewDecision.MarkSilently) SeenFlags.Set(Key, BuildInfo.Version);
        else if (decision == WhatsNewDecision.Show) Open();
    }

    private static void Open()
    {
        var sections = WhatsNew.For(CoopText.RenderLang());
        if (sections.Count == 0) return;
        SeenFlags.Set(Key, BuildInfo.Version);   // once: it counts as seen the moment it is shown

        // Layout (rc6): eyebrow (section, accent) > title (large) > 2x2 cards > "and N more" > buttons, primary action on the right.
        var items = sections[0].Items.Take(MaxItems).ToList();
        float gridW = PanelW - 2f * Pad;
        var grid = WhatsNewCards.Grid(items, gridW, showTry: false);
        int more = sections[0].Items.Count - items.Count + sections.Skip(1).Sum(s => s.Items.Count);
        const float eyebrowH = 28f, titleH = 52f, moreH = 30f, buttonH = 58f;
        float panelH = Pad + eyebrowH + 4f + titleH + 20f + grid.Size.y + (more > 0 ? 12f + moreH : 0f) + 28f + buttonH + Pad;

        var dim = new UiPanel { CustomColor = new Color(0f, 0f, 0f, 0.6f), Anchors = new Vector4(0f, 0f, 1f, 1f) };
        var card = new UiPanel { Color = UiColor.Primary, CornerRadius = 22, Size = new Vector2(PanelW, panelH) };
        float y = panelH / 2f - Pad;
        card.Children.Add(MenuPolish.Label(sections[0].Title, gridW, eyebrowH, 20f, UiColor.TextCategory, TextAlignmentOptions.Left, new Vector2(0f, y - eyebrowH / 2f)));
        y -= eyebrowH + 4f;
        card.Children.Add(MenuPolish.Label(MenuText.WhatsNewHeading(BuildInfo.Version), gridW, titleH, 38f, UiColor.TextGeneral, TextAlignmentOptions.Left, new Vector2(0f, y - titleH / 2f)));
        y -= titleH + 20f;
        grid.Position = new Vector2(0f, y - grid.Size.y / 2f);
        card.Children.Add(grid);
        y -= grid.Size.y;
        if (more > 0)
        {
            y -= 12f;
            card.Children.Add(MenuPolish.Label("<alpha=#99>" + MenuText.PopupMore(more), gridW, moreH, 18f, UiColor.TextGeneral, TextAlignmentOptions.Left, new Vector2(0f, y - moreH / 2f)));
        }
        float by = -panelH / 2f + Pad + buttonH / 2f;
        const float gotItW = 240f, readW = 320f;
        card.Children.Add(MenuPolish.Button(MenuText.PopupGotIt, gotItW, buttonH, () => Close(true), false, new Vector2(gridW / 2f - gotItW / 2f, by)));
        card.Children.Add(MenuPolish.Button(MenuText.PopupRead, readW, buttonH, () => { Close(true); LycoreiaMenu.OpenWhatsNew(); }, true, new Vector2(gridW / 2f - gotItW - 16f - readW / 2f, by)));
        dim.Children.Add(card);

        UiCanvas.EnsureEventSystem();
        var r = dim.Render(UiCanvas.Root);
        r.gameObject.name = "LycoreiaWhatsNew";
        root = r; panel = card.Rendered;
        if (card.Picture) UiPlates.Apply(card.Picture!, PlateKind.Card, height: panelH);
        try { UiFade.Ensure(r.gameObject).Play(panel, 1f, 1f, 0.2f, false, null, 0f, 0.94f); }
        catch (Exception e) { OwnedLog.LogWarning("whats-new fade: " + e.Message); }
        UiFx.Play(UiCue.Open);
        OwnedLog.LogMessage($"whats-new popup opened: {items.Count} cards, height {panelH:0}, lang={CoopText.RenderLang()}, font={(UiFonts.Default ? UiFonts.Default!.name : "TMP default")}");
    }

    private static void Close(bool sound)
    {
        try
        {
            if (!root) return;
            var go = root!.gameObject;
            if (sound) UiFx.Play(UiCue.Close);
            UiFade.Ensure(go).Play(panel, 0f, 0.96f, 0.15f, true, () => { try { UnityEngine.Object.Destroy(go); } catch { } });
            root = null;
        }
        catch (Exception e)
        {
            OwnedLog.LogWarning("whats-new close: " + e.Message);
            try { if (root) UnityEngine.Object.Destroy(root!.gameObject); } catch { }
            root = null;
        }
    }
}

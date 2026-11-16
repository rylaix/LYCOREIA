// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Il2CppTMPro;
using Lycoreia.Text;
using UnityEngine.InputSystem;

namespace Lycoreia.Owned.Components.UI;

internal static class WhatsNewPopup
{
    internal const string Key = "whatsnew.version";
    private const float SettleSeconds = 1.5f, PanelW = 1020f, PanelH = 700f;
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

        var dim = new UiPanel { CustomColor = new Color(0f, 0f, 0f, 0.55f), Anchors = new Vector4(0f, 0f, 1f, 1f) };
        var card = new UiPanel { Color = UiColor.Primary, CornerRadius = 22, Size = new Vector2(PanelW, PanelH) };
        card.Children.Add(MenuPolish.Label("<b>" + MenuText.WhatsNewHeading(BuildInfo.Version) + "</b>", PanelW - 60f, 60f, 34f, UiColor.TextCategory, TextAlignmentOptions.Left, new Vector2(0f, PanelH / 2f - 20f - 30f)));
        card.Children.Add(MenuPolish.Label("<size=80%><alpha=#BB>" + sections[0].Title + "</size>", PanelW - 60f, 36f, 24f, UiColor.TextGeneral, TextAlignmentOptions.Left, new Vector2(0f, PanelH / 2f - 84f - 18f)));
        var items = sections[0].Items;
        float y = PanelH / 2f - 120f;
        for (int i = 0; i < Math.Min(MaxItems, items.Count); i++)
        {
            card.Children.Add(MenuPolish.Fixed("• " + items[i].text, PanelW - 70f, 22f, UiColor.TextGeneral, TextAlignmentOptions.TopLeft, new Vector2(0f, y - 48f), 96f));
            y -= 100f;
        }
        int more = items.Count - MaxItems + sections.Skip(1).Sum(s => s.Items.Count);
        if (more > 0) card.Children.Add(MenuPolish.Label("<alpha=#BB>" + MenuText.PopupMore(more), PanelW - 70f, 36f, 22f, UiColor.TextCategory, TextAlignmentOptions.Left, new Vector2(0f, y - 14f)));
        card.Children.Add(MenuPolish.Button(MenuText.PopupRead, 360f, 58f, () => { Close(true); LycoreiaMenu.OpenWhatsNew(); }, true, new Vector2(-200f, -PanelH / 2f + 20f + 29f)));
        card.Children.Add(MenuPolish.Button(MenuText.PopupGotIt, 360f, 58f, () => Close(true), false, new Vector2(200f, -PanelH / 2f + 20f + 29f)));
        dim.Children.Add(card);

        UiCanvas.EnsureEventSystem();
        var r = dim.Render(UiCanvas.Root);
        r.gameObject.name = "LycoreiaWhatsNew";
        root = r; panel = card.Rendered;
        try { UiFade.Ensure(r.gameObject).Play(panel, 1f, 1f, 0.2f, false, null, 0f, 0.94f); }
        catch (Exception e) { OwnedLog.LogWarning("whats-new fade: " + e.Message); }
        UiFx.Play(UiCue.Open);
        OwnedLog.LogMessage($"whats-new popup opened: {Math.Min(MaxItems, items.Count)} items, lang={CoopText.RenderLang()}, font={(UiFonts.Default ? UiFonts.Default!.name : "TMP default")}");
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

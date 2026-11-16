// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Il2CppTMPro;
using Lycoreia.Text;

namespace Lycoreia.Owned.Components.UI;

internal static class UpdateBanner
{
    private static RectTransform? root;
    private static UpdateStatus? builtFor;
    private static string builtLang = string.Empty;
    private static bool soundPlayed;

    internal static void Tick()
    {
        Lycoreia.Game.UpdateChecker.InGameToast ??= (title, body) => NativeToast.Show(title, body, null, UiCue.Notify);   // in game the same notice is a toast (no chat line)

        var update = Lycoreia.Mod.Update;
        bool want = update != null && (update.Kind == UpdateKind.UpdateAvailable || update.Kind == UpdateKind.AheadOfPublic)
                    && !Lycoreia.Mod.IsNetworkActive && !Lycoreia.Game.GameState.Ready && !LycoreiaMenu.IsOpen && !WhatsNewPopup.IsOpen;
        if (!want)
        {
            if (root && root!.gameObject.activeSelf) root.gameObject.SetActive(false);
            return;
        }

        var lang = CoopText.RenderLang();
        if (!root || builtFor != update || builtLang != lang) Build(update!, lang);
        if (!root || root!.gameObject.activeSelf) return;
        UiCanvas.EnsureEventSystem();
        root.gameObject.SetActive(true);
        try { UiFade.Ensure(root.gameObject).Play(root, 1f, 1f, 0.25f, false, null, 0f, 0.96f); } catch (Exception e) { OwnedLog.LogWarning("update banner fade: " + e.Message); }
        if (!soundPlayed) { soundPlayed = true; UiFx.Play(UiCue.Notify); }
    }

    private static void Build(UpdateStatus update, string lang)
    {
        if (root) UnityEngine.Object.Destroy(root!.gameObject);
        root = null;
        bool available = update.Kind == UpdateKind.UpdateAvailable;
        int buttons = available ? update.Offering.Count : 0;
        float width = 960f, height = buttons > 0 ? 132f : 84f;
        var panel = new UiPanel { Color = UiColor.Primary, CornerRadius = 18, Size = new Vector2(width, height) };
        var message = MenuText.UpdateMessage(update);
        panel.Children.Add(MenuPolish.Label(message, width - 40f, 70f, 24f, available ? UiColor.TextCategory : UiColor.TextGeneral,
            TextAlignmentOptions.Center, new Vector2(0f, height / 2f - 8f - 35f)));
        for (int i = 0; i < buttons; i++)
        {
            var source = update.Offering[i];
            float bw = 240f, x = -(buttons * (bw + 12f) - 12f) / 2f + bw / 2f + i * (bw + 12f);
            panel.Children.Add(MenuPolish.Button(MenuText.OpenSource(source.Source), bw, 46f, () => UnityEngine.Application.OpenURL(source.Url), true, new Vector2(x, -height / 2f + 12f + 23f)));
        }
        var rt = panel.Render(UiCanvas.Root);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(0f, -14f);
        rt.gameObject.name = "LycoreiaUpdateBanner";
        rt.gameObject.SetActive(false);
        root = rt; builtFor = update; builtLang = lang;
        OwnedLog.LogMessage($"update banner: uGUI, font={(UiFonts.Default ? UiFonts.Default!.name : "TMP default")}, kind={update.Kind}, sources={buttons}");
    }
}

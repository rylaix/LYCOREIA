// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Owned.Components.UI;

/// <summary>
/// Mod watermark: name, version and credit in the main menu; a small version tag in the corner
/// in game, so screenshots and bug reports always show which build was running.
/// </summary>
internal sealed partial class MultiplayerUI
{
    private const float WatermarkMargin = 8f;

    private static readonly string MenuWatermarkText =
        $"<b>{BuildInfo.Name} {BuildInfo.Version}</b>\n" +
        "Co-op for Slime Rancher 2 · Rylaix (core developer) · Kum1te (core QA)\n" +
        "github.com/rylaix/Lycoreia";

    private static readonly string GameWatermarkText = $"{BuildInfo.Name} {BuildInfo.Version}";

    private GUIStyle? menuWatermarkStyle;
    private GUIStyle? gameWatermarkStyle;

    private void DrawWatermark()
    {
        var inMenu = !Mod.IsNetworkActive && !Lycoreia.Game.GameState.Ready;   // offline main menu
        var style = inMenu ? MenuWatermarkStyle() : GameWatermarkStyle();
        var text = inMenu ? MenuWatermarkText : GameWatermarkText;

        var height = inMenu ? 60f : 20f;
        var rect = new Rect(WatermarkMargin, Screen.height - height - WatermarkMargin,
            Screen.width - WatermarkMargin * 2f, height);

        // Shadow first for readability on bright scenes.
        var color = style.normal.textColor;
        style.normal.textColor = new Color(0f, 0f, 0f, color.a * 0.6f);
        GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, style);
        style.normal.textColor = color;
        GUI.Label(rect, text, style);
        DrawUpdateBanner();
    }

    private GUIStyle? updateStyle;

    /// <summary>
    /// 2.1: the update notice in the main menu from the moment the startup check answers (no need to open the Multiplayer menu):
    /// a banner at the top centre with a button per source that has the newer version.
    /// </summary>
    private void DrawUpdateBanner()
    {
        var update = Lycoreia.Mod.Update;
        if (update == null || (update.Kind != Lycoreia.UpdateKind.UpdateAvailable && update.Kind != Lycoreia.UpdateKind.AheadOfPublic)) return;
        if (Mod.IsNetworkActive || Lycoreia.Game.GameState.Ready) return;                       // main menu only
        var style = updateStyle ??= CreateBannerStyle();
        bool available = update.Kind == Lycoreia.UpdateKind.UpdateAvailable;
        float width = Mathf.Min(900f, Screen.width - 40f), x = (Screen.width - width) / 2f, y = 14f;
        int buttons = available ? update.Offering.Count : 0;
        var box = new Rect(x, y, width, buttons > 0 ? 78f : 46f);
        var prev = GUI.color; GUI.color = new Color(0.106f, 0.133f, 0.188f, 0.92f); GUI.DrawTexture(box, Texture2D.whiteTexture); GUI.color = prev;
        style.normal.textColor = available ? new Color(0.949f, 0.702f, 0.239f, 1f) : new Color(0.91f, 0.925f, 0.949f, 1f);
        GUI.Label(new Rect(x + 12f, y + 6f, width - 24f, 34f), update.Message, style);
        for (int i = 0; i < buttons; i++)
        {
            var source = update.Offering[i]; float bw = 200f, bx = x + width / 2f - (buttons * (bw + 10f) - 10f) / 2f + i * (bw + 10f);
            if (GUI.Button(new Rect(bx, y + 42f, bw, 28f), "Open " + source.Source)) Application.OpenURL(source.Url);
        }
    }

    private static GUIStyle CreateBannerStyle()
        => new GUIStyle { font = GUI.skin.label.font, alignment = TextAnchor.MiddleCenter, fontSize = 16, richText = true, wordWrap = true, fontStyle = FontStyle.Bold };

    private GUIStyle MenuWatermarkStyle()
        => menuWatermarkStyle ??= CreateWatermarkStyle(14, true, 0.9f);

    private GUIStyle GameWatermarkStyle()
        => gameWatermarkStyle ??= CreateWatermarkStyle(11, false, 0.45f);

    // Il2Cpp GUIStyle has no copy constructor: build a label style by hand.
    private static GUIStyle CreateWatermarkStyle(int fontSize, bool richText, float alpha)
    {
        var style = new GUIStyle
        {
            font = GUI.skin.label.font,
            alignment = TextAnchor.LowerRight,
            fontSize = fontSize,
            richText = richText,
            wordWrap = false
        };
        style.normal.textColor = new Color(1f, 1f, 1f, alpha);
        return style;
    }
}

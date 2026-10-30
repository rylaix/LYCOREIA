namespace SR2MP.Components.UI;

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
        var inMenu = state == MenuState.DisconnectedMainMenu;
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
    }

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

// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Il2CppTMPro;
using Lycoreia.Text;

namespace Lycoreia.Owned.Components.UI;

internal static class WhatsNewCards
{
    internal const float Pad = 20f, Gap = 16f, BarW = 4f, BarGap = 14f, TitleSize = 24f, BodySize = 19f, TrySize = 17f;
    private const string BodyAlpha = "<alpha=#C8>";

    private static float TextWidth(float cardW) => cardW - 2f * Pad - BarW - BarGap;

    internal static float CardHeight(WhatsNew.Item item, float cardW, bool showTry)
    {
        float w = TextWidth(cardW);
        float h = 2f * Pad + LayoutText.RowHeight(item.Title, w, TitleSize) + 2f + LayoutText.RowHeight(item.Text, w, BodySize);
        if (showTry && item.TryIt.Length > 0) h += 6f + LayoutText.RowHeight(MenuText.TryIt(item.TryIt), w, TrySize);
        return h;
    }

    internal static UiPanel Card(WhatsNew.Item item, float w, float h, bool showTry, Vector2 at)
    {
        float tw = TextWidth(w), x = -w / 2f + Pad + BarW + BarGap + tw / 2f, top = h / 2f - Pad;
        var card = new UiPanel { Color = UiColor.Secondary, CornerRadius = UiPlates.RadiusCard, Size = new Vector2(w, h), Position = at };
        card.Children.Add(new UiPanel { CustomColor = UiTheme.AccentColor, CornerRadius = 2, Size = new Vector2(BarW, h - 2f * Pad), Position = new Vector2(-w / 2f + Pad + BarW / 2f, 0f) });
        UiText Add(string text, float size, UiColor color)
        {
            var node = MenuPolish.Fixed(text, tw, size, color, TextAlignmentOptions.TopLeft, null);
            node.Position = new Vector2(x, top - node.Size.y / 2f);
            card.Children.Add(node);
            return node;
        }
        top -= Add(item.Title, TitleSize, UiColor.TextGeneral).Size.y + 2f;
        top -= Add(BodyAlpha + item.Text, BodySize, UiColor.TextGeneral).Size.y + 6f;
        if (showTry && item.TryIt.Length > 0) Add(MenuText.TryIt(item.TryIt), TrySize, UiColor.TextCategory);
        return card;
    }

    /// <summary>Items in a 2-column grid; both cards of a row share the taller height. Returns a transparent block sized to its content.</summary>
    internal static UiPanel Grid(IList<WhatsNew.Item> items, float width, bool showTry)
    {
        float cw = (width - Gap) / 2f;
        var heights = new List<float>();
        for (int i = 0; i < items.Count; i += 2)
            heights.Add(Math.Max(CardHeight(items[i], cw, showTry), i + 1 < items.Count ? CardHeight(items[i + 1], cw, showTry) : 0f));
        float total = heights.Sum() + Gap * Math.Max(0, heights.Count - 1);
        var grid = new UiPanel { Color = UiColor.Transparent, Size = new Vector2(width, Math.Max(total, 1f)) };
        float y = total / 2f;
        for (int r = 0; r < heights.Count; r++)
        {
            for (int c = 0; c < 2 && r * 2 + c < items.Count; c++)
                grid.Children.Add(Card(items[r * 2 + c], cw, heights[r], showTry, new Vector2(-width / 2f + cw / 2f + c * (cw + Gap), y - heights[r] / 2f)));
            y -= heights[r] + Gap;
        }
        return grid;
    }
}

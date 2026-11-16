// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Il2CppTMPro;

namespace Lycoreia.Owned.Components.UI;

internal static class JobIcons
{
    private static readonly Dictionary<string, string[]> Keys = new()
    {
        ["corral"] = new[] { "corral" }, ["coop"] = new[] { "coop", "chicken" }, ["garden"] = new[] { "garden" }, ["silo"] = new[] { "silo" },
        ["pond"] = new[] { "pond" }, ["incinerator"] = new[] { "incinerator" }, ["plot"] = new[] { "landplot", "plot" }, ["gordo"] = new[] { "gordo" },
        ["spot"] = new[] { "waypoint", "mapmarker", "map_marker", "marker" },
    };

    private static readonly Dictionary<string, (Color color, string glyph)> Badges = new()
    {
        ["corral"] = (new Color(0.36f, 0.62f, 0.86f), "#"), ["coop"] = (new Color(0.86f, 0.62f, 0.30f), "~"), ["garden"] = (new Color(0.40f, 0.72f, 0.40f), "*"),
        ["silo"] = (new Color(0.62f, 0.62f, 0.70f), "O"), ["pond"] = (new Color(0.30f, 0.60f, 0.80f), "="), ["incinerator"] = (new Color(0.86f, 0.40f, 0.30f), "^"),
        ["plot"] = (new Color(0.60f, 0.50f, 0.40f), "+"), ["gordo"] = (new Color(0.84f, 0.46f, 0.66f), "G"), ["spot"] = (new Color(0.95f, 0.70f, 0.24f), "!"),
        ["generic"] = (new Color(0.45f, 0.50f, 0.60f), "-"),
    };

    private static readonly Dictionary<string, Sprite?> cache = new();
    private static List<Sprite>? iconSprites;

    private static Sprite? Native(string kind)
    {
        if (cache.TryGetValue(kind, out var hit)) return hit ? hit : null;
        Sprite? found = null;
        try
        {
            if (iconSprites == null)
            {
                iconSprites = new List<Sprite>();
                foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>())
                    if (s && s.name != null && s.name.IndexOf("icon", StringComparison.OrdinalIgnoreCase) >= 0) iconSprites.Add(s);
                OwnedLog.LogMessage($"job icons: {iconSprites.Count} icon-named sprites loaded");
            }
            if (Keys.TryGetValue(kind, out var words))
                foreach (var s in iconSprites)
                {
                    var n = s.name.ToLowerInvariant();
                    if (n.Contains("background") || n.Contains("frame") || n.Contains("_bg") || !words.Any(n.Contains)) continue;
                    if (found == null || s.name.Length < found.name.Length) found = s;
                }
        }
        catch (Exception e) { OwnedLog.LogWarning($"job icon lookup ({kind}) failed: {e.Message}"); }
        cache[kind] = found;
        OwnedLog.LogMessage($"job icon {kind}: " + (found ? $"native sprite '{found!.name}'" : "no native sprite found, using the badge"));
        return found;
    }

    /// <summary>A square icon node for a job kind (native sprite, else a coloured badge with a symbol).</summary>
    internal static UiNode Icon(string kind, float size, Vector2 position)
    {
        var sprite = Native(kind);
        if (sprite) return new UiPanel { Sprite = sprite, CustomColor = Color.white, Size = new Vector2(size, size), Position = position };
        var (color, glyph) = Badges.TryGetValue(kind, out var b) ? b : Badges["generic"];
        var badge = new UiPanel { CustomColor = color, CornerRadius = 10, Size = new Vector2(size, size), Position = position };
        badge.Children.Add(new UiText { Text = "<b>" + glyph + "</b>", FontSize = size * 0.62f, Color = UiColor.TextButton, Alignment = TextAlignmentOptions.Center, Anchors = new Vector4(0f, 0f, 1f, 1f) });
        return badge;
    }
}

// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Owned.Components.UI;

public static class UiCueScoring
{
    /// <summary>Indexed by the UiCue enum order: Hover, Click, Open, Close, Select, Confirm, Cancel, Notify, Success, Fail, Whoosh.</summary>
    public static readonly string[][] Words =
    {
        /* Hover   */ new[] { "hover", "highlight", "mouseover", "rollover" },
        /* Click   */ new[] { "submit", "click", "press", "confirm", "select" },
        /* Open    */ new[] { "open", "show", "popup", "expand", "enter" },
        /* Close   */ new[] { "close", "hide", "collapse", "exit", "back" },
        /* Select  */ new[] { "select", "tab", "focus", "toggle", "change", "click" },
        /* Confirm */ new[] { "confirm", "accept", "submit", "purchase", "buy", "click" },
        /* Cancel  */ new[] { "cancel", "back", "decline", "deny", "close" },
        /* Notify  */ new[] { "notification", "notify", "alert", "newitem", "unlock", "popup", "pedia" },
        /* Success */ new[] { "success", "complete", "reward", "unlock", "purchase", "positive", "confirm", "openpopup", "buttonclick" },
        /* Fail    */ new[] { "fail", "error", "invalid", "denied", "negative", "cannot", "reject", "cancel", "closepopup", "buttonfocused" },
        /* Whoosh  */ new[] { "whoosh", "swoosh", "swipe", "slide", "transition", "open" },
    };

    /// <summary>Higher is a better match; -1 for none. Prefers names that also mention button/menu. <paramref name="name"/> must be lower case.</summary>
    public static int Score(string name, string[] words)
    {
        var best = -1;
        for (var i = 0; i < words.Length; i++)
        {
            if (!name.Contains(words[i]))
                continue;
            var score = 100 - i * 10;
            if (name.Contains("button")) score += 5;
            if (name.Contains("menu")) score += 3;
            if (name.Contains("pause") || name.Contains("tip") || name.Contains("lab") || name.Contains("gadget")) score -= 4;
            best = System.Math.Max(best, score);
        }

        return best;
    }

    /// <summary>The best key for a cue index among the given keys (case-insensitive), or null.</summary>
    public static string? BestKey(int cueIndex, System.Collections.Generic.IEnumerable<string> keys)
    {
        string? bestKey = null;
        var best = -1;
        foreach (var key in keys)
        {
            var s = Score(key.ToLowerInvariant(), Words[cueIndex]);
            if (s > best) { best = s; bestKey = key; }
        }

        return bestKey;
    }
}

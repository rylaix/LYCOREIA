// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;

namespace Lycoreia.Text
{
    public static class LayoutText
    {
        /// <summary>Average glyph advance as a fraction of the font size (Cyrillic and mixed text are wider than plain Latin).</summary>
        public const float AvgGlyphEm = 0.56f;
        public const float LineHeightEm = 1.22f;

        /// <summary>The text without TMP rich-text tags (&lt;b&gt;, &lt;size=80%&gt;, &lt;alpha=#AA&gt; ...), which take no room.</summary>
        public static string StripTags(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var sb = new System.Text.StringBuilder(text.Length);
            bool inTag = false;
            foreach (var c in text)
            {
                if (c == '<') inTag = true;
                else if (c == '>' && inTag) inTag = false;
                else if (!inTag) sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>How many lines the text needs at this width and font size, counting explicit line breaks and greedy word wrap.</summary>
        public static int EstimateLines(string text, float width, float fontSize)
        {
            if (fontSize <= 0f || width <= 0f) return 1;
            int perLine = Math.Max(4, (int)(width / (fontSize * AvgGlyphEm)));
            int lines = 0;
            foreach (var para in StripTags(text).Split('\n'))
            {
                int used = 0, paraLines = 1;
                foreach (var word in para.Split(' '))
                {
                    int w = word.Length;
                    if (used == 0) { used = w; while (used > perLine) { paraLines++; used -= perLine; } }
                    else if (used + 1 + w <= perLine) used += 1 + w;
                    else { paraLines++; used = w; while (used > perLine) { paraLines++; used -= perLine; } }
                }
                lines += paraLines;
            }
            return Math.Max(1, lines);
        }

        /// <summary>Box height for a wrapped text row: its lines plus a little padding, never below minHeight.</summary>
        public static float RowHeight(string text, float width, float fontSize, float minHeight = 0f)
        {
            int lines = EstimateLines(text, width, fontSize);
            return Math.Max(minHeight, lines * fontSize * LineHeightEm + 10f);
        }

        /// <summary>True when a button label should stay on one line (shrunk to fit): short text with no line break.</summary>
        public static bool IsSingleLineLabel(string text)
        {
            var plain = StripTags(text);
            return plain.Length <= 40 && plain.IndexOf('\n') < 0;
        }
    }
}

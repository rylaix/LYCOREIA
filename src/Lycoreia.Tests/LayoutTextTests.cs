// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Lycoreia.Text;

namespace Lycoreia.Tests
{
    static class LayoutTextTests
    {
        public static void Register()
        {
            T.Test("layout text/tags take no room", () =>
            {
                T.Eq("abc def", LayoutText.StripTags("<b>abc</b> <size=80%>def</size>"), "stripped");
                T.Eq("", LayoutText.StripTags(null!), "null");
            });
            T.Test("layout text/short fits one line, long wraps, breaks count", () =>
            {
                T.Eq(1, LayoutText.EstimateLines("Hello", 860f, 26f), "short");
                T.True(LayoutText.EstimateLines(new string('x', 200), 860f, 26f) >= 3, "very long single word wraps");
                T.Eq(2, LayoutText.EstimateLines("a\nb", 860f, 26f), "explicit break");
                T.True(LayoutText.EstimateLines("слово слово слово слово слово слово слово слово слово слово слово слово слово слово слово слово слово слово слово слово слово слово", 860f, 26f) >= 2, "russian wraps");
            });
            T.Test("layout text/row height grows with lines and honours the minimum", () =>
            {
                float one = LayoutText.RowHeight("Hello", 860f, 26f, 36f), two = LayoutText.RowHeight(new string('x', 120) + " " + new string('y', 120), 860f, 26f, 36f);
                T.True(one >= 36f, "min"); T.True(two > one, "taller when wrapped");
            });
            T.Test("layout text/button labels: short one-liners shrink, long or multi-line wrap", () =>
            {
                T.True(LayoutText.IsSingleLineLabel("Новое в совместной игре"), "tab");
                T.True(!LayoutText.IsSingleLineLabel("a\nb"), "multi-line");
                T.True(!LayoutText.IsSingleLineLabel(new string('x', 60)), "long");
            });
        }
    }
}

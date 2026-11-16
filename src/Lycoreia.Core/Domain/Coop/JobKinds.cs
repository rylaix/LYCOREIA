// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;

namespace Lycoreia.Coop
{
    public static class JobKinds
    {
        public static readonly string[] All = { "corral", "coop", "garden", "silo", "pond", "incinerator", "plot", "gordo", "spot", "generic" };

        /// <summary>The kind of a job from its anchor ("plot:ID", "gordo:KEY", or none) and its text (the wheel's ready-made jobs name the place).</summary>
        public static string Of(string anchor, string text, bool hasPosition)
        {
            anchor = anchor ?? ""; var t = (text ?? "").ToLowerInvariant();
            if (anchor.StartsWith("gordo:", StringComparison.Ordinal) || t.Contains("gordo")) return "gordo";
            if (t.Contains("silo")) return "silo";
            if (t.Contains("pond")) return "pond";
            if (t.Contains("incinerat") || t.Contains("burn") || t.Contains("ash")) return "incinerator";
            if (t.Contains("chicken") || t.Contains("coop")) return "coop";
            if (t.Contains("garden") || t.Contains("harvest") || t.Contains("plant") || t.Contains("crop")) return "garden";
            if (t.Contains("corral")) return "corral";
            if (anchor.StartsWith("plot:", StringComparison.Ordinal)) return "plot";
            return hasPosition ? "spot" : "generic";
        }
    }
}

// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
namespace Lycoreia.World
{
    public static class HazardClockRules
    {
        // Local cooldowns were established against the guest save's clock. A host
        // correction backwards must preserve remaining cooldown, not years of save time.
        public static double RebaseDeadline(double deadline, double before, double after)
            => double.IsFinite(deadline) && double.IsFinite(before) && double.IsFinite(after) && after < before && deadline > after
                ? after + Math.Max(0, deadline - before) : deadline;
    }
}

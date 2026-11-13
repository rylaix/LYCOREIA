// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Lycoreia.Actors;
using Lycoreia.Net;
using Lycoreia.Net.Messages;

namespace Lycoreia.Tests
{
    static class Issue34Tests
    {
        public static void Register()
        {
            T.Test("ringtail: only the owner of a tracked actor runs the stone/release transformation", () =>
            {
                T.True(StatueRules.MayTransform(false, true, false, false), "offline: native");
                T.True(StatueRules.MayTransform(true, true, true, false), "owner runs it");
                T.False(StatueRules.MayTransform(true, true, false, true), "replica never runs it, whatever the fallback says");
                T.True(StatueRules.MayTransform(true, false, false, true), "untracked: generic authority");
                T.False(StatueRules.MayTransform(true, false, false, false), "untracked while applying: no");
            });

            T.Test("ringtail: statue links map statue to slime both ways and forget cleanly", () =>
            {
                var t = new StatueLinkTable();
                t.Note(10, 7); T.True(t.TryGet(10, out var s) && s == 7); T.True(t.IsStone(7));
                t.Note(11, 7); T.False(t.TryGet(10, out _), "a slime has one statue: the old link is replaced"); T.True(t.IsStone(7));
                t.Forget(10); T.True(t.IsStone(7), "forgetting a stale statue keeps the current one");
                t.Forget(11); T.False(t.IsStone(7)); T.Eq(0, t.Count);
                t.Note(0, 5); t.Note(5, 0); T.Eq(0, t.Count, "zero ids ignored");
                for (int i = 1; i <= StatueLinkTable.Cap + 5; i++) t.Note(i, i + 100000);
                T.True(t.Count <= StatueLinkTable.Cap, "bounded");
            });

            T.Test("slime eat: food owned by another seat is never eaten (its destroy would be suppressed)", () =>
            {
                T.True(FoodAuthority.MayEat(false, true, false), "offline");
                T.True(FoodAuthority.MayEat(true, true, true), "own food");
                T.False(FoodAuthority.MayEat(true, true, false), "foreign food");
                T.True(FoodAuthority.MayEat(true, false, false), "untracked local food");
                T.Eq(FoodAuthority.Verdict.EatHostConsumes, FoodAuthority.Decide(true, true, true, false, false), "host slime, guest food: host consumes it for everyone");
                T.Eq(FoodAuthority.Verdict.Skip, FoodAuthority.Decide(true, true, true, false, true), "never while a vacuum capture holds it");
                T.Eq(FoodAuthority.Verdict.Skip, FoodAuthority.Decide(true, false, true, false, false), "guest slime, host food: wait for ownership");
                T.Eq(FoodAuthority.Verdict.Eat, FoodAuthority.Decide(true, false, true, true, false));
            });

            T.Test("plort scale: only a plort above its prefab scale is corrected; produce scale needs a resource cycle", () =>
            {
                T.False(PlortScaleRule.IsOversized(0.5f, 0.5f)); T.False(PlortScaleRule.IsOversized(0.01f, 0.5f), "spawn tween starts at 0.01");
                T.False(PlortScaleRule.IsOversized(0.6f, 0.5f), "within tolerance"); T.True(PlortScaleRule.IsOversized(1.0f, 0.5f), "slime-sized");
                T.False(PlortScaleRule.IsOversized(float.NaN, 0.5f)); T.False(PlortScaleRule.IsOversized(5f, 0f), "unknown prefab scale");
                T.True(ProduceRules.ScaleApplies(true, 1f)); T.False(ProduceRules.ScaleApplies(false, 1f), "plort/slime under a produce id");
                T.False(ProduceRules.ScaleApplies(true, 0f)); T.False(ProduceRules.ScaleApplies(true, float.PositiveInfinity));
            });

            T.Test("world exit: main menu after a loaded world ends the session once; a menu-started server is untouched", () =>
            {
                var w = new WorldExitTracker();
                w.Observe(true, false); T.False(w.ShouldEndSession(true, "MainMenuUI"), "server started in the menu, no world yet");
                w.Observe(true, true); T.False(w.ShouldEndSession(true, "zoneFields"), "other scenes do not count");
                T.True(w.ShouldEndSession(true, "MainMenuUI")); T.False(w.ShouldEndSession(true, "MainMenuEnvironment"), "once per exit");
                w.Observe(true, true); w.Observe(false, false); T.False(w.ShouldEndSession(false, "MainMenuUI"), "session already over");
                w.Observe(true, true); T.False(w.ShouldEndSession(false, "MainMenuUI"), "not networked");
            });

            T.Test("census: type growth line lists the largest types and what grew", () =>
            {
                var before = new System.Collections.Generic.Dictionary<string, int> { ["Chicken"] = 10, ["PinkPlort"] = 50 };
                var now = new System.Collections.Generic.Dictionary<string, int> { ["Chicken"] = 90, ["PinkPlort"] = 52, ["Ringtail"] = 3 };
                T.Eq("top Chicken=90, PinkPlort=52", TypeGrowth.Describe(now, null, 2), "first line");
                T.Eq("top Chicken=90(+80), PinkPlort=52(+2), Ringtail=3(+3); grew Chicken +80", TypeGrowth.Describe(now, before, 5));
            });

            T.Test("statue link message round-trips", () =>
            {
                var m = new StatueLink { StatueId = 4518100001385, SlimeId = 52940001388, Stone = true };
                var w = new NetWriter(); m.Describe(new Codec(w)); var bytes = w.Span.ToArray();
                var back = new StatueLink(); var rd = NetReader.Rent(bytes, 0, bytes.Length); back.Describe(new Codec(rd)); rd.Return();
                T.Eq(m.StatueId, back.StatueId); T.Eq(m.SlimeId, back.SlimeId); T.Eq(true, back.Stone);
            });
        }
    }
}

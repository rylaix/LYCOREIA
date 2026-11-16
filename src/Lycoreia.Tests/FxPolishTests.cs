// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
// 2.1.0 native polish: pure logic of the boost squash curve and native-asset name scoring.
using Lycoreia.Game.Avatars;
using Lycoreia.Owned.Fx;

namespace Lycoreia.Tests
{
    static class FxPolishTests
    {
        public static void Register()
        {
            T.Test("fx/squash curve starts and ends exactly at rest", () =>
            {
                T.True(SquashCurve.Y(0f) == 1f);
                T.True(SquashCurve.Y(SquashCurve.Duration) == 1f);
                T.True(SquashCurve.Y(SquashCurve.Duration + 5f) == 1f);
                T.True(SquashCurve.Xz(SquashCurve.Duration) == 1f);
            });
            T.Test("fx/squash curve presses down then stays within a small band", () =>
            {
                T.True(SquashCurve.Y(SquashCurve.Press) < 1f - SquashCurve.Depth + 0.001f);
                for (float t = 0f; t <= SquashCurve.Duration; t += 0.005f)
                {
                    float y = SquashCurve.Y(t);
                    T.True(y > 0.7f && y < 1.15f);
                    T.True(SquashCurve.Xz(t) > 0.9f && SquashCurve.Xz(t) < 1.2f);
                }
            });
            T.Test("fx/name scoring prefers earlier words, penalises avoided names, rejects non-matches", () =>
            {
                var want = new[] { "jump", "jetpack" };
                var avoid = new[] { "slime" };
                T.True(FxNames.Score("PlayerJump", want, avoid) > FxNames.Score("JetpackLoop", want, avoid));
                T.True(FxNames.Score("SlimeJump", want, avoid) < FxNames.Score("PlayerJump", want, avoid));
                T.True(FxNames.Score("Click", want, avoid) == -1);
                T.True(FxNames.Score(null, want, avoid) == -1);
            });
        }
    }
}

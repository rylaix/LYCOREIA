// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using Lycoreia.Net;
using Lycoreia.Players;

namespace Lycoreia.Tests
{
    static class StageCTests
    {
        public static void Register()
        {
            T.Test("avatar/smoother interpolates one period behind, extrapolates past the newest, blends and snaps", () =>
            {
                var s = new AvatarSmoother(); T.False(s.HasState);
                s.Push(0.00, new Vec3(0, 0, 0), 0, 0); s.Push(0.05, new Vec3(1, 0, 0), 10, 0);
                var (p0, y0, _) = s.Target(0.05); T.True(Math.Abs(p0.X - 1f) < 1e-4f, "at the newest " + p0.X);
                var (p1, _, _) = s.Target(0.075); T.True(Math.Abs(p1.X - 1.5f) < 1e-3f, "extrapolated unclamped " + p1.X);
                for (int i = 0; i < 60; i++) s.Step(0.05, 1f / 60f);
                T.True(Math.Abs(s.Position.X - 1f) < 0.01f, "blended to target " + s.Position.X);
                s.Push(0.10, new Vec3(10, 0, 0), 10, 0); s.Step(0.10, 0.001f); T.True(s.Position.X > 9.9f, "more than 3 m off snaps");
                s.Push(0.15, new Vec3(float.NaN, 0, 0), 0, 0); s.Step(0.15, 0.01f); T.True(float.IsFinite(s.Position.X), "non-finite ignored");
            });
            T.Test("avatar/angles take the shortest arc", () =>
            {
                T.Eq(20f, AvatarSmoother.DeltaAngle(350, 10)); T.Eq(-20f, AvatarSmoother.DeltaAngle(10, 350)); T.Eq(180f, AvatarSmoother.DeltaAngle(0, 180));
                var s = new AvatarSmoother(); s.Push(0, Vec3.Zero, 350, 0); s.Push(0.05, Vec3.Zero, 10, 0);
                for (int i = 0; i < 30; i++) s.Step(0.05, 1f / 60f);
                T.True(s.Yaw < 15 || s.Yaw > 355, "went through 0, not around " + s.Yaw);
            });
            T.Test("avatar/visibility needs the same known scene group", () =>
            {
                T.True(AvatarRules.Visible(3, 3)); T.False(AvatarRules.Visible(3, 4)); T.False(AvatarRules.Visible(-1, -1), "unknown hides"); T.False(AvatarRules.Visible(-1, 3));
            });
            T.Test("avatar/nametag: smaller up close, constant screen size from 10 m growing to ~1.35x at 80 m, peak 1.7x, shrinks past 120 m", () =>
            {
                T.True(AvatarRules.NametagScale(2) >= AvatarRules.MinWorldScale && AvatarRules.NametagScale(2) < AvatarRules.NametagScale(9), "smaller up close");
                T.True(Math.Abs(AvatarRules.ScreenFactor(10) - 1f) < 1e-3f); T.True(Math.Abs(AvatarRules.ScreenFactor(80) - 1.35f) < 1e-3f);
                float peak = 0; for (float d = 10; d < 400; d += 1) peak = Math.Max(peak, AvatarRules.ScreenFactor(d));
                T.True(peak <= 1.7f + 1e-3f && peak >= 1.69f, "peak " + peak); T.True(AvatarRules.ScreenFactor(200) < AvatarRules.ScreenFactor(120), "shrinks past 120 m");
                T.True(AvatarRules.NametagScale(float.NaN) == AvatarRules.MinWorldScale);
            });
        }
    }
}

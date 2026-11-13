// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using Lycoreia.Net;
using Lycoreia.Players;

namespace Lycoreia.Tests
{
    static partial class Release21Tests
    {
        public static void Register()
        {
            T.Test("avatar: a turn never extrapolates past the newest sample (no spin during a packet gap)", () =>
            {
                var s = new AvatarSmoother();
                s.Push(0.00, new Vec3(0, 0, 0), 0, 0); s.Push(0.05, new Vec3(0, 0, 0), 30, 0);
                var (_, yaw, _) = s.Target(1.0);                                            // 19 periods after the newest
                T.True(Math.Abs(AvatarSmoother.DeltaAngle(yaw, 30)) < 0.01f, "held at the newest yaw " + yaw);
                for (int i = 0; i < 120; i++) s.Step(0.05 + i / 60.0, 1f / 60f);
                T.True(Math.Abs(AvatarSmoother.DeltaAngle(s.Yaw, 30)) < 0.5f, "shown yaw settles, never spins " + s.Yaw);
            });
            RegisterFeatures();
        }

        static partial void RegisterFeatures();
    }
}

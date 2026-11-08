// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Lycoreia.Actors;

namespace Lycoreia.Tests
{
    static class PauseTests
    {
        public static void Register()
        {
            T.Test("pause/keep simulating only for a networked, non-loading session", () =>
            {
                T.False(PausePolicy.KeepSimulating(false, 0, false));
                T.False(PausePolicy.KeepSimulating(true, 0, false)); // native pause semantics
                T.False(PausePolicy.KeepSimulating(true, 1, false));
                T.False(PausePolicy.KeepSimulating(true, 0, true));
            });
            T.Test("pause/cursor allowed from native pause or zero delta time, never against gamepad", () =>
            {
                T.True(PausePolicy.CursorAllowed(true, 0.016f, true));
                T.False(PausePolicy.CursorAllowed(false, 0.016f, true));
                T.True(PausePolicy.CursorAllowed(false, 0f, true));
                T.False(PausePolicy.CursorAllowed(true, 0f, false));
            });
        }
    }
}

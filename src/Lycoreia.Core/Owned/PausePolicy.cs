// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Actors
{
    public static class PausePolicy
    {
        // Menus pause natively (input maps, Esc pairing); during a live session only world time is kept running.
        // Forcing timeScale back to 1 behind native menus broke native UI flows (Slimepedia tablet never closed, Esc no longer
        // closed the pause menu: the game's pause logic keys on timeScale). Keep native pause semantics; flip only with proof.
#if DEEPLOG
        // Deeplog A/B repro only: LYCOREIA_KEEP_SIM=1 keeps the world simulating behind menus. Release builds keep the const false.
        public static readonly bool MenusKeepSimulating = System.Environment.GetEnvironmentVariable("LYCOREIA_KEEP_SIM") == "1";
#else
        public const bool MenusKeepSimulating = false;
#endif
        public static bool KeepSimulating(bool networked, int loaderDepth, bool loading) => MenusKeepSimulating && networked && loaderDepth <= 0 && !loading;
        public static bool CursorAllowed(bool nativePaused, float deltaTime, bool usingGamepadAllowsCursor) => (nativePaused || deltaTime == 0f) && usingGamepadAllowsCursor;
    }
}

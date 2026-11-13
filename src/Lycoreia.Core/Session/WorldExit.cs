// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia
{
    /// <summary>
    /// A seat that had a loaded world during this session and then reaches the main menu has left the world: the session ends.
    /// Before 2026-11-12 a host that quit to the main menu kept serving: 60 s later it answered a guest resync from the menu (no
    /// GameModel, Sanctuary step NRE, stale actor table of 4307 records). A server started from the main menu before any world
    /// was loaded (host page, direct launch) is not affected: it never had a world yet.
    /// </summary>
    public sealed class WorldExitTracker
    {
        bool _hadWorld;
        public bool HadWorld => _hadWorld;
        /// <summary>Called every frame: a running session with a loaded world.</summary>
        public void Observe(bool networkActive, bool worldLoaded) { if (networkActive && worldLoaded) _hadWorld = true; else if (!networkActive) _hadWorld = false; }
        public void Reset() => _hadWorld = false;
        public static bool IsMainMenuScene(string scene) => scene == "MainMenuUI" || scene == "MainMenuEnvironment";
        /// <summary>True once per exit: the caller ends the session.</summary>
        public bool ShouldEndSession(bool networkActive, string loadedScene)
        {
            if (!networkActive || !_hadWorld || !IsMainMenuScene(loadedScene)) return false;
            _hadWorld = false; return true;
        }
    }
}

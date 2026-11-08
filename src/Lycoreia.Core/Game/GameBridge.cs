// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.DataModel;

namespace Lycoreia.Game
{
    /// <summary>Shortcut to the scene's game model. Null-safe: patches can fire during the first scene load.</summary>
    public static class GameState
    {
        public static GameModel Model => SceneContext.Instance != null ? SceneContext.Instance.GameModel : null;
        public static bool Ready => Model != null;
        public static bool Loading => SystemContext.Instance?.SceneLoader?.IsSceneLoadInProgress == true;
        public static Il2CppSystem.Collections.Generic.Dictionary<string, LandPlotModel> LandPlots => Model?.landPlots;
    }

    /// <summary>Starts coroutines on the MelonLoader runner; failures are logged, never thrown into native code.</summary>
    public static class Coroutines
    {
        public static object Start(IEnumerator routine)
        {
            try { return MelonLoader.MelonCoroutines.Start(routine); }
            catch (Exception e) { Log.Error("could not start coroutine", e); return null; }
        }
        public static void Stop(object handle) { try { if (handle != null) MelonLoader.MelonCoroutines.Stop(handle); } catch { } }
    }
}

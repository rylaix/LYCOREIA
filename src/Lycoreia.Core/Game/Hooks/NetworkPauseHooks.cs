// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using HarmonyLib;
using Il2Cpp;
using System;
using Il2CppMonomiPark.SlimeRancher.SceneManagement;
using Il2CppMonomiPark.SlimeRancher.UI.Pedia;
using Il2CppMonomiPark.SlimeRancher.UI;
using Il2CppMonomiPark.SlimeRancher.UI.Framework.Displays;
using Il2CppMonomiPark.SlimeRancher.UI.Map;
using Lycoreia.Actors;

namespace Lycoreia.Game.Hooks
{
    public static class GameMenus
    {
        // True while any native pauser (menu, map, pedia, sleep, loader) holds TimeDirector.
        public static bool NativePaused => SceneContext.Instance?.TimeDirector?.HasPauser() == true;
    }

    // Native pause/unpause, Esc and input maps run untouched. In a live session only the time-scale
    // zero that Pause() applies is undone, so the world keeps simulating behind open menus.
    [HarmonyPatch(typeof(TimeDirector), nameof(TimeDirector.Pause))]
    static class NetworkKeepSimulatingHook
    {
        static void Postfix(TimeDirector __instance)
        {
            try
            {
                if (!PausePolicy.KeepSimulating(Mod.IsNetworkActive, NetworkLoadingPause.Depth, GameState.Loading)) return;
                if (UnityEngine.Time.timeScale != 0f) return;
                float scale = __instance._savedTimeScale;
                if (float.IsNaN(scale) || float.IsInfinity(scale) || scale < 1f) scale = 1f;
                UnityEngine.Time.timeScale = scale;
                Mod.Diagnostics?.Record("player", "network-pause", $"pausers={__instance._pauserCount}; restored-scale={scale:0.00}; top={UIDisplayManager.Instance?._lastState?.TopFocus?.Name}");
            }
            catch (Exception e) { HookGuard.Fail("network-keep-simulating", e); }
        }
    }

    static class NetworkMenuInput
    {
        static bool? _lastLoading;
        static double _nextLoadReport;
        static string _lastState;
        static void TraceLoading()
        {
            bool loading = GameState.Loading;
            double now = UnityEngine.Time.unscaledTimeAsDouble;
            if (_lastLoading == loading && (!loading || now < _nextLoadReport)) return;
            _lastLoading = loading; _nextLoadReport = now + 10;
            var loader = SystemContext.Instance?.SceneLoader;
            Mod.Diagnostics?.Record("scene", "network-loading-state", $"loading={loading}; scene={loader?.CurrentSceneGroup?.ReferenceId}; processing={loader?._isProcessingQueue}; actions={loader?._loadActions?.Count}; errors={loader?.HasAnyLoadErrors}; time-scale={UnityEngine.Time.timeScale}; pausers={SceneContext.Instance?.TimeDirector?._pauserCount}; physics={UnityEngine.Physics.autoSimulation}; target-fps={UnityEngine.Application.targetFrameRate}; render-interval={UnityEngine.Rendering.OnDemandRendering.renderFrameInterval}");
        }

        // Diagnostics only: no state is written here.
        public static void Pump()
        {
            if (!Mod.IsNetworkActive) { _lastLoading = null; _lastState = null; return; }
            try
            {
                TraceLoading();
                // A pause taken before the session started (hosting from the pause menu) zeroed the clock without
                // a later 0->1 transition: release it too. Never inside the loader's pause or its pre-flag window.
                var td = SceneContext.Instance?.TimeDirector;
                double now = UnityEngine.Time.unscaledTimeAsDouble;
                if (td != null && UnityEngine.Time.timeScale == 0f && td.HasPauser() && PausePolicy.KeepSimulating(true, NetworkLoadingPause.Depth, GameState.Loading)
                    && now - NetworkLoadingPause.LastRequestAt > LoadingHazardPolicy.PreLoadWindowSeconds)
                {
                    float scale = td._savedTimeScale; if (float.IsNaN(scale) || float.IsInfinity(scale) || scale < 1f) scale = 1f;
                    UnityEngine.Time.timeScale = scale;
                    Mod.Diagnostics?.Record("player", "network-pause-released", $"pausers={td._pauserCount}; restored-scale={scale:0.00}; top={UIDisplayManager.Instance?._lastState?.TopFocus?.Name}");
                }
                var input = GameContext.Instance?.InputDirector;
                int pausers = SceneContext.Instance?.TimeDirector?._pauserCount ?? -1;
                var top = UIDisplayManager.Instance?._lastState?.TopFocus?.Name;
                var main = input?._mainGame?.Map?.enabled; var paused = input?._paused?.Map?.enabled;
                var key = $"{pausers}|{top}|{main}|{paused}";
                if (key == _lastState) return;
                _lastState = key;
                Mod.Diagnostics?.Record("player", "network-menu-state", $"pausers={pausers}; time-scale={UnityEngine.Time.timeScale:0.00}; main={main}; paused={paused}; top={top}");
            }
            catch (Exception e) { HookGuard.Fail("network-menu-input", e); }
        }
    }

    // SceneLoader requests its pause before its public loading flag is necessarily set.
    // Its real native pause/paired unpause must survive network menu suppression.
    [HarmonyPatch(typeof(SceneLoader), nameof(SceneLoader.TryPauseGame))]
    static class NetworkLoadingPause
    {
        [ThreadStatic] internal static int Depth;
        internal static double LastRequestAt = double.NegativeInfinity;
        static void Prefix()
        {
            LastRequestAt = UnityEngine.Time.unscaledTimeAsDouble;
            Depth++;
            try { ActorRuntime.ReleaseForLoading(); }
            catch (Exception e) { HookGuard.Fail("loading-simulator-release", e); }
        }
        static Exception Finalizer(Exception __exception) { if (Depth > 0) Depth--; return __exception; }
    }

    [HarmonyPatch(typeof(CursorLockHandler), nameof(CursorLockHandler.CheckCanEnableCursor))]
    static class NetworkMenuCursorHook
    {
        static bool Prefix(ref bool __result)
        {
            if (!Mod.IsNetworkActive) return true;
            try
            {
                __result = PausePolicy.CursorAllowed(GameMenus.NativePaused, UnityEngine.Time.deltaTime, CursorLockHandler.CanEnableCursorForInput());
                return false;
            }
            catch (Exception e) { HookGuard.Fail("network-menu-cursor", e); return true; }
        }
    }

    static class MapDiagnostics
    {
        internal static void Record(string kind) =>
            Mod.Diagnostics?.Record("player", kind, $"pausers={SceneContext.Instance?.TimeDirector?._pauserCount}; top={UIDisplayManager.Instance?._lastState?.TopFocus?.Name}");
    }

    [HarmonyPatch(typeof(MapUI), "Start")]
    static class MapOpenDiagnosticHook
    {
        static void Postfix() { try { MapDiagnostics.Record("map-open"); } catch (Exception e) { HookGuard.Fail("map-open-diag", e); } }
    }

    [HarmonyPatch(typeof(MapUI), "OnDestroy")]
    static class MapCloseDiagnosticHook
    {
        static void Prefix() { try { MapDiagnostics.Record("map-close"); } catch (Exception e) { HookGuard.Fail("map-close-diag", e); } }
    }
}

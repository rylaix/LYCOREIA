// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;

namespace Lycoreia.Owned.Components.UI;

internal static class InputGate
{
    private static bool blocked, mainWas, pausedWas, moveWas, submitWas, cancelWas, nativePausedWas;
    private static InputSystemUIInputModule? module;

    internal static bool Blocked => blocked;

    private static readonly HashSet<string> owners = new();

    /// <summary>Each owner (menu typing, co-op wheel, note box) says whether it needs the keys; the gate is closed while any does.</summary>
    internal static void Set(string owner, bool on)
    {
        if (on) owners.Add(owner); else owners.Remove(owner);
        if (owners.Count > 0) Block(); else Unblock();
    }
    internal static void Typing(bool menu, bool chat) => Set("menu", menu || chat);
    /// <summary>Owners that do not call Set every frame call this: the pause switch can re-enable a map while we hold the keys.</summary>
    internal static void Reassert() { if (owners.Count > 0) Block(); }
    /// <summary>Drops every owner (scene change, session end): input never stays disabled.</summary>
    internal static void ReleaseAll() { owners.Clear(); Unblock(); }

    private static void Block()
    {
        try
        {
            var d = GameContext.Instance ? GameContext.Instance.InputDirector : null;
            var main = d?._mainGame?.Map; var paused = d?._paused?.Map;
            if (!blocked)
            {
                mainWas = main != null && main.enabled; pausedWas = paused != null && paused.enabled; nativePausedWas = Lycoreia.Game.Hooks.GameMenus.NativePaused;
                module = CurrentModule();
                moveWas = Enabled(module?.move); submitWas = Enabled(module?.submit); cancelWas = Enabled(module?.cancel);
                blocked = true;
                OwnedLog.LogDiagnostic("menu", "input-gate", $"closed; main={mainWas}; paused={pausedWas}; ui-keys={moveWas}/{submitWas}/{cancelWas}");
            }
            // re-assert every frame: the native pause switch or InputDirector may re-enable a map
            if (main != null && main.enabled) main.Disable();
            if (paused != null && paused.enabled) paused.Disable();
            if (!module) { module = CurrentModule(); moveWas |= Enabled(module?.move); submitWas |= Enabled(module?.submit); cancelWas |= Enabled(module?.cancel); }   // new scene: new module
            if (module) { Disable(module!.move); Disable(module.submit); Disable(module.cancel); }
        }
        catch (Exception ex) { OwnedLog.LogDiagnostic("menu", "input-gate-failed", ex.Message, "menu:input-gate", 5000); }
    }

    private static void Unblock()
    {
        if (!blocked) return;
        blocked = false;
        try
        {
            var d = GameContext.Instance ? GameContext.Instance.InputDirector : null;
            // the pause state may have changed while we held the keys (e.g. Resume clicked): then follow the current state, not the snapshot
            bool pausedNow = Lycoreia.Game.Hooks.GameMenus.NativePaused;
            bool wantMain = pausedNow == nativePausedWas ? mainWas : !pausedNow, wantPaused = pausedNow == nativePausedWas ? pausedWas : pausedNow;
            if (wantMain) d?._mainGame?.Map?.Enable();
            if (wantPaused) d?._paused?.Map?.Enable();
            if (module)
            {
                if (moveWas) module!.move?.action?.Enable();
                if (submitWas) module!.submit?.action?.Enable();
                if (cancelWas) module!.cancel?.action?.Enable();
            }
            module = null;
            OwnedLog.LogDiagnostic("menu", "input-gate", "opened");
        }
        catch (Exception ex) { OwnedLog.LogDiagnostic("menu", "input-gate-failed", ex.Message, "menu:input-gate", 5000); }
    }

    private static InputSystemUIInputModule? CurrentModule()
        => EventSystem.current ? EventSystem.current.currentInputModule?.TryCast<InputSystemUIInputModule>() : null;
    private static bool Enabled(InputActionReference? r) { try { return r != null && r.action != null && r.action.enabled; } catch { return false; } }
    private static void Disable(InputActionReference? r) { try { if (r != null && r.action != null && r.action.enabled) r.action.Disable(); } catch { } }
}

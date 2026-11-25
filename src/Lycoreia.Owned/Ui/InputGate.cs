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

    private static readonly InputGateState state = new();
    private static bool pointWas, clickWas, scrollWas;

    /// <summary>Each owner (menu typing, co-op wheel, note box) says whether it needs the keys; the gate is closed while any does.</summary>
    internal static void Set(string owner, bool on)
    {
        state.Set(owner, on);
        if (state.Blocked) Block(); else Unblock();
    }
    internal static void Typing(bool menu, bool chat) => Set("menu", menu || chat);
    /// <summary>Owners that do not call Set every frame call this: the pause switch can re-enable a map while we hold the keys.</summary>
    internal static void Reassert() { if (state.Blocked) Block(); }
    /// <summary>Drops every owner (scene change, session end): input never stays disabled.</summary>
    internal static void ReleaseAll() { state.ReleaseAll(); Unblock(); }

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
                pointWas = Enabled(module?.point); clickWas = Enabled(module?.leftClick); scrollWas = Enabled(module?.scrollWheel);
                blocked = true;
                OwnedLog.LogDiagnostic("menu", "input-gate", $"closed; owners={state.Owners}; main={mainWas}; paused={pausedWas}; ui-keys={moveWas}/{submitWas}/{cancelWas}; pointer={pointWas}/{clickWas}/{scrollWas}");
            }
            // re-assert every frame: the native pause switch or InputDirector may re-enable a map
            // A map reads as enabled while ANY of its actions is on: after KeepPointer switched point/click/scroll back on, the UI map looked
            // enabled again every frame, was disabled again, repaired again (rc3 log: ~3900 repairs per 40 s, per-frame Enable/Disable churn).
            // Only a map with some other action running counts as re-enabled by the game.
            if (main != null && OtherEnabled(main)) main.Disable();
            if (paused != null && OtherEnabled(paused)) paused.Disable();
            if (!module) { module = CurrentModule(); moveWas |= Enabled(module?.move); submitWas |= Enabled(module?.submit); cancelWas |= Enabled(module?.cancel); }   // new scene: new module
            if (module) { Disable(module!.move); Disable(module.submit); Disable(module.cancel); }
            // The gate only holds KEYS. Disabling the maps above can take the UI map's pointer actions down with it (clicks dead while a text
            // field had focus, Esc still worked): point/click/scroll are switched straight back on.
            if (module) KeepPointer(module!, pointWas, clickWas, scrollWas, "gate");
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
            if (module) KeepPointer(module!, true, true, true, "gate-release");
            module = null;
            OwnedLog.LogDiagnostic("menu", "input-gate", "opened");
        }
        catch (Exception ex) { OwnedLog.LogDiagnostic("menu", "input-gate-failed", ex.Message, "menu:input-gate", 5000); }
    }

    /// <summary>Switches the module's pointer actions on again where they are off. Returns the names it had to repair (empty: all fine).</summary>
    private static string KeepPointer(InputSystemUIInputModule m, bool point, bool click, bool scroll, string why)
    {
        string fixedNames = "";
        try
        {
            if (point && Repair(m.point)) fixedNames += "point ";
            if (click && Repair(m.leftClick)) fixedNames += "leftClick ";
            if (scroll && Repair(m.scrollWheel)) fixedNames += "scroll ";
            if (fixedNames.Length > 0) OwnedLog.LogDiagnostic("menu", "pointer-actions-repaired", $"why={why}; actions={fixedNames.Trim()}", "menu:pointer-repair", 2000);
        }
        catch (Exception ex) { OwnedLog.LogDiagnostic("menu", "input-gate-failed", "pointer: " + ex.Message, "menu:input-gate", 5000); }
        return fixedNames.Trim();
    }
    private static bool OtherEnabled(InputActionMap map)
    {
        try
        {
            if (!map.enabled) return false;
            var skip = new string[3]; int n = 0;
            if (module) { foreach (var r in new[] { module!.point, module.leftClick, module.scrollWheel }) if (r?.action != null) skip[n++] = r.action.name; }
            var acts = map.actions;
            for (int i = 0; i < acts.Count; i++)
            {
                var a = acts[i];
                if (!a.enabled) continue;
                bool ours = false; for (int k = 0; k < n; k++) if (a.name == skip[k]) { ours = true; break; }
                if (!ours) return true;
            }
            return false;
        }
        catch { return map.enabled; }
    }
    private static bool Repair(InputActionReference? r) { if (r == null || r.action == null || r.action.enabled) return false; r.action.Enable(); return true; }

    /// <summary>Menu open / every frame while the Lycoreia menu is up: pointer actions must be on whatever state the native menu left them in.</summary>
    internal static string EnsurePointer(string why)
    {
        var m = CurrentModule();
        return m ? KeepPointer(m!, true, true, true, why) : "";
    }

    /// <summary>One line of facts about the pointer path for the menu-open diagnostic.</summary>
    internal static string PointerReport()
    {
        try
        {
            var es = EventSystem.current; var m = CurrentModule();
            return $"eventSystem={(es ? es!.name : "none")}; module={(m ? m!.GetType().Name + (m.enabled ? "" : "(disabled)") : (es && es!.currentInputModule ? es.currentInputModule.GetType().Name : "none"))}; "
                 + $"pointer={Enabled(m?.point)}/{Enabled(m?.leftClick)}/{Enabled(m?.scrollWheel)}; gate={(blocked ? "closed" : "open")}";
        }
        catch (Exception ex) { return "pointer report failed: " + ex.Message; }
    }

    private static InputSystemUIInputModule? CurrentModule()
        => EventSystem.current ? EventSystem.current.currentInputModule?.TryCast<InputSystemUIInputModule>() : null;
    private static bool Enabled(InputActionReference? r) { try { return r != null && r.action != null && r.action.enabled; } catch { return false; } }
    private static void Disable(InputActionReference? r) { try { if (r != null && r.action != null && r.action.enabled) r.action.Disable(); } catch { } }
}

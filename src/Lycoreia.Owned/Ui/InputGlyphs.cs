// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
// Key / button labels for hints, read from the game's own input bindings so rebinding and gamepads show correctly.
using UnityEngine.InputSystem;

namespace Lycoreia.Owned.Components.UI;

/// <summary>
/// Returns a TMP rich-text label for a key or button. Source: the game's InputDirector input actions (InputActionAsset.FindAction /
/// bindings, InputControlPath.ToHumanReadableString), so remapped keys and the active device are honoured. The native glyph sprites
/// (InputDeviceAssetIconMapping / IInputIcon) are not TMP sprite assets and are not used; the label is bracketed bold text, e.g.
/// "[E]" or "[MMB]". Falls back to "[arg]". Main thread only; never throws; logs the source once per input.
/// </summary>
internal static class InputGlyphs
{
    private static readonly System.Collections.Generic.Dictionary<string, string> alias = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["MMB"] = "<Mouse>/middleButton", ["LMB"] = "<Mouse>/leftButton", ["RMB"] = "<Mouse>/rightButton",
    };

    private static readonly System.Collections.Generic.HashSet<string> logged = new();

    /// <summary>True when the last device the player used is a gamepad (InputDirector.CurrentDevice).</summary>
    internal static bool UsingGamepad
    {
        get
        {
            try
            {
                var d = GameContext.Instance ? GameContext.Instance.InputDirector : null;
                var dev = d != null ? d.CurrentDevice : null;
                return dev != null && dev.TryCast<Gamepad>() != null;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Label for an input action name (e.g. "Interact"), a binding path ("&lt;Keyboard&gt;/e") or a short mouse name ("MMB").
    /// Always returns something printable.
    /// </summary>
    internal static string Label(string actionOrBinding)
    {
        if (string.IsNullOrWhiteSpace(actionOrBinding))
            return string.Empty;
        string text = null, source = "fallback";
        try
        {
            var path = alias.TryGetValue(actionOrBinding, out var p) ? p : actionOrBinding;
            if (path.Contains('/'))
            {
                text = InputControlPath.ToHumanReadableString(path, InputControlPath.HumanReadableStringOptions.OmitDevice, null);
                source = "path";
            }
            else
            {
                var asset = GameContext.Instance ? GameContext.Instance.InputDirector?._inputActions : null;
                if (asset)
                {
                    var pad = UsingGamepad;
                    string keyboard = null, gamepad = null;
                    var action = asset.FindAction(actionOrBinding, false);
                    var binds = action != null ? action.bindings : default;
                    for (var i = 0; action != null && i < binds.Count; i++)
                    {
                        var b = binds[i];
                        if (b.isComposite)
                            continue;
                        var ep = b.effectivePath;
                        if (string.IsNullOrEmpty(ep))
                            continue;
                        if (ep.StartsWith("<Gamepad>", System.StringComparison.OrdinalIgnoreCase)) gamepad ??= ep;
                        else if (ep.StartsWith("<Keyboard>", System.StringComparison.OrdinalIgnoreCase) || ep.StartsWith("<Mouse>", System.StringComparison.OrdinalIgnoreCase)) keyboard ??= ep;
                    }

                    var use = pad ? gamepad ?? keyboard : keyboard ?? gamepad;
                    if (use != null)
                    {
                        text = InputControlPath.ToHumanReadableString(use, InputControlPath.HumanReadableStringOptions.OmitDevice, null);
                        source = "action";
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            source = "fallback(" + ex.GetType().Name + ")";
        }

        if (string.IsNullOrEmpty(text))
            text = actionOrBinding;
        if (logged.Add(actionOrBinding))
            OwnedLog.LogMessage($"input glyph {actionOrBinding}: src={source}, text={text}");
        return "<b>[" + text.Replace("<", "").Replace(">", "") + "]</b>";
    }
}

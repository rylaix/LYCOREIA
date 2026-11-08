// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using HarmonyLib;
using MelonLoader;
using Il2CppMonomiPark.SlimeRancher.Player.CharacterController;

namespace Lycoreia.Owned.Features.WidescreenFov;

/// <summary>
/// Widescreen FOV fix: a self-contained Lycoreia module (single player and co-op; nothing here touches
/// networking). It doubles as a small example of how a feature can live inside Lycoreia on its own: one folder,
/// its own preferences category, two Harmony patches, one settings page (LycoreiaMenu).
///
/// Measured in the rig (2026-11-01, 21:9 window): the game feeds the FOV slider straight in as the camera's
/// VERTICAL FOV (slider step 5 = 70, step 11 = 100, 5 per step). 100 vertical is ~130 deg horizontal on 16:9
/// and ~140 deg on 21:9, so the world stretches badly while hands/vac (drawn with their own FOV) look fine.
/// Fix (what most PC games do): the slider is the HORIZONTAL FOV of a 16:9 screen. We keep that vertical FOV
/// and let wider screens add view at the sides (Hor+): 100 -> 67.7 deg vertical -> 100 deg on 16:9, 114 on 21:9.
///
/// Patched at the game's camera controller (SRCameraController.FieldOfView): the setter converts, the getter
/// returns the unconverted value, so the game's own FOV effects (dash, wobble) that read-modify-write the
/// property never convert twice.
///
/// An HDRP Panini "edge correction" was tried in rc.8/rc.9 and removed in rc.10: it magnifies the image centre
/// (visibly lower resolution) and pinches the middle of the screen (owner: worse nausea).
/// </summary>
internal static class WidescreenFov
{
    private const string Category = "Lycoreia_WidescreenFov";
    internal const float ReferenceAspect = 16f / 9f;

    private static MelonPreferences_Entry<bool>? enabledEntry;
    private static MelonPreferences_Entry<float>? fovEntry;

    internal const float MinFov = 60f;
    internal const float MaxFov = 150f;

    /// <summary>Horizontal FOV on a 16:9 screen that replaces the game's base FOV (the game's dash/wobble still add on top).</summary>
    internal static float Fov => Mathf.Clamp(fovEntry?.Value ?? 100f, MinFov, MaxFov);

    private static readonly Dictionary<IntPtr, (float Logical, float Applied)> State = new();
    private static string lastLogged = string.Empty;
    private static float baseline, lastValue, lastSetAt;
    private static bool baselineKnown;

    internal static bool Enabled => enabledEntry?.Value ?? true;

    private static bool warnedNoEntries, dirty;
    private static float dirtyAt;

    internal static void Initialize()
    {
        if (enabledEntry != null && fovEntry != null) return;
        var category = MelonPreferences.CreateCategory(Category, "Lycoreia: Widescreen FOV");
        enabledEntry = category.CreateEntry("enabled", true, "Widescreen FOV fix (slider = 16:9 horizontal FOV)");
        fovEntry = category.CreateEntry("fov", 100f, "Field of view (horizontal on 16:9, 60-150)");
    }

    private static bool EnsureEntries()
    {
        if (enabledEntry == null || fovEntry == null)
        {
            try { Initialize(); }
            catch (Exception ex) { OwnedLog.LogWarning($"[WidescreenFov] Initialize failed: {ex.Message}"); }
        }
        if (enabledEntry != null && fovEntry != null) return true;
        if (!warnedNoEntries) { warnedNoEntries = true; OwnedLog.LogWarning("[WidescreenFov] preferences unavailable; setting ignored"); }
        return false;
    }

    internal static void SetFov(float value)
    {
        if (!EnsureEntries()) return;
        fovEntry!.Value = Mathf.Clamp(Mathf.Round(value), MinFov, MaxFov);
        dirty = true;
        dirtyAt = UnityEngine.Time.unscaledTime;
        Reapply();
    }

    /// <summary>Debounced save: writes the preferences once, 0.5 s after the last slider change.</summary>
    internal static void Tick()
    {
        if (!dirty || UnityEngine.Time.unscaledTime - dirtyAt < 0.5f) return;
        dirty = false;
        MelonPreferences.Save();
    }

    private static void Reapply()
    {
        foreach (var controller in Object.FindObjectsOfType<SRCameraController>())
            if (controller)
                controller.FieldOfView = controller.FieldOfView; // getter = game's value, setter converts again
    }

    internal static void SetEnabled(bool value)
    {
        if (!EnsureEntries())
            return;
        // Re-apply the current value with the new mode.
        var logical = new List<(SRCameraController Controller, float Value)>();
        foreach (var controller in Object.FindObjectsOfType<SRCameraController>())
            if (controller)
                logical.Add((controller, controller.FieldOfView)); // getter returns the unconverted value
        enabledEntry!.Value = value;
        MelonPreferences.Save();
        foreach (var (controller, fov) in logical)
            controller.FieldOfView = fov;
    }

    /// <summary>Vertical FOV (deg) that shows <paramref name="horizontal"/> deg across a screen of this aspect.</summary>
    internal static float VerticalFromHorizontal(float horizontal, float aspect)
        => 2f * Mathf.Atan(Mathf.Tan(horizontal * 0.5f * Mathf.Deg2Rad) / aspect) * Mathf.Rad2Deg;

    internal static float HorizontalFromVertical(float vertical, float aspect)
        => 2f * Mathf.Atan(Mathf.Tan(vertical * 0.5f * Mathf.Deg2Rad) * aspect) * Mathf.Rad2Deg;

    /// <summary>Setter prefix: the game's value in, the camera's vertical FOV out.</summary>
    internal static float Convert(SRCameraController controller, float value)
    {
        if (!Enabled || value <= 1f || value >= 179f)
        {
            State.Remove(controller.Pointer);
            return value;
        }
        // The game's value = its slider base + short effects (dash, wobble). Our FOV replaces the base: a value
        // that stayed put for a moment is the base, anything on top of it is an effect and is kept.
        var now = UnityEngine.Time.unscaledTime;
        if (!baselineKnown || (now - lastSetAt > 0.5f && Mathf.Abs(lastValue - baseline) > 0.01f))
        {
            baseline = baselineKnown ? lastValue : value;
            baselineKnown = true;
        }
        lastValue = value;
        lastSetAt = now;

        var horizontal = Mathf.Clamp(Fov + (value - baseline), 1f, 170f);
        var applied = VerticalFromHorizontal(horizontal, ReferenceAspect);
        State[controller.Pointer] = (value, applied);
        Log(horizontal, applied);
        return applied;
    }

    /// <summary>Getter postfix: hand the game back its own (unconverted) value.</summary>
    internal static float Report(SRCameraController controller, float actual)
    {
        if (!State.TryGetValue(controller.Pointer, out var state))
            return actual;
        return Mathf.Abs(actual - state.Applied) < 0.01f ? state.Logical : actual;
    }

    private static void Log(float slider, float vertical)
    {
        var aspect = Camera.main ? Camera.main.aspect : ReferenceAspect;
        var key = $"{Mathf.Round(slider)}|{aspect:F2}";
        if (key == lastLogged)
            return;
        lastLogged = key;
        OwnedLog.LogMessage($"[WidescreenFov] slider {slider:F0} -> vertical {vertical:F1} deg " +
                            $"(horizontal {HorizontalFromVertical(vertical, ReferenceAspect):F0} on 16:9, " +
                            $"{HorizontalFromVertical(vertical, aspect):F0} on this {aspect:F2} screen)");
    }

    // ---- probe (test builds): every enabled camera's FOV/aspect when it changes ----
    private static float nextProbeAt;
    private static string lastProbe = string.Empty;

    internal static void Probe()
    {
#if LYCOREIA_TEST || LYCOREIA_DEEPLOG
        var now = UnityEngine.Time.unscaledTime;
        if (now < nextProbeAt)
            return;
        nextProbeAt = now + 1f;
        try
        {
            var parts = new List<string>();
            foreach (var camera in Camera.allCameras)
                if (camera && camera.enabled)
                    parts.Add($"{camera.name}:fov={camera.fieldOfView:F1},aspect={camera.aspect:F2}");
            var text = string.Join(" | ", parts);
            if (text == lastProbe)
                return;
            lastProbe = text;
            OwnedLog.LogMessage($"[WidescreenFov] cameras: {text}");
        }
        catch (Exception ex)
        {
            OwnedLog.LogWarning($"[WidescreenFov] probe: {ex.Message}");
        }
#endif
    }
}

[HarmonyPatch(typeof(SRCameraController), nameof(SRCameraController.FieldOfView), MethodType.Setter)]
internal static class WidescreenFovSetterPatch
{
    public static void Prefix(SRCameraController __instance, ref float value)
    {
        try { value = WidescreenFov.Convert(__instance, value); }
        catch (Exception ex) { OwnedLog.LogWarning($"[WidescreenFov] {ex.Message}"); }
    }
}

[HarmonyPatch(typeof(SRCameraController), nameof(SRCameraController.FieldOfView), MethodType.Getter)]
internal static class WidescreenFovGetterPatch
{
    public static void Postfix(SRCameraController __instance, ref float __result)
    {
        try { __result = WidescreenFov.Report(__instance, __result); }
        catch (Exception ex) { OwnedLog.LogWarning($"[WidescreenFov] {ex.Message}"); }
    }
}

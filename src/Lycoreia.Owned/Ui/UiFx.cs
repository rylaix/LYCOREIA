// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Il2CppInterop.Runtime.Attributes;
using Il2CppMonomiPark.SlimeRancher.UI.Framework.Audio;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using EventTrigger = UnityEngine.EventSystems.EventTrigger;

namespace Lycoreia.Owned.Components.UI;

/// <summary>Timed fade (CanvasGroup alpha) plus scale-in/out of a target transform. Reusable mid-animation.</summary>
internal sealed class UiFade : MonoBehaviour
{
    public UiFade(IntPtr ptr) : base(ptr) { }

    private CanvasGroup? group;
    private Transform? scaleTarget;
    private float fromAlpha, toAlpha, fromScale, toScale, duration, elapsed;
    private bool running, deactivateAtEnd;
    private Action? onDone;

    internal static UiFade Ensure(GameObject go)
    {
        if (!Il2CppInterop.Runtime.Injection.ClassInjector.IsTypeRegisteredInIl2Cpp<UiFade>())
            Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<UiFade>();
        var fade = go.GetComponent<UiFade>();
        if (!fade)
            fade = go.AddComponent<UiFade>();
        if (!fade.group)
            fade.group = go.GetComponent<CanvasGroup>() ?? go.AddComponent<CanvasGroup>();
        return fade;
    }

    /// <summary>Fade/scale from the current state to the target. Cancels any animation in flight.</summary>
    [HideFromIl2Cpp]
    internal void Play(Transform? scaled, float alpha, float scale, float seconds, bool deactivate, Action? done = null, float? startAlpha = null, float? startScale = null)
    {
        scaleTarget = scaled;
        fromAlpha = startAlpha ?? group!.alpha;
        fromScale = startScale ?? (scaled ? scaled!.localScale.x : 1f);
        toAlpha = alpha;
        toScale = scale;
        duration = Mathf.Max(0.01f, seconds);
        elapsed = 0f;
        deactivateAtEnd = deactivate;
        onDone = done;
        running = true;
        group!.alpha = fromAlpha;
        group.blocksRaycasts = alpha > 0.5f;
        group.interactable = alpha > 0.5f;
        Apply(0f);
    }

    private void Apply(float t)
    {
        var e = 1f - Mathf.Pow(1f - t, 3f);   // ease-out cubic
        group!.alpha = Mathf.Lerp(fromAlpha, toAlpha, e);
        if (scaleTarget)
        {
            var s = Mathf.Lerp(fromScale, toScale, e);
            scaleTarget!.localScale = new Vector3(s, s, 1f);
        }
    }

    private void Update()
    {
        if (!running)
            return;
        try
        {
            elapsed += UnityEngine.Time.unscaledDeltaTime;
            var t = Mathf.Clamp01(elapsed / duration);
            Apply(t);
            if (t < 1f)
                return;
            running = false;
            var done = onDone; onDone = null;
            if (deactivateAtEnd)
                gameObject.SetActive(false);
            done?.Invoke();
        }
        catch (Exception ex)
        {
            running = false;
            OwnedLog.LogWarning($"UI fade failed: {ex.Message}");
        }
    }
}

/// <summary>Smooth hover/press scale for widgets, driven by pointer events.</summary>
internal sealed class UiHover : MonoBehaviour
{
    public UiHover(IntPtr ptr) : base(ptr) { }

    internal float HoverScale = 1.04f;
    private bool hover, down;
    private float scale = 1f;

    internal static UiHover Ensure(GameObject go, float hoverScale)
    {
        if (!Il2CppInterop.Runtime.Injection.ClassInjector.IsTypeRegisteredInIl2Cpp<UiHover>())
            Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<UiHover>();
        var h = go.GetComponent<UiHover>();
        if (!h)
            h = go.AddComponent<UiHover>();
        h.HoverScale = hoverScale;
        return h;
    }

    internal void SetHover(bool value) { hover = value; if (!value) down = false; enabled = true; }
    internal void SetDown(bool value) { down = value; enabled = true; }
    private void OnDisable() { scale = 1f; transform.localScale = Vector3.one; hover = down = false; }

    private void Update()
    {
        var target = down ? 0.97f : hover ? HoverScale : 1f;
        scale = Mathf.Lerp(scale, target, 1f - Mathf.Exp(-18f * UnityEngine.Time.unscaledDeltaTime));
        if (Mathf.Abs(scale - target) < 0.0005f)
        {
            scale = target;
            if (target == 1f)
                enabled = false;
        }
        transform.localScale = new Vector3(scale, scale, 1f);
    }
}

/// <summary>Wires hover/press visuals and native sounds onto a widget. Never throws.</summary>
internal static class UiFx
{
    internal static void Attach(GameObject go, bool scale, bool clickOnPointerDown, float hoverScale = 1.04f)
    {
        try
        {
            UiHover? hover = scale ? UiHover.Ensure(go, hoverScale) : null;
            if (hover)
                hover!.enabled = false;
            var trigger = go.GetComponent<EventTrigger>() ?? go.AddComponent<EventTrigger>();
            Add(trigger, EventTriggerType.PointerEnter, _ =>
            {
                if (!Active(go)) return;
                hover?.SetHover(true);
                UiSounds.Play(UiSounds.Cue.Hover);
            });
            Add(trigger, EventTriggerType.PointerExit, _ => hover?.SetHover(false));
            Add(trigger, EventTriggerType.PointerDown, _ =>
            {
                if (!Active(go)) return;
                hover?.SetDown(true);
                if (clickOnPointerDown)
                    UiSounds.Play(UiSounds.Cue.Click);
            });
            Add(trigger, EventTriggerType.PointerUp, _ => hover?.SetDown(false));
        }
        catch (Exception ex)
        {
            OwnedLog.LogWarning($"UI feedback not attached: {ex.Message}");
        }
    }

    private static bool Active(GameObject go)
    {
        var selectable = go.GetComponent<Selectable>();
        return !selectable || selectable!.IsInteractable();
    }

    private static void Add(EventTrigger trigger, EventTriggerType type, Action<BaseEventData> action)
    {
        var entry = new EventTrigger.Entry { eventID = type };
        entry.callback.AddListener((UnityAction<BaseEventData>)(data =>
        {
            try { action(data); } catch { /* feedback only */ }
        }));
        trigger.triggers.Add(entry);
    }
}

/// <summary>Plays the game's own UI cues (found by name in its UIAudioTable assets); silent if none are found.</summary>
internal static class UiSounds
{
    internal enum Cue : byte { Hover, Click }

    private static SECTR_AudioCue? hoverCue, clickCue;
    private static bool resolved, failed;
    private static float lastPlay = -1f;

    internal static void Play(Cue kind)
    {
        if (failed)
            return;
        try
        {
            var now = UnityEngine.Time.unscaledTime;
            if (now - lastPlay < 0.04f)
                return;
            Resolve();
            var cue = kind == Cue.Hover ? hoverCue : clickCue;
            if (!cue)
                return;
            lastPlay = now;
            SECTR_AudioSystem.Play(cue, Vector3.zero, false);
        }
        catch (Exception ex)
        {
            failed = true;
            OwnedLog.LogWarning($"UI sounds disabled: {ex.Message}");
        }
    }

    private static void Resolve()
    {
        if (resolved && (hoverCue || clickCue))
            return;
        if (resolved && UnityEngine.Time.unscaledTime < retryAt)
            return;
        resolved = true;
        retryAt = UnityEngine.Time.unscaledTime + 5f;
        SECTR_AudioCue? hover = null, click = null;
        int hoverScore = -1, clickScore = -1;
        try
        {
            foreach (var table in Resources.FindObjectsOfTypeAll<UIAudioTable>())
            {
                if (!table)
                    continue;
                var cues = table.AudioCues;
                if (cues == null)
                    continue;
                foreach (var pair in cues)
                {
                    var name = (pair.Key ?? string.Empty).ToLowerInvariant();
                    var cue = pair.Value;
                    if (!cue)
                        continue;
                    var h = Score(name, "hover", "highlight", "mouseover", "rollover");
                    if (h > hoverScore) { hoverScore = h; hover = cue; }
                    var c = Score(name, "submit", "click", "press", "confirm", "select");
                    if (c > clickScore) { clickScore = c; click = cue; }
                }
            }
        }
        catch (Exception ex)
        {
            OwnedLog.LogWarning($"Native UI cue lookup failed: {ex.Message}");
        }

        hoverCue = hover;
        clickCue = click;
        OwnedLog.LogMessage($"UI sounds: hover={(hover ? hover!.name : "none")} click={(click ? click!.name : "none")}");
    }

    private static float retryAt;

    /// <summary>Higher is a better match; -1 for none. Prefers names that also mention button/menu.</summary>
    private static int Score(string name, params string[] words)
    {
        var best = -1;
        for (var i = 0; i < words.Length; i++)
        {
            if (!name.Contains(words[i]))
                continue;
            var score = 100 - i * 10;
            if (name.Contains("button")) score += 5;
            if (name.Contains("menu")) score += 3;
            if (name.Contains("pause") || name.Contains("tip") || name.Contains("lab") || name.Contains("gadget")) score -= 4;
            best = Mathf.Max(best, score);
        }

        return best;
    }
}

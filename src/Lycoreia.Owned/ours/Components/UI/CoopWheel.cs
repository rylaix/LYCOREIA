// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Il2CppTMPro;
using Lycoreia.Coop;
using Lycoreia.Game.Coop;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Lycoreia.Owned.Components.UI;

internal static class CoopWheel
{
    private const float HoldSeconds = 0.35f, Radius = 210f, MaxAim = 60f;
    private static float pressedAt = -1f;
    private static bool open;
    private static Vector2 aim;
    private static List<WheelOption> options = new();
    private static (string anchor, string label, string kind, Lycoreia.Net.Vec3 position, int scene) target;
    private static int selected = -1;

    private static GameObject? root;
    private static TextMeshProUGUI? center;
    private static readonly List<(Image bg, TextMeshProUGUI text)> wedges = new();

    // text box
    private static GameObject? boxRoot;
    private static TMP_InputField? boxInput;
    private static TextMeshProUGUI? boxTitle;
    private static WheelAction boxAction;
    private static bool boxOpen;
    private static int boxOpenedFrame;

    internal static bool Busy => open || boxOpen;

    /// <summary>Session end, scene change, ping failure: close everything and give the keys back.</summary>
    internal static void Cancel()
    {
        try { if (open) Close(); if (boxOpen) CloseBox(); } catch { }
        open = boxOpen = false; pressedAt = -1f; InputGate.Set("coop-wheel", false); InputGate.Set("coop-box", false);
    }

    /// <summary>Called by PlayerPings every frame instead of its own middle-click check. Returns true when a plain ping should fire.</summary>
    internal static bool Tick(bool inputFree, bool coop)
    {
        try
        {
            if (boxOpen) { InputGate.Reassert(); TickBox(); return false; }
            var mouse = Mouse.current; if (mouse == null) return false;
            if (!inputFree && !open) { pressedAt = -1f; return false; }
            var button = mouse.middleButton; float now = Time.unscaledTime;
            if (button.wasPressedThisFrame) pressedAt = now;
            if (!coop)
            {
                if (button.wasPressedThisFrame) { pressedAt = -1f; return true; }           // offline: ping as before
                return false;
            }
            if (pressedAt >= 0f && button.isPressed && !open && now - pressedAt >= HoldSeconds) Open();
            if (open && !button.isPressed && !button.wasReleasedThisFrame) { Close(); pressedAt = -1f; return false; }   // release missed (focus loss)
            if (open)
            {
                InputGate.Reassert();
                aim += mouse.delta.ReadValue(); if (aim.magnitude > MaxAim * 3f) aim = aim.normalized * MaxAim * 3f;
                int pick = CoopWheelRules.Pick(aim.x, aim.y, options.Count);
                if (pick != selected) { selected = pick; Paint(); }
            }
            if (button.wasReleasedThisFrame && pressedAt >= 0f)
            {
                pressedAt = -1f;
                if (open) { var choice = selected >= 0 && selected < options.Count ? options[selected] : null; Close(); if (choice != null) Choose(choice); return false; }
                return true;                                                                 // a tap: ping
            }
            return false;
        }
        catch (Exception ex)
        {
            OwnedLog.LogDiagnostic("coop", "wheel-failed", ex.Message, "coop:wheel", 5000);
            Close(); return false;
        }
    }

    private static void Open()
    {
        var cam = Camera.main; var player = SceneContext.Instance ? SceneContext.Instance.Player : null; if (!cam || !player) return;
        var point = cam!.transform.position + cam.transform.forward * 6f;
        foreach (var hit in Physics.RaycastAll(cam.transform.position, cam.transform.forward, 60f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
            if (hit.collider && !hit.collider.transform.IsChildOf(player!.transform)) { point = hit.point; break; }
        target = CoopRuntime.At(point);
        var board = CoopRuntime.Board; string me = Lycoreia.Mod.LocalId;
        var jobs = target.anchor.Length == 0 ? new List<JobHere>() :
            board.Jobs.Where(j => !j.Done && j.Anchor == target.anchor).Select(j => new JobHere { Id = j.Id, Text = j.Text, Claimed = j.ClaimedBy.Length > 0, Mine = j.ClaimedBy == me }).ToList();
        bool noteHere = target.anchor.Length > 0 && board.Notes.Any(n => n.Anchor == target.anchor);
        options = CoopWheelRules.For(target.kind, jobs, noteHere);
        aim = Vector2.zero; selected = -1; open = true;
        InputGate.Set("coop-wheel", true);                                                   // camera look and other keys stop while choosing
        Build(); Paint(); root!.SetActive(true);
    }

    private static void Close()
    {
        open = false; selected = -1; if (root) root!.SetActive(false);
        InputGate.Set("coop-wheel", false);
    }

    private static void Choose(WheelOption o)
    {
        var t = target;
        switch (o.Action)
        {
            case WheelAction.PinJob: Pin(o.Text, t); break;
            case WheelAction.Note: Note(o.Text, t); break;
            case WheelAction.ClaimJob: CoopRuntime.Do(new CoopOp { Kind = CoopOpKind.JobClaim, Id = o.JobId }); break;
            case WheelAction.FinishJob: CoopRuntime.Do(new CoopOp { Kind = CoopOpKind.JobDone, Id = o.JobId }); break;
            case WheelAction.RemoveNote:
                var note = CoopRuntime.Board.Notes.FirstOrDefault(n => n.Anchor == t.anchor);
                if (note != null) CoopRuntime.Do(new CoopOp { Kind = CoopOpKind.NoteRemove, Id = note.Id }); break;
            case WheelAction.WriteJob: case WheelAction.WriteNote: OpenBox(o.Action); break;
        }
    }

    private static void Pin(string text, (string anchor, string label, string kind, Lycoreia.Net.Vec3 position, int scene) t)
        => CoopRuntime.Do(new CoopOp { Kind = CoopOpKind.JobAdd, Text = text, Anchor = t.anchor, HasPosition = true, Position = t.position, Scene = t.scene });
    private static void Note(string text, (string anchor, string label, string kind, Lycoreia.Net.Vec3 position, int scene) t)
        => CoopRuntime.Do(new CoopOp { Kind = CoopOpKind.NoteSet, Text = text, Anchor = t.anchor, HasPosition = true, Position = t.position, Scene = t.scene });

    // ---------------------------------------------------------------- wheel view
    private static void Build()
    {
        if (!root)
        {
            root = new GameObject("Lycoreia.CoopWheel"); UnityEngine.Object.DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 70;
            var scaler = root.AddComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = 1f;
            center = Text(root.transform, Vector2.zero, new Vector2(320, 90), 28f, FontStyles.Bold);
        }
        foreach (var w in wedges) if (w.bg) UnityEngine.Object.Destroy(w.bg.gameObject);
        wedges.Clear();
        for (int i = 0; i < options.Count; i++)
        {
            double a = i * 2 * Math.PI / options.Count; var pos = new Vector2((float)Math.Sin(a), (float)Math.Cos(a)) * Radius;
            var go = new GameObject("Wedge" + i); go.transform.SetParent(root!.transform, false);
            var rt = go.AddComponent<RectTransform>(); rt.anchoredPosition = pos; rt.sizeDelta = new Vector2(300, 64);
            var bg = go.AddComponent<Image>(); bg.raycastTarget = false;
            var text = Text(go.transform, Vector2.zero, new Vector2(286, 60), 22f, FontStyles.Normal);
            text.text = options[i].Label; text.enableAutoSizing = true; text.fontSizeMin = 14f; text.fontSizeMax = 22f; text.textWrappingMode = TextWrappingModes.Normal;
            wedges.Add((bg, text));
        }
        center!.text = $"Co-op\n<size=70%>{target.label}</size>";
    }

    private static void Paint()
    {
        for (int i = 0; i < wedges.Count; i++)
        {
            bool on = i == selected;
            wedges[i].bg.color = on ? UiTheme.AccentColor : new Color(UiTheme.PanelColor.r, UiTheme.PanelColor.g, UiTheme.PanelColor.b, 0.88f);
            wedges[i].text.color = on ? new Color(0.1f, 0.1f, 0.14f, 1f) : UiTheme.GeneralText;
        }
        if (center) center!.color = selected < 0 ? UiTheme.GeneralText : UiTheme.AccentColor;
    }

    private static TextMeshProUGUI Text(Transform parent, Vector2 pos, Vector2 size, float fontSize, FontStyles style)
    {
        var go = new GameObject("Text"); go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>(); rt.anchoredPosition = pos; rt.sizeDelta = size;
        var t = go.AddComponent<TextMeshProUGUI>(); var font = UiFonts.Default; if (font) t.font = font;
        t.fontSize = fontSize; t.fontStyle = style; t.alignment = TextAlignmentOptions.Center; t.raycastTarget = false; t.color = UiTheme.GeneralText; t.richText = true;
        return t;
    }

    // ---------------------------------------------------------------- write your own (job or note)
    private static void OpenBox(WheelAction action)
    {
        boxAction = action; BuildBox();
        boxTitle!.text = (action == WheelAction.WriteJob ? "New job at " : "Note for ") + target.label + "\n<size=70%>Enter to save, Esc to cancel</size>";
        boxInput!.text = string.Empty; boxRoot!.SetActive(true); boxOpen = true; boxOpenedFrame = Time.frameCount;
        InputGate.Set("coop-box", true);
        if (EventSystem.current) EventSystem.current.SetSelectedGameObject(boxInput.gameObject);
        boxInput.ActivateInputField();
    }

    private static void TickBox()
    {
        var kb = Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame) { CloseBox(); return; }
        if (kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) && Time.frameCount > boxOpenedFrame)
        {
            var text = CoopBoard.Clean(boxInput!.text, boxAction == WheelAction.WriteJob ? CoopBoard.MaxJobText : CoopBoard.MaxNoteText);
            if (text.Length > 0) { if (boxAction == WheelAction.WriteJob) Pin(text, target); else Note(text, target); }
            CloseBox(); return;
        }
        if (boxInput && !boxInput!.isFocused) { if (EventSystem.current) EventSystem.current.SetSelectedGameObject(boxInput.gameObject); boxInput.ActivateInputField(); }
    }

    private static void CloseBox()
    {
        boxOpen = false; if (boxRoot) boxRoot!.SetActive(false);
        if (boxInput) boxInput!.DeactivateInputField(false);
        if (EventSystem.current && boxInput && EventSystem.current.currentSelectedGameObject == boxInput!.gameObject) EventSystem.current.SetSelectedGameObject(null);
        InputGate.Set("coop-box", false);
    }

    private static void BuildBox()
    {
        if (boxRoot) return;
        boxRoot = new GameObject("Lycoreia.CoopNoteBox"); UnityEngine.Object.DontDestroyOnLoad(boxRoot);
        var canvas = boxRoot.AddComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 71;
        var scaler = boxRoot.AddComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = 1f;
        boxRoot.AddComponent<GraphicRaycaster>();
        var panel = new GameObject("Panel"); panel.transform.SetParent(boxRoot.transform, false);
        var prt = panel.AddComponent<RectTransform>(); prt.sizeDelta = new Vector2(760, 170); prt.anchoredPosition = new Vector2(0, -140);
        panel.AddComponent<Image>().color = new Color(UiTheme.PanelColor.r, UiTheme.PanelColor.g, UiTheme.PanelColor.b, 0.95f);
        boxTitle = Text(panel.transform, new Vector2(0, 42), new Vector2(720, 70), 24f, FontStyles.Bold);

        var field = new GameObject("Input"); field.SetActive(false); field.transform.SetParent(panel.transform, false);
        var frt = field.AddComponent<RectTransform>(); frt.anchoredPosition = new Vector2(0, -38); frt.sizeDelta = new Vector2(700, 56);
        var bg = field.AddComponent<Image>(); bg.color = UiTheme.SecondaryColor;
        var area = new GameObject("Text Area"); area.transform.SetParent(field.transform, false);
        var art = area.AddComponent<RectTransform>(); art.anchorMin = Vector2.zero; art.anchorMax = Vector2.one; art.offsetMin = new Vector2(12, 4); art.offsetMax = new Vector2(-12, -4);
        area.AddComponent<RectMask2D>();
        var text = Text(area.transform, Vector2.zero, Vector2.zero, 24f, FontStyles.Normal);
        var trt = text.rectTransform; trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
        text.alignment = TextAlignmentOptions.MidlineLeft; text.textWrappingMode = TextWrappingModes.NoWrap; text.outlineWidth = 0f;
        boxInput = field.AddComponent<TMP_InputField>();
        boxInput.textViewport = art; boxInput.textComponent = text; var font = UiFonts.Default; if (font) boxInput.fontAsset = font;
        boxInput.pointSize = 24f; boxInput.targetGraphic = bg; boxInput.transition = Selectable.Transition.None; boxInput.lineType = TMP_InputField.LineType.SingleLine;
        boxInput.characterLimit = CoopBoard.MaxJobText; boxInput.restoreOriginalTextOnEscape = false; boxInput.customCaretColor = true; boxInput.caretColor = Color.white; boxInput.caretWidth = 3;
        field.SetActive(true);
        boxRoot.SetActive(false);
    }
}

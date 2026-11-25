// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Il2CppTMPro;
using Lycoreia.Text;
using UnityEngine.InputSystem.Controls;
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

    private const float LabelW = 330f, LabelH = 64f, BackdropSize = 1500f;
    private static Sprite? backdrop;
    /// <summary>Soft dark vignette (alpha 0.78 in the middle, fading to 0 at the edge), generated once.</summary>
    private static Sprite? Backdrop()
    {
        if (backdrop) return backdrop;
        try
        {
            const int n = 128; float half = n / 2f;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "Lycoreia.WheelBackdrop", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x + 0.5f - half) * (x + 0.5f - half) + (y + 0.5f - half) * (y + 0.5f - half)) / half;
                    float t = Mathf.Clamp01((1f - d) / 0.55f); tex.SetPixel(x, y, new Color(0f, 0f, 0.02f, 0.78f * t * t * (3f - 2f * t)));
                }
            tex.Apply(false, true); tex.hideFlags = HideFlags.HideAndDontSave;
            backdrop = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f); backdrop.hideFlags = HideFlags.HideAndDontSave;
        }
        catch (Exception ex) { OwnedLog.LogWarning("wheel backdrop failed: " + ex.Message); }
        return backdrop;
    }

    private const float Outer = 250f, LabelGap = 14f, WedgeHoverScale = 1.07f;
    private static int dev = -1;                                                              // device that holds the wheel: 0 mouse, 1 gamepad
    private static string heldGlyph = "";

    private sealed class WedgeView { public GameObject go = null!; public Image ring = null!, icon = null!; public TextMeshProUGUI label = null!; public UiHover? hover; public bool nativeIcon; }

    private static GameObject? root, panel, chargeRoot;
    private static Image? chargeRing;
    private static UiFade? fade;
    private static TextMeshProUGUI? center, prompt;
    private static Image? centerPlate, promptPlate;
    private static readonly List<WedgeView> wedges = new();

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
        try { if (open) Close(true); if (boxOpen) CloseBox(); } catch { } try { Charge(-1f); } catch { }
        open = boxOpen = false; pressedAt = -1f; InputGate.Set("coop-wheel", false); InputGate.Set("coop-box", false);
    }

    /// <summary>Called by PlayerPings every frame instead of its own middle-click check. Returns true when a plain ping should fire.</summary>
    internal static bool Tick(bool inputFree, bool coop)
    {
        bool ping = TickCore(inputFree, coop);
        try { Charge(coop && !open && !boxOpen && pressedAt >= 0f ? (Time.unscaledTime - pressedAt) / HoldSeconds : -1f); } catch { }
        return ping;
    }

    private static bool TickCore(bool inputFree, bool coop)
    {
        try
        {
            if (boxOpen) { InputGate.Reassert(); TickBox(); return false; }
            var mouse = Mouse.current; var pad = Gamepad.current;
            var padButton = pad != null ? CoopWheelPad.Control(pad) : null;
            if (mouse == null && padButton == null) return false;
            if (!inputFree && !open) { pressedAt = -1f; return false; }
            float now = Time.unscaledTime;
            bool mousePressed = mouse != null && mouse.middleButton.wasPressedThisFrame, padPressed = padButton != null && padButton.wasPressedThisFrame;
            if (!open && (mousePressed || padPressed)) { pressedAt = now; dev = mousePressed ? 0 : 1; }
            ButtonControl? button = dev == 1 ? padButton : mouse?.middleButton;
            if (button == null) { pressedAt = -1f; return false; }
            if (!coop)
            {
                if (mousePressed || padPressed) { pressedAt = -1f; return true; }          // offline: ping as before
                return false;
            }
            if (pressedAt >= 0f && button.isPressed && !open && now - pressedAt >= HoldSeconds) Open();
            if (open && !button.isPressed && !button.wasReleasedThisFrame) { Close(false); pressedAt = -1f; return false; }   // release missed (focus loss)
            if (open)
            {
                InputGate.Reassert();
                int pick;
                if (dev == 1 && pad != null) { var v = pad.rightStick.ReadValue(); pick = CoopWheelLayout.PickStick(v.x, v.y, options.Count); }
                else
                {
                    aim += mouse!.delta.ReadValue(); if (aim.magnitude > MaxAim * 3f) aim = aim.normalized * MaxAim * 3f;
                    pick = CoopWheelRules.Pick(aim.x, aim.y, options.Count);
                }
                if (pick != selected) { selected = pick; if (pick >= 0) UiFx.Play(UiCue.Hover); Paint(); }
            }
            if (button.wasReleasedThisFrame && pressedAt >= 0f)
            {
                pressedAt = -1f;
                if (open)
                {
                    var choice = selected >= 0 && selected < options.Count ? options[selected] : null; Close(false);
                    if (choice != null) { UiFx.Play(UiCue.Confirm); Choose(choice); } else UiFx.Play(UiCue.Cancel);
                    return false;
                }
                return true;                                                                 // a tap: ping
            }
            return false;
        }
        catch (Exception ex)
        {
            OwnedLog.LogDiagnostic("coop", "wheel-failed", ex.ToString(), "coop:wheel", 5000);
            try { Close(true); } catch { }
            pressedAt = -1f;                                                                     // rc4 fix: no retry every frame; the next press tries again
            return false;
        }
    }

    private static void Open()
    {
        var cam = Camera.main; var player = SceneContext.Instance ? SceneContext.Instance.Player : null; if (!cam || !player) return;
        var point = cam!.transform.position + cam.transform.forward * 6f;
        foreach (var hit in Physics.RaycastAll(cam.transform.position, cam.transform.forward, 60f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
            if (hit.collider && !hit.collider.transform.IsChildOf(player!.transform)) { point = hit.point; break; }
        target = CoopRuntime.At(point);
        var board = CoopRuntime.Board; string me = Lycoreia.Mod.LocalId; bool host = Lycoreia.Mod.IsHost;
        // what is "here": on the aimed anchor, near the aimed point, or whose world marker the player is looking at (anchorless spot jobs and notes too)
        var aimW = new Lycoreia.Net.Vec3(point.x, point.y, point.z); var camT = cam.transform;
        var camW = new Lycoreia.Net.Vec3(camT.position.x, camT.position.y, camT.position.z); var fwdW = new Lycoreia.Net.Vec3(camT.forward.x, camT.forward.y, camT.forward.z);
        var jobs = board.Jobs.Where(j => !j.Done && CoopWheelRules.IsHere(j.Anchor, j.HasPosition, j.Position, j.Scene, target.anchor, target.scene, aimW, camW, fwdW))
            .OrderBy(j => j.ClaimedBy == me ? 0 : j.ClaimedBy.Length == 0 ? 1 : 2).ThenBy(j => j.Id)
            .Select(j => new JobHere { Id = j.Id, Text = j.Text, Claimed = j.ClaimedBy.Length > 0, Mine = j.ClaimedBy == me, CanRemove = host || j.CreatedBy == me }).ToList();
        var note = board.Notes.Where(n => CoopWheelRules.IsHere(n.Anchor, true, n.Position, n.Scene, target.anchor, target.scene, aimW, camW, fwdW)).OrderBy(n => n.Id).FirstOrDefault();
        options = CoopWheelRules.For(target.kind, jobs, note != null ? note.Id : 0);
        OwnedLog.LogDiagnostic("coop", "wheel-open", $"kind={target.kind}; anchor={target.anchor}; jobsHere={jobs.Count}; noteHere={note != null}; options={options.Count}; host={host}");
        aim = Vector2.zero; selected = -1; open = true;
        InputGate.Set("coop-wheel", true);                                                   // camera look and other keys stop while choosing
        heldGlyph = dev == 1 ? InputGlyphs.Label(CoopWheelPad.Path ?? "") : InputGlyphs.Label("MMB");
        if (root) root!.SetActive(true);                                                 // texts measured in Build/Paint must be active (TMP preferred size)
        Build(); Paint(); root!.SetActive(true);
        try { fade?.Play(panel!.transform, 1f, 1f, 0.14f, false, null, 0f, 0.82f); } catch { }
        UiFx.Play(UiCue.Open);
    }

    /// <summary>Closes the wheel: a short scale-out, or at once (session end, failures).</summary>
    private static void Close(bool immediate)
    {
        open = false; selected = -1; Charge(-1f);
        InputGate.Set("coop-wheel", false);
        if (!root) return;
        try
        {
            if (immediate || !root!.activeInHierarchy || fade == null) root!.SetActive(false);
            else fade.Play(panel!.transform, 0f, 0.92f, 0.09f, true);
        }
        catch { root!.SetActive(false); }
    }

    private static void Choose(WheelOption o)
    {
        var t = target;
        switch (o.Action)
        {
            case WheelAction.PinJob: Pin(o.Text, t); break;
            case WheelAction.Note: Note(o.Text, t); break;
            // the same requests as the Esc board (host applies them: claim, drop, done, remove)
            case WheelAction.ClaimJob: Act(o, CoopOpKind.JobClaim); break;
            case WheelAction.UnclaimJob: Act(o, CoopOpKind.JobUnclaim); break;
            case WheelAction.FinishJob: Act(o, CoopOpKind.JobDone); break;
            case WheelAction.RemoveJob: Act(o, CoopOpKind.JobRemove); break;
            case WheelAction.RemoveNote:
                int noteId = o.JobId > 0 ? o.JobId : CoopRuntime.Board.Notes.FirstOrDefault(n => n.Anchor == t.anchor)?.Id ?? 0;
                if (noteId > 0) Act(new WheelOption { Action = o.Action, JobId = noteId }, CoopOpKind.NoteRemove); break;
            case WheelAction.WriteJob: case WheelAction.WriteNote: OpenBox(o.Action); break;
        }
    }

    private static void Act(WheelOption o, CoopOpKind kind)
    {
        var job = CoopRuntime.Board.Jobs.FirstOrDefault(j => j.Id == o.JobId);
        OwnedLog.LogDiagnostic("coop", "wheel-action", $"action={o.Action}; op={kind}; id={o.JobId}; anchor={job?.Anchor ?? ""}; claimedBy={job?.ClaimedBy ?? ""}; host={Lycoreia.Mod.IsHost}");
        CoopRuntime.Do(new CoopOp { Kind = kind, Id = o.JobId });
    }

    private static void Pin(string text, (string anchor, string label, string kind, Lycoreia.Net.Vec3 position, int scene) t)
        => CoopRuntime.Do(new CoopOp { Kind = CoopOpKind.JobAdd, Text = text, Anchor = t.anchor, HasPosition = true, Position = t.position, Scene = t.scene });
    private static void Note(string text, (string anchor, string label, string kind, Lycoreia.Net.Vec3 position, int scene) t)
        => CoopRuntime.Do(new CoopOp { Kind = CoopOpKind.NoteSet, Text = text, Anchor = t.anchor, HasPosition = true, Position = t.position, Scene = t.scene });

    // ---------------------------------------------------------------- wheel view
    private static Canvas MakeCanvas(GameObject go, int order)
    {
        var canvas = go.AddComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = order;
        var scaler = go.AddComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = 1f;
        return canvas;
    }

    private static Image NewImage(string name, Transform parent, Vector2 pos, Vector2 size, Sprite? sprite)
    {
        var go = new GameObject(name); go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>(); rt.anchoredPosition = pos; rt.sizeDelta = size;
        var img = go.AddComponent<Image>(); img.raycastTarget = false; if (sprite) img.sprite = sprite; return img;
    }

    private static void Build()
    {
        if (!root)
        {
            root = new GameObject("Lycoreia.CoopWheel"); UnityEngine.Object.DontDestroyOnLoad(root);
            MakeCanvas(root, 70);
            panel = new GameObject("Panel"); panel.transform.SetParent(root.transform, false); panel.AddComponent<RectTransform>();
            fade = UiFade.Ensure(root);
            var back = NewImage("Backdrop", panel.transform, Vector2.zero, new Vector2(BackdropSize, BackdropSize * 0.72f), Backdrop());   // dark soft vignette: text stays readable on any scene
            back.color = Color.white;
            centerPlate = NewImage("Plate", panel.transform, Vector2.zero, new Vector2(300, 170), null); UiPlates.Apply(centerPlate, PlateKind.Card, null, true, true, 100f);
            center = Text(panel.transform, Vector2.zero, new Vector2(CenterTextW, 170), 26f, FontStyles.Bold);
            promptPlate = NewImage("Plate", panel.transform, new Vector2(0, -(Outer + 110f)), new Vector2(400, 40), null); UiPlates.Apply(promptPlate, PlateKind.Pill, null, true, true, 36f);
            prompt = Text(panel.transform, new Vector2(0, -(Outer + 110f)), new Vector2(900, 44), 20f, FontStyles.Normal);
        }
        foreach (var w in wedges) if (w.go) UnityEngine.Object.Destroy(w.go);
        wedges.Clear();
        var ring = CoopWheelArt.Ring(); int n = options.Count;
        for (int i = 0; i < n; i++)
        {
            var v = new WedgeView();
            v.go = new GameObject("Wedge" + i); v.go.transform.SetParent(panel!.transform, false); v.go.AddComponent<RectTransform>();
            v.ring = NewImage("Ring", v.go.transform, Vector2.zero, new Vector2(Outer * 2f, Outer * 2f), ring);
            if (ring)
            {
                v.ring.type = Image.Type.Filled; v.ring.fillMethod = Image.FillMethod.Radial360; v.ring.fillOrigin = (int)Image.Origin360.Top; v.ring.fillClockwise = true;
                v.ring.fillAmount = CoopWheelLayout.FillAmount(n);
                v.ring.rectTransform.localRotation = Quaternion.Euler(0f, 0f, CoopWheelLayout.ImageRotationZ(i, n));
            }
            var (x, y) = CoopWheelLayout.Position(i, n, Outer * (1f + CoopWheelArt.RingInnerRatio) / 2f);
            var (icon, native) = CoopWheelArt.Icon(CoopWheelLayout.IconKey(options[i].Action));
            v.icon = NewImage("Icon", v.go.transform, new Vector2(x, y), new Vector2(46, 46), icon); v.icon.enabled = icon; v.nativeIcon = native; v.icon.preserveAspect = true;
            int side = CoopWheelLayout.LabelSide(i, n); var (lx, ly) = CoopWheelLayout.Position(i, n, Outer + LabelGap);
            v.label = Text(v.go.transform, new Vector2(lx, ly), new Vector2(LabelW, LabelH), 20f, FontStyles.Normal);
            var lrt = v.label.rectTransform;
            lrt.pivot = new Vector2(side > 0 ? 0f : side < 0 ? 1f : 0.5f, side != 0 ? 0.5f : (ly >= 0 ? 0f : 1f));
            v.label.alignment = side > 0 ? TextAlignmentOptions.MidlineLeft : side < 0 ? TextAlignmentOptions.MidlineRight : (ly >= 0 ? TextAlignmentOptions.Bottom : TextAlignmentOptions.Top);
            v.label.text = WheelText.Label(options[i].Label); v.label.enableAutoSizing = true; v.label.fontSizeMin = 14f; v.label.fontSizeMax = 20f; v.label.textWrappingMode = TextWrappingModes.Normal;
            PlateLabel(v, side, lx, ly);
            try { v.hover = UiHover.Ensure(v.go, WedgeHoverScale); v.hover.enabled = false; } catch { }
            wedges.Add(v);
        }
        prompt!.text = dev == 1 ? WheelText.PadReleaseHint(heldGlyph) : WheelText.ReleaseHint(heldGlyph);
        FitPlate(promptPlate, prompt, PlateKind.Pill, 900f, 0f);
    }

    /// <summary>Resizes a plate to wrap the text's preferred size (token padding); hides it for empty text.</summary>
    /// <summary>Centre caption width: the card (text + padding) stays inside the ring hole (inner radius Outer * RingInnerRatio).</summary>
    private const float CenterTextW = 230f;

    private static void FitPlate(Image? plate, TextMeshProUGUI? text, PlateKind kind, float maxWidth, float yOffset)
    {
        if (!plate || !text) return;
        bool has = !string.IsNullOrEmpty(text!.text); plate!.gameObject.SetActive(has); if (!has) return;
        var pref = text.GetPreferredValues(text.text, maxWidth, 0f);
        plate.rectTransform.sizeDelta = UiPlates.Wrap(new Vector2(Mathf.Min(Mathf.Ceil(pref.x), maxWidth), Mathf.Ceil(pref.y)), kind);
        plate.rectTransform.anchoredPosition = text.rectTransform.anchoredPosition + new Vector2(0f, yOffset);
    }

    /// <summary>Pill plate behind a wedge label: the label rect shrinks to its text and moves in by the pill padding, so the plate starts where the label used to.</summary>
    private static void PlateLabel(WedgeView v, int side, float lx, float ly)
    {
        var lrt = v.label.rectTransform;
        var pref = v.label.GetPreferredValues(v.label.text, LabelW, 0f);
        float tw = Mathf.Min(Mathf.Ceil(pref.x) + 2f, LabelW), th = Mathf.Min(Mathf.Ceil(pref.y), LabelH);
        var size = UiPlates.Wrap(new Vector2(tw, th), PlateKind.Pill); var pad = UiPlates.PaddingOf(PlateKind.Pill);
        var plate = NewImage("Plate", v.go.transform, new Vector2(lx, ly), size, null);
        plate.rectTransform.pivot = lrt.pivot;
        plate.rectTransform.SetSiblingIndex(lrt.GetSiblingIndex());
        UiPlates.Apply(plate, PlateKind.Pill, null, true, true, size.y);
        lrt.sizeDelta = new Vector2(tw, th);
        lrt.anchoredPosition = new Vector2(lx + side * pad.x, ly + (side == 0 ? (ly >= 0f ? pad.y : -pad.y) : 0f));
    }

    private static void Paint()
    {
        var panelCol = new Color(UiTheme.PanelColor.r, UiTheme.PanelColor.g, UiTheme.PanelColor.b, 0.88f);
        for (int i = 0; i < wedges.Count; i++)
        {
            bool on = i == selected; var w = wedges[i];
            w.ring.color = on ? UiTheme.AccentColor : panelCol;
            w.label.color = on ? UiTheme.AccentColor : UiTheme.GeneralText;
            w.icon.color = w.nativeIcon ? Color.white : on ? new Color(0.1f, 0.1f, 0.14f, 1f) : UiTheme.GeneralText;
            try { w.hover?.SetHover(on); } catch { }
        }
        if (center)
        {
            bool any = selected >= 0 && selected < options.Count;
            center!.text = any ? WheelText.Label(options[selected].Label) + Detail(options[selected]) : WheelText.Title() + "\n<size=70%>" + target.label + "</size>" + HereInfo();
            center.color = any ? UiTheme.AccentColor : UiTheme.GeneralText; center.fontSize = any ? 22f : 26f;
            FitPlate(centerPlate, center, PlateKind.Card, CenterTextW, 0f);
        }
    }

    private static string Who(string id) => id.Length == 0 ? WheelText.Unclaimed() : WheelText.ClaimedBy(id == Lycoreia.Mod.LocalId ? WheelText.You() : CoopRuntime.NameOf(id));

    /// <summary>Wheel centre while an entry is highlighted: the full job text and who took it (or the note's text and author).</summary>
    private static string Detail(WheelOption o)
    {
        var board = CoopRuntime.Board;
        switch (o.Action)
        {
            case WheelAction.ClaimJob: case WheelAction.UnclaimJob: case WheelAction.FinishJob: case WheelAction.RemoveJob:
                var j = board.Jobs.FirstOrDefault(x => x.Id == o.JobId);
                return j == null ? "" : "\n<size=65%>" + j.Text + "\n" + Who(j.ClaimedBy) + "</size>";
            case WheelAction.RemoveNote:
                var n = board.Notes.FirstOrDefault(x => x.Id == o.JobId);
                return n == null ? "" : "\n<size=65%>" + n.Text + "\n" + WheelText.NoteBy(n.Author == Lycoreia.Mod.LocalId ? WheelText.You() : CoopRuntime.NameOf(n.Author)) + "</size>";
        }
        return "";
    }

    /// <summary>Wheel centre when nothing is highlighted: the jobs here and who took them.</summary>
    private static string HereInfo()
    {
        var sb = new System.Text.StringBuilder(); var seen = new HashSet<int>();
        foreach (var o in options)
        {
            if (o.JobId <= 0 || o.Action == WheelAction.RemoveNote || !seen.Add(o.JobId)) continue;
            var j = CoopRuntime.Board.Jobs.FirstOrDefault(x => x.Id == o.JobId); if (j == null) continue;
            sb.Append("\n<size=55%>").Append(CoopWheelRules.Short(j.Text, 30)).Append(": ").Append(Who(j.ClaimedBy)).Append("</size>");
        }
        return sb.ToString();
    }

    /// <summary>Hold progress ring at the crosshair while the button is held but the wheel is not open yet (progress 0..1; negative hides it).</summary>
    private static void Charge(float progress)
    {
        bool show = progress > 0.25f && progress < 1f;                                         // skip the first part so a tap shows nothing
        if (!show) { if (chargeRoot && chargeRoot!.activeSelf) chargeRoot.SetActive(false); return; }
        if (!chargeRoot)
        {
            var ring = CoopWheelArt.Ring(); if (!ring) return;
            chargeRoot = new GameObject("Lycoreia.CoopWheelCharge"); UnityEngine.Object.DontDestroyOnLoad(chargeRoot); MakeCanvas(chargeRoot, 69);
            chargeRing = NewImage("Ring", chargeRoot.transform, Vector2.zero, new Vector2(88, 88), ring);
            chargeRing.type = Image.Type.Filled; chargeRing.fillMethod = Image.FillMethod.Radial360; chargeRing.fillOrigin = (int)Image.Origin360.Top; chargeRing.fillClockwise = true;
            chargeRing.color = new Color(UiTheme.AccentColor.r, UiTheme.AccentColor.g, UiTheme.AccentColor.b, 0.85f);
        }
        if (!chargeRoot!.activeSelf) chargeRoot.SetActive(true);
        chargeRing!.fillAmount = Mathf.Clamp01(progress);
    }

    private static TextMeshProUGUI Text(Transform parent, Vector2 pos, Vector2 size, float fontSize, FontStyles style)
    {
        var go = new GameObject("Text"); go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>(); rt.anchoredPosition = pos; rt.sizeDelta = size;
        var t = go.AddComponent<TextMeshProUGUI>(); var font = UiFonts.Default; if (font) t.font = font;
        TmpOutline.Apply(t, 0.28f, new Color32(0, 0, 0, 235));                                   // dark outline under every label (safe: queued until active)
        t.fontSize = fontSize; t.fontStyle = style; t.alignment = TextAlignmentOptions.Center; t.raycastTarget = false; t.color = UiTheme.GeneralText; t.richText = true;
        return t;
    }

    // ---------------------------------------------------------------- write your own (job or note)
    private static void OpenBox(WheelAction action)
    {
        boxAction = action; BuildBox();
        boxTitle!.text = (action == WheelAction.WriteJob ? WheelText.NewJobAt(target.label) : WheelText.NoteFor(target.label)) + "\n<size=70%>" + WheelText.BoxHint() + "</size>";
        UiFx.Play(UiCue.Open);
        boxInput!.text = string.Empty; boxRoot!.SetActive(true); boxOpen = true; boxOpenedFrame = Time.frameCount;
        InputGate.Set("coop-box", true);
        if (EventSystem.current) EventSystem.current.SetSelectedGameObject(boxInput.gameObject);
        boxInput.ActivateInputField();
    }

    private static void TickBox()
    {
        var kb = Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame) { CloseBox(); UiFx.Play(UiCue.Cancel); return; }
        if (kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) && Time.frameCount > boxOpenedFrame)
        {
            var text = CoopBoard.Clean(boxInput!.text, boxAction == WheelAction.WriteJob ? CoopBoard.MaxJobText : CoopBoard.MaxNoteText);
            if (text.Length > 0) { if (boxAction == WheelAction.WriteJob) Pin(text, target); else Note(text, target); UiFx.Play(UiCue.Success); }
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
        UiPlates.Apply(panel.AddComponent<Image>(), PlateKind.Card, new Color(UiPlates.FillStrong.r, UiPlates.FillStrong.g, UiPlates.FillStrong.b, 0.95f), true, true, 170f);
        boxTitle = Text(panel.transform, new Vector2(0, 42), new Vector2(720, 70), 24f, FontStyles.Bold);

        var field = new GameObject("Input"); field.SetActive(false); field.transform.SetParent(panel.transform, false);
        var frt = field.AddComponent<RectTransform>(); frt.anchoredPosition = new Vector2(0, -38); frt.sizeDelta = new Vector2(700, 56);
        var bg = field.AddComponent<Image>(); UiPlates.Apply(bg, PlateKind.Label, UiTheme.SecondaryColor, true, false, 56f);
        var area = new GameObject("Text Area"); area.transform.SetParent(field.transform, false);
        var art = area.AddComponent<RectTransform>(); art.anchorMin = Vector2.zero; art.anchorMax = Vector2.one; art.offsetMin = new Vector2(12, 4); art.offsetMax = new Vector2(-12, -4);
        area.AddComponent<RectMask2D>();
        var text = Text(area.transform, Vector2.zero, Vector2.zero, 24f, FontStyles.Normal);
        var trt = text.rectTransform; trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
        text.alignment = TextAlignmentOptions.MidlineLeft; text.textWrappingMode = TextWrappingModes.NoWrap;
        boxInput = field.AddComponent<TMP_InputField>();
        boxInput.textViewport = art; boxInput.textComponent = text; var font = UiFonts.Default; if (font) boxInput.fontAsset = font;
        boxInput.pointSize = 24f; boxInput.targetGraphic = bg; boxInput.transition = Selectable.Transition.None; boxInput.lineType = TMP_InputField.LineType.SingleLine;
        boxInput.characterLimit = CoopBoard.MaxJobText; boxInput.restoreOriginalTextOnEscape = false; boxInput.customCaretColor = true; boxInput.caretColor = Color.white; boxInput.caretWidth = 3;
        field.SetActive(true);
        boxRoot.SetActive(false);
    }
}

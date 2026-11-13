// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Il2CppTMPro;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Lycoreia.Owned.Components.UI;

internal enum UiColor : byte { Primary, Secondary, Accent, Transparent, TextCategory, TextGeneral, TextWarning, TextButton }

internal enum UiButtonStyle : byte { Normal, Alternative, White, Gray }

/// <summary>Colours of the Lycoreia menu.</summary>
internal static class UiTheme
{
    internal static readonly Color PanelColor = new(0.106f, 0.133f, 0.188f, 0.97f);
    internal static readonly Color SecondaryColor = new(0.169f, 0.204f, 0.271f, 1f);
    internal static readonly Color AccentColor = new(0.949f, 0.702f, 0.239f, 1f);
    internal static readonly Color GeneralText = new(0.910f, 0.925f, 0.949f, 1f);
    internal static readonly Color WarningText = new(1f, 0.420f, 0.369f, 1f);

    internal static Color Get(UiColor color) => color switch
    {
        UiColor.Primary => PanelColor,
        UiColor.Secondary => SecondaryColor,
        UiColor.Accent => AccentColor,
        UiColor.Transparent => new Color(0f, 0f, 0f, 0f),
        UiColor.TextCategory => AccentColor,
        UiColor.TextGeneral => GeneralText,
        UiColor.TextWarning => WarningText,
        UiColor.TextButton => Color.white,
        _ => Color.white
    };

    internal static ColorBlock Buttons(UiButtonStyle style)
    {
        Color normal, highlighted, pressed;
        switch (style)
        {
            case UiButtonStyle.Alternative:
                normal = Hex(0x3A4456); highlighted = Hex(0x4A566C); pressed = Hex(0x2B3445); break;
            case UiButtonStyle.White:
                normal = Hex(0xF4F6FA); highlighted = Color.white; pressed = Hex(0xD0D5DE); break;
            case UiButtonStyle.Gray:
                normal = Hex(0x4A5160); highlighted = Hex(0x5E677A); pressed = Hex(0x3A404C); break;
            default:
                normal = Hex(0x3A6FD8); highlighted = Hex(0x5588EE); pressed = Hex(0x2B58B3); break;
        }

        var block = ColorBlock.defaultColorBlock;
        block.normalColor = normal;
        block.highlightedColor = highlighted;
        block.selectedColor = normal;
        block.pressedColor = pressed;
        block.disabledColor = new Color(0.35f, 0.37f, 0.42f, 0.7f);
        block.colorMultiplier = 1f;
        block.fadeDuration = 0.08f;
        return block;
    }

    private static Color Hex(int rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);
}

internal static class UiFonts
{
    private static TMP_FontAsset? cached;

    internal static TMP_FontAsset? Default
    {
        get
        {
            if (cached)
                return cached;
            cached = null;
            try
            {
                var fallback = TMP_Settings.defaultFontAsset;
                if (fallback)
                {
                    cached = fallback;
                    return cached;
                }
            }
            catch
            {
                // look through the loaded assets instead
            }

            try
            {
                TMP_FontAsset? any = null;
                foreach (var candidate in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                {
                    if (!candidate)
                        continue;
                    any ??= candidate;
                    if (!candidate.name.Contains("Fallback"))
                    {
                        cached = candidate;
                        return cached;
                    }
                }

                cached = any;
            }
            catch
            {
                // TMP default is used
            }

            return cached;
        }
    }
}

internal static class UiSprites
{
    private static readonly Dictionary<int, Sprite> Cache = new();

    /// <summary>White rounded square, 9-sliced; one per corner radius.</summary>
    internal static Sprite Rounded(int radius)
    {
        radius = Mathf.Max(1, radius);
        if (Cache.TryGetValue(radius, out var cached) && cached)
            return cached;

        var size = Mathf.Max(64, radius * 2 + 8);
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave
        };
        var pixels = new Color32[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            // Distance outside the rounded rectangle, antialiased over one pixel.
            var dx = Mathf.Max(Mathf.Max(radius - x - 0.5f, x + 0.5f - (size - radius)), 0f);
            var dy = Mathf.Max(Mathf.Max(radius - y - 0.5f, y + 0.5f - (size - radius)), 0f);
            var distance = Mathf.Sqrt(dx * dx + dy * dy) - radius;
            var alpha = (byte)(Mathf.Clamp01(0.5f - distance) * 255f);
            pixels[y * size + x] = new Color32(255, 255, 255, alpha);
        }

        texture.SetPixels32(pixels);
        texture.Apply();
        var sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0u,
            SpriteMeshType.FullRect, new Vector4(radius + 1, radius + 1, radius + 1, radius + 1));
        sprite.hideFlags = HideFlags.HideAndDontSave;
        Cache[radius] = sprite;
        return sprite;
    }
}

internal static class UiCanvas
{
    private static GameObject? rootObject;
    private static RectTransform? rootRect;

    internal static RectTransform Root
    {
        get
        {
            if (rootObject && rootRect)
                return rootRect!;

            rootObject = new GameObject("LycoreiaUI");
            Object.DontDestroyOnLoad(rootObject);
            var canvas = rootObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            RaiseAboveNative(canvas);
            var scaler = rootObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            rootObject.AddComponent<GraphicRaycaster>();
            rootRect = rootObject.GetComponent<RectTransform>();
            return rootRect!;
        }
    }

    /// <summary>
    /// Overlay canvases sort by sorting LAYER first, then order: the in-world native pause menu draws above a Default-layer canvas
    /// whatever its order. Our menu takes the topmost sorting layer and nearly the highest order.
    /// </summary>
    private static void RaiseAboveNative(Canvas canvas)
    {
        canvas.sortingOrder = 32000;
        try
        {
            var layers = SortingLayer.layers;
            if (layers != null && layers.Length > 0) canvas.sortingLayerID = layers[layers.Length - 1].id;   // array is in render order
        }
        catch (System.Exception e) { OwnedLog.LogWarning("LycUi sorting layer: " + e.Message); }
    }

    private static GameObject? ownEventSystem;

    /// <summary>Only the main-menu case may need our own EventSystem; never one in-game, never beside a native one.</summary>
    internal static void EnsureEventSystem()
    {
        try
        {
            ReleaseEventSystemIfNative();
            if (ownEventSystem || EventSystem.current || Object.FindObjectOfType<EventSystem>())
                return;
            if (SceneContext.Instance != null && SceneContext.Instance.Player != null)
                return;
            var go = new GameObject("LycoreiaEventSystem");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<EventSystem>();
            go.AddComponent<InputSystemUIInputModule>();
            ownEventSystem = go;
        }
        catch (Exception ex)
        {
            OwnedLog.LogWarning($"Could not ensure an EventSystem: {ex.Message}");
        }
    }

    /// <summary>Destroys our EventSystem as soon as any other one exists (call every frame).</summary>
    internal static void ReleaseEventSystemIfNative()
    {
        if (!ownEventSystem)
            return;
        foreach (var es in Object.FindObjectsOfType<EventSystem>())
        {
            if (es && es.gameObject != ownEventSystem)
            {
                Object.Destroy(ownEventSystem);
                ownEventSystem = null;
                return;
            }
        }
        if (SceneContext.Instance != null && SceneContext.Instance.Player != null)
        {
            Object.Destroy(ownEventSystem);
            ownEventSystem = null;
        }
    }
}

/// <summary>Esc closes the Lycoreia menu first; the native pause menu must not toggle in the same press.</summary>
[HarmonyPatch(typeof(Il2CppMonomiPark.SlimeRancher.Script.UI.Pause.PauseMenuDirector), "TogglePause")]
internal static class LycoreiaEscapeGuard
{
    private static bool Prefix() => LycoreiaMenu.ClosedFrame != UnityEngine.Time.frameCount;   // one-shot: only the Esc our menu consumed
}

/// <summary>Retained UI node tree rendered into uGUI objects.</summary>
internal abstract class UiNode
{
    public Vector2 Size;
    public Vector2 Position;
    public Vector4? Anchors;
    public List<UiNode> Children = new();
    public RectTransform? Rendered;

    public RectTransform Render(Transform parent)
    {
        var go = new GameObject(GetType().Name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        if (Anchors is { } a)
        {
            rt.anchorMin = new Vector2(a.x, a.y);
            rt.anchorMax = new Vector2(a.z, a.w);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
        else
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = Size;
            rt.anchoredPosition = Position;
        }

        Rendered = rt;
        // One widget that cannot be built (e.g. a game build with a different UI API) must not blank the whole panel.
        try { Build(rt); }
        catch (System.Exception e) { OwnedLog.LogError($"UI widget {GetType().Name} failed to build: {e.GetType().Name}: {e.Message}"); }
        foreach (var child in Children)
        {
            try { child.Render(rt); }
            catch (System.Exception e) { OwnedLog.LogError($"UI widget {child.GetType().Name} failed to render: {e.GetType().Name}: {e.Message}"); }
        }
        return rt;
    }

    protected abstract void Build(RectTransform rt);

    internal static Image AddImage(RectTransform rt, Color color, int cornerRadius, bool raycast)
    {
        var image = rt.gameObject.AddComponent<Image>();
        if (cornerRadius > 0)
        {
            image.sprite = UiSprites.Rounded(cornerRadius);
            image.type = Image.Type.Sliced;
        }

        image.color = color;
        image.raycastTarget = raycast;
        return image;
    }

    internal static RectTransform AddChild(RectTransform parent, string name)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        return rt;
    }

    internal static void Stretch(RectTransform rt, float left = 0f, float bottom = 0f, float right = 0f, float top = 0f)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(left, bottom);
        rt.offsetMax = new Vector2(-right, -top);
    }
}

internal sealed class UiPanel : UiNode
{
    public UiColor Color = UiColor.Primary;
    public UnityEngine.Color? CustomColor;
    public Sprite? Sprite;
    public int CornerRadius;
    public Image? Picture;

    protected override void Build(RectTransform rt)
    {
        UnityEngine.Color color = CustomColor ?? UiTheme.Get(Color);
        var transparent = CustomColor == null && Color == UiColor.Transparent;
        Picture = AddImage(rt, color, Sprite ? 0 : CornerRadius, !transparent);
        if (Sprite)
        {
            Picture.sprite = Sprite;
            Picture.type = UnityEngine.UI.Image.Type.Simple;
        }
    }
}

internal sealed class UiText : UiNode
{
    public string Text = string.Empty;
    public float FontSize = 24f;
    public UiColor Color = UiColor.TextGeneral;
    public TextAlignmentOptions Alignment = TextAlignmentOptions.Left;
    public bool AutoSize;
    public float AutoSizeMin = 14f;
    public float AutoSizeMax = 40f;

    protected override void Build(RectTransform rt)
    {
        var text = rt.gameObject.AddComponent<TextMeshProUGUI>();
        var font = UiFonts.Default;
        if (font)
            text.font = font;
        text.text = Text;
        text.fontSize = FontSize;
        text.color = UiTheme.Get(Color);
        text.alignment = Alignment;
        text.raycastTarget = false;
        if (AutoSize)
        {
            text.enableAutoSizing = true;
            text.fontSizeMin = AutoSizeMin;
            text.fontSizeMax = AutoSizeMax;
        }
    }
}

internal sealed class UiButton : UiNode
{
    public int CornerRadius = 16;
    public UiButtonStyle Style = UiButtonStyle.Normal;
    public bool Interactable = true;
    public Action? OnClick;

    protected override void Build(RectTransform rt)
    {
        var image = AddImage(rt, Color.white, CornerRadius, true);
        var button = rt.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.transition = Selectable.Transition.ColorTint;
        button.colors = UiTheme.Buttons(Style);
        button.interactable = Interactable;
        UiFx.Attach(rt.gameObject, scale: true, clickOnPointerDown: false);
        var click = OnClick;
        button.onClick.AddListener((UnityAction)(() =>
        {
            try
            {
                UiSounds.Play(UiSounds.Cue.Click);
                click?.Invoke();
            }
            catch (Exception ex)
            {
                OwnedLog.LogError($"Lycoreia menu action failed: {ex}");
            }
        }));
    }
}

internal sealed class UiInput : UiNode
{
    public string Value = string.Empty;
    public string Placeholder = string.Empty;
    public TMP_InputField.ContentType ContentType = TMP_InputField.ContentType.Standard;
    public float FontSize = 26f;
    public UiColor TextColor = UiColor.TextGeneral;
    public UiColor BackgroundColor = UiColor.Secondary;
    public int CornerRadius = 12;
    public Action<string>? OnChanged;

    protected override void Build(RectTransform rt)
    {
        // Built inactive so TMP_InputField enables with its text components already assigned.
        var go = rt.gameObject;
        go.SetActive(false);
        var background = AddImage(rt, UiTheme.Get(BackgroundColor), CornerRadius, true);

        var area = AddChild(rt, "Text Area");
        Stretch(area, 12f, 4f, 12f, 4f);
        area.gameObject.AddComponent<RectMask2D>();

        var textColor = UiTheme.Get(TextColor);
        var placeholder = MakeText(area, "Placeholder", Placeholder, new Color(textColor.r, textColor.g, textColor.b, textColor.a * 0.5f));
        placeholder.fontStyle = FontStyles.Italic;
        var text = MakeText(area, "Text", string.Empty, textColor);

        var input = go.AddComponent<TMP_InputField>();
        input.textViewport = area;
        input.textComponent = text;
        input.placeholder = placeholder;
        var font = UiFonts.Default;
        if (font)
            input.fontAsset = font;
        input.pointSize = FontSize;
        input.targetGraphic = background;
        input.transition = Selectable.Transition.None;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.contentType = ContentType;
        input.restoreOriginalTextOnEscape = false;
        input.onFocusSelectAll = false;
        input.customCaretColor = true;
        input.caretColor = Color.white;
        input.caretWidth = 3;
        input.caretBlinkRate = 0.8f;
        var accent = UiTheme.AccentColor;
        input.selectionColor = new Color(accent.r, accent.g, accent.b, 0.45f);
        input.text = Value ?? string.Empty;
        var changed = OnChanged;
        input.onValueChanged.AddListener((UnityAction<string>)(v => changed?.Invoke(v ?? string.Empty)));
        go.SetActive(true);
    }

    private TextMeshProUGUI MakeText(RectTransform parent, string name, string value, Color color)
    {
        var rect = AddChild(parent, name);
        Stretch(rect);
        var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        var font = UiFonts.Default;
        if (font)
            text.font = font;
        text.fontSize = FontSize;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Overflow;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.color = color;
        text.raycastTarget = false;
        text.text = value;
        return text;
    }
}

internal sealed class UiCheckbox : UiNode
{
    public bool Value;
    public UiColor BackgroundColor = UiColor.Secondary;
    public UiColor CheckColor = UiColor.Accent;
    public int CornerRadius = 10;
    public Action<bool>? OnChanged;

    protected override void Build(RectTransform rt)
    {
        var background = AddImage(rt, UiTheme.Get(BackgroundColor), CornerRadius, true);
        var check = AddChild(rt, "Checkmark");
        Stretch(check, 8f, 8f, 8f, 8f);
        var checkImage = AddImage(check, UiTheme.Get(CheckColor), Mathf.Max(4, CornerRadius - 4), false);

        var toggle = rt.gameObject.AddComponent<Toggle>();
        toggle.targetGraphic = background;
        toggle.graphic = checkImage;
        toggle.transition = Selectable.Transition.None;
        toggle.isOn = Value;
        UiFx.Attach(rt.gameObject, scale: true, clickOnPointerDown: true, hoverScale: 1.08f);
        var changed = OnChanged;
        toggle.onValueChanged.AddListener((UnityAction<bool>)(v => changed?.Invoke(v)));
    }
}

internal sealed class UiSlider : UiNode
{
    public float Min, Max = 1f, Value;
    public bool WholeNumbers;
    public UiColor BackgroundColor = UiColor.Secondary;
    public UiColor HandleColor = UiColor.Accent;
    public Action<float>? OnChanged;

    protected override void Build(RectTransform rt)
    {
        var track = AddChild(rt, "Background");
        track.anchorMin = new Vector2(0f, 0.3f);
        track.anchorMax = new Vector2(1f, 0.7f);
        track.offsetMin = track.offsetMax = Vector2.zero;
        AddImage(track, UiTheme.Get(BackgroundColor), 8, true);

        var fillArea = AddChild(rt, "Fill Area");
        fillArea.anchorMin = new Vector2(0f, 0.3f);
        fillArea.anchorMax = new Vector2(1f, 0.7f);
        fillArea.offsetMin = new Vector2(8f, 0f);
        fillArea.offsetMax = new Vector2(-8f, 0f);
        var fill = AddChild(fillArea, "Fill");
        fill.anchorMin = Vector2.zero;
        fill.anchorMax = new Vector2(0f, 1f);
        fill.offsetMin = fill.offsetMax = Vector2.zero;
        var handleColor = UiTheme.Get(HandleColor);
        AddImage(fill, new Color(handleColor.r, handleColor.g, handleColor.b, 0.55f), 8, false);

        var slideArea = AddChild(rt, "Handle Slide Area");
        Stretch(slideArea, 12f, 0f, 12f, 0f);
        var handle = AddChild(slideArea, "Handle");
        handle.anchorMin = handle.anchorMax = new Vector2(0f, 0.5f);
        handle.pivot = new Vector2(0.5f, 0.5f);
        handle.sizeDelta = new Vector2(24f, 36f);
        var handleImage = AddImage(handle, handleColor, 10, true);

        var slider = rt.gameObject.AddComponent<Slider>();
        slider.fillRect = fill;
        slider.handleRect = handle;
        slider.targetGraphic = handleImage;
        slider.direction = Slider.Direction.LeftToRight;
        slider.transition = Selectable.Transition.None;
        slider.minValue = Min;
        slider.maxValue = Max;
        slider.wholeNumbers = WholeNumbers;
        slider.SetValueWithoutNotify(Value);
        UiFx.Attach(rt.gameObject, scale: false, clickOnPointerDown: true);
        var changed = OnChanged;
        slider.onValueChanged.AddListener((UnityAction<float>)(v => changed?.Invoke(v)));
    }
}

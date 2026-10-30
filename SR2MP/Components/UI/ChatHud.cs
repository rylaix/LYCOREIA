using System.Text.RegularExpressions;
using Il2CppTMPro;
using Starlight.Enums;
using Starlight.Enums.Sounds;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SR2MP.Components.UI;

/// <summary>
/// In-game chat drawn with uGUI + TextMeshPro on its own overlay canvas (replaces the IMGUI box).
/// Enter opens it (animated), Enter sends, Esc closes, F5 hides it. Closed, recent messages stay for a few
/// seconds and fade. Typing plays soft procedural ticks. Driven every frame by <see cref="MultiplayerUI"/>.
/// </summary>
internal static class ChatHud
{
    // Layout in reference pixels (1920x1080, scaled with screen height).
    private const float Width = 620f;
    private const float LogHeight = 330f;
    private const float InputHeight = 50f;
    private const float Padding = 12f;
    private const float LeftMargin = 28f;
    private const float BottomMargin = 250f; // above the health/energy bars
    private const float RowGap = 4f;
    private const float FontSize = 24f;
    private const int MaxRows = 60;

    private const float OpenSeconds = 0.22f;
    private const float CloseSeconds = 0.16f;
    private const float MessageVisibleSeconds = 9f;
    private const float MessageFadeSeconds = 1.5f;
    private const float RowAppearSeconds = 0.28f;

    private static readonly Color PanelColor = new(0.07f, 0.08f, 0.11f, 0.86f);
    private static readonly Color InputColor = new(0.14f, 0.16f, 0.22f, 1f);
    private static readonly Color AccentColor = new(0.14f, 0.71f, 0.80f, 1f);
    private static readonly Color RowColor = new(0.05f, 0.06f, 0.08f, 0.55f);

    private sealed class Row
    {
        public MultiplayerUI.ChatMessage Message = null!;
        public RectTransform Rect = null!;
        public Image Background = null!;
        public TextMeshProUGUI Text = null!;
        public CanvasGroup Group = null!;
        public float Height;
        public float BornAt;
    }

    private static GameObject? root;
    private static CanvasGroup? rootGroup;
    private static RectTransform? panel;
    private static Image? panelImage;
    private static RectTransform? logArea;
    private static RectTransform? inputRect;
    private static Image? inputImage;
    private static CanvasGroup? inputGroup;
    private static TMP_InputField? input;
    private static TextMeshProUGUI? counter;
    private static Sprite? roundedSprite;
    private static TMP_FontAsset? font;

    private static readonly List<Row> rows = new();
    private static int seenRevision = -1;

    private static bool open;
    private static float openAmount; // 0 closed .. 1 open (animated)
    private static int openedFrame;
    private static float scroll;
    private static float typingPulse;
    private static float sendFlash;
    private static string lastInputText = string.Empty;
    private static int unfocusedFrames;
    private static bool inputDisabled;
    private static bool buildFailed;
    private static bool lastVisible;

    internal static bool IsOpen => open;

    internal static void Update(MultiplayerUI ui)
    {
        if (buildFailed)
            return;

        try
        {
            var visible = ui.IsChatAllowed && !ui.ChatHidden && inGame
                          && !MenuEUtil.isAnyMenuOpen && !MenuEUtil.isAnyPopUpOpen && UnityEngine.Time.timeScale > 0f;
            if (visible != lastVisible)
            {
                lastVisible = visible;
                SrLogger.LogMessage($"Chat HUD visible={visible} (allowed={ui.IsChatAllowed} hidden={ui.ChatHidden} " +
                                    $"inGame={inGame} menu={MenuEUtil.isAnyMenuOpen} popup={MenuEUtil.isAnyPopUpOpen} " +
                                    $"timeScale={UnityEngine.Time.timeScale})");
            }
            if (!visible)
            {
                if (open)
                    Close(false);
                if (root)
                    root!.SetActive(false);
                openAmount = 0f;
                return;
            }

            if (!root && !Build())
                return;
            root!.SetActive(true);

            SyncRows(ui);
            HandleKeys(ui);
            Animate();
        }
        catch (Exception ex)
        {
            buildFailed = true;
            SrLogger.LogError($"Chat HUD disabled after an error: {ex}");
            if (inputDisabled)
                MultiplayerUI.SetGameInputEnabled(true);
        }
    }

    // ---------------------------------------------------------------- input

    private static void HandleKeys(MultiplayerUI ui)
    {
        var keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        var enter = keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame;
        if (!open)
        {
            if (enter)
                Open();
            return;
        }

        if (keyboard.escapeKey.wasPressedThisFrame)
        {
            Close(true);
            return;
        }

        if (enter && UnityEngine.Time.frameCount != openedFrame)
        {
            var text = input ? input!.text : string.Empty;
            if (!string.IsNullOrWhiteSpace(text))
            {
                ui.SendChatMessage(text);
                sendFlash = 1f;
                ChatSounds.Play(ChatSounds.Kind.Send);
            }

            Close(string.IsNullOrWhiteSpace(text));
            return;
        }

        KeepFocus();
        TrackTyping();

        var mouse = Mouse.current;
        if (mouse != null)
        {
            var wheel = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(wheel) > 0.01f)
                scroll += Mathf.Sign(wheel) * 60f;
        }
    }

    private static void Open()
    {
        open = true;
        openedFrame = UnityEngine.Time.frameCount;
        scroll = 0f;
        unfocusedFrames = 0;
        if (input)
        {
            input!.SetTextWithoutNotify(string.Empty);
            lastInputText = string.Empty;
            Focus();
        }

        if (!inputDisabled)
        {
            MultiplayerUI.SetGameInputEnabled(false);
            inputDisabled = true;
        }

        ChatSounds.Play(ChatSounds.Kind.Open);
    }

    private static void Close(bool playSound)
    {
        open = false;
        scroll = 0f;
        if (input)
        {
            input!.SetTextWithoutNotify(string.Empty);
            input.DeactivateInputField(false);
            if (EventSystem.current && EventSystem.current.currentSelectedGameObject == input.gameObject)
                EventSystem.current.SetSelectedGameObject(null);
        }

        lastInputText = string.Empty;
        if (inputDisabled)
        {
            MultiplayerUI.SetGameInputEnabled(true);
            inputDisabled = false;
        }

        if (playSound)
            ChatSounds.Play(ChatSounds.Kind.Close);
    }

    private static void Focus()
    {
        if (EventSystem.current)
            EventSystem.current.SetSelectedGameObject(input!.gameObject);
        input!.ActivateInputField();
    }

    /// <summary>Game UI may grab the selection; while the chat is open the field keeps it.</summary>
    private static void KeepFocus()
    {
        if (!input || input!.isFocused)
        {
            unfocusedFrames = 0;
            return;
        }

        if (++unfocusedFrames < 2)
            return;

        unfocusedFrames = 0;
        Focus();
        input.MoveTextEnd(false);
    }

    private static void TrackTyping()
    {
        var text = input ? input!.text : string.Empty;
        if (text == lastInputText)
            return;

        ChatSounds.Play(text.Length < lastInputText.Length ? ChatSounds.Kind.Erase : ChatSounds.Kind.Type);
        typingPulse = 1f;
        lastInputText = text;

        if (counter)
        {
            var limit = input!.characterLimit;
            counter!.text = text.Length > limit * 0.75f ? $"{text.Length}/{limit}" : string.Empty;
        }
    }

    // ---------------------------------------------------------------- messages

    private static void SyncRows(MultiplayerUI ui)
    {
        if (ui.ChatRevision == seenRevision)
            return;
        seenRevision = ui.ChatRevision;

        // New messages are those after the last one already shown; anything else (session cleared)
        // rebuilds the log without animation.
        var messages = ui.ChatMessages;
        var lastShown = rows.Count > 0 ? messages.LastIndexOf(rows[^1].Message) : -1;
        if (lastShown < 0 || messages.Count - lastShown - 1 > 20)
        {
            foreach (var row in rows)
                Object.Destroy(row.Rect.gameObject);
            rows.Clear();
            foreach (var message in messages.Skip(Math.Max(0, messages.Count - MaxRows)))
                AddRow(message, animate: false);
        }
        else
        {
            for (var i = lastShown + 1; i < messages.Count; i++)
            {
                AddRow(messages[i], animate: true);
                if (!messages[i].isOwn)
                    ChatSounds.Play(ChatSounds.Kind.Receive);
            }
        }

        while (rows.Count > MaxRows)
        {
            Object.Destroy(rows[0].Rect.gameObject);
            rows.RemoveAt(0);
        }
    }

    private static void AddRow(MultiplayerUI.ChatMessage message, bool animate)
    {
        var go = new GameObject("ChatRow");
        var rect = go.AddComponent<RectTransform>();
        rect.SetParent(logArea, false);
        rect.anchorMin = rect.anchorMax = Vector2.zero;
        rect.pivot = Vector2.zero;

        var group = go.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false;
        group.interactable = false;

        var background = go.AddComponent<Image>();
        background.sprite = roundedSprite;
        background.type = Image.Type.Sliced;
        background.color = RowColor;
        background.raycastTarget = false;

        var textGo = new GameObject("Text");
        var textRect = textGo.AddComponent<RectTransform>();
        textRect.SetParent(rect, false);
        Stretch(textRect, 10f, 4f);
        var text = textGo.AddComponent<TextMeshProUGUI>();
        ApplyFont(text);
        text.fontSize = FontSize;
        text.richText = true;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.alignment = TextAlignmentOptions.TopLeft;
        text.raycastTarget = false;
        text.text = Format(message);

        var maxTextWidth = Width - Padding * 2f - 20f;
        var size = text.GetPreferredValues(text.text, maxTextWidth, 0f);
        var height = Mathf.Ceil(size.y) + 8f;
        rect.sizeDelta = new Vector2(Mathf.Min(Mathf.Ceil(size.x) + 22f, Width - Padding * 2f), height);

        rows.Add(new Row
        {
            Message = message,
            Rect = rect,
            Background = background,
            Text = text,
            Group = group,
            Height = height,
            BornAt = animate ? UnityEngine.Time.unscaledTime : UnityEngine.Time.unscaledTime - RowAppearSeconds
        });
    }

    private static readonly Regex ColoredName = new(@"^<color=#([0-9A-Fa-f]{6})>(.*)</color>$", RegexOptions.Compiled);

    private static string Format(MultiplayerUI.ChatMessage message)
    {
        var time = DateTimeOffset.FromUnixTimeSeconds(message.time).ToLocalTime().ToString("HH:mm");
        var stamp = $"<size=78%><alpha=#80>{time}<alpha=#FF></size>  ";
        if (message.isSystemMessage)
        {
            var color = message.systemMessageType switch
            {
                MultiplayerUI.SystemMessageConnect => "#7CE38B",
                MultiplayerUI.SystemMessageDisconnect => "#FFB347",
                MultiplayerUI.SystemMessageClose => "#FF6B6B",
                _ => "#7FC8F8"
            };
            return $"{stamp}<color={color}>{Escape(message.message)}</color>";
        }

        // Names arrive as "<color=#RRGGBB>name</color>" from the sender: only that form is honoured.
        var match = ColoredName.Match(message.playerName ?? string.Empty);
        var name = match.Success
            ? $"<color=#{match.Groups[1].Value}>{Escape(match.Groups[2].Value)}</color>"
            : Escape(message.playerName ?? "?");
        return $"{stamp}<b>{name}</b>: {Escape(message.message)}";
    }

    /// <summary>Shows text literally (no rich-text tags from other players).</summary>
    private static string Escape(string text)
        => "<noparse>" + text.Replace("</noparse>", "</​noparse>", StringComparison.OrdinalIgnoreCase) + "</noparse>";

    // ---------------------------------------------------------------- animation

    private static void Animate()
    {
        var dt = UnityEngine.Time.unscaledDeltaTime;
        openAmount = Mathf.MoveTowards(openAmount, open ? 1f : 0f, dt / (open ? OpenSeconds : CloseSeconds));
        var eased = open ? EaseOutBack(openAmount) : EaseOutCubic(openAmount);
        var fade = EaseOutCubic(openAmount);

        // Panel: fades in, grows from slightly smaller and slides in from the left.
        panelImage!.color = new Color(PanelColor.r, PanelColor.g, PanelColor.b, PanelColor.a * fade);
        panel!.localScale = new Vector3(Mathf.LerpUnclamped(0.94f, 1f, eased), Mathf.LerpUnclamped(0.9f, 1f, eased), 1f);
        panel.anchoredPosition = new Vector2(LeftMargin - 24f * (1f - fade), BottomMargin);

        // Input: rises from below; typing pulses its colour and size; sending flashes it.
        typingPulse = Mathf.MoveTowards(typingPulse, 0f, dt * 6f);
        sendFlash = Mathf.MoveTowards(sendFlash, 0f, dt * 3f);
        inputGroup!.alpha = fade;
        inputGroup.interactable = open;
        inputGroup.blocksRaycasts = open;
        inputRect!.anchoredPosition = new Vector2(Padding, Padding - 18f * (1f - eased));
        var punch = 1f + 0.018f * typingPulse;
        inputRect.localScale = new Vector3(punch, punch, 1f);
        inputImage!.color = Color.Lerp(InputColor, AccentColor, 0.28f * typingPulse + 0.6f * sendFlash);

        LayoutRows(fade);
    }

    private static void LayoutRows(float openFade)
    {
        var now = UnityEngine.Time.unscaledTime;
        var viewHeight = LogHeight;
        var total = rows.Sum(r => r.Height + RowGap);
        scroll = Mathf.Clamp(scroll, 0f, Mathf.Max(0f, total - viewHeight));

        var y = -scroll;
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            var row = rows[i];
            var appear = Mathf.Clamp01((now - row.BornAt) / RowAppearSeconds);
            var appearEased = EaseOutCubic(appear);

            // Closed chat: messages linger, then fade out.
            var age = now - row.BornAt;
            var linger = 1f - Mathf.Clamp01((age - MessageVisibleSeconds) / MessageFadeSeconds);
            var alpha = Mathf.Max(linger, openFade) * appearEased;
            var onScreen = y + row.Height > 0f && y < viewHeight;

            row.Group.alpha = onScreen ? alpha : 0f;
            row.Rect.anchoredPosition = new Vector2(-28f * (1f - appearEased), y);
            // Each line has its own backing when the panel is hidden; it melts into the panel when open.
            row.Background.color = new Color(RowColor.r, RowColor.g, RowColor.b, RowColor.a * (1f - 0.7f * openFade));
            y += row.Height + RowGap;
        }
    }

    private static float EaseOutCubic(float t) => 1f - Mathf.Pow(1f - Mathf.Clamp01(t), 3f);

    private static float EaseOutBack(float t)
    {
        t = Mathf.Clamp01(t);
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        return 1f + c3 * Mathf.Pow(t - 1f, 3f) + c1 * Mathf.Pow(t - 1f, 2f);
    }

    // ---------------------------------------------------------------- construction

    private static bool Build()
    {
        try
        {
            roundedSprite = MakeRoundedSprite(64, 18);
            font = ResolveFont();

            root = new GameObject("LycoreiaChat");
            Object.DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 400;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f;
            root.AddComponent<GraphicRaycaster>();
            rootGroup = root.AddComponent<CanvasGroup>();
            rootGroup.blocksRaycasts = true;

            var panelGo = new GameObject("Panel");
            panel = panelGo.AddComponent<RectTransform>();
            panel.SetParent(root.transform, false);
            panel.anchorMin = panel.anchorMax = Vector2.zero;
            panel.pivot = new Vector2(0f, 0f);
            panel.sizeDelta = new Vector2(Width, LogHeight + InputHeight + Padding * 3f);
            panelImage = panelGo.AddComponent<Image>();
            panelImage.sprite = roundedSprite;
            panelImage.type = Image.Type.Sliced;
            panelImage.raycastTarget = false;

            var logGo = new GameObject("Log");
            logArea = logGo.AddComponent<RectTransform>();
            logArea.SetParent(panel, false);
            logArea.anchorMin = logArea.anchorMax = Vector2.zero;
            logArea.pivot = Vector2.zero;
            logArea.anchoredPosition = new Vector2(Padding, InputHeight + Padding * 2f);
            logArea.sizeDelta = new Vector2(Width - Padding * 2f, LogHeight);
            logGo.AddComponent<RectMask2D>();

            BuildInput();
            root.SetActive(false);
            SrLogger.LogMessage($"Chat HUD ready (font {(font ? font!.name : "TMP default")}).");
            return true;
        }
        catch (Exception ex)
        {
            buildFailed = true;
            SrLogger.LogError($"Could not build the chat HUD: {ex}");
            if (root)
                Object.Destroy(root);
            root = null;
            return false;
        }
    }

    private static void BuildInput()
    {
        // Built inactive so TMP_InputField enables with its text components already assigned.
        var go = new GameObject("Input");
        go.SetActive(false);
        inputRect = go.AddComponent<RectTransform>();
        inputRect.SetParent(panel, false);
        inputRect.anchorMin = inputRect.anchorMax = Vector2.zero;
        inputRect.pivot = new Vector2(0f, 0f);
        inputRect.sizeDelta = new Vector2(Width - Padding * 2f, InputHeight);
        inputGroup = go.AddComponent<CanvasGroup>();
        inputImage = go.AddComponent<Image>();
        inputImage.sprite = roundedSprite;
        inputImage.type = Image.Type.Sliced;
        inputImage.color = InputColor;

        var areaGo = new GameObject("Text Area");
        var area = areaGo.AddComponent<RectTransform>();
        area.SetParent(inputRect, false);
        Stretch(area, 16f, 6f);
        area.offsetMax = new Vector2(-86f, -6f); // room for the character counter
        areaGo.AddComponent<RectMask2D>();

        var placeholder = MakeText("Placeholder", area, "Say something...   Enter to send · Esc to close");
        placeholder.fontStyle = FontStyles.Italic;
        placeholder.color = new Color(1f, 1f, 1f, 0.38f);
        var text = MakeText("Text", area, string.Empty);

        var counterGo = new GameObject("Counter");
        var counterRect = counterGo.AddComponent<RectTransform>();
        counterRect.SetParent(inputRect, false);
        counterRect.anchorMin = new Vector2(1f, 0f);
        counterRect.anchorMax = new Vector2(1f, 1f);
        counterRect.pivot = new Vector2(1f, 0.5f);
        counterRect.sizeDelta = new Vector2(80f, 0f);
        counterRect.anchoredPosition = new Vector2(-12f, 0f);
        counter = counterGo.AddComponent<TextMeshProUGUI>();
        ApplyFont(counter);
        counter.fontSize = 18f;
        counter.alignment = TextAlignmentOptions.MidlineRight;
        counter.color = new Color(1f, 1f, 1f, 0.5f);
        counter.raycastTarget = false;

        input = go.AddComponent<TMP_InputField>();
        input.textViewport = area;
        input.textComponent = text;
        input.placeholder = placeholder;
        if (font)
            input.fontAsset = font;
        input.pointSize = FontSize;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.characterLimit = 500;
        input.richText = false;
        input.onFocusSelectAll = false;
        input.restoreOriginalTextOnEscape = false;
        input.resetOnDeActivation = true;
        input.customCaretColor = true;
        input.caretColor = Color.white;
        input.caretWidth = 3;
        input.caretBlinkRate = 0.9f;
        input.selectionColor = new Color(AccentColor.r, AccentColor.g, AccentColor.b, 0.45f);
        input.targetGraphic = inputImage;
        input.transition = Selectable.Transition.None;
        go.SetActive(true);
    }

    private static TextMeshProUGUI MakeText(string name, RectTransform parent, string value)
    {
        var go = new GameObject(name);
        var rect = go.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        Stretch(rect, 0f, 0f);
        var text = go.AddComponent<TextMeshProUGUI>();
        ApplyFont(text);
        text.fontSize = FontSize;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Overflow;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.raycastTarget = false;
        text.text = value;
        return text;
    }

    private static void Stretch(RectTransform rect, float horizontal, float vertical)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = new Vector2(horizontal, vertical);
        rect.offsetMax = new Vector2(-horizontal, -vertical);
    }

    private static void ApplyFont(TMP_Text text)
    {
        if (font)
            text.font = font;
    }

    /// <summary>The game's own UI font (the one Starlight's menus use), else TMP's default.</summary>
    private static TMP_FontAsset? ResolveFont()
    {
        foreach (var theme in new[] { StarlightMenuFont.Default, StarlightMenuFont.Native, StarlightMenuFont.Bold })
        {
            try
            {
                var candidate = MenuEUtil.GetFontTheme(theme)?.DefaultFont;
                if (candidate)
                    return candidate;
            }
            catch
            {
                // try the next theme
            }
        }

        return TMP_Settings.defaultFontAsset;
    }

    /// <summary>White rounded square used 9-sliced for the panel, rows and input.</summary>
    private static Sprite MakeRoundedSprite(int size, int radius)
    {
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
        return sprite;
    }
}

/// <summary>
/// Small procedural UI sounds for the chat (no assets): soft key ticks, open/close swishes, send/receive
/// blips. Falls back to Starlight's menu sounds if clips cannot be created.
/// </summary>
internal static class ChatSounds
{
    internal enum Kind : byte
    {
        Type,
        Erase,
        Open,
        Close,
        Send,
        Receive,
    }

    private const int Rate = 44100;
    private static GameObject? host;
    private static AudioSource? source;
    private static readonly Dictionary<Kind, AudioClip> Clips = new();
    private static bool failed;
    private static float lastTypeAt;
    private static readonly System.Random Random = new();

    internal static void Play(Kind kind)
    {
        try
        {
            if (kind is Kind.Type or Kind.Erase)
            {
                // Key repeat / fast typing: keep ticks from piling up.
                var now = UnityEngine.Time.unscaledTime;
                if (now - lastTypeAt < 0.025f)
                    return;
                lastTypeAt = now;
            }

            if (failed || !EnsureSource())
            {
                PlayFallback(kind);
                return;
            }

            var (volume, pitchSpread) = kind switch
            {
                Kind.Type => (0.16f, 0.12f),
                Kind.Erase => (0.13f, 0.06f),
                Kind.Receive => (0.22f, 0f),
                _ => (0.24f, 0f)
            };
            source!.pitch = 1f + (float)(Random.NextDouble() * 2 - 1) * pitchSpread;
            source.PlayOneShot(Clips[kind], volume);
        }
        catch (Exception ex)
        {
            failed = true;
            SrLogger.LogWarning($"Chat sounds fall back to menu sounds: {ex.Message}");
        }
    }

    private static void PlayFallback(Kind kind)
    {
        try
        {
            var sound = kind switch
            {
                Kind.Open => MenuSound.OpenPopup,
                Kind.Close => MenuSound.ClosePopup,
                Kind.Send => MenuSound.Apply,
                Kind.Receive => MenuSound.Pop,
                _ => MenuSound.Hover
            };
            AudioEUtil.PlaySound(sound);
        }
        catch
        {
            // sounds are optional
        }
    }

    private static bool EnsureSource()
    {
        if (source)
            return true;

        host = new GameObject("LycoreiaChatSounds");
        Object.DontDestroyOnLoad(host);
        source = host.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 0f;
        source.ignoreListenerPause = true;

        Clips[Kind.Type] = Make("chat-type", 0.035f, Tick(2600f, 0.0045f, 0.35f));
        Clips[Kind.Erase] = Make("chat-erase", 0.035f, Tick(1700f, 0.005f, 0.25f));
        Clips[Kind.Open] = Make("chat-open", 0.12f, Sweep(480f, 880f, 0.12f));
        Clips[Kind.Close] = Make("chat-close", 0.1f, Sweep(760f, 420f, 0.1f));
        Clips[Kind.Send] = Make("chat-send", 0.13f, TwoNotes(660f, 990f, 0.13f));
        Clips[Kind.Receive] = Make("chat-receive", 0.18f, Pop(880f, 0.18f));
        return true;
    }

    private static AudioClip Make(string name, float seconds, Func<float, float> wave)
    {
        var count = Mathf.CeilToInt(seconds * Rate);
        var data = new float[count];
        for (var i = 0; i < count; i++)
            data[i] = Mathf.Clamp(wave(i / (float)Rate), -1f, 1f);

        // Short fade at both ends against clicks.
        var edge = Math.Min(64, count / 4);
        for (var i = 0; i < edge; i++)
        {
            data[i] *= i / (float)edge;
            data[count - 1 - i] *= i / (float)edge;
        }

        var clip = AudioClip.Create(name, count, 1, Rate, false);
        clip.SetData(data, 0);
        clip.hideFlags = HideFlags.HideAndDontSave;
        return clip;
    }

    /// <summary>Soft key tick: a damped high sine plus a little filtered noise.</summary>
    private static Func<float, float> Tick(float frequency, float decay, float noise)
    {
        var rng = new System.Random(7);
        var last = 0f;
        return t =>
        {
            var envelope = Mathf.Exp(-t / decay);
            var white = (float)(rng.NextDouble() * 2 - 1);
            last += (white - last) * 0.35f; // one-pole low-pass: softer than raw noise
            return envelope * (0.65f * Mathf.Sin(2f * Mathf.PI * frequency * t) + noise * last);
        };
    }

    /// <summary>Gliding tone (frequency eased from -> to) with a sine envelope; samples are generated in order.</summary>
    private static Func<float, float> Sweep(float from, float to, float length)
    {
        var phase = 0f;
        return t =>
        {
            var k = Mathf.Clamp01(t / length);
            phase += 2f * Mathf.PI * Mathf.Lerp(from, to, k * k * (3f - 2f * k)) / Rate;
            return 0.45f * Mathf.Sin(phase) * Mathf.Sin(Mathf.PI * k);
        };
    }

    private static Func<float, float> TwoNotes(float first, float second, float length)
    {
        return t =>
        {
            var half = length * 0.45f;
            var (frequency, start) = t < half ? (first, 0f) : (second, half);
            var local = t - start;
            return 0.4f * Mathf.Sin(2f * Mathf.PI * frequency * local) * Mathf.Exp(-local / 0.035f);
        };
    }

    private static Func<float, float> Pop(float frequency, float length)
    {
        return t =>
        {
            var envelope = Mathf.Exp(-t / (length * 0.25f));
            var bend = frequency * (1f + 0.25f * Mathf.Exp(-t / 0.01f));
            return envelope * (0.4f * Mathf.Sin(2f * Mathf.PI * bend * t)
                               + 0.12f * Mathf.Sin(2f * Mathf.PI * bend * 1.5f * t));
        };
    }
}

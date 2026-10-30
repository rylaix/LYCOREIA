using Il2CppInterop.Runtime.Attributes;
using Il2CppTMPro;
using Starlight;
using Starlight.Enums;
using Starlight.Enums.Sounds;
using Starlight.Storage;
using Starlight.UI;
using Starlight.UI.Blueprints;
using Starlight.Utils;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SR2MP.Components.UI;

/// <summary>
/// Native multiplayer menu (Starlight menu + UI blueprints, the same toolkit as Starlight's own Mod
/// menu). Opened from the game's main menu and pause menu ("Multiplayer"). Replaces the legacy IMGUI
/// window, which stays on F4 as a fallback. Starlight discovers and instantiates StarlightMenu types
/// from every package assembly.
/// </summary>
[InjectIntoIL]
internal sealed class LycoreiaMenu : StarlightMenu
{
    private enum Page : byte
    {
        Home,
        Join,
        Host,
        Settings,
    }

    private enum ColorMode : byte
    {
        Palette,
        Code,
        Picker,
    }

    private const float PanelWidth = 1000f;
    private const float MaxPanelHeight = 1000f;
    private const float RowWidth = 860f;
    private const float RowHeight = 64f;
    private const float RefreshSeconds = 0.5f;

    private RectTransform? content;
    private Page page = Page.Home;
    private bool joinByCode = true;
    private string signature = string.Empty;
    private float nextRefreshAt;
    private string notice = string.Empty;

    private string joinCodeInput = string.Empty;
    private string joinIpInput = string.Empty;
    private string joinPortInput = string.Empty;
    private string hostPortInput = string.Empty;
    private string usernameInput = string.Empty;
    private string colorInput = string.Empty;
    private bool allowCheatsInput;
    private bool disableAchievementsInput;
    private bool hostAutomatic = true;
    private ColorMode colorMode = ColorMode.Palette;

    protected override bool createCommands => false;
    protected override bool inGameOnly => false;

    public new static MenuIdentifier GetMenuIdentifier()
        => new("lycoreia.menu", StarlightMenuFont.Bold, StarlightMenuTheme.Starlight, "LycoreiaMenu", true, true);

    internal static void OpenMenu()
    {
        try
        {
            var menu = MenuEUtil.GetMenu<LycoreiaMenu>();
            if (menu)
            {
                // Pausing at the main menu freezes its animated background; only pause inside a save.
                var inGame = ContextShortcuts.inGame;
                menu!.openActions = inGame ? new[] { MenuActions.PauseGameFalse } : Array.Empty<MenuActions>();
                menu.closeActions = inGame
                    ? new[] { MenuActions.UnPauseGameFalse, MenuActions.EnableInput }
                    : new[] { MenuActions.EnableInput };
                menu.Open();
            }
            else
                SrLogger.LogWarning("Lycoreia menu is not available (Starlight did not create it).");
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"Could not open the Lycoreia menu: {ex.Message}");
        }
    }

    protected override void OnAwake()
    {
        GetComponent<RectTransform>().localPosition = Vector2.zero;
        // Same actions as Starlight's own blueprint menu. Do not DisableInput on open: it also blocks the
        // UI input the menu itself needs (the menu looked frozen).
        openActions = new[] { MenuActions.PauseGameFalse };
        closeActions = new[] { MenuActions.UnPauseGameFalse, MenuActions.EnableInput };
    }

    public new static GameObject GetMenuRootObject()
    {
        var root = new GameObject("LycoreiaMenu");
        var rect = root.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        rect.localScale = Vector3.one;
        return root;
    }

    protected override void OnOpen()
    {
        LoadInputsFromConfig();
        var ui = MultiplayerUI.Instance;
        page = ui && ui.IsFirstTimeSetup ? Page.Settings : Page.Home;
        notice = string.Empty;
        ui?.ClearNativeError();
        Rebuild();
    }

    protected override void OnClose()
    {
        DestroyContent();
    }

    public override void OnCloseUIPressed()
    {
        if (MenuEUtil.isAnyPopUpOpen)
            return;

        if (page != Page.Home && !(MultiplayerUI.Instance && MultiplayerUI.Instance.IsFirstTimeSetup))
        {
            page = Page.Home;
            Rebuild();
            return;
        }

        Close();
    }

    protected override void OnUpdate()
    {
        KeepInputFocus();
        UpdateFocusHighlight();
        UpdatePickerDrag();

        var now = UnityEngine.Time.unscaledTime;
        if (now < nextRefreshAt)
            return;
        nextRefreshAt = now + RefreshSeconds;

        // Live state (connecting -> connected, auto host result, player list) without losing typing focus.
        var current = ComputeSignature();
        if (current == signature || IsTyping() || pickerDrag != PickerPart.None)
            return;
        Rebuild();
    }

    private TMP_InputField? focusedInput;
    private Color focusedOriginalColor;

    /// <summary>The selected text field gets an accent background so it is obvious where typing goes.</summary>
    private void UpdateFocusHighlight()
    {
        var selected = EventSystem.current ? EventSystem.current.currentSelectedGameObject : null;
        var input = selected ? selected!.GetComponent<TMP_InputField>() : null;
        if (input == focusedInput)
            return;

        if (focusedInput && focusedInput!.targetGraphic)
            focusedInput.targetGraphic.color = focusedOriginalColor;

        focusedInput = input;
        if (!input || !input!.targetGraphic)
            return;

        focusedOriginalColor = input.targetGraphic.color;
        var accent = currentTheme != null ? currentTheme.AccentColor : new Color(1f, 0.75f, 0.2f);
        input.targetGraphic.color = Color.Lerp(focusedOriginalColor, accent, 0.55f);
    }

    private void StyleInputs()
    {
        if (!content)
            return;

        foreach (var input in content!.GetComponentsInChildren<TMP_InputField>(true))
        {
            input.caretWidth = 3;
            input.customCaretColor = true;
            input.caretColor = Color.white;
            input.caretBlinkRate = 0.8f;
            input.onFocusSelectAll = false;
        }
    }

    private bool IsTyping()
    {
        var selected = EventSystem.current ? EventSystem.current.currentSelectedGameObject : null;
        return lockedInput || selected && selected.GetComponent<TMP_InputField>();
    }

    private TMP_InputField? lockedInput;
    private int lockedLostFrames;

    /// <summary>
    /// A clicked text field keeps keyboard focus until the player clicks elsewhere or presses Enter/Esc/Tab:
    /// the game's own menu focus (pause/main menu underneath) could take the selection right after the
    /// click, so the field lost focus before anything could be typed.
    /// </summary>
    [HideFromIl2Cpp]
    private void KeepInputFocus()
    {
        var mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.wasPressedThisFrame)
        {
            lockedInput = InputUnderPointer(mouse.position.ReadValue());
            lockedLostFrames = 0;
            return;
        }

        if (!lockedInput)
        {
            lockedInput = null;
            return;
        }

        var keyboard = Keyboard.current;
        if (keyboard != null && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame
                                 || keyboard.escapeKey.wasPressedThisFrame || keyboard.tabKey.wasPressedThisFrame))
        {
            lockedInput = null;
            return;
        }

        if (lockedInput!.isFocused)
        {
            lockedLostFrames = 0;
            return;
        }

        // Give the click a frame to activate the field before treating it as lost.
        if (++lockedLostFrames < 2)
            return;

        lockedLostFrames = 0;
        var events = EventSystem.current;
        var thief = events && events!.currentSelectedGameObject ? events.currentSelectedGameObject.name : "(none)";
        SrLogger.LogDiagnostic("menu", "input-focus-restored", $"field={lockedInput.transform.parent?.name}; selected={thief}",
            "menu:input-focus-restored", 2000);
        if (events && events!.currentSelectedGameObject != lockedInput.gameObject)
            events.SetSelectedGameObject(lockedInput.gameObject);
        lockedInput.ActivateInputField();
        lockedInput.MoveTextEnd(false);
    }

    [HideFromIl2Cpp]
    private TMP_InputField? InputUnderPointer(Vector2 pointer)
    {
        if (!content)
            return null;

        foreach (var field in content!.GetComponentsInChildren<TMP_InputField>(true))
        {
            var rect = field.GetComponent<RectTransform>();
            if (rect && field.interactable && Contains(rect, pointer))
                return field;
        }

        return null;
    }

    private string ComputeSignature()
    {
        var ui = MultiplayerUI.Instance;
        var players = string.Join(",", PlayerManager.GetAllPlayers().Select(p => p.Username));
        return $"{page}|{joinByCode}|{ContextShortcuts.inGame}|{Main.Client.IsConnecting}|{Main.Client.IsConnected}|" +
               $"{Main.Server.IsRunning}|{players}|{ui?.HostJoinCode}|{ui?.IsHostAutoInProgress}|" +
               $"{ui?.HostAutoError}|{ui?.NativeLastError}|{notice}|{DirectLaunch.StatusText}|{DirectLaunch.LastError}";
    }

    private void LoadInputsFromConfig()
    {
        usernameInput = Main.RawUsername;
        colorInput = Main.UsernameColor;
        allowCheatsInput = Main.AllowCheats;
        disableAchievementsInput = Main.DisableAchievements;
        joinIpInput = Main.SavedConnectIP;
        joinPortInput = Main.SavedConnectPort;
        hostPortInput = string.IsNullOrWhiteSpace(Main.SavedHostPort) ? "1919" : Main.SavedHostPort;
        SyncPickerFromInput();
    }

    private void DestroyContent()
    {
        if (content)
            Destroy(content!.gameObject);
        content = null;
    }

    private void Rebuild()
    {
        DestroyContent();
        signature = ComputeSignature();

        try
        {
            focusedInput = null;
            lockedInput = null;
            content = BuildPanel().Render(currentTheme, currentFontTheme, transform);
            StyleInputs();
            BindPicker();
        }
        catch (Exception ex)
        {
            SrLogger.LogError($"Lycoreia menu render failed: {ex}");
        }
    }

    // ---------------------------------------------------------------- pages

    private PanelUIBlueprintV01 BuildPanel()
    {
        var rows = new List<UIBlueprint>();
        rows.Add(Title($"{BuildInfo.Name} {BuildInfo.Version}"));

        var ui = MultiplayerUI.Instance;
        if (!ui)
        {
            rows.Add(Label("The multiplayer UI is still starting. Try again in a moment."));
        }
        else if (page == Page.Settings || ui!.IsFirstTimeSetup)
        {
            AddSettingsPage(rows, ui!);
        }
        else if (Main.Client.IsConnecting)
        {
            rows.Add(Label("Connecting to the host..."));
            rows.Add(Button("Cancel", () => Main.Client.Disconnect()));
        }
        else if (Main.Client.IsConnected)
        {
            AddConnectedPage(rows);
        }
        else if (Main.Server.IsRunning)
        {
            AddHostingPage(rows, ui);
        }
        else if (DirectLaunch.IsBusy)
        {
            rows.Add(Heading(DirectLaunch.StatusText));
            rows.Add(Label("This menu can be closed; it continues in the background."));
            rows.Add(Button("Cancel", DirectLaunch.Cancel, alternative: true));
        }
        else if (!ContextShortcuts.inGame && page == Page.Join)
        {
            AddJoinPage(rows, ui);
        }
        else if (!ContextShortcuts.inGame && page == Page.Host)
        {
            AddMainMenuHostPage(rows, ui);
        }
        else if (!ContextShortcuts.inGame)
        {
            AddMainMenuPage(rows);
        }
        else if (page == Page.Join)
        {
            AddJoinPage(rows, ui);
        }
        else if (page == Page.Host)
        {
            AddHostPage(rows, ui);
        }
        else
        {
            AddInGameHomePage(rows);
        }

        if (!string.IsNullOrWhiteSpace(notice))
            rows.Add(Label(notice, UIColor.TextCategory));

        rows.Add(Button("Close", Close, alternative: true));

        // Stack rows top to bottom (Starlight's scroll layout blueprint is internal): positions are
        // relative to the panel centre, and the panel grows to fit its rows.
        const float padding = 36f;
        const float spacing = 12f;
        var contentHeight = rows.Sum(r => r.Size.y) + spacing * (rows.Count - 1);
        var panelHeight = Mathf.Clamp(contentHeight + padding * 2f, 360f, MaxPanelHeight);
        var y = panelHeight / 2f - padding;
        foreach (var row in rows)
        {
            row.Position = new Vector2(row.Position.x, y - row.Size.y / 2f);
            y -= row.Size.y + spacing;
        }

        return new PanelUIBlueprintV01
        {
            Color = UIColor.Primary,
            CornerRadius = 24,
            Size = new Vector2(PanelWidth, panelHeight),
            Children = rows
        };
    }

    private void AddMainMenuPage(List<UIBlueprint> rows)
    {
        rows.Add(Label($"Playing as <b>{Main.Username}</b>"));
        rows.Add(Button("Host a world", () => Navigate(Page.Host)));
        rows.Add(Button("Join a friend", () => Navigate(Page.Join)));
        AddError(rows, DirectLaunch.LastError);
        rows.Add(Button("Settings", () => Navigate(Page.Settings), alternative: true));
    }

    private const int MaxListedSaves = 5;

    [HideFromIl2Cpp]
    private void AddMainMenuHostPage(List<UIBlueprint> rows, MultiplayerUI ui)
    {
        rows.Add(Heading("Host a world"));
        rows.Add(Row(
            Button("Join code (automatic)", () => { hostAutomatic = true; Rebuild(); }, width: 420f, alternative: !hostAutomatic),
            Button("Fixed port", () => { hostAutomatic = false; Rebuild(); }, width: 420f, alternative: hostAutomatic)));
        if (hostAutomatic)
            rows.Add(Label("Opens a port on your router and gives you a code to share."));
        else
            rows.Add(LabeledInput("Port", hostPortInput, "1919", v => hostPortInput = v,
                TMP_InputField.ContentType.IntegerNumber));

        rows.Add(Heading("Pick the world to host"));
        var saves = DirectLaunch.GetSaves().Where(s => s.GameName != DirectLaunch.JoinSaveName).Take(MaxListedSaves).ToList();
        if (saves.Count == 0)
            rows.Add(Label("No saves yet. Start a new game first, then host from the pause menu.", UIColor.TextWarning));
        foreach (var save in saves)
        {
            rows.Add(Button(SaveLabel(save), () =>
            {
                if (!hostAutomatic && !ui.NativeValidateHostPort(hostPortInput))
                {
                    Rebuild();
                    return;
                }

                var port = hostPortInput;
                var automatic = hostAutomatic;
                DirectLaunch.LoadThen(save, "starting the server", () =>
                {
                    if (automatic)
                        ui.NativeHostAutomatic();
                    else
                        ui.NativeHostOnPort(port);
                }, hosting: true);
                CloseIfLaunching();
            }));
        }

        AddError(rows, ui.NativeLastError);
        AddError(rows, DirectLaunch.LastError);
        rows.Add(Button("Back", () => Navigate(Page.Home), alternative: true));
    }

    private static string SaveLabel(DirectLaunch.SaveEntry save)
        => $"{(int.TryParse(save.DisplayName, out _) ? "Save " + save.DisplayName : save.DisplayName)}" +
           $"  <alpha=#99>({save.Currency:N0} newbucks)";

    /// <summary>Main menu join: validate the address, then load the co-op save and connect there.</summary>
    [HideFromIl2Cpp]
    private void JoinFromMainMenu(MultiplayerUI ui, DirectLaunch.SaveEntry? useSave = null)
    {
        Action join;
        if (joinByCode)
        {
            if (!ui.NativeValidateJoinCode(joinCodeInput))
            {
                Rebuild();
                return;
            }

            var code = joinCodeInput;
            join = () => ui.NativeJoinWithCode(code);
        }
        else
        {
            if (!ui.NativeValidateJoinManual(joinIpInput, joinPortInput))
            {
                Rebuild();
                return;
            }

            var ip = joinIpInput;
            var port = joinPortInput;
            join = () => ui.NativeJoinManual(ip, port);
        }

        if (useSave != null)
            DirectLaunch.LoadThen(useSave, "joining", join);
        else
            DirectLaunch.JoinOnCoopSave("joining", join);
        CloseIfLaunching();
    }

    /// <summary>The load runs in the background; the menu would only sit over the loading screen.</summary>
    [HideFromIl2Cpp]
    private void CloseIfLaunching()
    {
        if (DirectLaunch.IsBusy)
            Close();
        else
            Rebuild();
    }

    private void AddInGameHomePage(List<UIBlueprint> rows)
    {
        rows.Add(Label($"Playing as <b>{Main.Username}</b>"));
        rows.Add(Button("Host this world", () => Navigate(Page.Host)));
        rows.Add(Button("Join a world", () => Navigate(Page.Join)));
        rows.Add(Label("Joining replaces this save with the host's world. Use an empty save to join.",
            UIColor.TextWarning));
        AddError(rows, DirectLaunch.LastError);
        rows.Add(Button("Settings", () => Navigate(Page.Settings), alternative: true));
    }

    private void AddJoinPage(List<UIBlueprint> rows, MultiplayerUI ui)
    {
        rows.Add(Heading("Join a world"));
        rows.Add(Row(
            Button("Join code", () => { joinByCode = true; Rebuild(); }, width: 420f, alternative: !joinByCode),
            Button("IP address", () => { joinByCode = false; Rebuild(); }, width: 420f, alternative: joinByCode)));

        if (joinByCode)
        {
            rows.Add(LabeledInput("Join code", joinCodeInput, "Paste the host's code", v => joinCodeInput = v));
            rows.Add(Button("Join", () => JoinNow(ui)));
        }
        else
        {
            rows.Add(LabeledInput("Host IP", joinIpInput, "e.g. 192.168.1.20", v => joinIpInput = v));
            rows.Add(LabeledInput("Port", joinPortInput, "e.g. 1919", v => joinPortInput = v,
                TMP_InputField.ContentType.IntegerNumber));
            rows.Add(Button("Join", () => JoinNow(ui)));
        }

        if (!ContextShortcuts.inGame)
            rows.Add(Label("Joining uses a separate co-op save, so your own worlds stay untouched."));
        AddError(rows, ui.NativeLastError);
        AddError(rows, DirectLaunch.LastError);
        if (!ContextShortcuts.inGame && DirectLaunch.LastError.Length > 0)
        {
            // All slots full: let the player sacrifice one of their saves explicitly.
            foreach (var save in DirectLaunch.GetSaves().Take(MaxListedSaves))
                rows.Add(Button("Use " + SaveLabel(save), () => JoinFromMainMenu(ui, save), alternative: true));
        }
        rows.Add(Button("Back", () => Navigate(Page.Home), alternative: true));
    }

    [HideFromIl2Cpp]
    private void JoinNow(MultiplayerUI ui)
    {
        if (!ContextShortcuts.inGame)
        {
            JoinFromMainMenu(ui);
            return;
        }

        if (joinByCode)
            ui.NativeJoinWithCode(joinCodeInput);
        else
            ui.NativeJoinManual(joinIpInput, joinPortInput);
    }

    private void AddHostPage(List<UIBlueprint> rows, MultiplayerUI ui)
    {
        rows.Add(Heading("Host this world"));
        rows.Add(Label("Automatic: opens a port on your router and gives you a join code to share."));
        rows.Add(Button(ui.IsHostAutoInProgress ? "Starting server..." : "Host automatically",
            ui.NativeHostAutomatic, interactable: !ui.IsHostAutoInProgress));
        AddError(rows, ui.HostAutoError);

        rows.Add(Heading("Or host on a port"));
        rows.Add(LabeledInput("Port", hostPortInput, "1919", v => hostPortInput = v,
            TMP_InputField.ContentType.IntegerNumber));
        rows.Add(Button("Start server", () => ui.NativeHostOnPort(hostPortInput),
            interactable: !ui.IsHostAutoInProgress));
        AddError(rows, ui.NativeLastError);
        rows.Add(Button("Back", () => Navigate(Page.Home), alternative: true));
    }

    private void AddHostingPage(List<UIBlueprint> rows, MultiplayerUI ui)
    {
        rows.Add(Heading("You are hosting"));
        rows.Add(Label($"Port: {(Main.StreamerMode ? "hidden (streamer mode)" : Main.Server.Port.ToString())}"));

        var code = ui.HostJoinCode;
        if (!string.IsNullOrWhiteSpace(code))
        {
            rows.Add(Label($"Join code: <b>{(Main.StreamerMode ? "hidden (streamer mode)" : code)}</b>"));
            rows.Add(Button("Copy join code", () =>
            {
                GUIUtility.systemCopyBuffer = code;
                notice = "Join code copied.";
            }));
        }

        AddPlayers(rows);
        rows.Add(Button("Resync everyone", () =>
        {
            Main.Server.ReSyncManager.SynchronizeAll();
            notice = "Resync sent to all players.";
        }));
        rows.Add(Button("Stop server", () => Main.Server.Close(), alternative: true));
    }

    private void AddConnectedPage(List<UIBlueprint> rows)
    {
        rows.Add(Heading("Connected"));
        AddPlayers(rows);
        rows.Add(Button("Request resync", () =>
        {
            Main.Server.ReSyncManager.RequestResync();
            notice = "Resync requested from the host.";
        }));
        rows.Add(Button("Disconnect", () => Main.Client.Disconnect(), alternative: true));
    }

    private void AddSettingsPage(List<UIBlueprint> rows, MultiplayerUI ui)
    {
        rows.Add(Heading(ui.IsFirstTimeSetup ? "Welcome! Choose your name" : "Settings"));
        rows.Add(LabeledInput("Username", usernameInput, "Player", v => usernameInput = v));
        rows.Add(Tabs(
            ("Palette", colorMode == ColorMode.Palette, () => SetColorMode(ColorMode.Palette)),
            ("Hex code", colorMode == ColorMode.Code, () => SetColorMode(ColorMode.Code)),
            ("Picker", colorMode == ColorMode.Picker, () => SetColorMode(ColorMode.Picker)),
            ("Random", false, () => { colorInput = Extensions.RandomHexColor(); SyncPickerFromInput(); Rebuild(); })));
        rows.Add(Label(PreviewText()));
        switch (colorMode)
        {
            case ColorMode.Palette:
                AddPalette(rows);
                break;
            case ColorMode.Code:
                rows.Add(LabeledInput("Name color (hex)", colorInput, "FF8800", v =>
                {
                    colorInput = v;
                    if (previewText)
                        previewText!.text = PreviewText();
                }));
                break;
            case ColorMode.Picker:
                AddPicker(rows);
                break;
        }

        rows.Add(CheckboxPair(
            "Allow cheats (host)", allowCheatsInput, v => allowCheatsInput = v,
            "Disable achievements", disableAchievementsInput, v => disableAchievementsInput = v));
        rows.Add(Button("Save", () =>
        {
            ui.NativeSaveSettings(usernameInput, colorInput, allowCheatsInput, disableAchievementsInput);
            if (string.IsNullOrWhiteSpace(ui.NativeLastError))
            {
                notice = "Settings saved.";
                page = Page.Home;
            }
            Rebuild();
        }));
        AddError(rows, ui.NativeLastError);
        if (!ui.IsFirstTimeSetup)
            rows.Add(Button("Back", () => Navigate(Page.Home), alternative: true));
    }

    [HideFromIl2Cpp]
    private string PreviewText()
        => $"Preview: <color=#{SafeColor(colorInput)}>{(string.IsNullOrWhiteSpace(usernameInput) ? "Player" : usernameInput)}</color>" +
           $"  <alpha=#88>#{SafeColor(colorInput)}";

    [HideFromIl2Cpp]
    private void SetColorMode(ColorMode mode)
    {
        colorMode = mode;
        if (mode == ColorMode.Picker)
            SyncPickerFromInput();
        Rebuild();
    }

    // ---------------------------------------------------------------- colour picker (HSV)

    private enum PickerPart : byte
    {
        None,
        Square,
        Hue,
    }

    private const int SquareTextureSize = 64;
    private const int HueTextureSize = 128;
    private const float SquareSize = 260f;
    private const float HueWidth = 56f;
    private const float PickerMarker = 22f;

    private static Texture2D? squareTexture;
    private static Texture2D? hueTexture;
    private static Sprite? squareSprite;
    private static Sprite? hueSprite;
    private static Sprite? swatchSprite;
    private static float squareTextureHue = -1f;

    private float pickerHue;
    private float pickerSat = 1f;
    private float pickerVal = 1f;
    private PickerPart pickerDrag = PickerPart.None;
    private RectTransform? squareRect;
    private RectTransform? hueRect;
    private RectTransform? squareMarker;
    private RectTransform? hueMarker;
    private Image? swatchImage;
    private TMP_Text? previewText;

    [HideFromIl2Cpp]
    private void SyncPickerFromInput()
    {
        if (!ColorUtility.TryParseHtmlString("#" + SafeColor(colorInput), out var color))
            return;

        Color.RGBToHSV(color, out var h, out var sat, out var val);
        // Grey has no hue: keep the current one so the hue bar does not jump.
        if (sat > 0.001f && val > 0.001f)
            pickerHue = h;
        pickerSat = sat;
        pickerVal = val;
    }

    private static Sprite MakeSprite(Texture2D texture)
    {
        var sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    private static Texture2D MakeTexture(int width, int height)
        => new(width, height, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave
        };

    private static void EnsurePickerTextures(float hue)
    {
        if (!hueTexture)
        {
            hueTexture = MakeTexture(1, HueTextureSize);
            var pixels = new Color32[HueTextureSize];
            for (var y = 0; y < HueTextureSize; y++)
                pixels[y] = Color.HSVToRGB(y / (HueTextureSize - 1f), 1f, 1f);
            hueTexture.SetPixels32(pixels);
            hueTexture.Apply();
            hueSprite = MakeSprite(hueTexture);
        }

        if (!swatchSprite)
        {
            var white = MakeTexture(4, 4);
            var pixels = new Color32[16];
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32(255, 255, 255, 255);
            white.SetPixels32(pixels);
            white.Apply();
            swatchSprite = MakeSprite(white);
        }

        if (!squareTexture)
        {
            squareTexture = MakeTexture(SquareTextureSize, SquareTextureSize);
            squareSprite = MakeSprite(squareTexture);
            squareTextureHue = -1f;
        }

        if (Mathf.Abs(squareTextureHue - hue) < 0.0005f)
            return;

        squareTextureHue = hue;
        var square = new Color32[SquareTextureSize * SquareTextureSize];
        for (var y = 0; y < SquareTextureSize; y++)
        for (var x = 0; x < SquareTextureSize; x++)
            square[y * SquareTextureSize + x] = Color.HSVToRGB(hue, x / (SquareTextureSize - 1f), y / (SquareTextureSize - 1f));
        squareTexture!.SetPixels32(square);
        squareTexture.Apply();
    }

    /// <summary>Saturation/brightness square, hue bar and a large swatch of the result.</summary>
    [HideFromIl2Cpp]
    private void AddPicker(List<UIBlueprint> rows)
    {
        EnsurePickerTextures(pickerHue);
        const float gap = 36f;
        var left = -RowWidth / 2f;
        var swatchWidth = RowWidth - SquareSize - HueWidth - gap * 2f;
        ColorUtility.TryParseHtmlString("#" + SafeColor(colorInput), out var current);

        rows.Add(new PanelUIBlueprintV01
        {
            Color = UIColor.Transparent,
            Size = new Vector2(RowWidth, SquareSize),
            Children = new List<UIBlueprint>
            {
                new PanelUIBlueprintV01
                {
                    Sprite = squareSprite,
                    CustomColor = Color.white,
                    Size = new Vector2(SquareSize, SquareSize),
                    Position = new Vector2(left + SquareSize / 2f, 0f),
                    Children = new List<UIBlueprint> { Marker(PickerMarker, PickerMarker, 11) }
                },
                new PanelUIBlueprintV01
                {
                    Sprite = hueSprite,
                    CustomColor = Color.white,
                    Size = new Vector2(HueWidth, SquareSize),
                    Position = new Vector2(left + SquareSize + gap + HueWidth / 2f, 0f),
                    Children = new List<UIBlueprint> { Marker(HueWidth + 12f, 8f, 3) }
                },
                new PanelUIBlueprintV01
                {
                    Sprite = swatchSprite,
                    CustomColor = current,
                    CornerRadius = 16,
                    Size = new Vector2(swatchWidth, SquareSize),
                    Position = new Vector2(RowWidth / 2f - swatchWidth / 2f, 0f)
                }
            }
        });
    }

    private static PanelUIBlueprintV01 Marker(float width, float height, int radius)
        => new()
        {
            Color = UIColor.Transparent,
            Size = new Vector2(width, height),
            Children = new List<UIBlueprint>
            {
                // White ring on a dark outline so it shows on light and dark colours.
                new PanelUIBlueprintV01 { CustomColor = Color.black, CornerRadius = radius + 2, Size = new Vector2(width + 4f, height + 4f) },
                new PanelUIBlueprintV01 { CustomColor = Color.white, CornerRadius = radius, Size = new Vector2(width, height) }
            }
        };

    /// <summary>Finds the rendered picker parts (by their sprites) so dragging can update them in place.</summary>
    [HideFromIl2Cpp]
    private void BindPicker()
    {
        squareRect = hueRect = squareMarker = hueMarker = null;
        swatchImage = null;
        previewText = null;
        if (!content)
            return;

        foreach (var text in content!.GetComponentsInChildren<TMP_Text>(true))
        {
            if (text.text.StartsWith("Preview:", StringComparison.Ordinal))
                previewText = text;
        }

        if (colorMode != ColorMode.Picker || previewText == null)
            return;

        foreach (var image in content.GetComponentsInChildren<Image>(true))
        {
            if (!image.sprite)
                continue;
            if (image.sprite == squareSprite)
                squareRect = image.rectTransform;
            else if (image.sprite == hueSprite)
                hueRect = image.rectTransform;
            else if (image.sprite == swatchSprite)
                swatchImage = image;
        }

        squareMarker = FirstChild(squareRect);
        hueMarker = FirstChild(hueRect);

        PlacePickerMarkers();
    }

    private static RectTransform? FirstChild(RectTransform? rect)
        => rect && rect!.childCount > 0 ? rect.GetChild(0).GetComponent<RectTransform>() : null;

    [HideFromIl2Cpp]
    private void PlacePickerMarkers()
    {
        if (squareMarker && squareRect)
        {
            var size = squareRect!.rect.size;
            squareMarker!.anchoredPosition = new Vector2((pickerSat - 0.5f) * size.x, (pickerVal - 0.5f) * size.y);
        }

        if (hueMarker && hueRect)
            hueMarker!.anchoredPosition = new Vector2(0f, (pickerHue - 0.5f) * hueRect!.rect.size.y);
    }

    [HideFromIl2Cpp]
    private void UpdatePickerDrag()
    {
        if (colorMode != ColorMode.Picker || !squareRect || !hueRect)
        {
            pickerDrag = PickerPart.None;
            return;
        }

        var mouse = Mouse.current;
        if (mouse == null)
            return;

        var pointer = mouse.position.ReadValue();
        if (pickerDrag == PickerPart.None)
        {
            if (!mouse.leftButton.wasPressedThisFrame)
                return;
            if (Contains(squareRect!, pointer))
                pickerDrag = PickerPart.Square;
            else if (Contains(hueRect!, pointer))
                pickerDrag = PickerPart.Hue;
            else
                return;
        }

        if (!mouse.leftButton.isPressed)
        {
            pickerDrag = PickerPart.None;
            Rebuild();
            return;
        }

        var target = pickerDrag == PickerPart.Square ? squareRect! : hueRect!;
        var normalized = Normalized(target, pointer);
        if (pickerDrag == PickerPart.Square)
        {
            pickerSat = normalized.x;
            pickerVal = normalized.y;
        }
        else
        {
            pickerHue = normalized.y;
            EnsurePickerTextures(pickerHue);
        }

        var color = Color.HSVToRGB(pickerHue, pickerSat, pickerVal);
        colorInput = ToHex(color);
        if (swatchImage)
            swatchImage!.color = color;
        if (previewText)
            previewText!.text = PreviewText();
        PlacePickerMarkers();
    }

    /// <summary>ColorUtility.ToHtmlStringRGB is stripped from the game build.</summary>
    private static string ToHex(Color color)
    {
        Color32 c = color;
        return $"{c.r:X2}{c.g:X2}{c.b:X2}";
    }

    private static Camera? CanvasCamera(RectTransform rect)
    {
        var canvas = rect.GetComponentInParent<Canvas>();
        return canvas && canvas!.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
    }

    private static bool Contains(RectTransform rect, Vector2 screenPoint)
        => RectTransformUtility.RectangleContainsScreenPoint(rect, screenPoint, CanvasCamera(rect));

    private static Vector2 Normalized(RectTransform rect, Vector2 screenPoint)
    {
        RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, screenPoint, CanvasCamera(rect), out var local);
        var r = rect.rect;
        return new Vector2(Mathf.Clamp01((local.x - r.xMin) / r.width), Mathf.Clamp01((local.y - r.yMin) / r.height));
    }

    private static readonly string[] PaletteColors =
    {
        "FFFFFF", "FF5A5A", "FF9F43", "FFD93D", "8BE04E", "2ED8A3", "36C5F0", "4D7CFE",
        "A06CFF", "F368E0", "FF8FB1", "C8A27A", "7F8C8D", "B8E994", "82CCDD", "F8C291"
    };

    private const int PaletteColumns = 8;
    private const float SwatchWidth = 96f;
    private const float SwatchHeight = 52f;

    private void AddPalette(List<UIBlueprint> rows)
    {
        var selected = SafeColor(colorInput);
        for (var start = 0; start < PaletteColors.Length; start += PaletteColumns)
        {
            var swatches = new List<UIBlueprint>();
            var count = Math.Min(PaletteColumns, PaletteColors.Length - start);
            var gap = (RowWidth - count * SwatchWidth) / (count - 1);
            for (var i = 0; i < count; i++)
            {
                var hex = PaletteColors[start + i];
                var x = -RowWidth / 2f + SwatchWidth / 2f + i * (SwatchWidth + gap);
                swatches.Add(Swatch(hex, string.Equals(hex, selected, StringComparison.OrdinalIgnoreCase), x));
            }

            rows.Add(new PanelUIBlueprintV01
            {
                Color = UIColor.Transparent,
                Size = new Vector2(RowWidth, SwatchHeight),
                Children = swatches
            });
        }
    }

    private ButtonUIBlueprintV01 Swatch(string hex, bool selected, float x)
    {
        ColorUtility.TryParseHtmlString("#" + hex, out var color);
        return new ButtonUIBlueprintV01
        {
            Size = new Vector2(SwatchWidth, SwatchHeight),
            Position = new Vector2(x, 0f),
            CornerRadius = 12,
            // The selected swatch keeps the bright button frame around it.
            ButtonColors = selected ? UIColorBlock.White : UIColorBlock.GrayButtons,
            UseClickSound = true,
            ClickSound = MenuSound.Click,
            OnClick = () =>
            {
                colorInput = hex;
                SyncPickerFromInput();
                Rebuild();
            },
            Children = new List<UIBlueprint>
            {
                new PanelUIBlueprintV01
                {
                    Size = new Vector2(SwatchWidth - 12f, SwatchHeight - 12f),
                    CornerRadius = 9,
                    CustomColor = color
                }
            }
        };
    }

    private static void AddPlayers(List<UIBlueprint> rows)
    {
        var players = PlayerManager.GetAllPlayers().ToList();
        rows.Add(Heading($"Players ({players.Count})"));
        foreach (var player in players)
            rows.Add(Label(string.IsNullOrEmpty(player.Username) ? "(unnamed player)" : player.Username));
    }

    private static void AddError(List<UIBlueprint> rows, string? error)
    {
        if (!string.IsNullOrWhiteSpace(error))
            rows.Add(Label(error!, UIColor.TextWarning));
    }

    private void Navigate(Page target)
    {
        page = target;
        notice = string.Empty;
        MultiplayerUI.Instance?.ClearNativeError();
        DirectLaunch.ClearError();
        if (target == Page.Settings)
            LoadInputsFromConfig();
        Rebuild();
    }

    private static string SafeColor(string color)
    {
        var value = (color ?? string.Empty).Trim().TrimStart('#');
        return Extensions.IsValidHexColor(value) ? value : "FFFFFF";
    }

    // ---------------------------------------------------------------- blueprint helpers

    private static TextUIBlueprintV01 Text(string text, float width, float height, float fontSize,
        UIColor color, TextAlignmentOptions alignment)
        => new()
        {
            Size = new Vector2(width, height),
            TextContent = text,
            DisableAutoTranslation = true,
            FontSize = fontSize,
            Color = color,
            Alignment = alignment,
            EnableAutoSizing = true,
            FontAutoSizeMin = 14f,
            FontAutoSizeMax = fontSize
        };

    private static TextUIBlueprintV01 Title(string text)
        => Text(text, RowWidth, 70f, 42f, UIColor.TextCategory, TextAlignmentOptions.Center);

    private static TextUIBlueprintV01 Heading(string text)
        => Text(text, RowWidth, 52f, 32f, UIColor.TextCategory, TextAlignmentOptions.Left);

    private static TextUIBlueprintV01 Label(string text, UIColor color = UIColor.TextGeneral, float width = RowWidth)
        => Text(text, width, 48f, 26f, color, TextAlignmentOptions.Left);

    private static ButtonUIBlueprintV01 Button(string text, Action onClick, float width = RowWidth,
        bool alternative = false, bool interactable = true)
        => new()
        {
            Size = new Vector2(width, RowHeight),
            CornerRadius = 16,
            ButtonColors = alternative ? UIColorBlock.AlternativeButtons : UIColorBlock.Buttons,
            Interactable = interactable,
            UseClickSound = true,
            ClickSound = MenuSound.Click,
            OnClick = () =>
            {
                try
                {
                    onClick();
                }
                catch (Exception ex)
                {
                    SrLogger.LogError($"Lycoreia menu action failed: {ex}");
                }
            },
            Children = new List<UIBlueprint>
            {
                new TextUIBlueprintV01
                {
                    TextContent = text,
                    DisableAutoTranslation = true,
                    Alignment = TextAlignmentOptions.Center,
                    FontSize = 28f,
                    Color = UIColor.TextButton,
                    Anchors = new Vector4(0f, 0f, 1f, 1f)
                }
            }
        };

    private static PanelUIBlueprintV01 Row(UIBlueprint left, UIBlueprint right)
    {
        // Two children side by side inside a transparent row of RowWidth.
        left.Position = new Vector2(-(RowWidth - left.Size.x) / 2f, 0f);
        right.Position = new Vector2((RowWidth - right.Size.x) / 2f, 0f);
        return new PanelUIBlueprintV01
        {
            Color = UIColor.Transparent,
            Size = new Vector2(RowWidth, RowHeight),
            Children = new List<UIBlueprint> { left, right }
        };
    }

    /// <summary>Equal-width buttons in one row; the active one uses the normal colours.</summary>
    private static PanelUIBlueprintV01 Tabs(params (string Text, bool Active, Action OnClick)[] tabs)
    {
        const float gap = 12f;
        var width = (RowWidth - gap * (tabs.Length - 1)) / tabs.Length;
        var children = new List<UIBlueprint>();
        for (var i = 0; i < tabs.Length; i++)
        {
            var button = Button(tabs[i].Text, tabs[i].OnClick, width, alternative: !tabs[i].Active);
            button.Position = new Vector2(-RowWidth / 2f + width / 2f + i * (width + gap), 0f);
            children.Add(button);
        }

        return new PanelUIBlueprintV01
        {
            Color = UIColor.Transparent,
            Size = new Vector2(RowWidth, RowHeight),
            Children = children
        };
    }

    private static PanelUIBlueprintV01 LabeledInput(string label, string value, string placeholder,
        Action<string> onChanged, TMP_InputField.ContentType contentType = TMP_InputField.ContentType.Standard)
        => Row(
            Label(label, width: 330f),
            new InputUIBlueprintV01
            {
                Size = new Vector2(510f, RowHeight - 6f),
                CornerRadius = 12,
                DefaultValue = value ?? string.Empty,
                PlaceHolderContent = placeholder,
                DisablePlaceHolderAutoTranslation = true,
                ContentType = contentType,
                FontSize = 26f,
                TextColor = UIColor.TextGeneral,
                BackgroundColor = UIColor.Secondary,
                RestoreOriginalTextOnEscape = false,
                OnValueChanged = v => onChanged(v ?? string.Empty)
            });

    private static CheckboxUIBlueprintV01 Checkbox(bool value, Action<bool> onChanged)
        => new()
        {
            Size = new Vector2(56f, 56f),
            CornerRadius = 10,
            DefaultValue = value,
            BackgroundColor = UIColor.Secondary,
            CheckColor = UIColor.Accent,
            OnValueChanged = onChanged
        };

    /// <summary>Two checkboxes side by side (keeps the settings page within the panel height).</summary>
    private static PanelUIBlueprintV01 CheckboxPair(string leftLabel, bool leftValue, Action<bool> leftChanged,
        string rightLabel, bool rightValue, Action<bool> rightChanged)
    {
        const float half = RowWidth / 2f;
        const float labelWidth = half - 80f;
        var children = new List<UIBlueprint>();
        foreach (var (label, value, changed, offset) in new[]
                 {
                     (leftLabel, leftValue, leftChanged, -half),
                     (rightLabel, rightValue, rightChanged, 0f)
                 })
        {
            var text = Label(label, width: labelWidth);
            text.Position = new Vector2(offset + labelWidth / 2f, 0f);
            var box = Checkbox(value, changed);
            box.Position = new Vector2(offset + half - 28f - 24f, 0f);
            children.Add(text);
            children.Add(box);
        }

        return new PanelUIBlueprintV01
        {
            Color = UIColor.Transparent,
            Size = new Vector2(RowWidth, RowHeight),
            Children = children
        };
    }
}

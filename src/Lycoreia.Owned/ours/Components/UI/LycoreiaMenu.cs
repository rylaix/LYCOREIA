// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Il2CppInterop.Runtime.Attributes;
using Lycoreia.Text;
using Il2CppTMPro;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Lycoreia.Owned.Components.UI;

/// <summary>
/// Multiplayer menu drawn with the Lycoreia UI toolkit (<see cref="LycUi"/> types) on its own overlay canvas.
/// Opened from the launcher button on the main menu and pause menu. Driven every frame by
/// <see cref="MultiplayerUI"/> through <see cref="Tick"/>.
/// </summary>
internal sealed partial class LycoreiaMenu
{
    private enum Page : byte
    {
        Home,
        Join,
        Host,
        Settings,
        WidescreenFov,
        Coop,
        WhatsNew,
        Recap,
    }

    private enum ColorMode : byte
    {
        Palette,
        Code,
        Picker,
    }

    private const float PanelWidth = 1000f;
    private const float RowWidth = 860f;
    private const float RowHeight = 56f;
    private const float RefreshSeconds = 0.5f;

    private static LycoreiaMenu? _instance;
    private static bool _isOpen;
    internal static bool IsOpen => _isOpen;
    internal static int ClosedFrame = -1;

    private GameObject? menuRoot;
    private RectTransform? contentContainer;
    private bool _pausedTime;
    private TimeDirector? _pausedDirector;   // unpause only the director we paused (a scene change replaces it)

    private RectTransform? content;
    private Page page = Page.Home;
    private bool joinByCode = true;
    private bool joinSteam = true;   // rc6: the Steam friends tab is the first tab
    private string signature = string.Empty;
    private float nextRefreshAt;
    private string notice = string.Empty;

    private string joinCodeInput = string.Empty;
    private string letInInput = string.Empty;
    private string joinIpInput = string.Empty;
    private string joinPortInput = string.Empty;
    private string hostPortInput = string.Empty;
    private string usernameInput = string.Empty;
    private string colorInput = string.Empty;
    private bool allowCheatsInput;
    private bool disableAchievementsInput;
    private bool hostAutomatic = true;
    private ColorMode colorMode = ColorMode.Palette;

    private static bool InGame
    {
        get
        {
            var scene = SceneContext.Instance;
            return scene != null && scene.Player != null;
        }
    }

#if LYCOREIA_TEST
    /// <summary>Test-only (photo mode): open the menu on a given page / colour mode.</summary>
    internal static void TestShow(string pageName, string? colorModeName)
    {
        if (!_isOpen)
            OpenMenu();
        var menu = _instance;
        if (menu == null)
            return;
        if (Enum.TryParse<Page>(pageName, true, out var parsed))
            menu.page = parsed;
        if (colorModeName != null && Enum.TryParse<ColorMode>(colorModeName, true, out var mode))
            menu.colorMode = mode;
        menu.hostAutomatic = false;
        menu.joinByCode = false;
        menu.Rebuild();
    }

    internal static void TestClose()
    {
        if (_isOpen)
            _instance?.Close();
    }
#endif

    internal static void OpenMenu()
    {
        try
        {
            if (_isOpen)
                return;
            _instance ??= new LycoreiaMenu();
            _instance.Open();
        }
        catch (Exception ex)
        {
            OwnedLog.LogWarning($"Could not open the Lycoreia menu: {ex.Message}");
        }
    }

    /// <summary>Called every frame by MultiplayerUI.Update.</summary>
    internal static void Tick()
    {
        var menu = _instance;
        if (menu == null || !_isOpen)
            return;
        try
        {
            menu.OnUpdate();
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame && _isOpen)
                menu.OnCloseUIPressed();
        }
        catch (Exception ex)
        {
            OwnedLog.LogError($"Lycoreia menu update failed: {ex}");
        }
    }

    private void EnsureRoot()
    {
        if (menuRoot && contentContainer)
        {
            menuRoot!.transform.SetAsLastSibling();
            return;
        }

        var root = new GameObject("LycoreiaMenu");
        var rect = root.AddComponent<RectTransform>();
        rect.SetParent(UiCanvas.Root, false);
        Stretch(rect);
        menuRoot = root;

        var dim = new GameObject("Dim");
        var dimRect = dim.AddComponent<RectTransform>();
        dimRect.SetParent(rect, false);
        Stretch(dimRect);
        var dimImage = dim.AddComponent<Image>();
        dimImage.color = new Color(0f, 0f, 0f, 0.55f);
        dimImage.raycastTarget = true;

        var container = new GameObject("Content");
        contentContainer = container.AddComponent<RectTransform>();
        contentContainer.SetParent(rect, false);
        Stretch(contentContainer);
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        rect.localScale = Vector3.one;
    }

    private void Open()
    {
        UiCanvas.EnsureEventSystem();
        EnsureRoot();
        probePending = true;
        var wasActive = menuRoot!.activeSelf;
        menuRoot.SetActive(true);
        PlayFade(true, wasActive);
        _isOpen = true;
        // Pause only inside a save (pausing the main menu would freeze its animated background). The
        // native pause count also drives the cursor and input maps; never touch those here.
        if (InGame && !_pausedTime)
        {
            try
            {
                _pausedDirector = SceneContext.Instance.TimeDirector;
                _pausedDirector.Pause();
                _pausedTime = true;
            }
            catch (Exception ex)
            {
                OwnedLog.LogWarning($"Could not pause for the Lycoreia menu: {ex.Message}");
            }
        }

        OnOpen();
        OwnedLog.LogDiagnostic("menu", "menu-open", $"page={page}; inGame={InGame}; scene={UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}; " + InputGate.PointerReport());
    }

    private bool probePending;

    /// <summary>Top raycast hit under the pointer: shows whether something invisible sits above the menu buttons.</summary>
    [HideFromIl2Cpp]
    private string TopHit()
    {
        try
        {
            var es = EventSystem.current; var mouse = Mouse.current;
            if (!es || mouse == null) return "no eventsystem/mouse";
            var ped = new PointerEventData(es!) { position = mouse.position.ReadValue() };
            var hits = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
            es!.RaycastAll(ped, hits);
            if (hits.Count == 0) return "none";
            string Path(GameObject g) { var s = g.name; var t = g.transform.parent; for (int i = 0; t && i < 3; i++, t = t.parent) s = t.name + "/" + s; return s; }
            var top = hits[0].gameObject; bool ours = menuRoot && top && top.transform.IsChildOf(menuRoot!.transform);
            return $"top={(top ? Path(top) : "null")}; ours={ours}; hits={hits.Count}";
        }
        catch (Exception ex) { return "probe failed: " + ex.Message; }
    }

    internal void Close()
    {
        if (_isOpen) OwnedLog.LogDiagnostic("menu", "menu-close", "page=" + page + "; " + InputGate.PointerReport());
        InputGate.Typing(false, false);
        lockedInput = null;
        if (!_isOpen)
            return;
        try
        {
            OnClose();
        }
        finally
        {
            _isOpen = false;
            if (!PlayFade(false, true) && menuRoot)
                menuRoot!.SetActive(false);
            ClosedFrame = UnityEngine.Time.frameCount;
            if (_pausedTime)
            {
                _pausedTime = false;
                try
                {
                    var td = _pausedDirector; _pausedDirector = null;
                    if (td != null && td == SceneContext.Instance?.TimeDirector) td.Unpause();
                }
                catch (Exception ex)
                {
                    OwnedLog.LogWarning($"Could not unpause after the Lycoreia menu: {ex.Message}");
                }
            }
        }
    }

    private void OnOpen()
    {
        LoadInputsFromConfig();
        var ui = MultiplayerUI.Instance;
        page = ui && ui.IsFirstTimeSetup ? Page.Settings : Page.Home;
        notice = string.Empty;
        ui?.ClearNativeError();
        Rebuild();
    }

    private void OnClose()
    {
        // The content stays visible while the close fade runs; it is destroyed when the fade ends
        // (or by Rebuild on reopen).
    }

    private const float FadeSeconds = 0.17f;

    /// <summary>Visual-only open/close tween (unscaled time). Returns false when it could not run.</summary>
    private bool PlayFade(bool opening, bool wasActive)
    {
        try
        {
            if (!menuRoot || !contentContainer)
                return false;
            var fade = UiFade.Ensure(menuRoot!);
            if (opening)
            {
                var startAlpha = wasActive ? (float?)null : 0f;
                fade.Play(contentContainer!.transform, 1f, 1f, FadeSeconds, false, null, startAlpha, startAlpha == null ? null : 0.94f);
            }
            else
            {
                fade.Play(contentContainer!.transform, 0f, 0.96f, FadeSeconds * 0.8f, true, () =>
                {
                    if (!_isOpen)
                        DestroyContent();
                });
            }

            return true;
        }
        catch (Exception ex)
        {
            OwnedLog.LogWarning($"Menu fade failed: {ex.Message}");
            return false;
        }
    }

    private void OnCloseUIPressed()
    {
        if (page != Page.Home && !(MultiplayerUI.Instance && MultiplayerUI.Instance.IsFirstTimeSetup))
        {
            page = Page.Home;
            Rebuild();
            return;
        }

        Close();
    }

    [HideFromIl2Cpp]
    private void OnUpdate()
    {
        FitToScreen();
        InputGate.EnsurePointer("menu");                                                        // clicks must work whatever state the native menu left the input actions in
        if (probePending) { probePending = false; OwnedLog.LogDiagnostic("menu", "raycast-probe", TopHit()); }
        KeepInputFocus();
        InputGate.Typing(IsTyping(), false);                                                    // 2.1: keys typed here never reach the game or the pause menu
        UpdateFocusHighlight();
        UpdatePickerDrag();

        var now = UnityEngine.Time.unscaledTime;
        if (now < nextRefreshAt)
            return;
        nextRefreshAt = now + RefreshSeconds;
        UpdateLiveLines();

        // Live state (connecting -> connected, auto host result, player list) without losing typing focus.
        var current = ComputeSignature();
        if (current == signature || IsTyping() || pickerDrag != PickerPart.None)
            return;
        Rebuild();
    }

    private TMP_InputField? focusedInput;
    private Color focusedOriginalColor;

    /// <summary>The selected text field gets an accent background so it is obvious where typing goes.</summary>
    [HideFromIl2Cpp]
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
        var accent = UiTheme.AccentColor;
        input.targetGraphic.color = Color.Lerp(focusedOriginalColor, accent, 0.55f);
    }

    [HideFromIl2Cpp]
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

    [HideFromIl2Cpp]
    private bool IsTyping()
    {
        var selected = EventSystem.current ? EventSystem.current.currentSelectedGameObject : null;
        var field = selected ? selected!.GetComponent<TMP_InputField>() : null;
        // a field that merely stays SELECTED (after Esc, or after a click on a button) is not typing; only a focused one holds the keys
        return InputGateState.IsTyping(lockedInput, field, field && field!.isFocused);
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
            OwnedLog.LogDiagnostic("menu", "menu-press", TopHit() + "; " + InputGate.PointerReport(), "menu:press", 300);
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
        OwnedLog.LogDiagnostic("menu", "input-focus-restored", $"field={lockedInput.transform.parent?.name}; selected={thief}",
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

    [HideFromIl2Cpp]
    private string ComputeSignature()
    {
        var ui = MultiplayerUI.Instance;
        var players = string.Join(",", Roster.GetAllPlayers().Select(p => p.Username));
        return $"{page}|{joinByCode}|{InGame}|{ModFacade.Client.IsConnecting}|{ModFacade.Client.IsConnected}|" +
               $"{ModFacade.Server.IsRunning}|{players}|{ui?.HostJoinCode}|{ui?.IsHostAutoInProgress}|" +
               $"{ui?.HostAutoStage}|{ui?.HostAutoError}|{ui?.HostAutoWarning}|{ui?.HostCodePending}|{ui?.HostLetIn}|{Lycoreia.Mod.Session?.PunchWaiting}|{Lycoreia.Mod.Session?.Route}|{joinSteam}|{Lycoreia.SteamNet.Unavailable}|{Lycoreia.Mod.Session?.LetInCode}|{string.Join(",", Lycoreia.SteamNet.FriendsHosting.Select(f => f.Name + f.Code))}|{ui?.NativeLastError}|{notice}|{DirectLaunch.StatusText}|{DirectLaunch.LastError}|{Lycoreia.Mod.Update?.Kind}|{CoopSignature()}";
    }

    [HideFromIl2Cpp]
    private void LoadInputsFromConfig()
    {
        usernameInput = ModFacade.RawUsername;
        colorInput = ModFacade.UsernameColor;
        allowCheatsInput = ModFacade.AllowCheats;
        disableAchievementsInput = ModFacade.DisableAchievements;
        joinIpInput = ModFacade.SavedConnectIP;
        joinPortInput = ModFacade.SavedConnectPort;
        hostPortInput = string.IsNullOrWhiteSpace(ModFacade.SavedHostPort) ? "1919" : ModFacade.SavedHostPort;
        SyncPickerFromInput();
    }

    [HideFromIl2Cpp]
    private void DestroyContent()
    {
        if (content)
            Object.Destroy(content!.gameObject);
        content = null;
    }

    private Vector3 renderedContentScale = Vector3.one;

    /// <summary>Keep the temporary menu inside the game viewport, including on window resize.</summary>
    [HideFromIl2Cpp]
    private void FitToScreen()
    {
        if (!content) return;

        // Rendering compensates for its canvas scale when parenting. Preserve that
        // scale and measure the resulting screen pixels instead of using the parent's rect.
        content!.localScale = renderedContentScale;
        var rect = content.rect;
        var camera = CanvasCamera(content);
        var bottomLeft = RectTransformUtility.WorldToScreenPoint(camera,
            content.TransformPoint(new Vector3(rect.xMin, rect.yMin, 0f)));
        var topRight = RectTransformUtility.WorldToScreenPoint(camera,
            content.TransformPoint(new Vector3(rect.xMax, rect.yMax, 0f)));
        var width = Mathf.Abs(topRight.x - bottomLeft.x);
        var height = Mathf.Abs(topRight.y - bottomLeft.y);
        if (width <= 1f || height <= 1f) return;

        var scale = Mathf.Min(1f, Screen.width * 0.94f / width, Screen.height * 0.94f / height);
        content.localScale = renderedContentScale * scale;
    }

    [HideFromIl2Cpp]
    private void Rebuild()
    {
        DestroyContent();
        signature = ComputeSignature();
        expProgressLine = expTeamLine = null;

        try
        {
            focusedInput = null;
            lockedInput = null;
            content = BuildPanel().Render(contentContainer!);
            renderedContentScale = content.localScale;
            FitToScreen();
            StyleInputs();
            BindPicker();
        }
        catch (Exception ex)
        {
            OwnedLog.LogError($"Lycoreia menu render failed: {ex}");
        }
    }

    // ---------------------------------------------------------------- pages

    [HideFromIl2Cpp]
    private UiPanel BuildPanel()
    {
        var rows = new List<UiNode>();
        rows.Add(Title($"{BuildInfo.Name} {BuildInfo.Version}"));
        if (Lycoreia.Mod.GameVersionWarning is { } versionWarning)
            rows.Add(Label(versionWarning, UiColor.TextWarning));
        if (Lycoreia.Mod.Update is { Kind: Lycoreia.UpdateKind.UpdateAvailable or Lycoreia.UpdateKind.AheadOfPublic } update)
        {
            rows.Add(Label(MenuText.UpdateMessage(update), UiColor.TextWarning));
            if (update.Kind == Lycoreia.UpdateKind.UpdateAvailable)
                foreach (var source in update.Offering)
                {
                    var url = source.Url;
                    rows.Add(Button(MenuText.OpenSource(source.Source), () => UnityEngine.Application.OpenURL(url), alternative: true));
                }
        }

        var ui = MultiplayerUI.Instance;
        if (Lycoreia.Mod.GameUnsupported)
        {
            // unsupported game build: only the explanation and Close (no name field, host or join)
        }
        else if (!ui)
        {
            rows.Add(Label(MenuText.StillStarting));
        }
        else if (page == Page.WidescreenFov && !ui!.IsFirstTimeSetup)
        {
            AddWidescreenFovPage(rows);
        }
        else if (page == Page.Settings || ui!.IsFirstTimeSetup)
        {
            AddSettingsPage(rows, ui!);
        }
        else if (page == Page.WhatsNew)
        {
            AddWhatsNewPage(rows);
        }
        else if (page == Page.Recap)
        {
            AddRecapPage(rows);
        }
        else if (page == Page.Coop && Lycoreia.Mod.IsNetworkActive && InGame)
        {
            AddCoopPage(rows);
        }
        else if (ModFacade.Client.IsConnecting)
        {
            rows.Add(Label(string.IsNullOrEmpty(Lycoreia.Mod.Session.Route) ? MenuText.Connecting : MenuText.ConnectingVia(Lycoreia.Mod.Session.Route)));
            if (Lycoreia.Mod.Session.PunchWaiting)
            {
                rows.Add(Label(MenuText.WaitingLetIn, UiColor.TextCategory));
                var letIn = Lycoreia.Mod.Session.LetInCode;
                if (string.IsNullOrEmpty(letIn)) rows.Add(Label(MenuText.LetInPending));
                else
                {
                    rows.Add(Label(MenuText.YourLetInCode(letIn)));
                    rows.Add(Button(MenuText.CopyLetIn, () => { GUIUtility.systemCopyBuffer = letIn; notice = MenuText.CodeCopied; }));
                }
            }
            rows.Add(Button(MenuText.Cancel, () => ModFacade.Client.Disconnect()));
        }
        else if (ModFacade.Client.IsConnected)
        {
            AddConnectedPage(rows);
        }
        else if (ModFacade.Server.IsRunning)
        {
            AddHostingPage(rows, ui);
        }
        else if (DirectLaunch.IsBusy)
        {
            rows.Add(Heading(DirectLaunch.StatusText));
            rows.Add(Label(MenuText.MayCloseMenu));
            rows.Add(Button(MenuText.Cancel, DirectLaunch.Cancel, alternative: true));
        }
        else if (!InGame && page == Page.Join)
        {
            AddJoinPage(rows, ui);
        }
        else if (!InGame && page == Page.Host)
        {
            AddMainMenuHostPage(rows, ui);
        }
        else if (!InGame)
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
            rows.Add(Label(notice, UiColor.TextCategory));

        rows.Add(Button(MenuText.Close, Close, alternative: true));

        // Stack rows top to bottom (a simple fixed stack): positions are
        // relative to the panel centre, and the panel grows to fit its rows.
        const float padding = 28f;
        const float spacing = 8f;
        var contentHeight = rows.Sum(r => r.Size.y) + spacing * (rows.Count - 1);
        // The panel always holds all its rows; FitToScreen shrinks it when it is taller than the screen
        // (a fixed height cap left the last buttons below the box, owner 2026-11-01).
        var panelHeight = Mathf.Max(contentHeight + padding * 2f, 360f);
        var y = panelHeight / 2f - padding;
        foreach (var row in rows)
        {
            row.Position = new Vector2(row.Position.x, y - row.Size.y / 2f);
            y -= row.Size.y + spacing;
        }

        return new UiPanel
        {
            Color = UiColor.Primary,
            CornerRadius = UiPlates.RadiusCard,
            Plate = true,
            Size = new Vector2(PanelWidth, panelHeight),
            Children = rows
        };
    }

    [HideFromIl2Cpp]
    private void AddMainMenuPage(List<UiNode> rows)
    {
        rows.Add(Label(MenuText.PlayingAs(ModFacade.Username)));
        AddSteamStatus(rows);
        rows.Add(Button(MenuText.HostWorld, () => Navigate(Page.Host)));
        rows.Add(Button(MenuText.JoinFriend, () => Navigate(Page.Join)));
        AddError(rows, DirectLaunch.LastError);
        AddWhatsNewAndRecapButtons(rows);
        rows.Add(Button(MenuText.Settings, () => Navigate(Page.Settings), alternative: true));
    }

    private const int MaxListedSaves = 5;

    /// <summary>rc6: one line saying whether Steam connect works, on every Multiplayer home page.</summary>
    [HideFromIl2Cpp]
    private static void AddSteamStatus(List<UiNode> rows)
    {
        if (Lycoreia.SteamNet.Ready) rows.Add(Label(MenuText.SteamStatusReady, UiColor.TextCategory));
        else if ((Lycoreia.SteamNet.Unavailable ?? "").StartsWith("Steam library not loaded") || Lycoreia.SteamNet.Unavailable == "not checked") rows.Add(Label(MenuText.SteamStatusStarting));
        else rows.Add(Label(MenuText.SteamStatusOff(Lycoreia.SteamNet.Unavailable ?? "?"), UiColor.TextWarning));
    }

    [HideFromIl2Cpp]
    private void AddMainMenuHostPage(List<UiNode> rows, MultiplayerUI ui)
    {
        rows.Add(Heading(MenuText.HostWorld));
        rows.Add(Row(
            Button(MenuText.ModeCode, () => { hostAutomatic = true; Rebuild(); }, width: 420f, alternative: !hostAutomatic),
            Button(MenuText.ModePort, () => { hostAutomatic = false; Rebuild(); }, width: 420f, alternative: hostAutomatic)));
        if (hostAutomatic)
            rows.Add(Label(MenuText.HostAutoInfo));
        else
            rows.Add(LabeledInput(MenuText.Port, hostPortInput, "1919", v => hostPortInput = v,
                TMP_InputField.ContentType.IntegerNumber));

        rows.Add(Heading(MenuText.PickWorld));
        var saves = DirectLaunch.GetSaves().Where(s => s.GameName != DirectLaunch.JoinSaveName).Take(MaxListedSaves).ToList();
        if (saves.Count == 0)
            rows.Add(Label(MenuText.NoSaves, UiColor.TextWarning));
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
        rows.Add(Button(MenuText.Back, () => Navigate(Page.Home), alternative: true));
    }

    [HideFromIl2Cpp]
    private static string SaveLabel(DirectLaunch.SaveEntry save)
        => (int.TryParse(save.DisplayName, out _) ? MenuText.SaveNumber(save.DisplayName) : save.DisplayName) +
           "  <alpha=#99>" + MenuText.SaveCurrency((long)save.Currency);

    /// <summary>ModFacade menu join: validate the address, then load the co-op save and connect there.</summary>
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

    [HideFromIl2Cpp]
    private void AddInGameHomePage(List<UiNode> rows)
    {
        rows.Add(Label(MenuText.PlayingAs(ModFacade.Username)));
        AddSteamStatus(rows);
        rows.Add(Button(MenuText.HostThisWorld, () => Navigate(Page.Host)));
        rows.Add(Button(MenuText.JoinWorld, () => Navigate(Page.Join)));
        rows.Add(Label(MenuText.JoinReplaces,
            UiColor.TextWarning));
        AddError(rows, DirectLaunch.LastError);
        AddWhatsNewAndRecapButtons(rows);
        rows.Add(Button(MenuText.Settings, () => Navigate(Page.Settings), alternative: true));
    }

    [HideFromIl2Cpp]
    private void AddJoinPage(List<UiNode> rows, MultiplayerUI ui)
    {
        rows.Add(Heading(MenuText.JoinHeading));
        rows.Add(Tabs(
            (MenuText.TabSteam, joinSteam, () => { joinSteam = true; Rebuild(); }),
            (MenuText.TabJoinCode, !joinSteam && joinByCode, () => { joinSteam = false; joinByCode = true; Rebuild(); }),
            (MenuText.TabIp, !joinSteam && !joinByCode, () => { joinSteam = false; joinByCode = false; Rebuild(); })));

        if (joinSteam)
        {
            if (!Lycoreia.SteamNet.Ready) rows.Add(Label(MenuText.SteamNotHere(Lycoreia.SteamNet.Unavailable ?? "?"), UiColor.TextWarning));
            else
            {
                rows.Add(Label(MenuText.SteamJoinHelp));
                if (Lycoreia.SteamNet.FriendsHosting.Count == 0) rows.Add(Label(MenuText.SteamNobodyHosting, UiColor.TextCategory));
                foreach (var f in Lycoreia.SteamNet.FriendsHosting.Take(6))
                {
                    var friendCode = f.Code;
                    rows.Add(Button(MenuText.JoinSteamFriend(f.Name), () => { joinByCode = true; joinCodeInput = friendCode; JoinNow(ui); }));
                    if (!string.IsNullOrEmpty(f.Version) && f.Version != Lycoreia.ModVersion.Value) rows.Add(Label(MenuText.FriendOtherVersion(f.Version), UiColor.TextWarning));
                }
            }
        }
        else if (joinByCode)
        {
            rows.Add(LabeledInput(MenuText.JoinCode, joinCodeInput, MenuText.JoinCodePlaceholder, v => joinCodeInput = v));
            rows.Add(Button(MenuText.Join, () => JoinNow(ui)));
        }
        else
        {
            rows.Add(LabeledInput(MenuText.HostIp, joinIpInput, MenuText.IpPlaceholder, v => joinIpInput = v));
            rows.Add(LabeledInput(MenuText.Port, joinPortInput, MenuText.PortPlaceholder, v => joinPortInput = v,
                TMP_InputField.ContentType.IntegerNumber));
            rows.Add(Button(MenuText.Join, () => JoinNow(ui)));
        }

        if (!InGame)
            rows.Add(Label(MenuText.SeparateSave));
        AddError(rows, ui.NativeLastError);
        AddError(rows, DirectLaunch.LastError);
        if (!InGame && DirectLaunch.LastError.Length > 0)
        {
            // All slots full: let the player sacrifice one of their saves explicitly.
            foreach (var save in DirectLaunch.GetSaves().Take(MaxListedSaves))
                rows.Add(Button(MenuText.UseSave(SaveLabel(save)), () => JoinFromMainMenu(ui, save), alternative: true));
        }
        rows.Add(Button(MenuText.Back, () => Navigate(Page.Home), alternative: true));
    }

    [HideFromIl2Cpp]
    private void JoinNow(MultiplayerUI ui)
    {
        if (!InGame)
        {
            JoinFromMainMenu(ui);
            return;
        }

        if (joinByCode)
            ui.NativeJoinWithCode(joinCodeInput);
        else
            ui.NativeJoinManual(joinIpInput, joinPortInput);
    }

    [HideFromIl2Cpp]
    private void AddHostPage(List<UiNode> rows, MultiplayerUI ui)
    {
        rows.Add(Heading(MenuText.HostThisWorld));
        rows.Add(Label(MenuText.HostAutoBlurb));
        rows.Add(Button(ui.IsHostAutoInProgress ? ui.HostAutoStage : MenuText.HostAuto,
            ui.NativeHostAutomatic, interactable: !ui.IsHostAutoInProgress));
        if (ui.IsHostAutoInProgress) rows.Add(Button(MenuText.CancelAuto, ui.NativeCancelAutomatic, alternative: true));
        AddError(rows, ui.HostAutoError);

        rows.Add(Heading(MenuText.OrHostPort));
        rows.Add(LabeledInput(MenuText.Port, hostPortInput, "1919", v => hostPortInput = v,
            TMP_InputField.ContentType.IntegerNumber));
        rows.Add(Button(MenuText.StartServer, () => ui.NativeHostOnPort(hostPortInput),
            interactable: !ui.IsHostAutoInProgress));
        AddError(rows, ui.NativeLastError);
        rows.Add(Button(MenuText.Back, () => Navigate(Page.Home), alternative: true));
    }

    [HideFromIl2Cpp]
    private void AddHostingPage(List<UiNode> rows, MultiplayerUI ui)
    {
        rows.Add(Heading(MenuText.YouAreHosting));
        rows.Add(Label(MenuText.PortLine(ModFacade.StreamerMode ? MenuText.Hidden : ModFacade.Server.Port.ToString())));

        var code = ui.HostJoinCode;
        if (ui.HostCodePending) rows.Add(Label(MenuText.CodePending));
        AddError(rows, ui.HostAutoWarning);
        if (!string.IsNullOrWhiteSpace(code))
        {
            rows.Add(Label(MenuText.CodeLine(ModFacade.StreamerMode ? MenuText.Hidden : code)));
            rows.Add(Button(MenuText.CopyCode, () =>
            {
                GUIUtility.systemCopyBuffer = code;
                notice = MenuText.CodeCopied;
            }));
            var lanIp = Lycoreia.Mod.AutoHost?.LanIp;
            if (lanIp != null) rows.Add(Label(MenuText.LanLine(ModFacade.StreamerMode ? MenuText.Hidden : lanIp + ":" + ModFacade.Server.Port)));
            AddError(rows, MenuText.HostReachWarning(Lycoreia.Mod.AutoHost?.Reach ?? Lycoreia.HostReach.Unknown));
            if (Lycoreia.Mod.AutoHost?.FirewallFailed == true && Lycoreia.Mod.AutoHost?.MappedPort > 0) AddError(rows, MenuText.HostFirewallWarning);
            if (ui.HostViaSteam) rows.Add(Label(MenuText.SteamHostLine, UiColor.TextCategory));
            if (ui.HostLetIn)
            {
                rows.Add(Heading(MenuText.LetInHeading));
                rows.Add(Label(MenuText.LetInHelp));
                rows.Add(LabeledInput(MenuText.LetInField, letInInput, MenuText.LetInPlaceholder, v => letInInput = v));
                rows.Add(Button(MenuText.LetInButton, () =>
                {
                    ui.NativeLetIn(letInInput);
                    if (string.IsNullOrEmpty(ui.NativeLastError)) { notice = MenuText.LetInStarted; letInInput = string.Empty; }
                    Rebuild();
                }));
                AddError(rows, ui.NativeLastError);
            }
        }

        AddPlayers(rows);
        if (InGame) rows.Add(Button(MenuText.BoardButton, () => Navigate(Page.Coop)));
        rows.Add(Button(MenuText.ResyncAll, () =>
        {
            ModFacade.Server.ReSyncManager.SynchronizeAll();
            notice = MenuText.ResyncSent;
        }));
        rows.Add(Button(MenuText.Settings, () => Navigate(Page.Settings), alternative: true));
        rows.Add(Button(MenuText.StopServer, () => ModFacade.Server.Close(), alternative: true));
    }

    [HideFromIl2Cpp]
    private void AddConnectedPage(List<UiNode> rows)
    {
        rows.Add(Heading(MenuText.Connected));
        rows.Add(Label(MenuText.ConnectedVia(Lycoreia.Mod.Session.Route), UiColor.TextCategory));
        AddPlayers(rows);
        if (InGame) rows.Add(Button(MenuText.BoardButton, () => Navigate(Page.Coop)));
        rows.Add(Button(MenuText.RequestResync, () =>
        {
            ModFacade.Server.ReSyncManager.RequestResync();
            notice = MenuText.ResyncRequested;
        }));
        rows.Add(Button(MenuText.Settings, () => Navigate(Page.Settings), alternative: true));
        rows.Add(Button(MenuText.Disconnect, () => ModFacade.Client.Disconnect(), alternative: true));
    }

    [HideFromIl2Cpp]
    private void AddSettingsPage(List<UiNode> rows, MultiplayerUI ui)
    {
        rows.Add(Heading(MenuText.SettingsHeading(ui.IsFirstTimeSetup)));
        rows.Add(LabeledInput(MenuText.Username, usernameInput, MenuText.DefaultName, v => usernameInput = v));
        rows.Add(Tabs(
            (MenuText.TabPalette, colorMode == ColorMode.Palette, () => SetColorMode(ColorMode.Palette)),
            (MenuText.TabHex, colorMode == ColorMode.Code, () => SetColorMode(ColorMode.Code)),
            (MenuText.TabPicker, colorMode == ColorMode.Picker, () => SetColorMode(ColorMode.Picker)),
            (MenuText.TabRandom, false, () => { colorInput = ColorUtil.RandomHexColor(); SyncPickerFromInput(); Rebuild(); })));
        rows.Add(Label(PreviewText()));
        switch (colorMode)
        {
            case ColorMode.Palette:
                AddPalette(rows);
                break;
            case ColorMode.Code:
                rows.Add(LabeledInput(MenuText.NameColor, colorInput, "FF8800", v =>
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
            MenuText.AllowCheats, allowCheatsInput, v => allowCheatsInput = v,
            MenuText.DisableAchievements, disableAchievementsInput, v => disableAchievementsInput = v));
        // Saved right away: they only change what friends can do to this player.
        rows.Add(CheckboxPair(
            MenuText.FriendsVac, ModFacade.FriendsCanVacMe, v => ModFacade.SetConfigValue("friends_can_vac_me", v),
            MenuText.FriendsKick, ModFacade.FriendsCanKickMe, v => ModFacade.SetConfigValue("friends_can_kick_me", v)));
        // The FOV module has its own page (the settings page is already at the panel's height limit).
        rows.Add(Button(MenuText.WidescreenHeading, () => Navigate(Page.WidescreenFov), alternative: true));
        rows.Add(Button(MenuText.Save, () =>
        {
            ui.NativeSaveSettings(usernameInput, colorInput, allowCheatsInput, disableAchievementsInput);
            if (string.IsNullOrWhiteSpace(ui.NativeLastError))
            {
                notice = MenuText.SettingsSaved;
                page = Page.Home;
            }
            Rebuild();
        }));
        AddError(rows, ui.NativeLastError);
        if (!ui.IsFirstTimeSetup)
            rows.Add(Button(MenuText.Back, () => Navigate(Page.Home), alternative: true));
    }

    [HideFromIl2Cpp]
    private string PreviewText()
        => $"{MenuText.Preview} <color=#{SafeColor(colorInput)}>{(string.IsNullOrWhiteSpace(usernameInput) ? MenuText.DefaultName : usernameInput)}</color>" +
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

    [HideFromIl2Cpp]
    private static Sprite MakeSprite(Texture2D texture)
    {
        var sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    [HideFromIl2Cpp]
    private static Texture2D MakeTexture(int width, int height)
        => new(width, height, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave
        };

    [HideFromIl2Cpp]
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
    private void AddPicker(List<UiNode> rows)
    {
        EnsurePickerTextures(pickerHue);
        const float gap = 36f;
        var left = -RowWidth / 2f;
        var swatchWidth = RowWidth - SquareSize - HueWidth - gap * 2f;
        ColorUtility.TryParseHtmlString("#" + SafeColor(colorInput), out var current);

        pickerSquareNode = new UiPanel
        {
            Sprite = squareSprite,
            CustomColor = Color.white,
            Size = new Vector2(SquareSize, SquareSize),
            Position = new Vector2(left + SquareSize / 2f, 0f),
            Children = new List<UiNode> { Marker(PickerMarker, PickerMarker, 11) }
        };
        pickerHueNode = new UiPanel
        {
            Sprite = hueSprite,
            CustomColor = Color.white,
            Size = new Vector2(HueWidth, SquareSize),
            Position = new Vector2(left + SquareSize + gap + HueWidth / 2f, 0f),
            Children = new List<UiNode> { Marker(HueWidth + 12f, 8f, 3) }
        };
        pickerSwatchNode = new UiPanel
        {
            Sprite = swatchSprite,
            CustomColor = current,
            CornerRadius = 16,
            Size = new Vector2(swatchWidth, SquareSize),
            Position = new Vector2(RowWidth / 2f - swatchWidth / 2f, 0f)
        };
        rows.Add(new UiPanel
        {
            Color = UiColor.Transparent,
            Size = new Vector2(RowWidth, SquareSize),
            Children = new List<UiNode> { pickerSquareNode, pickerHueNode, pickerSwatchNode }
        });
    }

    private UiPanel? pickerSquareNode;
    private UiPanel? pickerHueNode;
    private UiPanel? pickerSwatchNode;

    [HideFromIl2Cpp]
    private static UiPanel Marker(float width, float height, int radius)
        => new()
        {
            Color = UiColor.Transparent,
            Size = new Vector2(width, height),
            Children = new List<UiNode>
            {
                // White ring on a dark outline so it shows on light and dark colours.
                new UiPanel { CustomColor = Color.black, CornerRadius = radius + 2, Size = new Vector2(width + 4f, height + 4f) },
                new UiPanel { CustomColor = Color.white, CornerRadius = radius, Size = new Vector2(width, height) }
            }
        };

    /// <summary>Keeps the rendered picker parts (from the nodes) so dragging can update them in place.</summary>
    [HideFromIl2Cpp]
    private void BindPicker()
    {
        squareRect = hueRect = squareMarker = hueMarker = null;
        swatchImage = null;
        previewText = null;
        if (colorMode != ColorMode.Picker)
            pickerSquareNode = pickerHueNode = pickerSwatchNode = null;
        if (!content)
            return;

        foreach (var text in content!.GetComponentsInChildren<TMP_Text>(true))
        {
            if (text.text.StartsWith(MenuText.Preview, StringComparison.Ordinal))
                previewText = text;
        }

        if (colorMode != ColorMode.Picker || previewText == null)
            return;

        squareRect = pickerSquareNode?.Rendered;
        hueRect = pickerHueNode?.Rendered;
        swatchImage = pickerSwatchNode?.Picture;

        squareMarker = FirstChild(squareRect);
        hueMarker = FirstChild(hueRect);

        PlacePickerMarkers();
    }

    [HideFromIl2Cpp]
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
    [HideFromIl2Cpp]
    private static string ToHex(Color color)
    {
        Color32 c = color;
        return $"{c.r:X2}{c.g:X2}{c.b:X2}";
    }

    [HideFromIl2Cpp]
    private static Camera? CanvasCamera(RectTransform rect)
    {
        var canvas = rect.GetComponentInParent<Canvas>();
        return canvas && canvas!.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
    }

    [HideFromIl2Cpp]
    private static bool Contains(RectTransform rect, Vector2 screenPoint)
        => RectTransformUtility.RectangleContainsScreenPoint(rect, screenPoint, CanvasCamera(rect));

    [HideFromIl2Cpp]
    private static Vector2 Normalized(RectTransform rect, Vector2 screenPoint)
    {
        RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, screenPoint, CanvasCamera(rect), out var local);
        var r = rect.rect;
        return new Vector2(Mathf.Clamp01((local.x - r.xMin) / r.width), Mathf.Clamp01((local.y - r.yMin) / r.height));
    }

    private static readonly string[] PaletteColors =
    {
        // Lycoreia's own swatches (2026-11-08): row 1 bright slime tones, row 2 soft pastels and neutrals.
        "F4F1EA", "E8414F", "F2793B", "F7C548", "7BC950", "1FB89A", "2E9FE6", "5B5FE8",
        "9B4FD8", "D94FA6", "F5A3C0", "F9D9A8", "B6E3A1", "A3D8F0", "C9B8F2", "6E6A75"
    };

    private const int PaletteColumns = 8;
    private const float SwatchWidth = 96f;
    private const float SwatchHeight = 52f;

    [HideFromIl2Cpp]
    private void AddPalette(List<UiNode> rows)
    {
        var selected = SafeColor(colorInput);
        for (var start = 0; start < PaletteColors.Length; start += PaletteColumns)
        {
            var swatches = new List<UiNode>();
            var count = Math.Min(PaletteColumns, PaletteColors.Length - start);
            var gap = (RowWidth - count * SwatchWidth) / (count - 1);
            for (var i = 0; i < count; i++)
            {
                var hex = PaletteColors[start + i];
                var x = -RowWidth / 2f + SwatchWidth / 2f + i * (SwatchWidth + gap);
                swatches.Add(Swatch(hex, string.Equals(hex, selected, StringComparison.OrdinalIgnoreCase), x));
            }

            rows.Add(new UiPanel
            {
                Color = UiColor.Transparent,
                Size = new Vector2(RowWidth, SwatchHeight),
                Children = swatches
            });
        }
    }

    [HideFromIl2Cpp]
    private UiButton Swatch(string hex, bool selected, float x)
    {
        ColorUtility.TryParseHtmlString("#" + hex, out var color);
        return new UiButton
        {
            Size = new Vector2(SwatchWidth, SwatchHeight),
            Position = new Vector2(x, 0f),
            CornerRadius = 12,
            // The selected swatch keeps the bright button frame around it.
            Style = selected ? UiButtonStyle.White : UiButtonStyle.Gray,
            OnClick = () =>
            {
                colorInput = hex;
                SyncPickerFromInput();
                Rebuild();
            },
            Children = new List<UiNode>
            {
                new UiPanel
                {
                    Size = new Vector2(SwatchWidth - 12f, SwatchHeight - 12f),
                    CornerRadius = 9,
                    CustomColor = color
                }
            }
        };
    }

    [HideFromIl2Cpp]
    private static void AddPlayers(List<UiNode> rows)
    {
        var players = Roster.GetAllPlayers().ToList();
        rows.Add(Heading(MenuText.Players(players.Count)));
        foreach (var player in players)
            rows.Add(Label(string.IsNullOrEmpty(player.Username) ? MenuText.Unnamed : player.Username));
    }

    [HideFromIl2Cpp]
    private static void AddError(List<UiNode> rows, string? error)
    {
        if (!string.IsNullOrWhiteSpace(error))
            rows.Add(Label(error!, UiColor.TextWarning));
    }

    /// <summary>The Widescreen FOV module's page (Features/WidescreenFov); values are saved as they change.</summary>
    [HideFromIl2Cpp]
    private void AddWidescreenFovPage(List<UiNode> rows)
    {
        var fov = Features.WidescreenFov.WidescreenFov.Fov;
        rows.Add(Heading(MenuText.WidescreenHeading));
        rows.Add(Label(MenuText.WidescreenInfo,
            UiColor.TextCategory));
        rows.Add(CheckboxPair(
            MenuText.WidescreenUse, Features.WidescreenFov.WidescreenFov.Enabled,
            v => { Features.WidescreenFov.WidescreenFov.SetEnabled(v); Rebuild(); },
            null, false, null));
        rows.Add(SliderRow(MenuText.FovSlider(Features.WidescreenFov.WidescreenFov.MinFov.ToString("F0"), Features.WidescreenFov.WidescreenFov.MaxFov.ToString("F0")),
            Features.WidescreenFov.WidescreenFov.MinFov, Features.WidescreenFov.WidescreenFov.MaxFov,
            fov, true, Features.WidescreenFov.WidescreenFov.SetFov));
        rows.Add(Label(MenuText.WidescreenHint, UiColor.TextCategory));
        rows.Add(Button(MenuText.Back, () => Navigate(Page.Settings), alternative: true));
    }

    [HideFromIl2Cpp]
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

    [HideFromIl2Cpp]
    private static string SafeColor(string color)
    {
        var value = (color ?? string.Empty).Trim().TrimStart('#');
        return ColorUtil.IsValidHexColor(value) ? value : "FFFFFF";
    }

    // ---------------------------------------------------------------- UI helpers

    [HideFromIl2Cpp]
    private static UiText Text(string text, float width, float height, float fontSize,
        UiColor color, TextAlignmentOptions alignment)
        => new()
        {
            Size = new Vector2(width, height),
            Text = text,
            FontSize = fontSize,
            Color = color,
            Alignment = alignment,
            AutoSize = true,
            AutoSizeMin = 14f,
            AutoSizeMax = fontSize
        };

    [HideFromIl2Cpp]
    private static UiText Title(string text)
        => Text(text, RowWidth, 70f, 42f, UiColor.TextCategory, TextAlignmentOptions.Center);

    [HideFromIl2Cpp]
    private static UiText Heading(string text)
        => Text(text, RowWidth, 52f, 32f, UiColor.TextCategory, TextAlignmentOptions.Left);

    [HideFromIl2Cpp]
    private static UiText Label(string text, UiColor color = UiColor.TextGeneral, float width = RowWidth)
        => Text(text, width, 48f, 26f, color, TextAlignmentOptions.Left);

    [HideFromIl2Cpp]
    private static UiButton Button(string text, Action onClick, float width = RowWidth,
        bool alternative = false, bool interactable = true)
        => new()
        {
            Size = new Vector2(width, RowHeight),
            CornerRadius = UiPlates.RadiusLabel,
            Style = alternative ? UiButtonStyle.Alternative : UiButtonStyle.Normal,
            Interactable = interactable,
            OnClick = onClick,
            Children = new List<UiNode>
            {
                new UiText
                {
                    Text = text,
                    Alignment = TextAlignmentOptions.Center,
                    FontSize = 28f,
                    Color = UiColor.TextButton,
                    Anchors = new Vector4(0f, 0f, 1f, 1f)
                }
            }
        };

    [HideFromIl2Cpp]
    private static UiPanel Row(UiNode left, UiNode right)
    {
        // Two children side by side inside a transparent row of RowWidth.
        left.Position = new Vector2(-(RowWidth - left.Size.x) / 2f, 0f);
        right.Position = new Vector2((RowWidth - right.Size.x) / 2f, 0f);
        return new UiPanel
        {
            Color = UiColor.Transparent,
            Size = new Vector2(RowWidth, RowHeight),
            Children = new List<UiNode> { left, right }
        };
    }

    /// <summary>Equal-width buttons in one row; the active one uses the normal colours.</summary>
    [HideFromIl2Cpp]
    private static UiPanel Tabs(params (string Text, bool Active, Action OnClick)[] tabs)
    {
        const float gap = 12f;
        var width = (RowWidth - gap * (tabs.Length - 1)) / tabs.Length;
        var children = new List<UiNode>();
        for (var i = 0; i < tabs.Length; i++)
        {
            var button = Button(tabs[i].Text, tabs[i].OnClick, width, alternative: !tabs[i].Active);
            button.Position = new Vector2(-RowWidth / 2f + width / 2f + i * (width + gap), 0f);
            children.Add(button);
        }

        return new UiPanel
        {
            Color = UiColor.Transparent,
            Size = new Vector2(RowWidth, RowHeight),
            Children = children
        };
    }

    [HideFromIl2Cpp]
    private static UiPanel LabeledInput(string label, string value, string placeholder,
        Action<string> onChanged, TMP_InputField.ContentType contentType = TMP_InputField.ContentType.Standard)
        => Row(
            Label(label, width: 330f),
            new UiInput
            {
                Size = new Vector2(510f, RowHeight - 6f),
                CornerRadius = UiPlates.RadiusLabel,
                Value = value ?? string.Empty,
                Placeholder = placeholder,
                ContentType = contentType,
                FontSize = 26f,
                TextColor = UiColor.TextGeneral,
                BackgroundColor = UiColor.Secondary,
                OnChanged = v => onChanged(v ?? string.Empty)
            });

    [HideFromIl2Cpp]
    private static UiCheckbox Checkbox(bool value, Action<bool> onChanged)
        => new()
        {
            Size = new Vector2(48f, 48f),
            CornerRadius = UiPlates.RadiusLabel,
            Value = value,
            BackgroundColor = UiColor.Secondary,
            CheckColor = UiColor.Accent,
            OnChanged = onChanged
        };

    /// <summary>Label on the left half, slider on the right half.</summary>
    [HideFromIl2Cpp]
    private static UiPanel SliderRow(string label, float min, float max, float value, bool wholeNumbers,
        Action<float> onChanged)
    {
        const float half = RowWidth / 2f;
        var text = Label(label, width: half - 20f);
        text.Position = new Vector2(-half + (half - 20f) / 2f, 0f);
        var slider = new UiSlider
        {
            Size = new Vector2(half - 40f, 40f),
            Position = new Vector2(half / 2f, 0f),
            Min = min,
            Max = max,
            Value = value,
            WholeNumbers = wholeNumbers,
            BackgroundColor = UiColor.Secondary,
            HandleColor = UiColor.Accent,
            OnChanged = onChanged
        };
        return new UiPanel
        {
            Color = UiColor.Transparent,
            Size = new Vector2(RowWidth, RowHeight),
            Children = new List<UiNode> { text, slider }
        };
    }

    /// <summary>Two checkboxes side by side (keeps the settings page within the panel height).</summary>
    [HideFromIl2Cpp]
    private static UiPanel CheckboxPair(string leftLabel, bool leftValue, Action<bool> leftChanged,
        string? rightLabel, bool rightValue, Action<bool>? rightChanged)
    {
        const float half = RowWidth / 2f;
        const float labelWidth = half - 80f;
        var children = new List<UiNode>();
        foreach (var (label, value, changed, offset) in new[]
                 {
                     (leftLabel, leftValue, leftChanged, -half),
                     (rightLabel, rightValue, rightChanged, 0f)
                 })
        {
            if (label == null || changed == null)
                continue; // single checkbox row
            var text = Label(label, width: labelWidth);
            text.Position = new Vector2(offset + labelWidth / 2f, 0f);
            var box = Checkbox(value, changed);
            box.Position = new Vector2(offset + half - 28f - 24f, 0f);
            children.Add(text);
            children.Add(box);
        }

        return new UiPanel
        {
            Color = UiColor.Transparent,
            Size = new Vector2(RowWidth, RowHeight),
            Children = children
        };
    }
}

using MelonLoader;
using Starlight.Storage;
using Starlight.Utils;

namespace SR2MP.Components.UI;

// todo: Asset bundle
[InjectIntoIL]
internal sealed partial class MultiplayerUI : MonoBehaviour
{
    public static MultiplayerUI Instance { get; private set; }

    private void Awake()
    {
        firstTime = Main.SetupUI;
        usernameInput = Main.RawUsername;
        usernameColorInput = Main.UsernameColor;
        allowCheatsInput = Main.AllowCheats;
        disableAchievementsInput = Main.DisableAchievements;
        joinIpInput = Main.SavedConnectIP;
        joinPortInput = Main.SavedConnectPort;
        hostLocalPortInput = Main.SavedHostPort;

        if (Instance)
        {
            SrLogger.LogError("Tried to create instance of MultiplayerUI, but it already exists!");
            Destroy(this);
            return;
        }

        Instance = this;
    }

    private void OnDestroy()
    {
        Instance = null!;
    }

    private void Update()
    {
        HandleUIToggle();
        HandleChatToggle();
        ProcessPendingMessages();
        ChatHud.Update(this);
        DirectLaunch.Pump();
    }

    private void OnGUI()
    {
        GUI.skin.label.richText = true;

        if (Event.current.type == EventType.Layout)
        {
            state = GetState();
            UpdateChatVisibility();
        }

        if (Event.current.type == EventType.Repaint)
            DrawWatermark();

        previousLayoutRect = new Rect(6, 16, WindowWidth, 0);
        previousLayoutHorizontalIndex = 0;

        if (!MenuEUtil.isAnyMenuOpen && !MenuEUtil.isAnyPopUpOpen)
            DrawWindow();
    }

    // The native LycoreiaMenu (main/pause menu "Multiplayer") replaces this window; F4 shows it as a fallback.
    private bool legacyWindowHidden = true;

    private void DrawWindow()
    {
        if (state == MenuState.Hidden || legacyWindowHidden) return;

        GUI.Box(new Rect(6, 6, WindowWidth, WindowHeight), $"{BuildInfo.Name} {BuildInfo.Version} (F4 to toggle)");

        switch (state)
        {
            case MenuState.SettingsInitial:
                FirstTimeScreen();
                break;
            case MenuState.SettingsMain:
                SettingsScreen();
                break;
            case MenuState.DisconnectedMainMenu:
                MainMenuScreen();
                break;
            case MenuState.DisconnectedInGame:
                InGameScreen();
                break;
            case MenuState.Connecting:
                ConnectingScreen();
                break;
            case MenuState.ConnectedClient:
                ConnectedScreen();
                break;
            case MenuState.ConnectedHost:
                HostingScreen();
                break;
            default:
                UnimplementedScreen();
                break;
        }

        AdjustInputValues();
    }
}
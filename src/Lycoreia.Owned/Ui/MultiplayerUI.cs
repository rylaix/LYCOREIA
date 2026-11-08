// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System.Net;
using System.Threading;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine.InputSystem;
using System.Text.RegularExpressions;
using Il2CppMonomiPark.SlimeRancher.UI;
using Il2CppMonomiPark.SlimeRancher.UI.Framework.Displays;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using ChatMessage_Net = Lycoreia.Net.Messages.ChatMessage;

namespace Lycoreia.Owned.Components.UI;

/// <summary>Chat model and menu operations on top of the new session. Drawn by ChatHud and LycoreiaMenu.</summary>
internal sealed partial class MultiplayerUI : MonoBehaviour
{
    public MultiplayerUI(IntPtr ptr) : base(ptr) { }
    public static MultiplayerUI? Instance { get; private set; }

    public const byte SystemMessageNormal = 0, SystemMessageConnect = 1, SystemMessageDisconnect = 2, SystemMessageClose = 3;
    private const int MaxStoredChatMessages = 100, MaxChatMessageLength = 1000;

    internal sealed class ChatMessage
    {
        public string message = string.Empty, playerName = string.Empty, messageId = string.Empty;
        public long time; public bool isSystemMessage, isOwn; public byte systemMessageType;
    }

    private readonly List<ChatMessage> chatMessages = new();
    internal int ChatRevision { get; private set; }
    [HideFromIl2Cpp]
    internal List<ChatMessage> ChatMessages => chatMessages;
    internal bool ChatHidden { get; private set; }
    internal bool IsChatAllowed => Mod.Initialised && (Mod.IsHost || Mod.Session.Connected);

    // native menu state
    internal bool IsFirstTimeSetup => Lycoreia.Game.Prefs.Get<bool>(Settings.UiFirstRun);
    internal bool IsHostAutoInProgress { get; private set; }
    internal string HostAutoStage { get; private set; } = "Starting server...";
    private CancellationTokenSource? hostCancellation;
    private int hostAttempt;
    internal string HostAutoError { get; private set; } = string.Empty;
    internal string HostJoinCode { get; private set; } = string.Empty;
    internal string NativeLastError { get; private set; } = string.Empty;
    internal string SavedJoinIp => Lycoreia.Game.Prefs.Get<string>(Settings.RecentIp);
    internal string SavedJoinPort => Lycoreia.Game.Prefs.Get<int>(Settings.RecentPort).ToString();
    internal string SavedHostPort => Lycoreia.Game.Prefs.Get<int>(Settings.HostPort).ToString();

    internal static void Initialize()
    {
        if (Instance) return;
        if (!Il2CppInterop.Runtime.Injection.ClassInjector.IsTypeRegisteredInIl2Cpp<MultiplayerUI>())
            Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<MultiplayerUI>();
        var root = new GameObject("Lycoreia_MultiplayerUI");
        root.AddComponent<MultiplayerUI>();
        DontDestroyOnLoad(root);
    }

    private static bool menuButtonsRegistered;
    private static bool launcherFailed;
    private static RectTransform? launcher;
    private static bool launcherShown;

    /// <summary>Creates the "Multiplayer" launcher button (shown over the main menu and the pause menu).</summary>
    internal static void RegisterMenuButtons()
    {
        if (menuButtonsRegistered) return;
        menuButtonsRegistered = true;
        TryBuildLauncher();
    }

    private static void TryBuildLauncher()
    {
        if (launcher || launcherFailed) return;
        try
        {
            Sprite? icon = null;
            try
            {
                var assembly = typeof(MultiplayerUI).Assembly;
                var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("LycoreiaMultiplayer.png", StringComparison.Ordinal));
                if (name != null)
                {
                    using var stream = assembly.GetManifestResourceStream(name);
                    if (stream != null)
                    {
                        using var memory = new System.IO.MemoryStream();
                        stream.CopyTo(memory);
                        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                        if (ImageConversion.LoadImage(texture, memory.ToArray()))
                            icon = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
                    }
                }
            }
            catch (Exception e) { Log.Warn("Multiplayer button icon not loaded: " + e.Message); }

            var button = new UiButton
            {
                Size = new Vector2(260f, 64f),
                CornerRadius = 16,
                OnClick = LycoreiaMenu.OpenMenu
            };
            var labelWidth = 260f;
            var labelX = 0f;
            if (icon)
            {
                button.Children.Add(new UiPanel { Sprite = icon, CustomColor = Color.white, Size = new Vector2(40f, 40f), Position = new Vector2(-96f, 0f) });
                labelWidth = 190f; labelX = 32f;
            }
            button.Children.Add(new UiText
            {
                Text = "Multiplayer", FontSize = 28f, Color = UiColor.TextButton, Alignment = Il2CppTMPro.TextAlignmentOptions.Center,
                Size = new Vector2(labelWidth, 64f), Position = new Vector2(labelX, 0f)
            });
            launcher = button.Render(UiCanvas.Root);
            launcher!.anchorMin = launcher.anchorMax = Vector2.zero;
            launcher.pivot = Vector2.zero;
            launcher.anchoredPosition = new Vector2(40f, 40f);
            launcher.gameObject.SetActive(false);
            Log.Info("Multiplayer launcher button created");
        }
        catch (Exception e) { launcherFailed = true; Log.Error("Multiplayer launcher creation", e); }
    }

    private static void UpdateLauncher()
    {
        UiCanvas.ReleaseEventSystemIfNative();
        if (!menuButtonsRegistered) return;
        TryBuildLauncher();
        if (!launcher) return;
        var show = false;
        try
        {
            if (!LycoreiaMenu.IsOpen)
            {
                var scene = SceneContext.Instance;
                var inGame = scene != null && scene.Player != null;
                var top = UIDisplayManager.Instance?._lastState?.TopFocus?.Name;
                show = inGame ? top == "Pause" : top == "MainMenu";
            }
        }
        catch { show = false; }
        if (show == launcherShown) return;
        launcherShown = show;
        launcher!.gameObject.SetActive(show);
        if (show) UiCanvas.EnsureEventSystem();
    }

    private void Awake()
    {
        Instance = this;
        if (!Mod.Initialised) return;
        Mod.Session.ChatReceived += OnChat; Mod.Session.Notice += text => AddSystem(text, SystemMessageNormal);
        Mod.Session.ErrorShown += text => AddSystem(text, SystemMessageDisconnect);
        Mod.Session.PlayerAdded += p => { if (!p.IsHost) AddSystem($"{p.Name} joined the world!", SystemMessageConnect); };
        Mod.Session.PlayerRemoved += p => AddSystem($"{p.Name} left the world!", SystemMessageDisconnect);
        AddSystem($"Welcome to {BuildInfo.Name} {BuildInfo.Version}! Press Enter to chat, F5 to hide the chat.", SystemMessageNormal);
    }

    private void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard.f5Key.wasPressedThisFrame) ChatHidden = !ChatHidden;
        DirectLaunch.Pump();
        LycoreiaMenu.Tick();
        UpdateLauncher();
        ChatHud.Update(this);
    }
    private void OnGUI() { try { DrawWatermark(); } catch { } }

    // ---------------------------------------------------------------- chat
    [HideFromIl2Cpp]
    private void OnChat(ChatMessage_Net m)
    {
        if (!Mod.Chat.Register(m.MessageId)) return;
        if (m.Kind == ChatKind.System) AddSystem(m.Text, SystemMessageNormal, m.MessageId);
        else Add(new ChatMessage { message = m.Text, playerName = ColouredName(m.Username), messageId = m.MessageId, time = Now() });
    }

    /// <summary>Wraps a chat name in its owner's selected colour ("&lt;color=#RRGGBB&gt;name&lt;/color&gt;", the only form the chat honours).</summary>
    private static string ColouredName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name!.StartsWith("<color=")) return name ?? string.Empty;
        var s = Mod.Session; if (s == null) return name;
        string? hex = name == s.LocalName && s.LocalId != null ? s.ColourOf(s.LocalId) : null;
        if (hex == null) foreach (var p in s.Players.Values) if (p.Name == name) { hex = s.ColourOf(p.Id); if (hex != null) break; }
        return hex != null ? $"<color={hex}>{name}</color>" : name;
    }

    internal void SendChatMessage(string message)
    {
        if (Mod.Initialised) Lycoreia.CommandRouter.HandleChatInput(Lycoreia.Compat.CommandEnv.Instance, message, ShowLocalSystemMessage, MaxChatMessageLength);
    }

    internal void ShowLocalSystemMessage(string message) => AddSystem(message, SystemMessageNormal, "local_" + Guid.NewGuid().ToString("N"));

    internal void ShowLocalChat(string name, string text)
    {
        string id = Guid.NewGuid().ToString("N"); Mod.Chat.Register(id);
        Add(new ChatMessage { message = text, playerName = ColouredName(name), messageId = id, time = Now(), isOwn = true });
    }

    private void AddSystem(string text, byte type, string? id = null)
        => Add(new ChatMessage { message = (text ?? string.Empty).Trim(), messageId = id ?? Guid.NewGuid().ToString("N"), time = Now(), isSystemMessage = true, systemMessageType = type });
    [HideFromIl2Cpp]
    private void Add(ChatMessage m)
    {
        chatMessages.Add(m); if (chatMessages.Count > MaxStoredChatMessages) chatMessages.RemoveRange(0, chatMessages.Count - MaxStoredChatMessages); ChatRevision++;
    }
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    internal void ClearChat() { chatMessages.Clear(); ChatRevision++; }

    internal static void SetGameInputEnabled(bool enabled)
    {
        try
        {
            var map = GameContext.Instance?.InputDirector?._mainGame?.Map;
            if (map == null) return;
            if (enabled) map.Enable(); else map.Disable();
        }
        catch (Exception ex) { OwnedLog.LogWarning($"Could not {(enabled ? "enable" : "disable")} game input for chat: {ex.Message}"); }
    }

    // ---------------------------------------------------------------- native menu operations
    internal void ClearNativeError() { NativeLastError = string.Empty; }

    internal bool NativeValidateHostPort(string portText)
    {
        NativeLastError = string.Empty;
        if (ushort.TryParse((portText ?? "").Trim(), out var p) && p != 0) return true;
        NativeLastError = "The port must be a number from 1 to 65535."; return false;
    }

    internal bool NativeValidateJoinCode(string code)
    {
        NativeLastError = string.Empty;
        if (Lycoreia.AutoHost.TryDecodeJoinCode((code ?? "").Replace(" ", ""), out _, out _)) return true;
        NativeLastError = "That join code is not valid."; return false;
    }

    internal bool NativeValidateJoinManual(string ip, string portText)
    {
        ip = (ip ?? "").Trim(); portText = (portText ?? "").Trim(); NativeLastError = string.Empty;
        if (ip.Length == 0) NativeLastError = "Enter the host's IP address.";
        else if ((ip == "127.0.0.1" || ip == "::1") && !Mod.Session.DevMode) NativeLastError = "127.0.0.1 is not allowed. Use the host's LAN or public IP (PlayIt: the left-side address).";
        else if (!ushort.TryParse(portText, out var port) || port == 0) NativeLastError = "The port must be a number from 1 to 65535.";
        return NativeLastError.Length == 0;
    }

    internal void NativeJoinWithCode(string code)
    {
        if (!NativeValidateJoinCode(code)) return;
        Lycoreia.AutoHost.TryDecodeJoinCode((code ?? "").Replace(" ", ""), out var ip, out var port);
        Join(new IPEndPoint(ip!, port));
    }

    internal void NativeJoinManual(string ip, string portText)
    {
        if (!NativeValidateJoinManual(ip, portText)) return;
        Lycoreia.Game.Prefs.Set(Settings.RecentIp, ip.Trim()); Lycoreia.Game.Prefs.Set(Settings.RecentPort, int.Parse(portText.Trim()));
        var err = Mod.Session.Connect($"{ip.Trim()}:{portText.Trim()}"); NativeLastError = err ?? string.Empty;
    }

    [HideFromIl2Cpp]
    private void Join(IPEndPoint ep) { var err = Mod.Session.Connect(ep); NativeLastError = err ?? string.Empty; }

    internal void NativeHostOnPort(string portText) { if (NativeValidateHostPort(portText)) NativeHostOnPort(ushort.Parse(portText.Trim())); }
    internal void NativeHostOnPort(ushort port)
    {
        if (IsHostAutoInProgress) NativeCancelAutomatic();
        Lycoreia.Game.Prefs.Set(Settings.HostPort, (int)port);
        var err = Mod.Session.StartServer(port, ipv6: true); NativeLastError = err ?? string.Empty;
    }

    internal void NativeCancelAutomatic()
    {
        if (!IsHostAutoInProgress) return;
        hostAttempt++; hostCancellation?.Cancel();
        hostCancellation = null;
        IsHostAutoInProgress = false;
        HostAutoError = "Automatic setup cancelled. You can retry or host on a port.";
    }

    internal void NativeHostAutomatic()
    {
        if (IsHostAutoInProgress) return;
        IsHostAutoInProgress = true; HostAutoError = string.Empty;
        HostAutoStage = "Finding public address...";
        int attempt = ++hostAttempt;
        var cancellation = hostCancellation = new CancellationTokenSource(AutoHost.SetupTimeout);
        Mod.AutoHost.StageChanged = text =>
        {
            Mod.Diagnostics?.Record("hosting", "auto-stage", text);
            Mod.Node.Post(() => { if (hostAttempt == attempt) HostAutoStage = text + "..."; });
        };
        _ = CompleteAutomatic(Task.Run(() => Mod.AutoHost.PrepareAsync(cancellation.Token)), attempt, cancellation);
    }

    [HideFromIl2Cpp]
    private async Task CompleteAutomatic(Task<int> prepare, int attempt, CancellationTokenSource cancellation)
    {
        try
        {
            int port = await prepare.WaitAsync(AutoHost.SetupTimeout, cancellation.Token).ConfigureAwait(false);
            Mod.Node.Post(() =>
            {
                if (hostAttempt != attempt || cancellation.IsCancellationRequested) return;
                try
                {
                    var err = Mod.Session.StartServer(port, ipv6: true);
                    if (err != null) HostAutoError = err;
                    else HostJoinCode = Mod.AutoHost.JoinCode ?? string.Empty;
                    Mod.Diagnostics?.Record("hosting", "auto-complete", $"started={err == null}; error={err}");
                }
                catch (Exception e) { HostAutoError = "Starting the server failed: " + e.Message; }
                finally { IsHostAutoInProgress = false; if (hostCancellation == cancellation) hostCancellation = null; cancellation.Dispose(); }
            });
        }
        catch (Exception e)
        {
            cancellation.Cancel();
            string error = e is OperationCanceledException || e is TimeoutException
                ? "Automatic setup timed out. Retry, or host on a forwarded port."
                : e is AutoHostException ? e.Message : "Automatic hosting failed: " + e.Message;
            Mod.Node.Post(() =>
            {
                if (hostAttempt != attempt) { cancellation.Dispose(); return; }
                if (hostCancellation == cancellation) hostCancellation = null;
                HostAutoError = error; IsHostAutoInProgress = false; cancellation.Dispose();
                Mod.Diagnostics?.Record("hosting", "auto-failed", error);
            });
        }
    }

    internal void NativeSaveSettings(string username, string color, bool allowCheats, bool disableAchievements)
    {
        NativeLastError = string.Empty; username = (username ?? "").Trim();
        if (username.Length == 0) { NativeLastError = "Enter a name."; return; }
        if (username.Length > 24) { NativeLastError = "The name can be at most 24 characters."; return; }
        if (!Regex.IsMatch(color ?? "", "^#?[0-9A-Fa-f]{6}$")) { NativeLastError = "The colour must be a hex value like #4D95CB."; return; }
        Lycoreia.Game.Prefs.Set(Settings.Username, username); Lycoreia.Game.Prefs.Set(Settings.UsernameColor, color!.StartsWith("#") ? color : "#" + color);
        Lycoreia.Game.Prefs.Set(Settings.AllowCheats, allowCheats); Lycoreia.Game.Prefs.Set(Settings.DisableAchievements, disableAchievements); Lycoreia.Game.Prefs.Set(Settings.UiFirstRun, false);
        Mod.Session.LocalName = username; Mod.Session.AllowCheats = allowCheats;
    }
}

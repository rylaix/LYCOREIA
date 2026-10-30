using Il2CppMonomiPark.SlimeRancher.Persist;
using Il2CppMonomiPark.SlimeRancher.UI.ButtonBehavior;
using Il2CppMonomiPark.SlimeRancher.UI.IntroSequence;
using Il2CppMonomiPark.SlimeRancher.UI.MainMenu;
using Il2CppMonomiPark.SlimeRancher.UI.MainMenu.Model;

namespace SR2MP.Components.UI;

/// <summary>
/// Host or join straight from the main menu: loads a save (or starts a fresh co-op save for joining)
/// and runs the host/join action once gameplay is ready. Pumped by <see cref="MultiplayerUI"/>.
/// </summary>
internal static class DirectLaunch
{
    internal sealed class SaveEntry
    {
        public string GameName = string.Empty;
        public string DisplayName = string.Empty;
        public int SlotIndex;
        public int Currency;
        public long Ticks;
        public Summary Summary = null!;
    }

    private enum Stage : byte
    {
        Idle,
        OpeningNewGame,
        LoadingWorld,
        Settling,
        AwaitingServer,
        AwaitingConnection,
    }

    private const string JoinSaveKey = "coop_join_game";
    private const float TimeoutSeconds = 240f;
    private const float SettleSeconds = 2f;

    private static Stage stage = Stage.Idle;
    private static Action? pendingAction;
    private static string pendingLabel = string.Empty;
    private static bool rememberJoinSave;
    private static float startedAt;
    private static float stageAt;
    private static float nextNewGameAttempt;
    private static bool newGameMenuOpened;
    private static bool showMenuWhenHosting;
    private static bool watchConnection;
    private static float nextIntroSkip;

    internal static string LastError { get; private set; } = string.Empty;
    internal static bool IsBusy => stage != Stage.Idle;
    internal static string StatusText => stage switch
    {
        Stage.OpeningNewGame => "Creating a fresh co-op save...",
        Stage.LoadingWorld => $"Loading the world, then {pendingLabel}...",
        Stage.Settling => $"World loaded, {pendingLabel}...",
        Stage.AwaitingServer => "Starting the server...",
        Stage.AwaitingConnection => "Connecting to the host...",
        _ => string.Empty
    };

    internal static string JoinSaveName => Main.CoopJoinGame;

    internal static void ClearError() => LastError = string.Empty;

    /// <summary>Most recent save of every game, newest first.</summary>
    internal static List<SaveEntry> GetSaves()
    {
        var result = new List<SaveEntry>();
        try
        {
            var director = GameContext.Instance ? GameContext.Instance.AutoSaveDirector : null;
            if (director == null)
                return result;

            var games = director.AvailableGamesByGameName();
            foreach (var pair in games)
            {
                Summary? latest = null;
                foreach (var summary in pair.Value)
                {
                    if (summary == null || summary.IsInvalid)
                        continue;
                    if (latest == null || summary.SaveNumber > latest.SaveNumber)
                        latest = summary;
                }

                if (latest == null)
                    continue;

                result.Add(new SaveEntry
                {
                    GameName = pair.Key,
                    DisplayName = string.IsNullOrWhiteSpace(latest.DisplayName) ? pair.Key : latest.DisplayName,
                    SlotIndex = latest.SaveSlotIndex,
                    Currency = latest.Currency,
                    Ticks = latest.SaveTimestamp.ToUnixTimeMilliseconds(),
                    Summary = latest
                });
            }
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"Could not list saves: {ex.Message}");
        }

        return result.OrderByDescending(s => s.Ticks).ToList();
    }

    internal static int FreeSlotCount(List<SaveEntry> saves)
    {
        var director = GameContext.Instance ? GameContext.Instance.AutoSaveDirector : null;
        var slots = director != null && director._configuration ? director._configuration.SaveSlotCount : 0;
        return Math.Max(0, slots - saves.Select(s => s.SlotIndex).Distinct().Count());
    }

    /// <summary>Loads <paramref name="save"/>, then runs <paramref name="action"/> in the world.</summary>
    internal static void LoadThen(SaveEntry save, string label, Action action, bool hosting = false)
    {
        if (!Begin(label, action))
            return;

        showMenuWhenHosting = hosting;
        watchConnection = !hosting;

        if (!LoadSave(save.Summary))
        {
            Fail("Could not load that save. Load it from the game's menu, then use Multiplayer in the pause menu.");
            return;
        }

        rememberJoinSave = false;
        SetStage(Stage.LoadingWorld);
    }

    /// <summary>
    /// Joining replaces the world, so it runs on a dedicated co-op save: the one created last time if it
    /// still exists, otherwise a new game in a free slot.
    /// </summary>
    internal static void JoinOnCoopSave(string label, Action join)
    {
        var saves = GetSaves();
        var existing = saves.FirstOrDefault(s => s.GameName == JoinSaveName);
        if (existing != null)
        {
            LoadThen(existing, label, join);
            return;
        }

        if (FreeSlotCount(saves) == 0)
        {
            LastError = "All save slots are full. Pick a save below to use for joining (it will be replaced by the host's world), or delete one in the game's Load menu.";
            return;
        }

        if (!Begin(label, join))
            return;

        showMenuWhenHosting = false;
        watchConnection = true;
        rememberJoinSave = true;
        newGameMenuOpened = false;
        nextNewGameAttempt = 0f;
        SetStage(Stage.OpeningNewGame);
    }

    internal static void Cancel()
    {
        Log("cancelled", $"stage={stage}");
        Reset();
    }

    internal static void Pump()
    {
        if (stage == Stage.Idle)
            return;

        try
        {
            var now = UnityEngine.Time.unscaledTime;
            if (now - startedAt > TimeoutSeconds)
            {
                Fail("Loading the world took too long. Use Multiplayer in the pause menu once you are in the game.");
                return;
            }

            switch (stage)
            {
                case Stage.OpeningNewGame:
                    PumpNewGame(now);
                    break;
                case Stage.LoadingWorld:
                    SkipIntroIfShown();
                    if (IsGameplayReady())
                        SetStage(Stage.Settling);
                    break;
                case Stage.Settling:
                    if (!IsGameplayReady())
                    {
                        SetStage(Stage.LoadingWorld);
                        break;
                    }

                    if (now - stageAt >= SettleSeconds)
                        Finish();
                    break;
                case Stage.AwaitingServer:
                    // Show the hosting page (join code) once the server is up.
                    var ui = MultiplayerUI.Instance;
                    if (Main.Server.IsRunning && !(ui && ui!.IsHostAutoInProgress)
                        || ui && !string.IsNullOrWhiteSpace(ui!.HostAutoError) || now - stageAt > 60f)
                    {
                        Reset();
                        LycoreiaMenu.OpenMenu();
                    }
                    break;
                case Stage.AwaitingConnection:
                    // IsConnected is set when the socket opens; IsJoined once the host answered.
                    if (Main.Client.IsJoined)
                    {
                        Log("connected", string.Empty);
                        Reset();
                    }
                    else if (!Main.Client.IsConnected && !Main.Client.IsConnecting && now - stageAt > 1f
                             || now - stageAt > 45f)
                    {
                        Reset();
                        LastError = "Could not reach the host. Check the code or address and try again (Join a world).";
                        Log("connect-failed", string.Empty);
                        LycoreiaMenu.OpenMenu();
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            SrLogger.LogError($"Direct host/join failed: {ex}");
            Fail("Something went wrong while loading. Use Multiplayer in the pause menu instead.");
        }
    }

    private static bool Begin(string label, Action action)
    {
        if (IsBusy)
            return false;

        LastError = string.Empty;
        pendingAction = action;
        pendingLabel = label;
        startedAt = UnityEngine.Time.unscaledTime;
        Log("begin", label);
        return true;
    }

    private static void Finish()
    {
        var action = pendingAction;
        if (rememberJoinSave)
        {
            var name = GameContext.Instance.AutoSaveDirector.CurrentSaveGameName();
            if (!string.IsNullOrWhiteSpace(name))
                Main.SetConfigValue(JoinSaveKey, name);
            Log("join-save-created", name ?? "(none)");
        }

        Log("run-action", pendingLabel);
        var hosting = showMenuWhenHosting;
        var joining = watchConnection;
        Reset();
        action?.Invoke();
        if (MultiplayerUI.Instance && !string.IsNullOrWhiteSpace(MultiplayerUI.Instance.NativeLastError))
            LycoreiaMenu.OpenMenu();
        else if (hosting || joining)
        {
            startedAt = UnityEngine.Time.unscaledTime;
            SetStage(hosting ? Stage.AwaitingServer : Stage.AwaitingConnection);
        }
    }

    private static void Fail(string message)
    {
        Log("failed", $"stage={stage}; {message}");
        Reset();
        LastError = message;
        LycoreiaMenu.OpenMenu();
    }

    private static void Reset()
    {
        stage = Stage.Idle;
        pendingAction = null;
        rememberJoinSave = false;
        showMenuWhenHosting = false;
        watchConnection = false;
    }

    private static void SetStage(Stage next)
    {
        Log("stage", $"{stage}->{next}");
        stage = next;
        stageAt = UnityEngine.Time.unscaledTime;
    }

    private static bool IsGameplayReady()
    {
        if (!inGame || !SystemContext.Instance || SystemContext.Instance.SceneLoader.IsSceneLoadInProgress
            || !SystemContext.Instance.SceneLoader.IsCurrentSceneGroupGameplay())
            return false;

        var player = SceneContext.Instance ? SceneContext.Instance.player : null;
        return player && player!.transform.position.sqrMagnitude > 1f
               && !Object.FindObjectOfType<IntroSequenceUIRoot>();
    }

    private static void SkipIntroIfShown()
    {
        if (!rememberJoinSave || UnityEngine.Time.unscaledTime < nextIntroSkip)
            return;

        var intro = Object.FindObjectOfType<IntroSequenceUIRoot>();
        if (intro && intro!.gameObject.activeInHierarchy)
        {
            // The host's world replaces this save, so its intro is pointless.
            intro.EndSequence();
            nextIntroSkip = UnityEngine.Time.unscaledTime + 1f;
            Log("intro-skipped", string.Empty);
        }
    }

    /// <summary>Runs the game's own Load behaviour (the Continue button's model) for another save.</summary>
    private static bool LoadSave(Summary summary)
    {
        foreach (var model in LandingModels())
        {
            var load = model.TryCast<LoadGameBehaviorModel>();
            if (load == null)
                continue;

            load.GameDataSummary = summary;
            Log("load", $"game={summary.Name} via={model.Definition?.name}");
            load.InvokeBehavior();
            return true;
        }

        Log("load-missing", "no Continue/Load model in the main menu");
        return false;
    }

    /// <summary>
    /// Opens the game's New Game screen through its main-menu button, then starts the game with the
    /// screen's own slot and default settings (the same call its Start button makes).
    /// </summary>
    private static void PumpNewGame(float now)
    {
        foreach (var root in Resources.FindObjectsOfTypeAll<NewGameOptionsUIRoot>())
        {
            if (!root || !root.gameObject.activeInHierarchy || root._gameSettingsModel == null || root.SaveSlotIndex < 0)
                continue;
            if (SystemContext.Instance.SceneLoader.IsSceneLoadInProgress)
                return;

            Log("new-game", $"slot={root.SaveSlotIndex}");
            GameContext.Instance.AutoSaveDirector.StartNewGame(root.SaveSlotIndex, root._gameSettingsModel);
            SetStage(Stage.LoadingWorld);
            return;
        }

        if (now < nextNewGameAttempt)
            return;
        nextNewGameAttempt = now + 3f;

        if (newGameMenuOpened && now - stageAt > 20f)
        {
            Fail("Could not open the game's New Game screen. Start a new game yourself, then join from the pause menu.");
            return;
        }

        // With saves present the landing menu has no New Game button (it lives in Save Files, one per
        // empty slot): open the New Game display ourselves with a free slot, as that screen does.
        var slot = FreeSlotIndex();
        if (slot < 0)
        {
            Fail("All save slots are full. Pick a save to use for joining, or delete one in the game's Load menu.");
            return;
        }

        var definitions = Resources.FindObjectsOfTypeAll<OpenDisplayItemDefinition>();
        foreach (var definition in definitions)
        {
            if (!definition || !IsNewGameDefinition(definition))
                continue;

            var model = definition.CreateButtonBehaviorModel()?.TryCast<OpenDisplayBehaviorModel>();
            if (model == null)
                continue;

            var setup = new NewGameUISetup { SelectedIndex = slot };
            model._openWithData = setup;
            Log("new-game-open", $"via={definition.name} display={DisplayName(definition)} slot={slot}");
            model.InvokeBehavior();
            newGameMenuOpened = true;
            return;
        }

        if (!newGameMenuOpened)
        {
            // Definitions of the Save Files screen may load only with it: open it once and look again.
            foreach (var model in LandingModels())
            {
                if (model.Definition && model.Definition.name.IndexOf("SaveFiles", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log("new-game-open-savefiles", model.Definition.name);
                    model.InvokeBehavior();
                    newGameMenuOpened = true;
                    stageAt = now;
                    return;
                }
            }
        }

        Log("new-game-missing", string.Join(",", definitions.Where(d => d).Select(d => $"{d.name}>{DisplayName(d)}")));
        Fail("Could not open the game's New Game screen. Start a new game yourself, then join from the pause menu.");
    }

    private static bool IsNewGameDefinition(OpenDisplayItemDefinition definition)
        => definition.name.IndexOf("NewGame", StringComparison.OrdinalIgnoreCase) >= 0
           || DisplayName(definition).IndexOf("NewGame", StringComparison.OrdinalIgnoreCase) >= 0;

    private static string DisplayName(OpenDisplayItemDefinition definition)
    {
        var handle = definition.DisplayToOpen;
        return handle.IsValid() ? handle.Name ?? string.Empty : string.Empty;
    }

    private static int FreeSlotIndex()
    {
        var director = GameContext.Instance ? GameContext.Instance.AutoSaveDirector : null;
        var slots = director != null && director._configuration ? director._configuration.SaveSlotCount : 0;
        var used = GetSaves().Select(s => s.SlotIndex).ToHashSet();
        Log("slots", $"count={slots} used={string.Join(",", used)}");
        for (var i = 0; i < slots; i++)
        {
            if (!used.Contains(i))
                return i;
        }

        return -1;
    }

    private static IEnumerable<ButtonBehaviorModel> LandingModels()
    {
        foreach (var menu in Resources.FindObjectsOfTypeAll<MainMenuLandingRootUI>())
        {
            if (!menu || !menu.gameObject.activeInHierarchy || menu._models == null)
                continue;
            foreach (var model in menu._models)
            {
                if (model != null)
                    yield return model;
            }
        }
    }

    private static void Log(string eventName, string details)
    {
        SrLogger.LogMessage($"[DirectLaunch] {eventName} {details}");
        SrLogger.LogDiagnostic("menu", "direct-" + eventName, details);
    }
}

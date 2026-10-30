using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Sanctuary;
using Il2CppMonomiPark.SlimeRancher.UI.Themes;
using Il2CppMonomiPark.SlimeRancher.World;
using SR2MP.Packets.Loading;
using SR2MP.Packets.World;

namespace SR2MP.Shared.Managers;

internal static class NetworkSanctuaryManager
{
    private static readonly Dictionary<string, SanctuaryRoomState> PendingRoomStates = new();
    private static readonly Dictionary<string, bool> PendingThemeChanges = new();
    private static HashSet<string>? pendingOwnedThemeSnapshot;
    private static bool pendingOwnedThemeSnapshotNeedsReconcile;
    private static int nextPendingPumpFrame;
    private static bool initialized;

    internal static void Initialize()
    {
        if (initialized || Main.Server == null || Main.Client == null)
            return;

        initialized = true;
        Main.Server.OnServerStarted += ClearPending;
        Main.Client.OnConnected += _ => ClearPending();
        Main.Client.OnDisconnected += ClearPending;
    }

    internal static InitialSanctuaryStatePacket CreateSnapshot()
    {
        var packet = new InitialSanctuaryStatePacket();

        foreach (var pair in GameState.AllSanctuaryRooms())
        {
            if (pair.value == null)
                continue;

            packet.Rooms.Add(CaptureRoom(pair.key, pair.value));
        }

        var inventory = GetThemeInventory();
        if (inventory?._ownedThemes != null)
        {
            foreach (var theme in inventory._ownedThemes)
            {
                if (theme && !string.IsNullOrEmpty(theme.ReferenceId))
                    packet.OwnedThemeReferenceIds.Add(theme.ReferenceId);
            }
        }

        return packet;
    }

    internal static void SendRoomState(SanctuaryRoomModel model)
    {
        if (HandlingPacket || Main.Server == null || Main.Client == null ||
            (!Main.Server.IsRunning && !Main.Client.IsConnected))
            return;

        var id = ResolveRoomId(model);
        if (string.IsNullOrEmpty(id))
            return;

        Main.SendToAllOrServer(new SanctuaryRoomStatePacket { State = CaptureRoom(id, model) });
    }

    internal static void SendThemeOwnership(ThemeDefinition theme, bool owned)
    {
        if (HandlingPacket || Main.Server == null || Main.Client == null ||
            (!Main.Server.IsRunning && !Main.Client.IsConnected) || !theme)
            return;

        var referenceId = theme.ReferenceId;
        if (string.IsNullOrEmpty(referenceId))
            return;

        Main.SendToAllOrServer(new SanctuaryThemeOwnershipPacket
        {
            ThemeReferenceId = referenceId,
            IsOwned = owned
        });
    }

    internal static void ApplySnapshot(InitialSanctuaryStatePacket packet)
    {
        PendingRoomStates.Clear();
        PendingThemeChanges.Clear();
        pendingOwnedThemeSnapshot = new HashSet<string>();
        pendingOwnedThemeSnapshotNeedsReconcile = true;
        foreach (var referenceId in packet.OwnedThemeReferenceIds)
        {
            if (!string.IsNullOrEmpty(referenceId))
                pendingOwnedThemeSnapshot.Add(referenceId);
        }

        foreach (var room in packet.Rooms)
            ApplyRoomState(room);

        ApplyOwnedThemeSnapshot();
    }

    internal static void ApplyRoomState(SanctuaryRoomState state)
    {
        if (string.IsNullOrWhiteSpace(state.Id))
            return;

        var model = GetOrCreateRoomModel(state.Id);
        if (model == null)
        {
            PendingRoomStates[state.Id] = state;
            return;
        }

        var targetTheme = string.IsNullOrEmpty(state.ThemeReferenceId)
            ? null
            : FindTheme(state.ThemeReferenceId);
        var themePending = !string.IsNullOrEmpty(state.ThemeReferenceId) && !targetTheme;

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            if (!themePending)
            {
                var controller = FindController(model);
                if (targetTheme)
                {
                    var roomThemeChanged = model.GetTheme() != targetTheme;
                    if (roomThemeChanged)
                        model.SetTheme(targetTheme);

                    if (controller &&
                        (roomThemeChanged || controller!.GetTheme() != targetTheme))
                        controller!.SetTheme(targetTheme);
                }
                else
                {
                    // A missing theme means the vanilla room theme. SetTheme has no
                    // clear overload; the model's generated field accessor is available
                    // in SR2 1.3.0. Restore the controller's default visual first, then
                    // preserve the model's null (no custom theme) value.
                    if (controller && controller!._defaultTheme &&
                        (model.GetTheme() || controller!.GetTheme() != controller!._defaultTheme))
                        controller!.SetTheme(controller!._defaultTheme);

                    model._theme = null!;
                }
            }

            ReplacePreplacedGadgets(model, state.PreplacedGadgets);
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        if (themePending)
            PendingRoomStates[state.Id] = state;
        else
            PendingRoomStates.Remove(state.Id);
    }

    internal static void ApplyThemeOwnership(string referenceId, bool owned)
    {
        if (string.IsNullOrWhiteSpace(referenceId))
            return;

        var theme = FindTheme(referenceId);
        var inventory = GetThemeInventory();
        if (!theme || inventory == null)
        {
            PendingThemeChanges[referenceId] = owned;
            return;
        }

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            SetThemeOwned(inventory, theme!, owned);
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        PendingThemeChanges.Remove(referenceId);
    }

    internal static void Pump()
    {
        if (PendingRoomStates.Count == 0 && PendingThemeChanges.Count == 0 && pendingOwnedThemeSnapshot == null)
            return;

        if (UnityEngine.Time.frameCount < nextPendingPumpFrame)
            return;

        nextPendingPumpFrame = UnityEngine.Time.frameCount + 30;

        if (PendingRoomStates.Count > 0)
        {
            foreach (var state in PendingRoomStates.Values.ToArray())
                ApplyRoomState(state);
        }

        if (pendingOwnedThemeSnapshot != null)
            ApplyOwnedThemeSnapshot();

        if (PendingThemeChanges.Count > 0)
        {
            foreach (var change in PendingThemeChanges.ToArray())
                ApplyThemeOwnership(change.Key, change.Value);
        }
    }

    private static SanctuaryRoomState CaptureRoom(string id, SanctuaryRoomModel model)
    {
        var state = new SanctuaryRoomState
        {
            Id = id,
            ThemeReferenceId = model.GetTheme()?.ReferenceId,
            PreplacedGadgets = new List<SanctuaryRoomState.PreplacedGadget>()
        };

        if (model._preplacedGadgets == null)
            return state;

        foreach (var pair in model._preplacedGadgets)
        {
            if (string.IsNullOrEmpty(pair.key.Value))
                continue;

            state.PreplacedGadgets.Add(new SanctuaryRoomState.PreplacedGadget
            {
                Id = pair.key.Value,
                ActorId = pair.value.Value
            });
        }

        return state;
    }

    private static string? ResolveRoomId(SanctuaryRoomModel model)
    {
        foreach (var pair in GameState.AllSanctuaryRooms())
        {
            if (pair.value != null && pair.value.Pointer == model.Pointer)
                return pair.key;
        }

        return null;
    }

    private static SanctuaryRoomModel? GetOrCreateRoomModel(string id)
    {
        var rooms = GameState.AllSanctuaryRooms();
        if (rooms.TryGetValue(id, out var existing) && existing != null)
            return existing;

        return GameState.InitializeSanctuaryRoomModel(id);
    }

    private static void ReplacePreplacedGadgets(SanctuaryRoomModel model,
        List<SanctuaryRoomState.PreplacedGadget> desiredGadgets)
    {
        var desired = new Dictionary<string, long>();
        foreach (var gadget in desiredGadgets)
        {
            if (!string.IsNullOrWhiteSpace(gadget.Id))
                desired[gadget.Id] = gadget.ActorId;
        }

        var existingIds = new List<string>();
        if (model._preplacedGadgets != null)
        {
            foreach (var pair in model._preplacedGadgets)
            {
                var id = pair.key.Value;
                if (!string.IsNullOrEmpty(id))
                    existingIds.Add(id);
            }
        }

        foreach (var id in existingIds)
        {
            if (!desired.ContainsKey(id))
                model.ClearPreplacedGadget(new SanctuaryRoomPreplacedGadgetId(id));
        }

        foreach (var (id, actorId) in desired)
        {
            if (model.TryGetPreplacedGadget(new SanctuaryRoomPreplacedGadgetId(id), out var existing)
                && existing.Value == actorId)
                continue;

            model.SetPreplacedGadget(new SanctuaryRoomPreplacedGadgetId(id), new ActorId(actorId));
        }
    }

    private static void ApplyOwnedThemeSnapshot()
    {
        var desired = pendingOwnedThemeSnapshot;
        if (desired == null)
            return;

        var inventory = GetThemeInventory();
        if (inventory == null)
            return;

        var reconcileCurrentOwnership = pendingOwnedThemeSnapshotNeedsReconcile;
        var allThemesResolved = true;
        var unresolvedThemes = new HashSet<string>();
        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            var currentlyOwned = new List<ThemeDefinition>();
            if (inventory._ownedThemes != null)
            {
                foreach (var theme in inventory._ownedThemes)
                {
                    if (theme)
                        currentlyOwned.Add(theme);
                }
            }

            if (reconcileCurrentOwnership)
            {
                foreach (var theme in currentlyOwned)
                {
                    if (!desired.Contains(theme.ReferenceId))
                        inventory.SetThemeUnowned(theme);
                }
            }

            foreach (var referenceId in desired)
            {
                var theme = FindTheme(referenceId);
                if (!theme)
                {
                    allThemesResolved = false;
                    unresolvedThemes.Add(referenceId);
                    continue;
                }

                SetThemeOwned(inventory, theme!, true);
            }
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        pendingOwnedThemeSnapshotNeedsReconcile = false;
        pendingOwnedThemeSnapshot = allThemesResolved ? null : unresolvedThemes;
    }

    private static void SetThemeOwned(SanctuaryThemeInventoryModel inventory, ThemeDefinition theme, bool owned)
    {
        if (inventory.IsThemeOwned(theme) == owned)
            return;

        if (owned)
        {
            var director = SceneContext.Instance.SanctuaryDirector;
            if (director)
                director.SetThemeOwned(theme);
            else
                inventory.SetThemeOwned(theme);
        }
        else
        {
            inventory.SetThemeUnowned(theme);
        }
    }

    private static ThemeDefinition? FindTheme(string referenceId)
        => Resources.FindObjectsOfTypeAll<ThemeDefinition>()
            .FirstOrDefault(theme => theme && theme.ReferenceId == referenceId);

    private static SanctuaryRoomController? FindController(SanctuaryRoomModel model)
        => Resources.FindObjectsOfTypeAll<SanctuaryRoomController>()
            .FirstOrDefault(controller => controller && controller._model != null
                && controller._model.Pointer == model.Pointer);

    private static SanctuaryThemeInventoryModel? GetThemeInventory()
    {
        var sceneContext = SceneContext.Instance;
        var director = sceneContext ? sceneContext.SanctuaryDirector : null;
        return director ? director!._themesModel! : null;
    }

    private static void ClearPending()
    {
        PendingRoomStates.Clear();
        PendingThemeChanges.Clear();
        pendingOwnedThemeSnapshot = null;
        pendingOwnedThemeSnapshotNeedsReconcile = false;
        nextPendingPumpFrame = 0;
    }
}

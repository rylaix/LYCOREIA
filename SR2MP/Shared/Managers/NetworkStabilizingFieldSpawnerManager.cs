using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Packets.World;

namespace SR2MP.Shared.Managers;

internal static class NetworkStabilizingFieldSpawnerManager
{
    private readonly record struct SpawnerState(double NextTriggerTime, double DespawnAtWorldTime, bool HasSpawned)
    {
        internal bool Matches(StabilizingFieldSpawnerModel model)
            => BitConverter.DoubleToInt64Bits(NextTriggerTime) == BitConverter.DoubleToInt64Bits(model.NextTriggerTime) &&
               BitConverter.DoubleToInt64Bits(DespawnAtWorldTime) == BitConverter.DoubleToInt64Bits(model.DespawnAtWorldTime) &&
               HasSpawned == model.HasSpawned;
    }

    private static readonly Dictionary<string, SpawnerState> LastSentStates = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, SpawnerState> PendingHostStates = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, SpawnerState> PendingStates = new(StringComparer.Ordinal);
    private static int nextPendingPumpFrame;
    private static int nextHostBroadcastFrame;
    private static bool initialized;
    private const int HostBroadcastIntervalFrames = 12;

    internal static void Initialize()
    {
        if (initialized || Main.Server == null || Main.Client == null)
            return;

        initialized = true;
        Main.Server.OnServerStarted += ClearHostState;
        Main.Client.OnConnected += _ => ClearPendingStates();
        Main.Client.OnDisconnected += ClearPendingStates;
    }

    internal static StabilizingFieldSpawnerStatePacket? CreateSnapshot()
    {
        var gameModel = GetGameModel();
        if (gameModel == null)
            return null;

        var packet = new StabilizingFieldSpawnerStatePacket { IsSnapshot = true };
        foreach (var pair in gameModel!.AllStabilizingFieldSpawners())
        {
            var model = pair.value;
            var id = pair.key;
            if (model == null || string.IsNullOrWhiteSpace(id))
                continue;

            var state = Capture(id, model);
            packet.Spawners.Add(state);
        }

        packet.Spawners.Sort((left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
        return packet;
    }

    internal static void Broadcast(StabilizingFieldSpawnerModel? model)
    {
        if (HandlingPacket || !Main.Server.IsRunning || model == null || string.IsNullOrWhiteSpace(model.Id))
            return;

        var id = model.Id;
        var packetState = Capture(id, model);
        var state = new SpawnerState(packetState.NextTriggerTime, packetState.DespawnAtWorldTime, packetState.HasSpawned);
        if (LastSentStates.TryGetValue(id, out var last) && last == state)
        {
            PendingHostStates.Remove(id);
            return;
        }

        PendingHostStates[id] = state;
        if (Time.frameCount >= nextHostBroadcastFrame)
            FlushHostStates();
    }

    internal static void ApplySnapshot(StabilizingFieldSpawnerStatePacket packet)
    {
        if (packet.IsSnapshot)
        {
            PendingStates.Clear();
            // Apply all host IDs below, but retain local-only models. The game API
            // exposes lookup/initialization, not a safe unregister-and-rebind path
            // for an already attached spawner component.
        }

        foreach (var state in packet.Spawners)
            ApplyState(state);
    }

    internal static void Pump()
    {
        if (PendingHostStates.Count > 0 && Time.frameCount >= nextHostBroadcastFrame)
            FlushHostStates();

        if (PendingStates.Count == 0 || Time.frameCount < nextPendingPumpFrame)
            return;

        nextPendingPumpFrame = Time.frameCount + 30;
        foreach (var pair in PendingStates.ToArray())
            ApplyState(pair.Key, pair.Value);
    }

    private static void ApplyState(StabilizingFieldSpawnerStatePacket.Spawner state)
    {
        if (state == null || string.IsNullOrWhiteSpace(state.Id) || double.IsNaN(state.NextTriggerTime) ||
            double.IsNaN(state.DespawnAtWorldTime))
            return;

        ApplyState(state.Id, new SpawnerState(state.NextTriggerTime, state.DespawnAtWorldTime, state.HasSpawned));
    }

    private static void ApplyState(string id, SpawnerState state)
    {
        var gameModel = GetGameModel();
        if (gameModel == null)
        {
            PendingStates[id] = state;
            return;
        }

        var model = GetOrInitializeModel(gameModel!, id);
        if (model == null)
        {
            PendingStates[id] = state;
            return;
        }

        PendingStates.Remove(id);
        if (state.Matches(model))
            return;

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            model.NextTriggerTime = state.NextTriggerTime;
            model.DespawnAtWorldTime = state.DespawnAtWorldTime;
            model.HasSpawned = state.HasSpawned;
            model.NotifyParticipants();
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }
    }

    private static StabilizingFieldSpawnerStatePacket.Spawner Capture(string id, StabilizingFieldSpawnerModel model)
        => new()
        {
            Id = id,
            NextTriggerTime = model.NextTriggerTime,
            DespawnAtWorldTime = model.DespawnAtWorldTime,
            HasSpawned = model.HasSpawned
        };

    private static StabilizingFieldSpawnerModel? GetOrInitializeModel(GameModel gameModel, string id)
    {
        try
        {
            var spawners = gameModel.AllStabilizingFieldSpawners();
            if (spawners.TryGetValue(id, out var model) && model != null)
                return model;

            model = gameModel.InitializeStabilizingFieldModel(id);
            if (model != null)
                return model;
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"Could not initialize stabilizing field model '{id}': {ex.Message}");
        }

        return null;
    }

    private static GameModel? GetGameModel()
    {
        var context = SceneContext.Instance;
        return context && context!.GameModel != null ? context!.GameModel : null;
    }

    private static void ClearHostState()
    {
        LastSentStates.Clear();
        PendingHostStates.Clear();
        PendingStates.Clear();
        nextPendingPumpFrame = 0;
        nextHostBroadcastFrame = 0;
    }

    private static void ClearPendingStates()
    {
        PendingStates.Clear();
        nextPendingPumpFrame = 0;
    }

    private static void FlushHostStates()
    {
        if (!Main.Server.IsRunning || PendingHostStates.Count == 0)
            return;

        var packet = new StabilizingFieldSpawnerStatePacket { IsSnapshot = false };
        foreach (var pair in PendingHostStates.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            packet.Spawners.Add(new StabilizingFieldSpawnerStatePacket.Spawner
            {
                Id = pair.Key,
                NextTriggerTime = pair.Value.NextTriggerTime,
                DespawnAtWorldTime = pair.Value.DespawnAtWorldTime,
                HasSpawned = pair.Value.HasSpawned
            });
            LastSentStates[pair.Key] = pair.Value;
        }

        PendingHostStates.Clear();
        nextHostBroadcastFrame = Time.frameCount + HostBroadcastIntervalFrames;
        Main.SendToAllOrServer(packet);
    }
}

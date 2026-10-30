using System.Collections;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using SR2MP.Components.Actor;
using SR2MP.Packets.Actor;
using SR2MP.Shared.Utils;

namespace SR2MP.Shared.Managers;

internal sealed partial class NetworkActorManager
{
    public readonly Dictionary<long, IdentifiableModel> Actors = new();
    public readonly Dictionary<int, IdentifiableType> ActorTypes = new();
    internal readonly Dictionary<long, string> OwnershipByActorId = new();
    // Per-actor ownership epoch (see OwnershipEpochPolicy). The host is the only issuer; clients mirror
    // the value from ownership packets and the join snapshot. Absent = 0.
    internal readonly Dictionary<long, uint> EpochByActorId = new();

    internal uint GetEpoch(long actorId) => EpochByActorId.GetValueOrDefault(actorId);
    internal void SetEpoch(long actorId, uint epoch) => EpochByActorId[actorId] = epoch;
    internal bool HasAuthoritativeOwnershipSnapshot { get; private set; }

    internal bool CanPublishLocalActorSpawn(long actorId)
    {
        if (Main.Server.IsRunning)
            return ActorSpawnPublicationPolicy.IsHostActorId(actorId);

        return Main.Client.IsConnected &&
               ActorSpawnPublicationPolicy.CanClientPublish(
                   actorId, LocalID, HasAuthoritativeOwnershipSnapshot);
    }

    public static int GetPersistentID(IdentifiableType type)
        => GameContext.Instance.AutoSaveDirector._saveReferenceTranslation.GetPersistenceId(type);

    internal void Initialize(GameContext context)
    {
        ActorTypes.Clear();
        Actors.Clear();
        OwnershipByActorId.Clear();
        EpochByActorId.Clear();
        OwnershipArbiter.Reset();
        HasAuthoritativeOwnershipSnapshot = false;

        foreach (var type in context.AutoSaveDirector._saveReferenceTranslation._identifiableTypeLookup)
            ActorTypes.TryAdd(GetPersistentID(type.value), type.value);

        ActorTypes[-1] = null!;

        StartCoroutine(ZoneLoadingLoop());
        StartCoroutine(OwnUnownedSlimesLoop());
        StartCoroutine(OwnershipArbiterLoop());
    }

    private IEnumerator OwnershipArbiterLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(OwnershipArbiter.TickSeconds);

            if (!Main.Server.IsRunning)
                continue;

            OwnershipArbiter.Tick(UnityEngine.Time.unscaledTime);
        }
    }

    private IEnumerator OwnUnownedSlimesLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(2f);

            if (!Main.Server.IsRunning && !Main.Client.IsConnected)
                continue;

            if (!SystemContext.Instance.SceneLoader.IsCurrentSceneGroupGameplay())
                continue;

            if (SceneContext.Instance?.player == null)
                continue;

            TakeOwnershipOfUnownedNearby();
        }
    }

    private IEnumerator ZoneLoadingLoop()
    {
        while (true)
        {
            yield return new WaitForSceneGroupLoad(false);
            yield return new WaitForSceneGroupLoad();

            if (!Main.Server.IsRunning && !Main.Client.IsConnected)
                continue;

            if (!SystemContext.Instance.SceneLoader.IsCurrentSceneGroupGameplay())
                continue;

            var gameModel = SceneContext.Instance?.GameModel;
            if (!gameModel)
                continue;

            var scene = SystemContext.Instance.SceneLoader.CurrentSceneGroup;
            var ownershipByActorId = new Dictionary<long, (string OwnerId, bool LocallyOwned)>();

            foreach (var actor in gameModel!.identifiables)
            {
                if (actor.value.ident.IsPlayer)
                    continue;

                if (actor.value.TryCast<ActorModel>() == null)
                    continue;

                var obj = actor.value.GetGameObject();
                if (!obj)
                    continue;

                var networkActor = obj.GetComponent<NetworkActor>();
                if (networkActor)
                {
                    var ownerId = networkActor.CurrentOwnerId;
                    var locallyOwned = networkActor.LocallyOwned;
                    if (OwnershipByActorId.TryGetValue(actor.value.actorId.Value, out var snapshotOwner))
                    {
                        ownerId = snapshotOwner;
                        locallyOwned = snapshotOwner == LocalID;
                    }

                    ownershipByActorId[actor.value.actorId.Value] =
                        (ownerId, locallyOwned);
                    networkActor.SuppressDestroyPacket = true;
                }

                Object.Destroy(obj);
                Actors.Remove(actor.value.actorId.Value);
            }

            foreach (var actor2 in gameModel.identifiables)
            {
                if (actor2.value.ident.IsPlayer)
                    continue;

                var model = actor2.value.TryCast<ActorModel>();

                if (model == null)
                    continue;

                if (!model.ident.prefab)
                    continue;

                if (actor2.value.sceneGroup != scene)
                    continue;

                var obj = InstantiateActorWithoutNetworkSync(model);

                if (!obj)
                    continue;

                var networkComponent = obj.AddComponent<NetworkActor>();

                if (ownershipByActorId.TryGetValue(model.actorId.Value, out var ownership))
                {
                    networkComponent.CurrentOwnerId = ownership.OwnerId;
                    networkComponent.LocallyOwned = ownership.LocallyOwned;
                }
                else if (OwnershipByActorId.TryGetValue(model.actorId.Value, out var snapshotOwner))
                {
                    networkComponent.CurrentOwnerId = snapshotOwner;
                    networkComponent.LocallyOwned = snapshotOwner == LocalID;
                }

                networkComponent.previousPosition = model.lastPosition;
                networkComponent.nextPosition     = model.lastPosition;
                networkComponent.previousRotation = model.lastRotation;
                networkComponent.nextRotation     = model.lastRotation;

                Actors[model.actorId.Value] = model;
            }

            TakeOwnershipOfUnownedNearby();
        }
    }

    private static bool ActorIDAlreadyInUse(ActorId id)
        => SceneContext.Instance?.GameModel?.TryGetIdentifiableModel(id, out _) ?? false;

    public static long GetHighestActorIdInRange(long min, long max)
    {
        var result = min;
        foreach (var actor in GameState.identifiables)
        {
            var id = actor.value.actorId.Value;
            if (id < min || id >= max)
                continue;
            if (id > result)
                result = id;
        }

        return result;
    }
    
    // Proximity interest: actors nobody is simulating (no owner / owner gone / owner released) inside the loaded
    // bounds, plus actors owned by someone else that are close enough to contest. Both host and client only express
    // interest here; the host arbiter decides.
    private const float ContestRadius = 60f;
    private const int MaxInterestPerPass = 128;
    private int interestCursor;

    internal void TakeOwnershipOfUnownedNearby()
    {
        if (Main.Client.IsConnected && !HasAuthoritativeOwnershipSnapshot)
            return;

        var player = SceneContext.Instance?.player;
        if (player == null)
            return;

        var boundsSize = new Vector3(600, 1250, 600);
        var bounds = new Bounds(player.transform.position, boundsSize);
        var sent = 0;
        var all = Actors.Values.ToArray();
        if (all.Length == 0)
            return;
        // Rotate the start so a cap of contested actors can never starve free actors further down the table.
        interestCursor = (interestCursor + MaxInterestPerPass) % all.Length;

        for (var step = 0; step < all.Length; step++)
        {
            var actor = all[(interestCursor + step) % all.Length];
            try
            {
                if (sent >= MaxInterestPerPass)
                    break;

                if (actor == null)
                    continue;

                if (!bounds.Contains(actor.lastPosition))
                    continue;

                if (!actor.TryGetNetworkComponent(out var netActor))
                    continue;

                var actorId = netActor.ActorId;
                if (actorId.Value == 0)
                    continue;

                if (netActor.LocallyOwned || netActor.RegionMember?._hibernating == true)
                    continue;

                var ownerId = netActor.CurrentOwnerId;
                var free = string.IsNullOrEmpty(ownerId) || !PlayerManager.CheckPlayerExists(ownerId) ||
                           WasReleasedByOwner(actorId.Value, ownerId);
                if (!free)
                {
                    // Contest only when clearly closer than the owner (the host applies the same 25 % rule), so
                    // hopeless interest is never sent.
                    var myDistance = Vector3.Distance(player.transform.position, actor.lastPosition);
                    var ownerPlayer = PlayerManager.GetPlayer(ownerId);
                    if (myDistance > ContestRadius || ownerPlayer == null ||
                        myDistance > 0.75f * Vector3.Distance(ownerPlayer.Position, actor.lastPosition))
                        continue;
                }

#if SR2MP_DEEPLOG || SR2MP_TEST
                ActorDiag.Log("ownership-interest", actorId.Value,
                    $"reason={(free ? "owner-free" : "contest")}; owner={ownerId}; interested={LocalID}",
                    perActorMilliseconds: 2000);
#endif
                sent++;
                ExpressOwnershipInterest(actorId, ownerId);
            }
            catch { /* ignored */ }
        }
    }

    internal void ReplaceOwnershipSnapshot(
        Dictionary<long, SR2MP.Packets.Loading.InitialActorsPacket.OwnershipEntry> ownership, bool isAuthoritative)
    {
        OwnershipByActorId.Clear();
        EpochByActorId.Clear();
        foreach (var (actorId, entry) in ownership)
        {
            OwnershipByActorId[actorId] = entry.OwnerId ?? string.Empty;
            EpochByActorId[actorId] = entry.Epoch;
        }
        HasAuthoritativeOwnershipSnapshot = isAuthoritative;
    }
    
    internal void AssignOwnershipOfUnowned()
    {
        if (SceneContext.Instance?.player == null)
            return;

        var bounds = new Vector3(600, 1250, 600);
        var now = UnityEngine.Time.unscaledTime;

        var allPlayers = new List<(string PlayerId, Vector3 Position)>
        {
            (LocalID, SceneContext.Instance.player.transform.position)
        };

        foreach (var (playerId, playerObject) in PlayerObjects)
        {
            if (playerObject)
                allPlayers.Add((playerId, playerObject.transform.position));
        }

        foreach (var actor in Actors.Values.ToArray())
        {
            try
            {
                if (actor == null)
                    continue;

                if (!actor.TryGetNetworkComponent(out var netActor))
                    continue;

                if (netActor.CurrentOwnerId == LocalID || PlayerManager.CheckPlayerExists(netActor.CurrentOwnerId))
                    continue;

                var actorId = netActor.ActorId;
                if (actorId.Value == 0)
                    continue;

                // Every in-range player counts as interested; the arbiter picks the nearest (host on ties).
                var position = actor.lastPosition;
                var registered = false;

                foreach (var (playerId, playerPosition) in allPlayers)
                {
                    if (!new Bounds(playerPosition, bounds).Contains(position))
                        continue;

                    OwnershipArbiter.RegisterInterest(playerId, actorId.Value,
                        Vector3.Distance(playerPosition, position), now);
                    registered = true;
                }

                if (registered)
                    OwnershipArbiter.EvaluateNow(actorId.Value);
            }
            catch { /* ignored */ }
        }
    }

    public static T[] GetAllOfType<T>() where T : IdentifiableModel
    {
        var result = new List<T>();

        var identifiables = sceneContext?.GameModel?.identifiables;

        if (identifiables == null)
            return result.ToArray();

        foreach (var ident in identifiables)
        {
            var casted = ident.value.TryCast<T>(); 
            if (casted != null)
                result.Add(casted);
        }
        return result.ToArray();
    }
    
}

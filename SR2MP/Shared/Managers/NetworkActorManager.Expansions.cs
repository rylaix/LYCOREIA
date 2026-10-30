using System.Collections;
using System.Net;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Player.PlayerItems;
using Il2CppMonomiPark.SlimeRancher.Slime;
using Il2CppMonomiPark.SlimeRancher.VFX;
using SR2MP.Components.Actor;
using SR2MP.Packets.Actor;
using SR2MP.Shared.Utils;

namespace SR2MP.Shared.Managers;

internal sealed partial class NetworkActorManager
{
    /// <summary>
    /// Validates actor mutation packets on the host against the authenticated UDP
    /// endpoint. Client-side packet handlers receive broadcasts from the server and
    /// do not have a remote client endpoint to validate here.
    /// </summary>
    internal static bool IsCurrentActorOwner(ActorId actorId, IPEndPoint? senderEndpoint)
    {
        if (!Main.Server.IsRunning)
            return true;

        if (senderEndpoint == null ||
            !Main.Server.ClientManager.TryGetClient(senderEndpoint, out var sender) || sender == null)
            return false;

        if (!TryGetCurrentOwnerId(actorId.Value, out var ownerId))
            return false;

        return ownerId == sender.PlayerId;
    }

    /// <summary>
    /// Sends an ownership change through the authoritative route. The host applies
    /// its own transition before broadcasting; clients wait for the server's echo.
    /// </summary>
    internal static void RequestOwnershipTransfer(ActorTransferPacket packet)
    {
        if (packet.ActorId.Value == 0 || string.IsNullOrWhiteSpace(packet.OwnerId))
            return;

#if SR2MP_DEEPLOG || SR2MP_TEST
        ActorDiag.Log("ownership-send", packet.ActorId.Value,
            $"previous={packet.PreviousOwnerId}; claimant={packet.OwnerId}; intent={packet.CaptureIntent}",
            ownerHint: packet.PreviousOwnerId, perActorMilliseconds: 500);
#endif
#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") == "1")
            SrLogger.LogMessage(
                $"[SR2MP_TEST] actor-ownership send actor={packet.ActorId.Value:X} " +
                $"previous={packet.PreviousOwnerId} claimant={packet.OwnerId} " +
                $"host={Main.Server.IsRunning} client={Main.Client.IsConnected}");
#endif

        if (!Main.Server.IsRunning)
        {
            // Client request: carry the epoch we believe current. The host stays authoritative and only logs it.
            packet.Epoch = ActorManager.GetEpoch(packet.ActorId.Value);
            packet.HasEpoch = true;
        }

        if (Main.Server.IsRunning)
        {
            if (NetworkActorCaptureManager.HasServerCaptureReservation(packet.ActorId.Value) &&
                !string.Equals(packet.OwnerId, packet.PreviousOwnerId, StringComparison.Ordinal))
            {
                NetworkActorCaptureManager.DeferServerOwnershipTransfer(packet);
                SrLogger.LogDebug(
                    $"Queued actor ownership transfer for {packet.ActorId.Value}: a guest capture is prepared.");
                return;
            }

            ApplyOwnership(packet);
            Main.Server.SendToAll(packet);
        }
        else if (Main.Client.IsConnected)
        {
            Main.Client.SendPacket(packet);
        }
    }

    private static bool TryGetCurrentOwnerId(long actorId, out string ownerId)
    {
        if (ActorManager.Actors.TryGetValue(actorId, out var actor) &&
            actor.TryGetNetworkComponent(out var networkActor) &&
            !string.IsNullOrWhiteSpace(networkActor.CurrentOwnerId))
        {
            ownerId = networkActor.CurrentOwnerId;
            return true;
        }

        if (ActorManager.OwnershipByActorId.TryGetValue(actorId, out var cachedOwnerId) &&
            !string.IsNullOrWhiteSpace(cachedOwnerId))
        {
            ownerId = cachedOwnerId;
            return true;
        }

        ownerId = string.Empty;
        return false;
    }

    public void RegisterSpawnOverNetwork(GameObject actor)
    {
        if (!actor) return;
        if (actor.GetComponent<NetworkActor>() != null) return;

        var identifiableActor = actor.GetComponent<IdentifiableActor>();
        if (!identifiableActor) return;

        var model = identifiableActor._model;
        if (model == null) return;
        if (NetworkDroneManager.IsDroneModel(model)) return;

        if (!CanPublishLocalActorSpawn(model.actorId.Value))
        {
            ActorSpawnPublicationPolicy.TraceSuppressed("RegisterSpawnOverNetwork", model.actorId.Value);
            return;
        }

        var networkActor = actor.AddComponent<NetworkActor>();
        networkActor.LocallyOwned = true;
        networkActor.CurrentOwnerId = LocalID;

        Actors[model.actorId.Value] = model;
        OwnershipByActorId[model.actorId.Value] = LocalID;

        var packet = new ActorSpawnPacket
        {
            ActorId = identifiableActor.GetActorId(),
            ActorType = GetPersistentID(actor.GetComponent<Identifiable>().identType),
            SceneGroup = (byte)NetworkSceneManager.GetPersistentID(model.sceneGroup),
            Position = actor.transform.position,
            Rotation = actor.transform.rotation,
            SpawnType = (byte)ActorSpawnType.Actor,
            OwnerId = LocalID
        };

        var slimeModel = model.TryCast<SlimeModel>();
        if (slimeModel != null)
        {
            packet.SpawnType = (byte)ActorSpawnType.Slime;
            packet.Emotions = slimeModel.Emotions;
            packet.Sleeping = slimeModel.isSleeping;
            packet.FirstAppearance = slimeModel.firstAppearanceSaveSet;
            packet.SecondAppearance = slimeModel.secondAppearanceSaveSet;
            packet.Radiancy = (byte)ActorAppearanceType.Default;
        }

#if SR2MP_DEEPLOG || SR2MP_TEST
        ActorDiag.Log("spawn-send", packet.ActorId.Value, "origin=RegisterSpawnOverNetwork",
            packet.ActorType, packet.OwnerId, packet.SceneGroup, packet.Position);
#endif
#if SR2MP_TEST
        if (Environment.GetEnvironmentVariable("SR2MP_TEST_TRACE_ACTORS") == "1")
            SrLogger.LogMessage(
                $"[SR2MP_TEST] actor-spawn send origin=RegisterSpawnOverNetwork actor={packet.ActorId.Value:X} " +
                $"type={packet.ActorType} spawnType={packet.SpawnType} owner={packet.OwnerId} " +
                $"scene={packet.SceneGroup} position={packet.Position}");
#endif

        Main.SendToAllOrServer(packet);
    }
    
    internal void SendActorTypeRegistry(IPEndPoint clientEndPoint)
    {
        if (!Main.Server.IsRunning) return;

        var packet = new ActorTypeRegistryPacket
        {
            Registry = new Dictionary<int, string>(ActorTypes.Count)
        };

        foreach (var (persistentId, type) in ActorTypes)
        {
            if (type == null) continue;
            packet.Registry[persistentId] = type.ReferenceId;
        }

        Main.Server.SendToClient(packet, clientEndPoint);
    }
    
    internal static bool ApplyRadiancy(SlimeModel slime, ActorAppearanceType radiancy = ActorAppearanceType.Default)
    {
        if (slime == null) return false;
        
        var gameObj = slime.GetGameObject();
        if (!gameObj) return false;
        
        var applicator = gameObj.GetComponent<SlimeAppearanceApplicator>();
        if (!applicator) return false;
        
        var def = gameObj.GetComponent<Identifiable>().identType.TryCast<SlimeDefinition>();
        if (!def) return false;
        
        if (radiancy == ActorAppearanceType.Default && slime.IsRadiant)
        {
            if (def!.RadiantBase && def.RadiantBase.AppearType == SlimeAppearance.AppearanceType.RADIANT_BASE)
                radiancy = ActorAppearanceType.BaseRadiant;
            else if (def.RadiantLargo0 &&
                     def.RadiantLargo0.AppearType == SlimeAppearance.AppearanceType.RADIANT_LARGO_0)
                radiancy = ActorAppearanceType.LargoRadiant0;
            else if (def.RadiantLargo1 &&
                     def.RadiantLargo1.AppearType == SlimeAppearance.AppearanceType.RADIANT_LARGO_1)
                radiancy = ActorAppearanceType.LargoRadiant1;
        }
        
        var newAppearance = radiancy switch
        {
            ActorAppearanceType.BaseRadiant => def!.RadiantBase,
            ActorAppearanceType.LargoRadiant0 => def!.RadiantLargo0,
            ActorAppearanceType.LargoRadiant1 => def!.RadiantLargo1,
            _ => applicator.Appearance
        };
        
        if (!newAppearance) return false;
        
        var slimeRadiant = gameObj.GetComponent<SlimeRadiant>();
        if (slimeRadiant)
        {
            slimeRadiant.SetRadiant();
            slimeRadiant.SetRadiantAppearance();
        }
        
        slime.GetAmmoMetadata().Radiant = true;
        applicator.Appearance = newAppearance;
        applicator.ApplyAppearance();
        applicator.HandleChosenAppearanceChanged(def, newAppearance);
        
        return true;
    }
    
    private static IEnumerator EnsureSlimeAppearance(SlimeModel slime)
    {
        yield return new WaitFrames(2);

        if (slime == null) yield break;

        var gameObj = slime.GetGameObject();
        if (!gameObj) yield break;

        var applicator = gameObj.GetComponent<SlimeAppearanceApplicator>();
        if (!applicator || applicator.Appearance) yield break;

        if (slime.IsRadiant && ApplyRadiancy(slime)) yield break;

        var def = gameObj.GetComponent<Identifiable>()?.identType.TryCast<SlimeDefinition>();
        if (!def) yield break;

        var appearance = def!.IsLargo
            ? def.GetLargoAppearance(slime.firstAppearanceSaveSet, slime.secondAppearanceSaveSet)
            : def.GetAppearanceForSet(slime.firstAppearanceSaveSet);

        if (!appearance)
            appearance = SceneContext.Instance?.SlimeAppearanceDirector?.GetChosenSlimeAppearance(def);

        if (!appearance)
            appearance = def.GetDefaultAppearance();

        if (!appearance) yield break;

        applicator.Appearance = appearance;
        applicator.ApplyAppearance();
    }

    internal static IEnumerator ApplySprinkleMaterial(GameObject gameObj, SprinkleMaterialType material)
    {
        yield return new WaitFrames(2);
        
        if (!gameObj) yield break;
        
        var sprinkle = gameObj.GetComponent<RandomMaterial>();
        if (!sprinkle) yield break;

        sprinkle.SetMaterial((int)material);
    }

    // Actors whose connected owner let them hibernate (accepted ActorUnload). The host arbiter treats these as free
    // to reassign; every peer records it so a client knows to express interest in them.
    private static readonly Dictionary<long, string> ReleasedByOwner = new();

    internal static void MarkReleasedByOwner(long actorId, string ownerId)
    {
        if (ReleasedByOwner.Count > 4096)
            ReleasedByOwner.Clear();
        ReleasedByOwner[actorId] = ownerId;
    }

    internal static bool WasReleasedByOwner(long actorId, string ownerId)
        => ReleasedByOwner.TryGetValue(actorId, out var releasedBy) && releasedBy == ownerId;

    private static readonly Dictionary<long, float> LastInterestSent = new();

    /// <summary>
    /// The only way a proximity/wake/slime-touch/resource path asks for an actor. The host registers the interest
    /// with its arbiter (which may grant at once); a client sends an interest claim, rate-limited per actor, and
    /// waits for the host's normal ownership broadcast. Nothing is ever applied optimistically.
    /// </summary>
    internal static void ExpressOwnershipInterest(ActorId actorId, string? currentOwner, bool force = false)
    {
        if (actorId.Value == 0 || (!Main.Server.IsRunning && !Main.Client.IsConnected))
            return;

        var now = UnityEngine.Time.unscaledTime;
        // One interest per actor per second on every peer (slime-touch hooks can fire every frame); region wake
        // forces it so a woken owner's interest is never swallowed by the throttle.
        if (!force && LastInterestSent.TryGetValue(actorId.Value, out var lastExpressed) && now - lastExpressed < 1f)
            return;
        if (LastInterestSent.Count >= 4096)
            LastInterestSent.Clear();
        LastInterestSent[actorId.Value] = now;
        var distance = float.PositiveInfinity;
        try
        {
            var player = SceneContext.Instance?.player;
            if (player && ActorManager.Actors.TryGetValue(actorId.Value, out var model) && model != null)
            {
                var gameObject = model.GetGameObject();
                var position = gameObject ? gameObject.transform.position : model.lastPosition;
                distance = Vector3.Distance(player.transform.position, position);
            }
        }
        catch
        {
            // distance stays unknown; the host uses its own position view anyway
        }

        if (Main.Server.IsRunning)
        {
            OwnershipArbiter.RegisterInterest(LocalID, actorId.Value, distance, now);
            OwnershipArbiter.EvaluateNow(actorId.Value);
            return;
        }


        Main.Client.SendPacket(new ActorTransferPacket
        {
            ActorId = actorId,
            OwnerId = LocalID,
            PreviousOwnerId = currentOwner ?? string.Empty,
            HasPreviousOwnerId = true,
            OwnershipInterest = true,
            InterestDistance = distance
        });
    }

    private static void LogStaleOwnership(ActorTransferPacket packet, string storedOwner, uint storedEpoch, string reason)
        => Logger.LogDiagnostic("actor", "ownership-stale-dropped",
            $"actorId={packet.ActorId.Value:X}; reason={reason}; packetOwner={packet.OwnerId}; packetEpoch={packet.Epoch}; " +
            $"storedOwner={storedOwner}; storedEpoch={storedEpoch}",
            rateKey: "actor/ownership-stale-dropped", minimumIntervalMilliseconds: 500);

    /// <summary>
    /// Applies an ownership change. Host: the single epoch issuer; bumps on every owner change (and the first grant)
    /// and stamps the packet so the following broadcast carries the new epoch. Client: applies only if the packet's
    /// epoch is not older than the stored one. Returns false when a client dropped the packet.
    /// </summary>
    internal static bool ApplyOwnership(ActorTransferPacket packet)
    {
        var actorKey = packet.ActorId.Value;
        var storedOwner = ActorManager.OwnershipByActorId.GetValueOrDefault(actorKey, string.Empty);
        var storedEpoch = ActorManager.GetEpoch(actorKey);

        if (Main.Server.IsRunning)
        {
            var next = OwnershipEpochPolicy.ShouldBump(storedOwner, packet.OwnerId, storedEpoch)
                ? OwnershipEpochPolicy.Next(storedEpoch)
                : storedEpoch;
            ActorManager.SetEpoch(actorKey, next);
            OwnershipArbiter.NoteGrant(actorKey, UnityEngine.Time.unscaledTime);
            packet.Epoch = next;
            packet.HasEpoch = true;
        }
        else
        {
            var decision = OwnershipEpochPolicy.DecideTransfer(
                storedEpoch, storedOwner, packet.HasEpoch, packet.Epoch, packet.OwnerId);
            // Only the host issues epochs, so on a client a same-epoch packet from the host is authoritative
            // (local owner writes without an epoch, e.g. spawn buffering, are what make it differ).
            if (decision == OwnershipEpochDecision.Conflict && !Main.Server.IsRunning)
                decision = OwnershipEpochDecision.Apply;
            if (decision != OwnershipEpochDecision.Apply)
            {
                LogStaleOwnership(packet, storedOwner, storedEpoch,
                    decision == OwnershipEpochDecision.Stale ? "older-epoch" : "same-epoch-different-owner");
                return false;
            }

            if (packet.HasEpoch)
                ActorManager.SetEpoch(actorKey, packet.Epoch);
        }

        ActorManager.OwnershipByActorId[packet.ActorId.Value] = packet.OwnerId;
        ReleasedByOwner.Remove(packet.ActorId.Value);

        if (!ActorManager.Actors.TryGetValue(packet.ActorId.Value, out var actor))
            return true;

        if (!actor.TryGetNetworkComponent(out var component))
            return true;
        
        component.CurrentOwnerId = packet.OwnerId;

        var locallyOwned = packet.OwnerId == LocalID;

        if (!locallyOwned)
        {
            try
            {
                var player = SceneContext.Instance.Player;
                var gameObject = actor.GetGameObject();

                if (player && gameObject)
                {
                    var vacItem = player.GetComponent<PlayerItemController>()._vacuumItem;

                    if (vacItem && vacItem._held == gameObject)
                    {
                        vacItem.LockJoint.connectedBody = null;
                        vacItem._held = null;
                        vacItem.SetHeldRad(0f);
                        vacItem._vacMode = VacuumItem.VacMode.NONE;
                        gameObject.GetComponent<Vacuumable>().Release();
                    }
                }
            }
            catch (Exception exception)
            {
                SrLogger.LogDebug($"Failed to release actor from vacuum on ownership change: {exception.Message}");
            }
        }

        component.LocallyOwned = locallyOwned;
        return true;
    }
    
    private static bool FreeActorIdForGadget(ActorId actorId, IdentifiableType incomingType)
    {
        try
        {
            if (GameState.TryGetIdentifiableModel(actorId, out var existing) && existing != null)
            {
                // Same gadget on the same id, not a collision.
                // Respawning would drop links, we don't want that
                if (existing.ident == incomingType || existing.ident?.IsPlayer == true)
                    return false;

                var gameObject = existing.GetGameObject();

                NetworkGadgetManager.RemoveTeleporterGadget(existing);
                NetworkGadgetManager.ForgetGadgetLink(actorId);

                HandlingPacket = true;
                if (gameObject)
                    Destroyer.DestroyAny(gameObject, "SR2MP.RemoveExistingGadgetModel");
                else
                    GameState.DestroyIdentifiableModel(existing);
                HandlingPacket = false;
            }
        }
        catch (Exception ex)
        {
            HandlingPacket = false;
            SrLogger.LogWarning($"Failed to free actor id {actorId.Value} for an incoming gadget: {ex.Message}");
        }

        RemoveExistingGadgetModel(actorId);
        return true;
    }

    internal static void RemoveExistingGadgetModel(ActorId actorId)
    {
        if (actorId.Value == 0) return;

        try
        {
            foreach (var gadget in GameState.AllGadgets().ToArray())
            {
                if (gadget == null || gadget.actorId.Value != actorId.Value)
                    continue;

                var gameObject = gadget.GetGameObject();

                NetworkGadgetManager.RemoveTeleporterGadget(gadget);
                NetworkGadgetManager.ForgetGadgetLink(gadget.actorId);

                HandlingPacket = true;
                if (gameObject)
                    Destroyer.DestroyGadget(gameObject, "SR2MP.RemoveExistingGadgetModel");
                else
                    GameState.DestroyGadgetModel(gadget);
                HandlingPacket = false;
                
                var mapDirector = SceneContext.Instance?.MapDirector;
                if (mapDirector != null)
                    mapDirector.DeregisterMarker(gadget);
            }
        }
        catch (Exception ex)
        {
            HandlingPacket = false;
            SrLogger.LogWarning($"Failed to remove existing gadget model for {actorId.Value}: {ex.Message}");
        }
    }
}

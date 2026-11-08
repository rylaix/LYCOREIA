// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Player.PlayerItems;
using Il2CppMonomiPark.SlimeRancher.Regions;
using Il2CppMonomiPark.SlimeRancher.Slime;
using Lycoreia.Actors;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using UnityEngine;

namespace Lycoreia.Game
{
    /// <summary>The game side of actor sync (IActorWorld) over the scene's GameModel. Internal removals never announce.</summary>
    public sealed class GameActorWorld : IActorWorld
    {
        /// <summary>True while the mod itself creates or removes actors; hooks must ignore those.</summary>
        public static int InternalDepth;
        static readonly Dictionary<long, (int instance, bool held)> HeldVisual = new Dictionary<long, (int, bool)>();
        internal static void ForgetVisual(long id) => HeldVisual.Remove(id);
        public static bool Internal => InternalDepth > 0;
        /// <summary>Loaded ranch cells' region boxes (refreshed every 10 s by the actor runtime).</summary>
        public static readonly RanchBoundsSet Ranch = new RanchBoundsSet();

        static GameModel Model => GameState.Model;
        internal static IdentifiableModel Find(long id) { var m = Model; return m != null && m.identifiables.TryGetValue(new ActorId(id), out var model) ? model : null; }
        internal static GameObject ObjectOf(long id) { try { return Find(id)?.GetGameObject(); } catch { return null; } }
        static Vector3 V(Vec3 v) => new Vector3(v.X, v.Y, v.Z);
        static Quaternion Q(Quat q) => new Quaternion(q.X, q.Y, q.Z, q.W);
        internal static Vec3 W(Vector3 v) => new Vec3(v.x, v.y, v.z);

        public bool TypeKnown(int typeId) => ActorTypes.TryGetType(typeId, out _);

        public bool CreateReplica(ActorEntry e)
        {
            InternalDepth++;
            try
            {
                if (!ActorTypes.TryGetType(e.TypeId, out var type)) return false;
                var group = SceneGroups.Get(e.Scene); var model = Model; if (model == null || group == null) return false;
                long alloc = ActorReconciler.AllocatorBefore();
                ActorModel actor;
                try
                {
                    actor = model.InstantiateActorWithoutNotify(new ActorId(e.Id), type, group, V(e.Position), Q(e.Rotation));
                    if (actor == null) return false;
                    if (SceneContext.Instance != null && SceneContext.Instance.GameModel != null) InstantiationHelpers.InstantiateActorFromModel(actor);   // model-only when its region is not loaded
                }
                finally { ActorReconciler.AllocatorAfter(alloc, e.Id); }                 // a replica's id must never move our own allocator
                ActorRuntime.Motion.Teleport(e.Id, e.Position, e.Rotation);
                return true;
            }
            catch (Exception ex) { Log.Warn($"could not create replica {e.Id}: {ex.Message}"); return false; }
            finally { InternalDepth--; }
        }

        public void DestroyReplica(long actorId)
        {
            InternalDepth++;
            try
            {
                ActorRuntime.Forget(actorId);
                var model = Find(actorId); if (model == null) return;
                var go = model.GetGameObject();
                if (go != null) Destroyer.DestroyActor(go, "Lycoreia.internal", true); else Model.DestroyIdentifiableModel(model);
            }
            catch (Exception ex) { Log.Warn($"could not remove actor {actorId}: {ex.Message}"); }
            finally { InternalDepth--; }
        }

        /// <summary>An owner sample: hibernating regions get the transform directly, live replicas are interpolated by the runtime.</summary>
        public void ApplyPose(ActorEntry e)
        {
            try
            {
                var model = Find(e.Id); if (model == null) return;
                var go = model.GetGameObject();
                if (go == null) { model.lastPosition = V(e.Position); return; }
                var vacuumable = go.GetComponent<Vacuumable>();
                if (vacuumable != null && !ActorRuntime.Motion.IsPredicted(e.Id) &&
                    (!HeldVisual.TryGetValue(e.Id, out var shown) || shown.instance != go.GetInstanceID() || shown.held != e.HeldByOwner))
                {
                    // A remote shader state must not turn the local replica into a
                    // native held object, changing capture eligibility or ownership facts.
                    bool nativeHeld = vacuumable._held;
                    try { vacuumable._held = e.HeldByOwner; vacuumable.UpdateMaterialsHeldState(); }
                    finally { vacuumable._held = nativeHeld; }
                    if (HeldVisual.Count > 8192) HeldVisual.Clear();
                    HeldVisual[e.Id] = (go.GetInstanceID(), e.HeldByOwner);
                    Mod.Diagnostics?.Record("actors", "held-visual-applied", $"id={e.Id}; type={e.TypeId}; owner={e.Owner}; held={e.HeldByOwner}");
                }
                if (Mod.IsHost && Mod.Actors.Captures.HasReservation(e.Id))
                {
                    BodyMode.Set(go, go.GetComponent<Rigidbody>(), true);
                    go.transform.SetPositionAndRotation(V(e.Position), Q(e.Rotation));
                }
                ActorRuntime.Motion.Push(e.Id, Time.unscaledTimeAsDouble, e.Position, e.Rotation, e.Velocity);
                var body = go.GetComponent<Rigidbody>();
                if (body != null && !body.isKinematic && !ActorRuntime.Motion.IsPredicted(e.Id)) body.velocity = V(e.Velocity);   // never fight the local vacuum's pull
            }
            catch (Exception ex) { Log.Debug($"pose for {e.Id}: {ex.Message}"); }
        }

        /// <summary>Emotions and sleep (slimes), cycle state and progress (produce), invulnerability (plorts). Only called for the owner's state.</summary>
        public void ApplyState(ActorState s)
        {
            try
            {
                var go = ObjectOf(s.ActorId); var model = Find(s.ActorId);
                switch (s.Kind)
                {
                    case ActorStateKind.Slime:
                        var em = go != null ? go.GetComponent<SlimeEmotions>() : null;
                        var values = new Unity.Mathematics.float4(s.Hunger, s.Agitation, s.Fear, s.Social);
                        if (em != null) { em.SetAll(values); if (em.IsSleeping() != s.Sleeping) em.SetAsleep(s.Sleeping); }
                        else if (model != null && model.TryCast<SlimeModel>() is { } sm) { sm.Emotions = values; sm.isSleeping = s.Sleeping; }
                        break;
                    case ActorStateKind.Produce:
                        // the game's own ripen / edible steps first (they compare against the model's stage, so the model is written after)
                        if (go != null) ProduceLink.SyncStage(go, s.CycleState, s.ActorId);
                        if (model != null && ProduceRules.IsCacheableTime(s.Progress) && model.TryCast<ProduceModel>() is { } pm) pm.Push((ResourceCycle.State)s.CycleState, s.Progress);
                        if (go != null && float.IsFinite(s.Scale) && s.Scale > 0.01f) go.transform.localScale = Vector3.one * s.Scale;
                        if (go != null) ProduceLink.SyncReplica(go, s.Attached, s.ActorId);
                        break;
                    case ActorStateKind.Plort:
                        var inv = go != null ? go.GetComponent<PlortInvulnerability>() : null;
                        if (inv != null && s.Invulnerable) inv.GoInvulnerable();
                        break;
                }
            }
            catch (Exception ex) { Log.Debug($"state for {s.ActorId}: {ex.Message}"); }
        }

        /// <summary>Owners simulate (dynamic body, live resource cycle); non-owners hold a frozen kinematic replica.</summary>
        public void ApplyLocalOwnership(ActorEntry e, bool locallyOwned)
        {
            try
            {
                var go = ObjectOf(e.Id);
                // A new owner starts a new transform stream. Do not let buffered samples or send baselines from the previous owner
                // pull the replica back along that owner's path after a hand-off.
                ActorRuntime.Sampler.Forget(e.Id);
                if (go != null)
                {
                    var shown = go.transform; var r = shown.rotation;
                    ActorRuntime.Motion.Teleport(e.Id, W(shown.position), new Quat(r.x, r.y, r.z, r.w));
                    var body = go.GetComponent<Rigidbody>();
                    var vacuumable = go.GetComponent<Vacuumable>(); var localVac = LocalVacuum;
                    if (locallyOwned && !ActorRuntime.Motion.IsPredicted(e.Id) && vacuumable != null &&
                        vacuumable.IsHeld() && !vacuumable.IsCaptive() && (localVac == null || localVac._held != go))
                        vacuumable.SetHeld(false);
                    if (body != null && !ActorRuntime.Motion.IsPredicted(e.Id)) BodyMode.Set(go, body, !locallyOwned);
                }
                else ActorRuntime.Motion.Remove(e.Id);
                if (locallyOwned) ActorRuntime.NoteOwned(e.Id);
                ActorRuntime.OnLocalOwnership(e.Id, locallyOwned);
            }
            catch (Exception ex) { Log.Debug($"ownership for {e.Id}: {ex.Message}"); }
        }

        public bool IsHibernating(long actorId)
        {
            var go = ObjectOf(actorId);
            return go == null || !go.activeInHierarchy || BodyMode.IsHibernating(go);
        }

        public bool IsProtectedRanchSlime(long actorId)
        {
            var go = ObjectOf(actorId); if (go == null) return false;
            return go.GetComponent<SlimeEat>() != null && IsProtectedRanchActor(go);
        }

        /// <summary>
        /// A slime (not Tarr, not produce) that is on the ranch or any loaded ranch expansion, by the game's own cell test OR by the loaded
        /// ranch-cell bounds. The native ranch test alone failed on guest-owned replicas (2026-11-08: ranch largos culled by
        /// DestroyOutsideHoursOfDay / MaybeCullOnReenable on the guests), so either answer protects.
        /// </summary>
        public static bool IsRanchSafeSlime(GameObject go)
        {
            try
            {
                if (go == null || go.GetComponent<SlimeEat>() == null || go.GetComponent<ResourceCycle>() != null) return false;
                var ident = go.GetComponent<IdentifiableActor>(); if (ident == null) return false;
                string n = ident.identType != null ? ident.identType.name ?? "" : "";
                if (n.Contains("Tarr", StringComparison.OrdinalIgnoreCase)) return false;
                if (IsProtectedRanchActor(go)) return true;
                return Ranch.Contains(W(go.transform.position)) == true;
            }
            catch { return false; }
        }

        /// <summary>Native despawn/cull sources that are never gameplay on a ranch (no player, slime or hazard caused them).</summary>
        public static bool IsCullSource(string source) => !string.IsNullOrEmpty(source) &&
            (source.StartsWith("DestroyOutsideHoursOfDay", StringComparison.Ordinal) || source.StartsWith("MaybeCullOnReenable", StringComparison.Ordinal) ||
             source.StartsWith("DestroyAfterTime", StringComparison.Ordinal) || source.StartsWith("GameObjectActorModelIdentifiableIndex.DestroyAllOfType", StringComparison.Ordinal));

        /// <summary>Ranch actors (the game's own ranch-cell test) except Tarr and produce.</summary>
        public static bool IsProtectedRanchActor(GameObject go)
        {
            try
            {
                var ident = go != null ? go.GetComponent<IdentifiableActor>() : null; if (ident == null) return false;
                var member = go.GetComponent<RegionMember>(); bool onRanch = member != null && CellDirector.IsOnRanch(member);
                string n = ident.identType != null ? ident.identType.name ?? "" : "";
                return RanchProtection.IsProtected(onRanch, n.Contains("Tarr", StringComparison.OrdinalIgnoreCase), go.GetComponent<ResourceCycle>() != null);
            }
            catch { return false; }
        }

        public CaptureFacts GetCaptureFacts(long actorId, string playerId)
        {
            var f = new CaptureFacts { MaxVacuumDistance = MaxVacuumDistance, PlayerToActor = float.PositiveInfinity };
            try
            {
                var model = Find(actorId); var go = model?.GetGameObject();
                f.Live = go != null; f.NativeReadable = f.Live;
                var vac = f.Live ? go.GetComponent<Vacuumable>() : null;
                f.HasVacuumable = vac != null;
                // CanCapture is a local per-vacpack cooldown check (Vacuumable._nextVacTime), not actor eligibility.
                // This code validates a remote request on the host, whose cooldown says nothing about the guest's vacpack.
                // The guest already checks its native CanCapture before requesting; the host still validates actor state,
                // captivity, scene and distance here.
                f.CanCapture = vac != null; f.Captive = vac != null && (vac.IsCaptive() || vac.IsHeld() || HostHoldsActor(actorId));
                f.PlayerToActor = f.Live ? DistanceToActor(playerId, actorId, float.PositiveInfinity) : float.PositiveInfinity;
                // The actor's scene group is known; a remote player's is not sent, so a remote player is assumed to be in the actor's
                // group (their avatar distance already proves they are next to it). The local player compares for real.
                f.ActorScene = model != null ? SceneGroups.IdOf(model.sceneGroup) : -1;
                f.PlayerScene = playerId == Mod.LocalId ? LocalSceneGroup(f.ActorScene) : f.ActorScene;
            }
            catch (Exception ex) { Log.Debug($"capture facts {actorId}: {ex.Message}"); f.NativeReadable = false; }
            return f;
        }

        static int LocalSceneGroup(int fallback)
        {
            try { var g = SceneContext.Instance?.Player?.GetComponent<RegionMember>(); var r = g != null && g.Regions != null && g.Regions.Count > 0 ? g.Regions[0] : null; return r != null ? SceneGroups.IdOf(r.SceneGroup) : fallback; }
            catch { return fallback; }
        }

        public bool RemoveForConsume(long actorId)
        {
            DestroyReplica(actorId); var m = Find(actorId); return m == null || m.GetGameObject() == null;
        }

        public static VacuumItem LocalVacuum
        {
            get { try { return SceneContext.Instance?.Player?.GetComponent<PlayerItemController>()?._vacuumItem; } catch { return null; } }
        }

        public bool HostHoldsActor(long actorId)
        {
            try { var vac = LocalVacuum; var go = ObjectOf(actorId); return vac != null && go != null && vac._held == go; }
            catch { return false; }
        }

        /// <summary>The native maximum vacuum distance, read from the local vacuum when it exists.</summary>
        public float MaxVacuumDistance { get { try { var v = LocalVacuum; if (v != null && v.MaxVacDist > 0) return v.MaxVacDist; } catch { } return 12f; } }

        public float DistanceToActor(string playerId, long actorId, float fallback)
        {
            // A remote simulator's accepted sample is authoritative even when this
            // peer retains a hibernating native object at an old island position.
            if (Mod.Actors != null && Mod.Actors.Table.TryGet(actorId, out var entry) && entry.Owner != Mod.LocalId)
                return Players.TryPosition(playerId, out var remotePlayer) ? Vector3.Distance(remotePlayer, V(entry.Position)) : fallback;
            var model = Find(actorId); if (model == null) return fallback;
            var go = model.GetGameObject(); Vector3 actorPos = go != null ? go.transform.position : model.lastPosition;
            return Players.TryPosition(playerId, out var p) ? Vector3.Distance(p, actorPos) : fallback;
        }

        // ---- ISpawnWorld
        static readonly Dictionary<IntPtr, SpawnClass> ClassCache = new();
        static readonly Dictionary<IntPtr, bool> ChickenCache = new();
        public SpawnClass ClassOf(int typeId) => ActorTypes.TryGetType(typeId, out var t) ? ClassOf(t) : SpawnClass.Item;
        public static SpawnClass ClassOf(IdentifiableType t)
        {
            if (t == null) return SpawnClass.Item;
            if (ClassCache.TryGetValue(t.Pointer, out var cached)) return cached;
            var result = ClassUncached(t); ClassCache[t.Pointer] = result; return result;
        }
        static SpawnClass ClassUncached(IdentifiableType t)
        {
            try { if (t.IsAnimal) return SpawnClass.Animal; if (t.IsPlort) return SpawnClass.Item; if (t.TryCast<SlimeDefinition>() != null) return SpawnClass.Slime; } catch { }
            string n = t.name ?? "";
            return n.Contains("Slime", StringComparison.OrdinalIgnoreCase) && !n.Contains("Plort", StringComparison.OrdinalIgnoreCase) ? SpawnClass.Slime : SpawnClass.Item;
        }
        public static bool IsChicken(IdentifiableType t)
        {
            if (t == null) return false;
            if (!ChickenCache.TryGetValue(t.Pointer, out var chicken)) ChickenCache[t.Pointer] = chicken = ChickenCap.IsChickenTypeName(t.name ?? "");
            return chicken;
        }

        /// <summary>Ranch test by loaded ranch-cell bounds. Unknown (nothing loaded) answers true so a spawn is never refused.</summary>
        public bool IsRanch(Vec3 p) => Ranch.Contains(p) ?? true;

        /// <summary>Counts from the networked table (any owner): native population checks only see part of the world.</summary>
        public int CountSameTypeWithin(int typeId, Vec3 position, float radius)
        {
            var a = Mod.Actors; if (a == null) return 0; int n = 0; float r2 = radius * radius;
            foreach (var e in a.Table.All) if (e.TypeId == typeId && !e.IsGadget && Dist2(e.Position, position) <= r2) n++;
            return n;
        }
        public int CountWildSlimesAndAnimals()
        {
            var a = Mod.Actors; if (a == null) return 0; int n = 0;
            foreach (var e in a.Table.All) { if (e.IsGadget) continue; var c = ClassOf(e.TypeId); if (c != SpawnClass.Item && Ranch.Contains(e.Position) != true) n++; }
            return n;
        }
        public static int CountWithin(Func<ActorEntry, bool> match, Vec3 position, float radius)
        {
            var a = Mod.Actors; if (a == null) return 0; int n = 0; float r2 = radius * radius;
            foreach (var e in a.Table.All) if (!e.IsGadget && Dist2(e.Position, position) <= r2 && match(e)) n++;
            return n;
        }
        internal static float Dist2(Vec3 a, Vec3 b) { float x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z; return x * x + y * y + z * z; }
        public bool TryPlayerPosition(string playerId, out Vec3 position) { if (Players.TryPosition(playerId, out var p)) { position = W(p); return true; } position = default; return false; }

        /// <summary>
        /// Guest snapshot step: removes this peer's own world actors (its save contents) that the table does not know, internally and
        /// unannounced. Gadgets, the player and drones stay; ids in <paramref name="keep"/> (irreversible captures) stay.
        /// </summary>
        public static int ClearLocalActors(ICollection<long> keep)
        {
            var m = Model; if (m == null) return 0; int n = 0; var ids = new List<IdentifiableModel>();
            foreach (var kv in m.identifiables) { var im = kv.Value; if (im != null && im.TryCast<ActorModel>() != null) ids.Add(im); }
            InternalDepth++;
            try
            {
                foreach (var im in ids)
                {
                    try
                    {
                        long id = im.actorId.Value; if (keep != null && keep.Contains(id)) continue;
                        var t = im.ident; if (t != null && t.IsPlayer) continue;
                        var go = im.GetGameObject();
                        if (go != null) { if (go.GetComponent<Il2CppMonomiPark.SlimeRancher.Drone.DroneStation>() != null) continue; Destroyer.DestroyActor(go, "Lycoreia.snapshot", true); }
                        else m.DestroyIdentifiableModel(im);
                        ActorRuntime.Forget(id); n++;
                    }
                    catch (Exception e) { Log.Debug("snapshot clear: " + e.Message); }
                }
            }
            finally { InternalDepth--; }
            return n;
        }

        /// <summary>Rebuilds the ranch boxes from the loaded ranch cells' region bounds.</summary>
        public static void RefreshRanchBounds(double now)
        {
            var list = new List<(Vec3, Vec3)>();
            try
            {
                foreach (var cd in UnityEngine.Object.FindObjectsOfType<CellDirector>())
                {
                    if (cd == null || !cd.IsRanch) continue; var r = cd._Region_k__BackingField; if (r == null) continue;
                    var b = r.Bounds; list.Add((W(b.center), W(b.size)));
                }
            }
            catch (Exception ex) { Log.Debug("ranch bounds: " + ex.Message); }
            Ranch.Set(list, now);
        }
    }

    /// <summary>Positions of players as this peer sees them (local avatar and remote avatars).</summary>
    public static class Players
    {
        public static readonly Dictionary<string, Transform> Avatars = new Dictionary<string, Transform>();
        public static bool TryPosition(string id, out Vector3 p)
        {
            if (id != null && id == Mod.LocalId) { var pl = SceneContext.Instance?.Player; if (pl != null) { p = pl.transform.position; return true; } }
            // Hidden cross-island render roots stop ticking. Gameplay distances must use
            // the last received player state, which continues arriving while hidden.
            else if (id != null && Lycoreia.Game.Avatars.RemoteAvatars.All.TryGetValue(id, out var avatar) && avatar.HasState) { p = avatar.MapPosition; return true; }
            p = default; return false;
        }
        public static IEnumerable<string> Known { get { if (Mod.LocalId != null) yield return Mod.LocalId; foreach (var k in Avatars.Keys) yield return k; } }
    }
}

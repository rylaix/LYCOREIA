// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Player.PlayerItems;
using Il2CppMonomiPark.SlimeRancher.Slime;
using Lycoreia.Actors;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using UnityEngine;

namespace Lycoreia.Game
{
    /// <summary>
    /// Runs once per frame from the expansion's pump list: replica interpolation (every frame), owner transform samples (20 Hz),
    /// owner state keep-alives (about every 15 s), region release detection, the interest pass (2 Hz), the produce size sweep (1 Hz),
    /// ranch bounds (every 10 s), /cleanup removal (40 per frame) and the guest capture client.
    /// </summary>
    public sealed class ActorRuntime : IFramePump
    {
        public string Name => "actors";
        public static readonly ReplicaMotion Motion = new ReplicaMotion();
        public static readonly OwnerSampler Sampler = new OwnerSampler();
        public static readonly InterestPass Interest = new InterestPass();
        public static readonly ProduceCycleCache ProduceCache = new ProduceCycleCache();
        static readonly HashSet<long> _reservationFrozen = new HashSet<long>();
        static readonly HashSet<long> _awakeOwned = new HashSet<long>();
        static readonly List<long> _cleanupQueue = new List<long>();
        static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);
        const double OwnerEvery = 0.05, InterestEvery = 0.5, SweepEvery = 0.25; const long StateEveryTicks = 300;   // 300 x 50 ms = 15 s
        double _nextOwner, _nextInterest, _nextSweep; long _tick; bool _wasActive;

        public static int CleanupPending => _cleanupQueue.Count;

        public void Pump()
        {
            if (GameState.Loading) return;
            var a = Mod.Actors; bool active = a != null && Mod.IsNetworkActive && GameState.Ready;
            if (!active) { if (_wasActive) ResetLocal(); _wasActive = false; return; }
            _wasActive = true;
            double now = Time.unscaledTimeAsDouble;
            if (GameActorWorld.Ranch.NeedsRefresh(now)) GameActorWorld.RefreshRanchBounds(now);
            Step(Motion, a, now);
            if (now >= _nextOwner) { _nextOwner = now + OwnerEvery; _tick++; OwnerPass(a, now); }
            if (now >= _nextInterest) { _nextInterest = now + InterestEvery; InterestPassRun(a, now); }
            if (now >= _nextSweep) { _nextSweep = now + SweepEvery; ProduceSweep(a); }
            if (_cleanupQueue.Count > 0) CleanupSlice(a);
            VacuumCapture.Pump(now);
        }

        /// <summary>A scene loader pauses this simulator. Release ordinary actors before
        /// the native objects disappear; in-flight captures and held items keep their transaction authority.</summary>
        public static void ReleaseForLoading()
        {
            var a = Mod.Actors; if (a == null || !Mod.IsNetworkActive) return;
            var ids = new List<long>();
            foreach (var e in a.Table.All)
                if (!e.IsGadget && e.Owner == Mod.LocalId && !e.ReleasedByOwner && !e.HeldByOwner &&
                    !Motion.IsPredicted(e.Id) && !VacuumCapture.Machine.OwnsConsumeRemoval(e.Id) &&
                    (!Mod.IsHost || !a.Captures.HasReservation(e.Id))) ids.Add(e.Id);
            foreach (var id in ids) a.SendUnload(id);
            if (ids.Count > 0) Mod.Diagnostics?.Record("actors", "loading-simulator-released", $"count={ids.Count}; captures=preserved");
        }

        static void ResetLocal() { _ownedAt.Clear(); SnapshotAppliedAt = double.NegativeInfinity; Motion.Clear(); Sampler.Clear(); _awakeOwned.Clear(); _reservationFrozen.Clear(); _cleanupQueue.Clear(); ProduceCache.Clear(); VacuumCapture.Reset(); GameActorWorld.Ranch.Clear(); _plortScale.Clear(); }

        public static void Forget(long id) { GameActorWorld.ForgetVisual(id); Motion.Remove(id); Sampler.Forget(id); _awakeOwned.Remove(id); _reservationFrozen.Remove(id); }

        // ------------------------------------------------------------------ settle window (removals right after a hand-over or a join)
        public const double GrantSettleSeconds = 5, JoinSettleSeconds = 15;
        static readonly Dictionary<long, double> _ownedAt = new Dictionary<long, double>();
        public static double SnapshotAppliedAt = double.NegativeInfinity;
        public static void NoteOwned(long id) { if (_ownedAt.Count > 4096) _ownedAt.Clear(); _ownedAt[id] = Time.unscaledTimeAsDouble; }
        /// <summary>The game removing an actor we only just gained (or right after joining) is its own load-time cleanup of a copy it
        /// cannot place yet (2026-11-02: 18 garden beets handed to a joining guest were removed natively and the broadcast destroyed
        /// them for everyone). Such removals release the actor instead of destroying it.</summary>
        public static bool InSettleWindow(long id)
        {
            double now = Time.unscaledTimeAsDouble;
            return now - SnapshotAppliedAt < JoinSettleSeconds || (_ownedAt.TryGetValue(id, out var t) && now - t < GrantSettleSeconds);
        }

        // ------------------------------------------------------------------ replicas
        static void Step(ReplicaMotion motion, ActorSync a, double now)
        {
            motion.Step(now, id => a.Table.TryGet(id, out var e) && e.Owner != Mod.LocalId, (id, p, q) =>
            {
                var go = GameActorWorld.ObjectOf(id); if (go == null) return;
                go.transform.SetPositionAndRotation(new Vector3(p.X, p.Y, p.Z), new Quaternion(q.X, q.Y, q.Z, q.W));
            });
        }

        /// <summary>Local vacuum prediction: the predicted replica gets a dynamic body for the pull and is skipped by interpolation.</summary>
        public static void SetPredicted(ActorLink l, bool on)
        {
            if (l == null) return; Motion.SetPredicted(l.Id, on);
            try
            {
                var body = l.GetComponent<Rigidbody>(); var cycle = l.GetComponent<ResourceCycle>();
                bool attached = cycle != null && cycle._joint != null && cycle._joint.Joint != null;
                if (body != null) BodyMode.Set(l.gameObject, body, BodyModeRule.PredictionKinematic(on, l.LocallyOwned, attached));
            }
            catch { }
        }

        // ------------------------------------------------------------------ owner side
        void OwnerPass(ActorSync a, double now)
        {
            string me = Mod.LocalId; List<long> released = null; var localVac = GameActorWorld.LocalVacuum;
            foreach (var e in a.Table.All)
            {
                if (e.Owner != me || e.IsGadget) continue;
                GameObject go; try { go = GameActorWorld.ObjectOf(e.Id); } catch { go = null; }
                if (go == null || !go.activeInHierarchy || BodyMode.IsHibernating(go))
                {
                    _awakeOwned.Remove(e.Id);
                    // An interest grant can arrive after this region went to sleep.
                    // Release that grant too, even if it was never sampled awake.
                    if (!e.ReleasedByOwner) (released ??= new List<long>()).Add(e.Id);
                    continue;
                }
                if (a.IsHost && a.Captures.HasReservation(e.Id))
                {
                    BodyMode.Set(go, go.GetComponent<Rigidbody>(), true); _reservationFrozen.Add(e.Id);
                    continue;
                }
                if (_reservationFrozen.Remove(e.Id) && !BodyMode.NativeHold(go)) BodyMode.Set(go, go.GetComponent<Rigidbody>(), false);
                bool woke = _awakeOwned.Add(e.Id); e.ReleasedByOwner = false;
                try
                {
                    var t = go.transform; var body = go.GetComponent<Rigidbody>();
                    var p = GameActorWorld.W(t.position); var r = t.rotation; var q = new Quat(r.x, r.y, r.z, r.w);
                    var v = body != null && !body.isKinematic ? GameActorWorld.W(body.velocity) : Vec3.Zero;
                    e.Position = p; e.Rotation = q; e.Velocity = v;
                    bool held = InOwnVacuum(go, localVac), heldChanged = held != e.HeldByOwner; e.HeldByOwner = held;
                    if (Sampler.ShouldSend(e.Id, p, q, v, _tick) || heldChanged) a.SendOwnedUpdate(e);
                    if ((woke || (_tick + e.Id) % StateEveryTicks == 0) && BuildState(e.Id, go) is { } st) a.SendOwnedState(st);
                }
                catch (Exception ex) { Rl.Warn("owner-pass", "owner sample failed: " + ex.Message); }
            }
            if (released != null) foreach (var id in released) a.SendUnload(id);     // region hibernated or unloaded the actor
        }

        /// <summary>The local vacuum holds this object or is pulling it in (captive on its joint).</summary>
        static bool InOwnVacuum(GameObject go, VacuumItem vac)
        {
            try
            {
                if (vac != null && vac._held != null && vac._held.Pointer == go.Pointer) return true;
                var v = go.GetComponent<Vacuumable>(); return v != null && (v.IsHeld() || v.IsCaptive());
            }
            catch { return false; }
        }

        /// <summary>The owner's current state for the state message (slimes and produce); null for other actors.</summary>
        public static ActorState BuildState(long id, GameObject go)
        {
            var em = go.GetComponent<SlimeEmotions>();
            if (em != null) { var v = em.GetValues(); return new ActorState { ActorId = id, Kind = ActorStateKind.Slime, Hunger = v.x, Agitation = v.y, Fear = v.z, Social = v.w, Sleeping = em.IsSleeping() }; }
            var rc = go.GetComponent<ResourceCycle>();
            if (rc != null && rc._model != null) { double p = rc._model.progressTime; if (!ProduceRules.IsCacheableTime(p)) return null; return new ActorState { ActorId = id, Kind = ActorStateKind.Produce, CycleState = (int)rc._model.state, Progress = p, Scale = go.transform.localScale.x, Attached = rc._joint != null && rc._joint.Joint != null }; }
            return null;
        }

        // ------------------------------------------------------------------ produce (4.11)
        /// <summary>Non-owners freeze the resource cycle; the real progress is cached and restored when ownership is gained.</summary>
        public static void OnLocalOwnership(long id, bool owned)
        {
            try
            {
                var model = GameActorWorld.Find(id)?.TryCast<ProduceModel>(); if (model == null) return;
                if (!owned) ProduceCache.Freeze(id, model.progressTime);
                else if (ProduceCache.Has(id)) { double now = SceneContext.Instance?.TimeDirector?.WorldTime() ?? 0; model.progressTime = ProduceCache.Restore(id, now); }
            }
            catch (Exception ex) { Rl.Warn("produce-own", "produce ownership: " + ex.Message); }
        }

        const double ProduceResendSeconds = 5;
        static readonly Dictionary<long, double> _produceResent = new Dictionary<long, double>(), _staleSince = new Dictionary<long, double>();
        static void ProduceSweep(ActorSync a)
        {
            double now = Time.unscaledTimeAsDouble;
            foreach (var e in a.Table.All)
            {
                if (e.IsGadget) continue;
                try
                {
                    if (PlortPrefabScale(e.TypeId) is float plortScale) { CheckPlortScale(e, plortScale); continue; }
                    var go = GameActorWorld.ObjectOf(e.Id); if (go == null) continue; var rc = go.GetComponent<ResourceCycle>(); if (rc == null) continue;
                    var phase = PhaseOf(rc.GetState()); bool onTree = rc._joint != null && rc._joint.Joint != null;
                    // owner: every attach change and every stage change (unripe, ripe, edible, rotten) goes out at once; a frozen copy only
                    // becomes vacuumable when the game's own ripen/edible step ran on it
                    if (e.Owner == Mod.LocalId && rc._model != null)
                    {
                        bool changed = onTree != e.Attached || (int)rc._model.state != e.SentStage;
                        // issue5 (fruit left floating on the other seat): a harvester's first state after its capture grant can reach the previous owner
                        // before that owner applied the grant and is dropped there ("not from the owner"); the sender had already recorded it as sent,
                        // so the old owner kept the fruit on its plant joint for up to 15 s. Re-send once a second for a few seconds after becoming owner.
                        bool retry = !changed && _ownedAt.TryGetValue(e.Id, out var since) && now - since < ProduceResendSeconds && now - (_produceResent.TryGetValue(e.Id, out var last) ? last : 0) >= 1.0;
                        if (changed || retry)
                        {
                            e.SentStage = (int)rc._model.state; if (_produceResent.Count > 4096) _produceResent.Clear(); _produceResent[e.Id] = now;
                            a.SendOwnedState(new ActorState { ActorId = e.Id, Kind = ActorStateKind.Produce, CycleState = e.SentStage, Progress = rc._model.progressTime, Scale = go.transform.localScale.x, Attached = onTree });
                            if (retry) Mod.Diagnostics?.Record("actors", "produce-state-resent", $"id={e.Id}; stage={e.SentStage}; attached={onTree}");
                        }
                    }
                    // replica: the owner says the fruit left its plant but this copy still hangs on the local joint (state lost or overtaken):
                    // release it, otherwise the frozen copy stays in the air where the owner's harvest started
                    else if (e.Owner != Mod.LocalId && onTree && !e.Attached && !Motion.IsPredicted(e.Id))
                    {
                        if (!_staleSince.TryGetValue(e.Id, out var st)) _staleSince[e.Id] = now;
                        else if (now - st >= 1.0)
                        {
                            _staleSince.Remove(e.Id);
                            GameActorWorld.InternalDepth++;
                            try { rc.DetachFromJoint(); } finally { GameActorWorld.InternalDepth--; }
                            Mod.Diagnostics?.Record("actors", "produce-stale-joint-released", $"id={e.Id}; owner={e.Owner}; stage={rc.GetState()}");
                        }
                    }
                    else _staleSince.Remove(e.Id);
                    var body = go.GetComponent<Rigidbody>(); bool resting = body == null || body.velocity.sqrMagnitude < 0.04f;
                    var vac = go.GetComponent<Vacuumable>(); bool pulled = Motion.IsPredicted(e.Id) || (vac != null && vac.IsCaptive()); bool held = vac != null && vac.IsHeld();
                    var full = rc._defaultScale; bool small = full.sqrMagnitude > 0 && go.transform.localScale.x < full.x * 0.95f;
                    if (ProduceRules.SweepRestores(phase, onTree, resting, small, pulled, held)) go.transform.localScale = full;
                }
                catch { }
            }
        }
        // ------------------------------------------------------------------ plort scale (issue4)
        static readonly Dictionary<int, float> _plortScale = new Dictionary<int, float>();
        /// <summary>The prefab scale of a plort type (cached per type id); null for every other type.</summary>
        static float? PlortPrefabScale(int typeId)
        {
            if (_plortScale.TryGetValue(typeId, out var s)) return s > 0 ? s : null;
            float v = 0;
            try { if (ActorTypes.TryGetType(typeId, out var t) && t.IsPlort && t.prefab != null) v = t.prefab.transform.localScale.x; } catch { v = 0; }
            if (ActorTypes.Count > 0) _plortScale[typeId] = v;                  // only cache once the registry exists
            return v > 0 ? v : null;
        }

        /// <summary>A plort bigger than its prefab is never native: put it back and record who owned it (evidence for the root cause).</summary>
        static void CheckPlortScale(ActorEntry e, float prefabScale)
        {
            var go = GameActorWorld.ObjectOf(e.Id); if (go == null) return;
            float scale = go.transform.localScale.x;
            if (!PlortScaleRule.IsOversized(scale, prefabScale)) return;
            go.transform.localScale = Vector3.one * prefabScale;
            string details = $"id={e.Id}; type={e.TypeId}; object={go.name}; owner={e.Owner}; mine={e.Owner == Mod.LocalId}; scale={scale:0.00}; prefab={prefabScale:0.00}";
            Rl.Warn("plort-scale", "oversized plort corrected: " + details);
            Mod.Diagnostics?.Record("actors", "plort-scale-corrected", details);
        }

        static ProduceRules.Phase PhaseOf(ResourceCycle.State s) => s switch { ResourceCycle.State.UNRIPE => ProduceRules.Phase.Unripe, ResourceCycle.State.RIPE => ProduceRules.Phase.Ripe, ResourceCycle.State.EDIBLE => ProduceRules.Phase.Edible, _ => ProduceRules.Phase.Rotten };

        // ------------------------------------------------------------------ interest (4.2)
        static readonly List<InterestCandidate> _cands = new List<InterestCandidate>();
        void InterestPassRun(ActorSync a, double now)
        {
            string me = Mod.LocalId; if (!Players.TryPosition(me, out var mine)) return;
            _cands.Clear();
            foreach (var e in a.Table.All)
            {
                if (e.IsGadget || e.Owner == me) continue;
                if (e.Attached && !e.ReleasedByOwner && a.IsPlayerConnected(e.Owner)) continue;  // produce on its plant belongs to that plant's simulator while it runs
                var go = GameActorWorld.ObjectOf(e.Id); if (go == null || !go.activeInHierarchy || BodyMode.IsHibernating(go)) continue; // only actors this peer can actually simulate
                var pos = go.transform.position; float myDist = Vector3.Distance(mine, pos);
                bool unsim = string.IsNullOrEmpty(e.Owner) || !a.IsPlayerConnected(e.Owner) || e.ReleasedByOwner;
                float ownerDist = !unsim && Players.TryPosition(e.Owner, out var op) ? Vector3.Distance(op, pos) : float.PositiveInfinity;
                _cands.Add(new InterestCandidate { Id = e.Id, Unsimulated = unsim, OwnedByOther = !unsim, OwnerSilent = OwnerSilence.IsSilent(e.Id, now, e.LastOwnerHeardAt), MyDistance = myDist, OwnerDistance = ownerDist });
                if (unsim && a.IsHost) foreach (var id in Players.Known)                      // on the host every in-range player counts as interested
                        if (id != me && a.IsPlayerConnected(id) && Players.TryPosition(id, out var playerPosition)) { float d = Vector3.Distance(playerPosition, pos); if (d <= InterestPass.NearRange) a.Arbiter.RegisterInterest(id, e.Id, d, (float)now); }
            }
            foreach (var (id, d) in Interest.Select(_cands)) if (a.Table.TryGet(id, out var e)) a.SendInterest(e, d);
        }

        /// <summary>A wake event, slime activity or produce attach: express interest in that one actor now.</summary>
        public static void NoteActivity(GameObject go)
        {
            var a = Mod.Actors; var l = ActorLink.Of(go); if (GameState.Loading || a == null || !l || l.LocallyOwned || !Mod.IsNetworkActive) return;
            if (Players.TryPosition(Mod.LocalId, out var mine) && l.Entry is { } e) a.SendInterest(e, Vector3.Distance(mine, go.transform.position));
        }

        // ------------------------------------------------------------------ spawn leader (4.5)
        public const float LeaderRange = 120f;
        /// <summary>Within 120 m only one game spawns wild slimes and animals: the host if in range, else the lowest player id in range.</summary>
        public static bool IsSpawnLeader(Vector3 at)
        {
            var s = Mod.Session; if (s == null || !Mod.IsNetworkActive) return true;
            string host = s.IsHost ? Mod.LocalId : s.HostId; bool hostIn = false; var inRange = new List<string>();
            foreach (var id in Players.Known.Distinct())
            {
                if (!Players.TryPosition(id, out var p) || Vector3.Distance(p, at) > LeaderRange) continue;
                inRange.Add(id); if (id == host) hostIn = true;
            }
            if (inRange.Count == 0) return true;
            return SpawnLeader.Leader(host, hostIn, inRange) == Mod.LocalId;
        }

        // ------------------------------------------------------------------ /cleanup (4.8)
        public static void QueueCleanup(IEnumerable<long> ids) { _cleanupQueue.AddRange(ids); }
        static void CleanupSlice(ActorSync a)
        {
            foreach (var id in Cleanup.Slice(_cleanupQueue)) { try { a.HostForceDestroy(id); } catch (Exception ex) { Rl.Warn("cleanup", "cleanup removal failed: " + ex.Message); } }
            if (_cleanupQueue.Count == 0) Log.Info("cleanup finished");
        }
    }
}

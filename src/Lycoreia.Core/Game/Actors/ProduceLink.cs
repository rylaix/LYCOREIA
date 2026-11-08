// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Lycoreia.Actors;
using Lycoreia.Game.Hooks;
using Lycoreia.Net;
using UnityEngine;

namespace Lycoreia.Game
{
    /// <summary>
    /// Produce on plants. One peer grows each plant (gardens: the garden owner; wild plants: the spawn leader within 120 m), its produce
    /// is owned by that peer while attached, and the other peers hang their replicas on the matching joint of their own copy of the plant
    /// so that copy never grows a second fruit into an occupied slot.
    /// </summary>
    public static class ProduceLink
    {
        public const float JointSearch = 0.75f, SpawnerSearch = 8f; const double SpawnerCacheSeconds = 5, LeaderCacheSeconds = 1;
        static SpawnResource[] _spawners = Array.Empty<SpawnResource>(); static double _spawnersAt = double.NegativeInfinity;
        static readonly Dictionary<int, (bool leader, double at)> _leader = new Dictionary<int, (bool, double)>();
        static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);

        /// <summary>A wild (non-garden) plant grows only at the spawn leader of its area, like wild slimes (cached for a second per plant).</summary>
        public static bool WildMayGrow(SpawnResource sr)
        {
            double now = Time.unscaledTimeAsDouble; int key = sr.GetInstanceID();
            bool had = _leader.TryGetValue(key, out var c);
            if (had && now - c.at < LeaderCacheSeconds) return c.leader;
            if (_leader.Count > 4096) _leader.Clear();
            bool l = ActorRuntime.IsSpawnLeader(sr.transform.position); _leader[key] = (l, now);
            if (l && had && !c.leader)
            {
                // this copy of the plant was paused while another peer grew it: restart its timer from now, otherwise it drops every
                // fruit it "missed" at once on top of the other peer's fruit (seen in game: pomegranates reappearing when the host arrived)
                GameActorWorld.InternalDepth++;
                try { sr.UpdateNextSpawnTime(0f); Mod.Diagnostics?.Record("actors", "plant-resumed", $"at={sr.transform.position}"); }
                catch (Exception e) { Rl.Warn("plant-resume", "plant timer reset failed: " + e.Message); }
                finally { GameActorWorld.InternalDepth--; }
            }
            return l;
        }

        static SpawnResource[] Spawners()
        {
            double now = Time.unscaledTimeAsDouble;
            if (now - _spawnersAt > SpawnerCacheSeconds) { _spawnersAt = now; try { _spawners = UnityEngine.Object.FindObjectsOfType<SpawnResource>(); } catch { _spawners = Array.Empty<SpawnResource>(); } }
            return _spawners;
        }

        /// <summary>
        /// Replica side of the owner's produce stage: runs the game's own ripen / edible / rot steps on the frozen copy (they switch its
        /// vacuumable state, scale and look). Never steps backwards; runs as an internal change so no hook publishes anything.
        /// </summary>
        public static void SyncStage(GameObject go, int stage, long id)
        {
            try
            {
                var rc = go != null ? go.GetComponent<ResourceCycle>() : null; if (rc == null) return;
                var want = (ResourceCycle.State)stage; var cur = rc.GetState();
                // ProgressResource (not Ripen itself) enables capture after ripening.
                // Frozen replicas never execute that wrapper, including after reload
                // when the model already has the received stage.
                var vacuumable = go.GetComponent<Vacuumable>();
                if (vacuumable != null && !vacuumable.enabled && (Rank(want) >= Rank(ResourceCycle.State.EDIBLE) ||
                    want == ResourceCycle.State.RIPE && rc.VacuumableWhenRipe))
                {
                    vacuumable.enabled = true;
                    Mod.Diagnostics?.Record("actors", "produce-capture-enabled", $"id={id}; current={cur}; target={want}; attached={rc._joint != null && rc._joint.Joint != null}");
                }
                if (want == cur || Rank(want) <= Rank(cur)) return;
                GameActorWorld.InternalDepth++;
                try
                {
                    if (Rank(cur) < Rank(ResourceCycle.State.RIPE) && Rank(want) >= Rank(ResourceCycle.State.RIPE)) rc.Ripen();
                    if (Rank(rc.GetState()) < Rank(ResourceCycle.State.EDIBLE) && Rank(want) >= Rank(ResourceCycle.State.EDIBLE)) { rc.MakeEdible(); rc.DetachFromJoint(); }
                    if (want == ResourceCycle.State.ROTTEN && rc.GetState() != ResourceCycle.State.ROTTEN) rc.ImmediatelyRot();
                }
                finally { GameActorWorld.InternalDepth--; }
                Mod.Diagnostics?.Record("actors", "produce-stage", $"id={id}; from={cur}; to={rc.GetState()}; want={want}");
            }
            catch (Exception e) { Rl.Warn("produce-stage", "produce stage sync failed: " + e.Message); }
        }
        static int Rank(ResourceCycle.State s) => s switch { ResourceCycle.State.UNRIPE => 0, ResourceCycle.State.RIPE => 1, ResourceCycle.State.EDIBLE => 2, _ => 3 };

        /// <summary>Replica side of the owner's attach state: hang it on the nearest free joint of the local plant, or let it go.</summary>
        public static void SyncReplica(GameObject go, bool attached, long id)
        {
            try
            {
                var rc = go != null ? go.GetComponent<ResourceCycle>() : null; if (rc == null) return;
                bool onJoint = rc._joint != null && rc._joint.Joint != null;
                if (attached == onJoint) return;
                if (!attached) { rc.DetachFromJoint(); return; }
                var pos = go.transform.position; Joint best = null; float bestD = float.MaxValue;
                foreach (var sr in Spawners())
                {
                    if (sr == null || Vector3.Distance(sr.transform.position, pos) > SpawnerSearch) continue;
                    var j = sr.NearestJoint(pos, JointSearch); if (j == null || j.connectedBody != null) continue;
                    float d = Vector3.Distance(j.transform.position, pos); if (d < bestD) { bestD = d; best = j; }
                }
                if (best == null) { Rl.Info("produce-nojoint", $"no free local joint for produce at {pos}"); return; }
                rc.Reattach(best);
                Mod.Diagnostics?.Record("actors", "produce-reattached", $"id={id}; dist={bestD:0.00}; linkable={(bool)ActorLink.Of(go)}");
            }
            catch (Exception e) { Rl.Warn("produce-attach", "produce attach sync failed: " + e.Message); }
        }
    }
}

namespace Lycoreia.Game.Hooks
{
    /// <summary>
    /// ResourceCycle.DestroyWithFX removes produce (and its model) without Destroyer.DestroyActor. Same rule as the destroy hook: the owner's
    /// removal is broadcast once, a non-owner's is suppressed (rotten local cleanup excepted).
    /// </summary>
    [HarmonyPatch(typeof(ResourceCycle), nameof(ResourceCycle.DestroyWithFX))]
    static class ProduceDestroyHook
    {
        static bool Prefix(ResourceCycle __instance)
        {
            try
            {
                if (!HookGuard.Active || Applying.Active) return true;
                var l = ActorLink.Of(__instance); if (!l) return true; var e = l.Entry; if (e == null) return true;
                bool owned = e.Owner == Mod.LocalId; bool rotten = __instance.GetState() == ResourceCycle.State.ROTTEN;
                if (Lycoreia.Owned.ActorDestroyPolicy.IsLocalOnlyRottenResourceCleanup(true, true, owned, rotten)) { Mod.Diagnostics?.Record("actors", "produce-destroy-local-rotten", $"id={l.Id}"); ActorRuntime.Forget(l.Id); return true; }
                if (!owned) { Mod.Diagnostics?.Record("actors", "produce-destroy-suppressed", $"id={l.Id}; owner={e.Owner}; state={__instance.GetState()}"); return false; }
                Mod.Diagnostics?.Record("actors", "produce-destroy-published", $"id={l.Id}; state={__instance.GetState()}");
                Mod.Actors.PublishDestroy(l.Id); ActorRuntime.Forget(l.Id); return true;
            }
            catch (Exception ex) { HookGuard.Fail("ResourceCycle.DestroyWithFX", ex); return true; }
        }
    }

    /// <summary>
    /// Catch-all for model removals that bypass both destroy hooks: an owned actor's removal is broadcast (so nobody keeps a copy of
    /// something its owner no longer has); a replica's is logged and the reconciler re-creates it from the table.
    /// </summary>
    [HarmonyPatch(typeof(GameModel), nameof(GameModel.DestroyIdentifiableModel), new[] { typeof(IdentifiableModel) })]
    static class ModelRemovalHook
    {
        static void Prefix(IdentifiableModel model) { if (model != null) Removed(model.actorId.Value, model.ident); }

        internal static void Removed(long id, IdentifiableType ident)
        {
            try
            {
                if (id == 0 || !HookGuard.Active || Applying.Active) return;
                if (!Mod.Actors.Table.TryGet(id, out var e) || e.IsGadget) return;
                string type = ident != null ? ident.name : "?";
                if (Mod.IsGuest && VacuumCapture.Machine.OwnsConsumeRemoval(id))
                {
                    Mod.Diagnostics?.Record("actors", "model-removed-consume", $"id={id}; type={type}; authority=host-capture");
                    return;
                }
                if (e.Owner == Mod.LocalId && ActorRuntime.InSettleWindow(id))
                {
                    Mod.Diagnostics?.Record("actors", "model-removed-guarded", $"id={id}; type={type}");     // released, not destroyed: the entry and the others' copies survive
                    Mod.Actors.SendUnload(id); ActorRuntime.Forget(id);
                }
                else if (e.Owner == Mod.LocalId) { Mod.Diagnostics?.Record("actors", "model-removed-published", $"id={id}; type={type}"); Mod.Actors.PublishDestroy(id); ActorRuntime.Forget(id); }
                else Mod.Diagnostics?.Record("actors", "model-removed-replica", $"id={id}; type={type}; owner={e.Owner}");
            }
            catch (Exception ex) { HookGuard.Fail("GameModel.DestroyIdentifiableModel", ex); }
        }
    }

    [HarmonyPatch(typeof(GameModel), nameof(GameModel.DestroyIdentifiableModel), new[] { typeof(ActorId), typeof(IdentifiableType) })]
    static class ModelRemovalByIdHook { static void Prefix(ActorId actorId, IdentifiableType ident) => ModelRemovalHook.Removed(actorId.Value, ident); }

    [HarmonyPatch(typeof(GameModel), nameof(GameModel.DestroyIdentifiableModel), new[] { typeof(GameObject) })]
    static class ModelRemovalByObjectHook
    {
        static void Prefix(GameObject gameObj) { try { var ia = gameObj != null ? gameObj.GetComponent<IdentifiableActor>() : null; if (ia != null && ia._model != null) ModelRemovalHook.Removed(ia._model.actorId.Value, ia._model.ident); } catch { } }
    }
}

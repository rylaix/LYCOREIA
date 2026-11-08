// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Player.PlayerItems;
using Lycoreia.Actors;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using UnityEngine;

namespace Lycoreia.Game.Hooks
{
    static class HookGuard
    {
        static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);
        public static void Fail(string hook, Exception e) => Rl.Warn(hook, $"hook {hook} failed: {e.Message}");
        public static bool Active => Mod.Initialised && Mod.Actors != null && Mod.IsNetworkActive && !GameActorWorld.Internal;
    }

    /// <summary>Marks actors created by the local vacuum's shot (vac shots are exempt from pile and wild caps).</summary>
    [HarmonyPatch(typeof(VacuumItem), nameof(VacuumItem.Expel), new[] { typeof(Il2CppMonomiPark.SlimeRancher.Player.AmmoSlot.AmmoMetadata), typeof(bool), typeof(float), typeof(bool) })]
    static class VacuumExpelHook
    {
        public static int Depth;
        static void Prefix() => Depth++;
        static Exception Finalizer(Exception __exception) { if (Depth > 0) Depth--; return __exception; }
    }

    /// <summary>Spawn publication: a locally created actor is marked owned, recorded and broadcast (a guest's spawn goes to the host).</summary>
    [HarmonyPatch(typeof(InstantiationHelpers), nameof(InstantiationHelpers.InstantiateActor))]
    static class InstantiateActorHook
    {
        static readonly ChickenCap Chickens = new ChickenCap();

        static void Postfix(GameObject __result)
        {
            try
            {
                if (!HookGuard.Active || Applying.Active || __result == null) return;
                var actor = __result.GetComponent<IdentifiableActor>(); if (actor == null || actor._model == null) return;
                var model = actor._model; int typeId = ActorTypes.IdOf(model.ident); if (typeId == 0) return;
                if (__result.GetComponent<Il2CppMonomiPark.SlimeRancher.Drone.DroneStation>() != null) return;                // drones are never world actors
                var pos = model.GetPos(); var rot = model.GetRot(); var p = new Vec3(pos.x, pos.y, pos.z);
                bool vacShot = VacuumExpelHook.Depth > 0; bool ranch = GameActorWorld.Ranch.Contains(p) == true;
                if (!vacShot && GameActorWorld.IsChicken(model.ident) && !ChickenAllowed(p, ranch, typeId)) { DropLocally(__result); return; }
                var e = new ActorEntry { Id = model.actorId.Value, TypeId = typeId, Scene = SceneGroups.IdOf(model.sceneGroup), Position = p, Rotation = new Quat(rot.x, rot.y, rot.z, rot.w), Kind = vacShot ? SpawnKind.VacShot : ranch ? SpawnKind.Ranch : SpawnKind.Normal };
                if (e.Kind == SpawnKind.Normal && Mod.IsGuest && Mod.Actors.Backoff.Blocks(p, Time.unscaledTimeAsDouble)) { DropLocally(__result); return; }   // backed off after a host refusal
                if (!Mod.Actors.GuestMayPublish(e.Id))
                {
                    // the guest allocator is not inside our block yet (before the snapshot) or was moved out: the host would refuse this id
                    Mod.Diagnostics?.Record("actors", "spawn-dropped", $"id={e.Id}; type={model.ident?.name}; reason=id-block");
                    DropLocally(__result); return;
                }
                Mod.Actors.PublishSpawn(e);
                ActorRuntime.Motion.Remove(e.Id);
            }
            catch (Exception ex) { HookGuard.Fail("InstantiateActor", ex); }
        }

        /// <summary>Wild chicken cap plus a higher 30 m ranch population ceiling to stop runaway local spawn bursts.</summary>
        static bool ChickenAllowed(Vec3 p, bool ranch, int typeId)
        {
            bool nearAvatar = Players.TryPosition(Mod.LocalId, out var me) && Vector3.Distance(me, new Vector3(p.X, p.Y, p.Z)) <= ChickenCap.AvatarExemption;
            int within = GameActorWorld.CountWithin(en => ActorTypes.TryGetType(en.TypeId, out var t) && GameActorWorld.IsChicken(t), p, ChickenCap.Radius);
            bool allowed = Chickens.Allow(p, within, ranch, Time.unscaledTimeAsDouble);
            if (!allowed) Mod.Diagnostics?.Record("actors", "chicken-cap-refused", $"type={typeId}; within30m={within}; ranch={ranch}; near-avatar={nearAvatar}; at=({p.X:0.0},{p.Y:0.0},{p.Z:0.0})");
            return allowed;
        }

        /// <summary>A dropped spawn is destroyed locally and never published.</summary>
        static void DropLocally(GameObject go)
        {
            GameActorWorld.InternalDepth++;
            try { Destroyer.DestroyActor(go, "Lycoreia.spawn-cap", true); } catch (Exception e) { HookGuard.Fail("spawn-drop", e); }
            finally { GameActorWorld.InternalDepth--; }
        }
    }

    /// <summary>
    /// Destroy: a non-owner never destroys a networked actor during gameplay (the native destroy is suppressed); the owner's destroy
    /// broadcasts once with an owner token. Mod-internal removals never reach this branch (GameActorWorld.Internal).
    /// Exceptions: rotten produce is removed locally without a message, and the awaited native consume of an approved capture.
    /// </summary>
    [HarmonyPatch(typeof(Destroyer), nameof(Destroyer.DestroyActor), new[] { typeof(GameObject), typeof(string), typeof(bool) })]
    static class DestroyActorHook
    {
        static readonly System.Collections.Generic.HashSet<long> _suppressedLogged = new System.Collections.Generic.HashSet<long>();

        /// <summary>Diagnostics for desync classification: who destroyed what, why (the game's own source string) and how far away.</summary>
        static void Note(string ev, long id, ActorEntry e, string source, GameObject go)
        {
            try
            {
                var d = Mod.Diagnostics; if (d == null) return;
                float dist = Players.TryPosition(Mod.LocalId, out var me) && go != null ? Vector3.Distance(me, go.transform.position) : -1f;
                d.Record("actors", ev, $"id={id}; owner={e.Owner}; type={e.TypeId}; source={source}; dist={dist:0}");
            }
            catch { }
        }

        static bool Prefix(GameObject actorObj, string source)
        {
            try
            {
                if (!HookGuard.Active || Applying.Active || actorObj == null) return true;
                var actor = actorObj.GetComponent<IdentifiableActor>(); if (actor == null || actor._model == null) return true;
                long id = actor._model.actorId.Value;
                if (!Mod.Actors.Table.TryGet(id, out var e)) return true;
                bool owned = e.Owner == Mod.LocalId;
                if (!e.IsGadget && GameActorWorld.IsCullSource(source) && GameActorWorld.IsRanchSafeSlime(actorObj))
                {
                    if (_suppressedLogged.Count < 4096 && _suppressedLogged.Add(id)) Note("destroy-ranch-protected", id, e, source, actorObj);
                    return false;                                                                                                                              // ranch slimes are never culled/despawned
                }
                var rc = actorObj.GetComponent<ResourceCycle>(); bool rotten = rc != null && rc.GetState() == ResourceCycle.State.ROTTEN;
                if (Lycoreia.Owned.ActorDestroyPolicy.IsLocalOnlyRottenResourceCleanup(true, true, owned, rotten)) { Note("destroy-local-rotten", id, e, source, actorObj); ActorRuntime.Forget(id); return true; }   // stale rotten visual: local, no message
                if (!e.IsGadget && VacuumCapture.AllowConsumeDestroy(id)) { Note("destroy-consume", id, e, source, actorObj); return true; }                       // awaited native consume: held until the host commits
                if (!e.IsGadget && Lycoreia.Owned.ActorDestroyPolicy.ShouldSuppress(true, true, owned, rotten))
                {
                    if (_suppressedLogged.Count < 4096 && _suppressedLogged.Add(id)) Note("destroy-suppressed", id, e, source, actorObj);                      // once per actor: some sources retry every frame
                    return false;                                                                                                                              // non-owner: suppressed
                }
                Note("destroy-published", id, e, source, actorObj);
                Mod.Actors.PublishDestroy(id); ActorRuntime.Forget(id); return true;
            }
            catch (Exception ex) { HookGuard.Fail("DestroyActor", ex); return true; }
        }
    }

    /// <summary>Non-owners freeze the resource cycle (the owner's state messages carry the real progress).</summary>
    [HarmonyPatch(typeof(ResourceCycle), nameof(ResourceCycle.UpdateInstance))]
    static class ResourceCycleFreezeHook
    {
        static bool Prefix(ResourceCycle __instance)
        {
            try { if (!HookGuard.Active) return true; var l = ActorLink.Of(__instance); return !l || l.LocallyOwned; }
            catch (Exception e) { HookGuard.Fail("ResourceCycle.UpdateInstance", e); return true; }
        }
    }

    /// <summary>
    /// Timed transforms (chick to hen, egg to chick, TransformAfterTime) run on the owner only. On a replica the transform's destroy is
    /// suppressed while its spawn is not, so the replica respawned a hen every frame (host log 2026-11-07 17:13:40, after a client's
    /// death time skip made every delay elapse at once).
    /// </summary>
    [HarmonyPatch(typeof(TransformAfterTime), nameof(TransformAfterTime.Update))]
    static class TransformAfterTimeOwnerOnlyHook
    {
        static bool Prefix(TransformAfterTime __instance)
        {
            try
            {
                if (!HookGuard.Active) return true; var l = ActorLink.Of(__instance);
                return !Lycoreia.Owned.ActorDestroyPolicy.ShouldSkipTimedTransform(true, l, l && l.LocallyOwned);
            }
            catch (Exception e) { HookGuard.Fail("TransformAfterTime.Update", e); return true; }
        }
    }

    /// <summary>Produce attach registers interest in that produce.</summary>
    [HarmonyPatch(typeof(ResourceCycle), nameof(ResourceCycle.Attach))]
    static class ResourceAttachHook
    {
        static void Postfix(ResourceCycle __instance) { try { if (HookGuard.Active) ActorRuntime.NoteActivity(__instance.gameObject); } catch (Exception e) { HookGuard.Fail("ResourceCycle.Attach", e); } }
    }
}

namespace Lycoreia.Game.Hooks
{
    /// <summary>
    /// A region loading brings hibernated actors back through InstantiateActorFromModel with the prefab's dynamic body. A replica
    /// (owned by someone else) must come up kinematic at the owner's last pose, otherwise local physics and AI move it away.
    /// </summary>
    [HarmonyPatch(typeof(InstantiationHelpers), nameof(InstantiationHelpers.InstantiateActorFromModel))]
    static class ActorFromModelHook
    {
        static void Postfix(ActorModel actorModel, GameObject __result)
        {
            try
            {
                if (__result == null || actorModel == null || Mod.Actors == null || !Mod.IsNetworkActive) return;
                long id = actorModel.actorId.Value;
                if (!Mod.Actors.Table.TryGet(id, out var e) || e.IsGadget || e.Owner == Mod.LocalId) return;
                if (ActorRuntime.Motion.IsPredicted(id)) return;
                ReplicaBodies.Apply(id, e, false);
                Mod.Actors.ReplayProduceState(id);
            }
            catch (Exception ex) { HookGuard.Fail("InstantiateActorFromModel", ex); }
        }
    }
}

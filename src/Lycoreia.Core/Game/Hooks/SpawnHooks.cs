// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.Regions;
using Lycoreia.Actors;
using Lycoreia.Net;
using UnityEngine;

namespace Lycoreia.Game.Hooks
{
    /// <summary>
    /// Spawner batch trim (the real gate). Never patches OnActorSpawned, CanContinueSpawning or DirectedAnimalSpawner.CanSpawn.
    /// Counts come from the networked table (any owner) and are cached 2 s per spawner.
    /// </summary>
    static class SpawnGate
    {
        public static readonly AnimalSpawnerGuard Guard = new AnimalSpawnerGuard();
        static readonly Dictionary<int, (double at, int animals, int slimes)> _counts = new Dictionary<int, (double, int, int)>();
        static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);
        public static void Reset() => _counts.Clear();

        static Vec3 P(Vector3 v) => new Vec3(v.x, v.y, v.z);
        static (int animals, int slimes) Nearby(DirectedActorSpawner s, double now)
        {
            int key = s.GetInstanceID();
            if (_counts.TryGetValue(key, out var c) && now - c.at < GuestSlimeSpawnerGate.CacheSeconds) return (c.animals, c.slimes);
            var p = P(s.transform.position); var w = Mod.Actors;
            int animals = GameActorWorld.CountWithin(e => ClassOf(e) == SpawnClass.Animal, p, AnimalSpawnerGuard.NearbyRadius);
            int slimes = GameActorWorld.CountWithin(e => ClassOf(e) == SpawnClass.Slime, p, GuestSlimeSpawnerGate.Radius);
            _counts[key] = (now, animals, slimes); return (animals, slimes);
        }
        static SpawnClass ClassOf(ActorEntry e) => ActorTypes.TryGetType(e.TypeId, out var t) ? GameActorWorld.ClassOf(t) : SpawnClass.Item;

        static bool OnRanch(DirectedActorSpawner s)
        {
            try { var cd = s._region != null ? s._region.CellDir : null; if (cd != null) return cd.IsRanch; } catch { }
            return GameActorWorld.Ranch.Contains(P(s.transform.position)) ?? false;
        }
        static int CellTarget(DirectedActorSpawner s) { try { var cd = s._region != null ? s._region.CellDir : null; return cd != null ? cd.TargetSlimeCount : 0; } catch { return 0; } }
        static bool BackedOff(Vector3 at) => Mod.IsGuest && Mod.Actors.Backoff.Blocks(P(at), Time.unscaledTimeAsDouble);

        public static void TrimAnimals(DirectedActorSpawner s, ref int count)
        {
            try
            {
                if (!HookGuard.Active || s == null || count <= 0) return; double now = Time.unscaledTimeAsDouble; var pos = s.transform.position;
                int requested = count;
                if (OnRanch(s)) count = Math.Min(count, Math.Max(0, ChickenCap.RanchWithin30m - Nearby(s, now).animals));
                count = SpawnerGate.AnimalBatch(Guard, s.GetInstanceID(), count, Nearby(s, now).animals, ActorRuntime.IsSpawnLeader(pos), BackedOff(pos), OnRanch(s), true, now, out var why);
                if (count < requested) Rl.Info("animal-trim:" + why, $"animal spawner batch {requested} -> {count} ({why})");
            }
            catch (Exception e) { HookGuard.Fail("DirectedAnimalSpawner.Spawn", e); }
        }

        public static void TrimSlimes(DirectedActorSpawner s, ref int count)
        {
            try
            {
                if (!HookGuard.Active || s == null || count <= 0 || s.TryCast<DirectedAnimalSpawner>() != null) return; double now = Time.unscaledTimeAsDouble; var pos = s.transform.position;
                int requested = count; count = SpawnerGate.SlimeBatch(Guard, s.GetInstanceID(), count, Nearby(s, now).slimes, CellTarget(s), ActorRuntime.IsSpawnLeader(pos), BackedOff(pos), OnRanch(s), true, now, out var why);
                if (count < requested) Rl.Info("slime-trim:" + why, $"slime spawner batch {requested} -> {count} ({why})");
            }
            catch (Exception e) { HookGuard.Fail("DirectedActorSpawner.Spawn", e); }
        }

        /// <summary>Cell need checks: false while out of turn, backed off, or when the table already meets the cap near its spawners.</summary>
        public static void NeedsMore(CellDirector cd, ref bool result, bool slimes)
        {
            try
            {
                if (!HookGuard.Active || cd == null || !result || cd.IsRanch) return; double now = Time.unscaledTimeAsDouble; var pos = cd.transform.position;
                bool leader = ActorRuntime.IsSpawnLeader(pos), back = BackedOff(pos);
                if (slimes)
                {
                    int near = 0; foreach (var sp in cd._spawners) if (sp != null) near = Math.Max(near, Nearby(sp, now).slimes);
                    result = SpawnerGate.NeedsMoreSlimes(result, true, false, leader, back, near, cd.TargetSlimeCount);
                }
                else
                {
                    var counts = new List<int>(); foreach (var sp in cd._animalSpawners) if (sp != null) counts.Add(Nearby(sp, now).animals);
                    if (counts.Count == 0) counts.Add(0);
                    result = SpawnerGate.NeedsMoreAnimals(result, true, false, leader, back, counts);
                }
            }
            catch (Exception e) { HookGuard.Fail(slimes ? "CellDirector.NeedsMoreSlimes" : "CellDirector.NeedsMoreAnimals", e); }
        }
    }

    [HarmonyPatch(typeof(DirectedAnimalSpawner), nameof(DirectedAnimalSpawner.Spawn))]
    static class AnimalSpawnerBatchHook { static void Prefix(DirectedAnimalSpawner __instance, ref int count) => SpawnGate.TrimAnimals(__instance, ref count); }

    [HarmonyPatch(typeof(DirectedActorSpawner), nameof(DirectedActorSpawner.Spawn))]
    static class SlimeSpawnerBatchHook { static void Prefix(DirectedActorSpawner __instance, ref int count) => SpawnGate.TrimSlimes(__instance, ref count); }

    [HarmonyPatch(typeof(CellDirector), nameof(CellDirector.NeedsMoreSlimes))]
    static class NeedsMoreSlimesHook { static void Postfix(CellDirector __instance, ref bool __result) => SpawnGate.NeedsMore(__instance, ref __result, true); }

    [HarmonyPatch(typeof(CellDirector), nameof(CellDirector.NeedsMoreAnimals))]
    static class NeedsMoreAnimalsHook { static void Postfix(CellDirector __instance, ref bool __result) => SpawnGate.NeedsMore(__instance, ref __result, false); }

    /// <summary>Native population culls are blocked while connected (replicas inflate the counts; slimes and protected ranch actors are never culled).</summary>
    [HarmonyPatch]
    static class CellCullHook
    {
        static IEnumerable<MethodBase> TargetMethods() { foreach (var m in AccessTools.GetDeclaredMethods(typeof(CellDirector))) if (m.Name == nameof(CellDirector.Despawn)) yield return m; }
        static bool Prefix() { try { return !HookGuard.Active; } catch { return true; } }
    }

    /// <summary>In a session, ranch slimes (largos included) never take the phosphor family's day/night despawn; wild ones still do.</summary>
    [HarmonyPatch(typeof(DestroyOutsideHoursOfDay), nameof(DestroyOutsideHoursOfDay.Update))]
    static class RanchHoursOfDayHook
    {
        static bool Prefix(DestroyOutsideHoursOfDay __instance)
        {
            try { return !(HookGuard.Active && Mod.IsNetworkActive && __instance != null && GameActorWorld.IsRanchSafeSlime(__instance.gameObject)); }
            catch (Exception e) { HookGuard.Fail("DestroyOutsideHoursOfDay.Update", e); return true; }
        }
    }

    /// <summary>Ranch slimes are never culled when they wake up after time passed.</summary>
    [HarmonyPatch(typeof(MaybeCullOnReenable), nameof(MaybeCullOnReenable.MaybeDestroy))]
    static class RanchCullOnReenableHook
    {
        static bool Prefix(MaybeCullOnReenable __instance)
        {
            try
            {
                if (!HookGuard.Active || !Mod.IsNetworkActive || __instance == null || !GameActorWorld.IsRanchSafeSlime(__instance.gameObject)) return true;
                Mod.Diagnostics?.Record("actors", "ranch-cull-blocked", $"source=MaybeCullOnReenable; at={__instance.transform.position}");
                return false;
            }
            catch (Exception e) { HookGuard.Fail("MaybeCullOnReenable.MaybeDestroy", e); return true; }
        }
    }

    /// <summary>Protected ranch actors never time out.</summary>
    [HarmonyPatch(typeof(DestroyAfterTime), nameof(DestroyAfterTime.DoDestroy))]
    static class LifetimeTimeoutHook
    {
        static bool Prefix(DestroyAfterTime __instance)
        {
            try
            {
                if (!HookGuard.Active || __instance == null || !GameActorWorld.IsProtectedRanchActor(__instance.gameObject)) return true;
                __instance.SetDeathTime(double.MaxValue / 4); return false;
            }
            catch (Exception e) { HookGuard.Fail("DestroyAfterTime.DoDestroy", e); return true; }
        }
    }

    /// <summary>Fall-trigger kills: protected ranch actors are rescued (owner) or kept (replica); unprotected ones fall as usual.</summary>
    [HarmonyPatch(typeof(KillOnTrigger), nameof(KillOnTrigger.OnTriggerEnter))]
    static class FallTriggerHook
    {
        static bool Prefix(KillOnTrigger __instance, Collider collider)
        {
            try
            {
                if (!HookGuard.Active || collider == null) return true;
                var player = SceneContext.Instance?.Player;
                if (player != null && LoadingHazardPolicy.DeferPlayerKill(collider.transform.IsChildOf(player.transform), GameState.Loading, UnityEngine.Time.unscaledTimeAsDouble, NetworkLoadingPause.LastRequestAt))
                {
                    Mod.Diagnostics?.Record("player", "loading-hazard-deferred", $"trigger={__instance?.gameObject?.name}; pos={player.transform.position}; loading={GameState.Loading}");
                    return false;
                }
                var ident = collider.GetComponentInParent<IdentifiableActor>(); if (ident == null) return true;
                var go = ident.gameObject; var before = go.transform.position;
                string triggerName = __instance != null && __instance.gameObject != null ? __instance.gameObject.name : "";
                bool lethalSea = RanchProtection.IsLethalSeaTrigger(triggerName);
                bool protectedRanch = GameActorWorld.IsProtectedRanchActor(go);
                var l = ActorLink.Of(go); bool local = !l || l.LocallyOwned;
                if (l && !local)
                {
                    var remoteAction = RanchProtection.OnFallTrigger(protectedRanch, false, false, lethalSea);
                    LogFall(collider, ident, l, before, before, protectedRanch, false, false, triggerName, remoteAction.ToString());
                    return false; // a replica waits for its owner's authoritative destroy or ranch rescue
                }
                if (lethalSea)
                {
                    LogFall(collider, ident, l, before, before, protectedRanch, local, false, triggerName, "sea-kill");
                    return true; // ranch fall rescue must not turn the game's lethal ocean volume into a platform
                }
                if (!protectedRanch) { LogFall(collider, ident, l, before, before, false, local, false, triggerName, "vanilla"); return true; }
                bool rescued = RescueFall(go);
                var after = go.transform.position;
                var action = RanchProtection.OnFallTrigger(true, true, rescued);
                LogFall(collider, ident, l, before, after, true, true, rescued, triggerName, action.ToString());
                return action == RanchProtection.FallAction.AllowDestroy;
            }
            catch (Exception e) { HookGuard.Fail("KillOnTrigger", e); return true; }
        }

        static void LogFall(Collider collider, IdentifiableActor ident, ActorLink link, Vector3 before, Vector3 after,
            bool protectedRanch, bool localOwner, bool rescued, string trigger, string action)
        {
            Mod.Diagnostics?.Record("actors", "fall-trigger",
                $"id={(link ? link.Id : 0)}; owner={(link ? link.CurrentOwnerId : "")}; type={ident.identType?.name}; " +
                $"trigger={trigger}; collider={collider.gameObject?.name}; ranch-protected={protectedRanch}; local-owner={localOwner}; " +
                $"rescued={rescued}; action={action}; before=({before.x:0.0},{before.y:0.0},{before.z:0.0}); " +
                $"after=({after.x:0.0},{after.y:0.0},{after.z:0.0})");
        }

        /// <summary>Raycast down from 300 m above, place 1 m above the hit, zero the velocity. False when no ground was found.</summary>
        public static bool RescueFall(GameObject go)
        {
            var p = go.transform.position; var origin = new Vector3(p.x, p.y + RanchProtection.RaycastFrom, p.z);
            if (!Physics.Raycast(origin, Vector3.down, out var hit, RanchProtection.RaycastFrom * 2f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return false;
            go.transform.position = hit.point + Vector3.up * RanchProtection.PlaceAbove;
            var body = go.GetComponent<Rigidbody>(); if (body != null) { body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; body.position = go.transform.position; }
            return true;
        }
    }
}

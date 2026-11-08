// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.SceneManagement;
using Il2CppMonomiPark.SlimeRancher.World;
using Lycoreia.Game.Hooks;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.Snapshot;
using Lycoreia.World;
using UnityEngine;

namespace Lycoreia.Game.World
{
    /// <summary>Placed gadgets and decorations in the world (the inventory counts travel with the refinery item counts).</summary>
    public sealed class GadgetAdapter : IGadgetWorld
    {
        public static GadgetSync Sync;
        static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);

        public static void Install(Session s, SnapshotHost snap)
        {
            Sync = new GadgetSync(s, snap, new GadgetAdapter(), () => Time.unscaledTimeAsDouble);
            Sync.Trace = (e, d) => { Mod.Diagnostics?.Record("gadgets", e, d); if (e == "gadget-snapshot") Census("after-snapshot"); };
            s.ServerStarted += () => Census("host-start");
        }

        static GameModel G => GameState.Model;
        public bool Ready => G != null && ActorTypes.Count > 0;

        static GadgetModel ModelOf(long id)
        {
            try { var g = G; return g != null && g.identifiables.TryGetValue(new ActorId(id), out var m) && m != null ? m.TryCast<GadgetModel>() : null; } catch { return null; }
        }

        public static GadgetRecord From(GadgetModel m, GameObject go = null)
        {
            var r = new GadgetRecord { Id = m.actorId.Value, TypeId = ActorTypes.IdOf(m.ident), Scene = SceneGroups.IdOf(m.sceneGroup), PrePlaced = m.IsPrePlaced };
            try { go ??= m.GetGameObject(); } catch { }
            if (go != null) { var p = go.transform.position; var e = go.transform.eulerAngles; r.Position = new Vec3(p.x, p.y, p.z); r.Euler = new Vec3(e.x, e.y, e.z); }
            else { var p = m.lastPosition; var e = m.eulerRotation; r.Position = new Vec3(p.x, p.y, p.z); r.Euler = new Vec3(e.x, e.y, e.z); }
            return r;
        }

        public IEnumerable<GadgetRecord> All()
        {
            var list = new List<GadgetRecord>(); var g = G; if (g == null) return list;
            try { foreach (var m in g.AllGadgets()) if (m != null) list.Add(From(m)); } catch (Exception e) { Rl.Warn("gadget-enum", "gadget scan failed: " + e.Message); }
            return list;
        }

        public bool Exists(long id) => ModelOf(id) != null;

        public bool Create(in GadgetRecord r)
        {
            var g = G; if (g == null) return false;
            if (!ActorTypes.TryGetType(r.TypeId, out var type) || type == null) { Rl.Warn("gadget-type:" + r.TypeId, $"gadget type {r.TypeId} is not known here"); return false; }
            var def = type.TryCast<GadgetDefinition>(); var group = SceneGroups.Get(r.Scene);
            if (def == null || group == null) { Rl.Warn("gadget-def:" + r.TypeId, $"gadget {r.Id}: type {r.TypeId} is not a gadget or scene {r.Scene} unknown"); return false; }
            GameActorWorld.InternalDepth++;
            long alloc = ActorReconciler.AllocatorBefore();
            try
            {
                var pos = new Vector3(r.Position.X, r.Position.Y, r.Position.Z);
                var m = g.CreateGadgetModel(def, new ActorId(r.Id), group, pos, false); if (m == null) return false;
                m.eulerRotation = new Vector3(r.Euler.X, r.Euler.Y, r.Euler.Z);
                bool loaded = false; try { var rr = SceneContext.Instance != null ? SceneContext.Instance.RegionRegistry : null; loaded = rr != null && rr.IsCurrentSceneGroup(group); } catch { }
                if (loaded) { var go = GadgetDirector.InstantiateGadgetFromModel(m); if (go != null) go.transform.SetPositionAndRotation(pos, Quaternion.Euler(m.eulerRotation)); }
                return true;
            }
            catch (Exception e) { Rl.Warn("gadget-create", $"gadget {r.Id} create failed: {e.Message}"); return false; }
            finally { ActorReconciler.AllocatorAfter(alloc, r.Id); GameActorWorld.InternalDepth--; }
        }

        public bool Remove(in GadgetRecord local)
        {
            var m = ModelOf(local.Id); if (m == null) return false;
            GameActorWorld.InternalDepth++;
            try { m.DestroyGadget(null, false); return true; }                                   // no refund: only the seat that picked it up gets the item
            catch (Exception e) { Rl.Warn("gadget-remove", $"gadget {local.Id} removal failed: {e.Message}"); return false; }
            finally { GameActorWorld.InternalDepth--; }
        }

        /// <summary>Record a remotely obtained gadget in the game's event history so its Recent menu category sees it.</summary>
        public static bool NoteRecentObtained(IdentifiableType type)
        {
            try
            {
                var definition = type != null ? type.TryCast<GadgetDefinition>() : null;
                var producer = SceneContext.Instance != null ? SceneContext.Instance.GadgetDirector?._gadgetObtainedEvent : null;
                if (definition == null || producer == null)
                {
                    Mod.Diagnostics?.Record("inventory", "recent-event-missing", $"type={(type != null ? type.name : "null")}; gadget={definition != null}; producer={producer != null}");
                    return false;
                }
                producer.RaiseEventForData(definition);
                Mod.Diagnostics?.Record("inventory", "recent-recorded", $"type={ActorTypes.IdOf(definition)}; name={definition.name}");
                return true;
            }
            catch (Exception e) { Rl.Warn("recent-obtained", "could not record recent gadget acquisition: " + e.Message); return false; }
        }

        public static void Pump() { try { Sync?.Pump(); } catch (Exception e) { Rl.Warn("gadget-pump", "gadget pump failed: " + e.Message); } }

        /// <summary>Per-seat evidence: placed and pre-placed gadget models, and gadget inventory entries (and how many have no wire id).</summary>
        public static void Census(string why)
        {
            try
            {
                var g = G; if (g == null) return; int placed = 0, pre = 0; foreach (var m in g.AllGadgets()) { if (m == null) continue; if (m.IsPrePlaced) pre++; else placed++; }
                int inv = 0, invUnmapped = 0, units = 0; var counts = g.GadgetsModel != null ? g.GadgetsModel._itemCounts : null;
                if (counts != null) foreach (var kv in counts) { if (kv.Key == null || kv.Key.TryCast<GadgetDefinition>() == null || kv.Value <= 0) continue; inv++; units += kv.Value; if (ActorTypes.IdOf(kv.Key) == 0) invUnmapped++; }
                Mod.Diagnostics?.Record("gadgets", "census", $"why={why}; placed={placed}; preplaced={pre}; inventory-kinds={inv}; inventory-units={units}; inventory-unmapped={invUnmapped}");
            }
            catch { }
        }

        internal static bool On => Sync != null && Mod.IsNetworkActive && GameState.Ready && !GameActorWorld.Internal && !Applying.Active;
    }
}

namespace Lycoreia.Game.Hooks
{
    using Lycoreia.Game.World;

    /// <summary>A gadget or decoration this seat placed (pre-placed scene gadgets are not placements).</summary>
    [HarmonyPatch(typeof(GadgetDirector), nameof(GadgetDirector.InstantiateGadget))]
    static class GadgetPlaceHook
    {
        static void Postfix(GameObject __result, bool isPrePlaced)
        {
            try
            {
                if (!GadgetAdapter.On || isPrePlaced || __result == null) return;
                var gad = __result.GetComponent<Gadget>(); var m = gad != null ? gad.GetModel() : null; if (m == null) return;
                GadgetAdapter.Sync.LocalPlaced(GadgetAdapter.From(m, __result));
            }
            catch (Exception e) { HookGuard.Fail("GadgetDirector.InstantiateGadget", e); }
        }
    }

    /// <summary>A gadget model this seat removed (picked up, stored, demolished); a scene unload never removes the model.</summary>
    [HarmonyPatch(typeof(GadgetModel), nameof(GadgetModel.DestroyGadget), new[] { typeof(GameObject), typeof(bool) })]
    static class GadgetRemoveHook
    {
        static void Prefix(GadgetModel __instance)
        {
            try { if (GadgetAdapter.On && __instance != null) GadgetAdapter.Sync.LocalRemoved(GadgetAdapter.From(__instance)); }
            catch (Exception e) { HookGuard.Fail("GadgetModel.DestroyGadget", e); }
        }
    }
}

// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.World.ResourceNode;
using Lycoreia.Game.Hooks;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.Snapshot;
using Lycoreia.World;
using UnityEngine;

namespace Lycoreia.Game.World
{
    public sealed class ResourceNodeAdapter : INodeWorld
    {
        public static ResourceNodeSync Sync; static ResourceNodeAdapter _w;
        static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);
        static Dictionary<string, ResourceNodeDefinition> _defs;
        static readonly Dictionary<string, ResourceNodeSpawner> _spawners = new Dictionary<string, ResourceNodeSpawner>(StringComparer.Ordinal);
        static double _nextSpawnerScan;

        public static void Install(Session s, SnapshotHost snap)
        {
            _w = new ResourceNodeAdapter(); Sync = new ResourceNodeSync(s, snap, _w);
            Sync.Trace = (e, d) => { Mod.Diagnostics?.Record("nodes", e, d); if (e == "node-snapshot") Census("after-snapshot"); };
            if ((int)ResourceNode.NodeState.HARVESTED != NodeRules.Harvested) Log.Warn($"resource node state HARVESTED is {(int)ResourceNode.NodeState.HARVESTED}, rules assume {NodeRules.Harvested}");
            s.ServerStarted += () => Census("host-start"); s.ClientDisconnected += _ => _spawners.Clear();
        }

        static GameModel G => GameState.Model;
        static Il2CppSystem.Collections.Generic.Dictionary<string, ResourceNodeSpawnerModel> Table { get { try { return G?.resourceNodeSpawnerModels; } catch { return null; } } }
        public bool Ready => Table != null;

        public static NodePlacement From(ResourceNodeSpawnerModel m)
        {
            var p = new NodePlacement { Id = m.nodeId, TypeIds = new List<int>() };
            var def = m.resourceNodeDefinition; p.HasNode = def != null; p.State = (int)m.nodeState;
            if (def != null)
            {
                p.Definition = def.name; p.VariantIndex = m.resourceNodeVariantIndex; p.DespawnAtWorldTime = m.despawnAtWorldTime;
                try { if (m.resourcesToSpawn != null) foreach (var t in m.resourcesToSpawn) p.TypeIds.Add(ActorTypes.IdOf(t)); } catch { }
            }
            return p;
        }

        public bool TryRead(string id, out NodePlacement p)
        {
            p = default; var t = Table; if (t == null || string.IsNullOrEmpty(id)) return false;
            if (!t.TryGetValue(id, out var m) || m == null) return false; p = From(m); return true;
        }

        public IEnumerable<NodePlacement> ReadAll()
        {
            var list = new List<NodePlacement>(); var t = Table; if (t == null) return list;
            try { foreach (var kv in t) if (kv.Value != null) list.Add(From(kv.Value)); } catch (Exception e) { Rl.Warn("node-enum", "resource node scan failed: " + e.Message); }
            return list;
        }

        public bool Apply(NodeApply action, in NodePlacement p)
        {
            var t = Table; if (t == null) return false;
            try
            {
                if (!t.TryGetValue(p.Id, out var m) || m == null) m = G.InitializeResourceNodeSpawnerModel(p.Id);
                if (m == null) return false;
                var sp = Spawner(p.Id);
                switch (action)
                {
                    case NodeApply.Remove:
                        if (sp != null) DropNodeObject(sp);
                        m.resourceNodeDefinition = null; m.nodeState = ResourceNode.NodeState.NONE; try { m.resourcesToSpawn?.Clear(); } catch { }
                        return true;
                    case NodeApply.MarkEmpty:
                        m.nodeState = ResourceNode.NodeState.HARVESTED;
                        var node = sp != null ? sp._attachedResourceNode : null; if (node != null) node.SetStateEmpty();
                        return true;
                    case NodeApply.Place:
                        var def = Definition(p.Definition);
                        if (def == null) { Rl.Warn("node-def:" + p.Definition, $"resource node definition '{p.Definition}' is not known to this game build"); return true; }
                        if (sp != null) DropNodeObject(sp);
                        m.resourceNodeDefinition = def; m.resourceNodeVariantIndex = p.VariantIndex; m.despawnAtWorldTime = p.DespawnAtWorldTime;
                        m.nodeState = (ResourceNode.NodeState)p.State;
                        var res = new Il2CppSystem.Collections.Generic.List<IdentifiableType>();
                        if (p.TypeIds != null) foreach (var id in p.TypeIds) if (ActorTypes.TryGetType(id, out var it)) res.Add(it);
                        m.resourcesToSpawn = res;
                        if (sp != null)
                        {
                            sp.SetModel(m);                                                     // the game's own load path: builds the node from the record
                            var n = sp._attachedResourceNode; if (n != null && p.State == NodeRules.Harvested) n.SetStateEmpty();
                        }
                        return true;
                }
                return true;
            }
            catch (Exception e) { Rl.Warn("node-apply", $"resource node {p.Id} {action} failed: {e.Message}"); return true; }
        }

        /// <summary>Removes the node object at once (the native despawn tweens it away and clears the record in a callback).</summary>
        static void DropNodeObject(ResourceNodeSpawner sp)
        {
            var n = sp._attachedResourceNode; if (n == null) return;
            try { UnityEngine.Object.Destroy(n.gameObject); } catch { }
            sp._attachedResourceNode = null;
        }

        static ResourceNodeDefinition Definition(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_defs == null || !_defs.ContainsKey(name))
            {
                _defs = new Dictionary<string, ResourceNodeDefinition>(StringComparer.Ordinal);
                foreach (var d in Resources.FindObjectsOfTypeAll<ResourceNodeDefinition>()) if (d != null && !string.IsNullOrEmpty(d.name)) _defs[d.name] = d;
            }
            return _defs.TryGetValue(name, out var r) ? r : null;
        }

        static ResourceNodeSpawner Spawner(string id)
        {
            if (_spawners.TryGetValue(id, out var sp) && sp != null) return sp;
            double now = Time.unscaledTimeAsDouble; if (now < _nextSpawnerScan) return null; _nextSpawnerScan = now + 0.5;
            _spawners.Clear();
            foreach (var s in UnityEngine.Object.FindObjectsOfType<ResourceNodeSpawner>()) { try { var m = s._model; if (m != null && !string.IsNullOrEmpty(m.nodeId)) _spawners[m.nodeId] = s; } catch { } }
            return _spawners.TryGetValue(id, out sp) ? sp : null;
        }

        public static void Pump() { try { Sync?.Pump(); } catch (Exception e) { Rl.Warn("node-pump", "resource node pump failed: " + e.Message); } }

        /// <summary>Before/after evidence for the "nodes the host sees but the guest does not" report: counts per seat.</summary>
        public static void Census(string why)
        {
            try
            {
                int models = 0, withNode = 0, empty = 0; var t = Table; if (t == null) return;
                foreach (var kv in t) { if (kv.Value == null) continue; models++; if (kv.Value.resourceNodeDefinition != null) { withNode++; if (kv.Value.nodeState == ResourceNode.NodeState.HARVESTED) empty++; } }
                int live = 0; foreach (var s in UnityEngine.Object.FindObjectsOfType<ResourceNodeSpawner>()) if (s != null && s._attachedResourceNode != null) live++;
                Mod.Diagnostics?.Record("nodes", "census", $"why={why}; models={models}; with-node={withNode}; harvested={empty}; live-nodes={live}");
            }
            catch { }
        }

        // ---------------------------------------------------------------- hooks
        internal static bool On => Sync != null && Mod.IsNetworkActive && GameState.Ready;
        internal static bool IsGuest => Mod.Session != null && Mod.Session.IsGuest;
    }
}

namespace Lycoreia.Game.Hooks
{
    using Lycoreia.Game.World;

    /// <summary>A guest's director never rolls a node before the host's records arrived (they would be replaced anyway).</summary>
    [HarmonyPatch(typeof(ResourceNodeSpawner), nameof(ResourceNodeSpawner.SpawnNode))]
    static class NodeSpawnHook
    {
        static bool Prefix()
        {
            try { return !ResourceNodeAdapter.On || Applying.Active || ResourceNodeSync.GuestMayRoll(ResourceNodeAdapter.IsGuest, ResourceNodeAdapter.Sync.SnapshotApplied, false); }
            catch (Exception e) { HookGuard.Fail("ResourceNodeSpawner.SpawnNode", e); return true; }
        }
        static void Postfix(ResourceNodeSpawner __instance)
        {
            try { if (ResourceNodeAdapter.On && !Applying.Active && __instance != null && __instance._model != null) ResourceNodeAdapter.Sync.LocalPlacement(ResourceNodeAdapter.From(__instance._model)); }
            catch (Exception e) { HookGuard.Fail("ResourceNodeSpawner.SpawnNode", e); }
        }
    }

    /// <summary>
    /// A despawn is published by the host (every seat's timer fires at the same world time). A timer left over from a node that a host
    /// record replaced must not take the new node away: the despawn only runs once the current record's time is reached.
    /// </summary>
    [HarmonyPatch(typeof(ResourceNodeSpawner), nameof(ResourceNodeSpawner.DespawnNode))]
    static class NodeDespawnHook
    {
        static bool Prefix(ResourceNodeSpawner __instance, out bool __state)
        {
            __state = true;                                                                                // the original runs
            try
            {
                if (!ResourceNodeAdapter.On || Applying.Active || __instance == null) return true;
                var m = __instance._model; var td = SceneContext.Instance != null ? SceneContext.Instance.TimeDirector : null;
                if (m != null && m.resourceNodeDefinition != null && td != null && !td.HasReached(m.despawnAtWorldTime - 1.0))
                { Mod.Diagnostics?.Record("nodes", "stale-despawn-skipped", $"id={m.nodeId}; at={m.despawnAtWorldTime:0}; now={td.WorldTime():0}"); __state = false; return false; }
                return true;
            }
            catch (Exception e) { HookGuard.Fail("ResourceNodeSpawner.DespawnNode", e); return true; }
        }
        static void Postfix(ResourceNodeSpawner __instance, bool __state)
        {
            try
            {
                if (!__state || !ResourceNodeAdapter.On || Applying.Active || ResourceNodeAdapter.IsGuest || __instance == null || __instance._model == null) return;
                ResourceNodeAdapter.Sync.LocalPlacement(new NodePlacement { Id = __instance._model.nodeId, HasNode = false, TypeIds = new System.Collections.Generic.List<int>() });
            }
            catch (Exception e) { HookGuard.Fail("ResourceNodeSpawner.DespawnNode", e); }
        }
    }

    /// <summary>A harvest on this seat (its record was not empty yet): the other seats show the node empty and never spawn its yield.</summary>
    [HarmonyPatch(typeof(ResourceNode), nameof(ResourceNode.SetStateEmpty))]
    static class NodeEmptiedHook
    {
        static void Prefix(ResourceNode __instance, out int __state)
        {
            __state = -1; try { if (__instance != null && __instance._model != null) __state = (int)__instance._model.nodeState; } catch { }
        }
        static void Postfix(ResourceNode __instance, int __state)
        {
            try
            {
                if (!ResourceNodeAdapter.On || Applying.Active || __instance == null || __instance._model == null) return;
                if (__state == NodeRules.Harvested || __state < 0) return;                                 // a load of an already empty node
                var p = ResourceNodeAdapter.From(__instance._model); if (!p.HasNode) return;
                ResourceNodeAdapter.Sync.LocalEmptied(p);
            }
            catch (Exception e) { HookGuard.Fail("ResourceNode.SetStateEmpty", e); }
        }
    }
}

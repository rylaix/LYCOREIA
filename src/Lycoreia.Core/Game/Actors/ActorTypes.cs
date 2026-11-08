// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.SceneManagement;
using Lycoreia.Net.Messages;
using UnityEngine;

namespace Lycoreia.Game
{
    /// <summary>Persistent id (assigned by the host, sent as the type registry) to game identifiable type, and scene group ids.</summary>
    public static class ActorTypes
    {
        static readonly Dictionary<int, string> _idToRef = new Dictionary<int, string>(); static readonly Dictionary<string, int> _refToId = new Dictionary<string, int>();
        static readonly Dictionary<int, IdentifiableType> _typeCache = new Dictionary<int, IdentifiableType>();
        public static int Count => _idToRef.Count;

        static Il2CppSystem.Collections.Generic.Dictionary<string, IdentifiableType> Table => GameContext.Instance?.LookupDirector?._identifiableTypeByRefId;

        /// <summary>Host: assigns ids 1..N to every known identifiable type, ordered by reference id so the table is stable per game build.</summary>
        public static ActorTypeRegistry BuildRegistry()
        {
            var msg = new ActorTypeRegistry(); var table = Table; if (table == null) return msg;
            var keys = new List<string>(); foreach (var k in table.Keys) keys.Add(k);
            // gadget and decoration definitions (inventory counts, placed gadgets) even when the lookup table does not list them
            var known = new HashSet<string>(keys); foreach (var g in Gadgets().Keys) if (known.Add(g)) keys.Add(g);
            keys.Sort(StringComparer.Ordinal);
            _idToRef.Clear(); _refToId.Clear(); _typeCache.Clear(); int id = 1;
            foreach (var k in keys) { _idToRef[id] = k; _refToId[k] = id; msg.Types.Add(new TypeEntry { Id = id, ReferenceId = k }); id++; }
            return msg;
        }
        public static void Apply(ActorTypeRegistry m)
        {
            _idToRef.Clear(); _refToId.Clear(); _typeCache.Clear();
            foreach (var t in m.Types) { _idToRef[t.Id] = t.ReferenceId; _refToId[t.ReferenceId] = t.Id; }
        }
        public static void Clear() { _idToRef.Clear(); _refToId.Clear(); _typeCache.Clear(); }

        static Dictionary<string, IdentifiableType> _gadgets;
        /// <summary>Every loaded GadgetDefinition asset by reference id (cached once found).</summary>
        static Dictionary<string, IdentifiableType> Gadgets()
        {
            if (_gadgets != null) return _gadgets;
            var d = new Dictionary<string, IdentifiableType>(StringComparer.Ordinal);
            try
            {
                if (GameContext.Instance?.LookupDirector == null) return d;
                foreach (var g in Resources.FindObjectsOfTypeAll<GadgetDefinition>()) if (g != null && !string.IsNullOrEmpty(g.ReferenceId)) d[g.ReferenceId] = g;
                if (d.Count > 0) _gadgets = d;
            }
            catch (Exception e) { Log.Debug("gadget definition scan failed: " + e.Message); }
            return d;
        }

        public static bool TryGetType(int id, out IdentifiableType type)
        {
            if (_typeCache.TryGetValue(id, out type)) return type != null;
            type = null; if (!_idToRef.TryGetValue(id, out var r)) return false;
            var table = Table; if (table == null || !table.TryGetValue(r, out type) || type == null) type = null;
            if (type == null && Gadgets().TryGetValue(r, out var gd)) type = gd;
            if (type == null) { Log.Debug($"actor type {id} ({r}) is not known to this game build"); return false; }
            _typeCache[id] = type; return true;
        }
        public static int IdOf(IdentifiableType type) => type != null && _refToId.TryGetValue(type.ReferenceId, out var id) ? id : 0;
    }

    /// <summary>Scene group to wire id: a stable hash of its reference id (the game assigns no id we can send).</summary>
    public static class SceneGroups
    {
        static Dictionary<int, SceneGroup> _byId; static Dictionary<string, int> _byRef;
        public static int StableId(string referenceId) { unchecked { uint h = 2166136261; foreach (char c in referenceId ?? "") h = (h ^ c) * 16777619; return (int)(h & 0x7FFFFFFF); } }
        static void Build()
        {
            _byId = new Dictionary<int, SceneGroup>(); _byRef = new Dictionary<string, int>();
            foreach (var g in Resources.FindObjectsOfTypeAll<SceneGroup>()) { if (g == null || string.IsNullOrEmpty(g.ReferenceId)) continue; int id = StableId(g.ReferenceId); _byId[id] = g; _byRef[g.ReferenceId] = id; }
        }
        public static int IdOf(SceneGroup g) { if (g == null) return -1; if (_byRef == null || !_byRef.ContainsKey(g.ReferenceId)) Build(); return _byRef.TryGetValue(g.ReferenceId, out var id) ? id : -1; }
        public static SceneGroup Get(int id) { if (_byId == null || !_byId.ContainsKey(id)) Build(); return _byId.TryGetValue(id, out var g) ? g : null; }
        public static void Invalidate() { _byId = null; _byRef = null; }
    }
}

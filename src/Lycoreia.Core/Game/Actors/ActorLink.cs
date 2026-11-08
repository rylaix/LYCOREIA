// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Lycoreia.Actors;
using UnityEngine;

namespace Lycoreia.Game
{
    /// <summary>
    /// The network view of one world actor. It is not a Unity component: it is resolved on demand from the game object's
    /// identifiable and the actor table, so it exists as soon as the table knows the id (a replica created while a message is
    /// being applied is "attached" before its first behaviour tick) and can never be left behind on a destroyed object.
    /// Converts to false when the object is not a networked actor, like a missing Unity component.
    /// </summary>
    public sealed class ActorLink
    {
        public readonly long Id; public readonly GameObject Object;
        ActorLink(long id, GameObject go) { Id = id; Object = go; }

        public ActorId ActorId => new ActorId(Id);
        public ActorId GetActorId() => ActorId;
        public Transform transform => Object != null ? Object.transform : null;
        public GameObject gameObject => Object;
        public ActorEntry Entry => Mod.Actors != null && Mod.Actors.Table.TryGet(Id, out var e) ? e : null;
        /// <summary>Current owner id ("" when unowned or unknown).</summary>
        public string CurrentOwnerId => Entry?.Owner ?? "";
        /// <summary>True when this peer simulates the actor. Offline every actor is local.</summary>
        public bool LocallyOwned => !Mod.IsNetworkActive || (Entry is { } e && e.Owner == Mod.LocalId);
        public bool IsDestroyed => Object == null || Entry == null;
        public bool LocalVacuumPrediction => ActorRuntime.Motion.IsPredicted(Id);
        public void SetLocalVacuumPrediction(bool on) => ActorRuntime.SetPredicted(this, on);
        public T GetComponent<T>() where T : Component => Object != null ? Object.GetComponent<T>() : null;
        public T[] GetComponentsInChildren<T>(bool includeInactive = false) where T : Component => Object != null ? Object.GetComponentsInChildren<T>(includeInactive) : Array.Empty<T>();
        public int GetInstanceID() => Object != null ? Object.GetInstanceID() : 0;

        public static implicit operator bool(ActorLink l) => l != null && l.Object != null;

        /// <summary>The networked actor that <paramref name="c"/> belongs to (searching parents), or null.</summary>
        public static ActorLink Of(Component c) => c == null ? null : Of(c.gameObject);
        public static ActorLink Of(GameObject go)
        {
            try
            {
                if (go == null || Mod.Actors == null) return null;
                var ident = go.GetComponentInParent<IdentifiableActor>(); if (ident == null) return null;
                long id = IdOfIdent(ident); if (id == 0 || !Mod.Actors.Table.TryGet(id, out _)) return null;
                return new ActorLink(id, ident.gameObject);
            }
            catch { return null; }
        }
        /// <summary>Actor id of an identifiable object even when the table does not (yet) know it; 0 if none.</summary>
        public static long IdOf(GameObject go)
        {
            try { var ident = go != null ? go.GetComponentInParent<IdentifiableActor>() : null; return ident != null ? IdOfIdent(ident) : 0; }
            catch { return 0; }
        }
        /// <summary>The model's id, or the component's own id when its model link is not set (seen on some produce replicas in game, 2026-11-02).</summary>
        static long IdOfIdent(IdentifiableActor ident)
        {
            try { if (ident._model != null) return ident._model.actorId.Value; } catch { }
            try { return ident.GetActorId().Value; } catch { return 0; }
        }
        public static ActorLink ById(long id)
        {
            var m = GameState.Model; if (m == null || Mod.Actors == null || !Mod.Actors.Table.TryGet(id, out _)) return null;
            return m.identifiables.TryGetValue(new ActorId(id), out var model) && model != null && model.GetGameObject() != null ? new ActorLink(id, model.GetGameObject()) : null;
        }
    }
}

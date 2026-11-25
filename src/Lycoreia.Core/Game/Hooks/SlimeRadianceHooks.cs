// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Slime;
using Lycoreia.Actors;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using UnityEngine;

namespace Lycoreia.Game.Hooks
{
    public static class SlimeRadiance
    {
        static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);
        static bool _installed;

        public static void Install()
        {
            if (_installed) return; _installed = true;
            Mod.Node.On<ActorRadiant>(Side.Host, (c, m) => HostReceive(c, m));
            Mod.Node.On<ActorRadiant>(Side.Guest, (c, m) => { Apply(m.ActorId, m.RadiantType, "received"); return Verdict.Accept; });
        }

        /// <summary>Actor type id of the model's radiant base slime; 0 when it is not radiant (or the type is unknown).</summary>
        public static int Of(IdentifiableModel model)
        {
            try { var sm = model?.TryCast<SlimeModel>(); var def = sm?.RadiantBaseType; return def == null ? 0 : ActorTypes.IdOf(def); }
            catch { return 0; }
        }

        /// <summary>Puts the radiance into a spawn entry (spawn message, snapshot record).</summary>
        public static void Fill(ActorEntry e, IdentifiableModel model)
        {
            int r = Of(model); if (r == 0 || e == null) return;
            e.HasSlime = true; e.Slime.Radiancy = r;
        }

        /// <summary>Replica side: the model gets the owner's radiance; a live slime also gets the radiant look (a later Start reads the model).</summary>
        public static void Apply(long id, int radiantType, string why)
        {
            if (radiantType == 0) return;
            try
            {
                var t = Mod.Actors?.Table; if (t != null && t.TryGet(id, out var e)) { e.HasSlime = true; e.Slime.Radiancy = radiantType; }
                var sm = GameActorWorld.Find(id)?.TryCast<SlimeModel>(); if (sm == null) return;
                if (!ActorTypes.TryGetType(radiantType, out var type)) { Rl.Warn("type:" + radiantType, $"radiant slime {id}: unknown base type {radiantType}"); return; }
                var def = type.TryCast<SlimeDefinition>(); if (def == null) return;
                if (sm.RadiantBaseType != null && sm.RadiantBaseType.ReferenceId == def.ReferenceId) return;
                sm.RadiantBaseType = def;
                var go = sm.GetGameObject(); var rad = go != null ? go.GetComponent<SlimeRadiant>() : null;
                if (rad != null) rad.SetRadiantAppearance(def);
                Mod.Diagnostics?.Record("actors", "radiant-applied", $"id={id}; type={radiantType}; live={go != null}; why={why}");
            }
            catch (Exception ex) { Rl.Warn("apply:" + id, $"could not make slime {id} radiant: {ex.Message}"); }
        }

        /// <summary>Before a replica's object exists: only the model field (SlimeRadiant.Start applies the look).</summary>
        internal static void ApplyToNewModel(ActorModel model, ActorEntry e)
        {
            if (model == null || e == null || !e.HasSlime || e.Slime.Radiancy == 0) return;
            try
            {
                var sm = model.TryCast<SlimeModel>(); if (sm == null) return;
                if (ActorTypes.TryGetType(e.Slime.Radiancy, out var type)) { var def = type.TryCast<SlimeDefinition>(); if (def != null) sm.RadiantBaseType = def; }
            }
            catch (Exception ex) { Rl.Warn("new:" + e.Id, $"could not make replica {e.Id} radiant: {ex.Message}"); }
        }

        static Verdict HostReceive(Ctx c, ActorRadiant m)
        {
            var t = Mod.Actors?.Table;
            if (t == null || !t.TryGet(m.ActorId, out var e) || e.Owner != c.SenderId) { Rl.Warn("reject", $"radiant {m.ActorId} from {c.SenderId} rejected (not its owner)"); return Verdict.Reject; }
            Apply(m.ActorId, m.RadiantType, "from-owner"); return Verdict.Relay;
        }

        /// <summary>Owner side: radiance set after the spawn was published (spawners) goes out once.</summary>
        [HarmonyPatch]
        static class SetRadiantHook
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                foreach (var m in AccessTools.GetDeclaredMethods(typeof(SlimeRadiant))) if (m.Name == nameof(SlimeRadiant.SetRadiant)) yield return m;
            }

            static void Postfix(SlimeRadiant __instance)
            {
                try
                {
                    if (!HookGuard.Active || Applying.Active || __instance == null) return;
                    var model = __instance._slimeModel; if (model == null) return;
                    long id = model.actorId.Value;
                    var t = Mod.Actors.Table; if (!t.TryGet(id, out var e) || e.Owner != Mod.LocalId) return;   // not published yet: the spawn carries it
                    int r = Of(model); if (r == 0 || (e.HasSlime && e.Slime.Radiancy == r)) return;
                    e.HasSlime = true; e.Slime.Radiancy = r;
                    var msg = new ActorRadiant { ActorId = id, RadiantType = r };
                    if (Mod.IsHost) Mod.Node.SendToAll(msg); else if (Mod.IsGuest) Mod.Node.SendToHost(msg);
                    Mod.Diagnostics?.Record("actors", "radiant-sent", $"id={id}; type={r}");
                }
                catch (Exception ex) { HookGuard.Fail("SlimeRadiant.SetRadiant", ex); }
            }
        }
    }
}

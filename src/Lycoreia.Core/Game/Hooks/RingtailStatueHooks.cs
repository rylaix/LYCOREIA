// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Lycoreia.Actors;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using UnityEngine;

namespace Lycoreia.Game.Hooks
{
    public static class RingtailStatues
    {
        public static readonly StatueLinkTable Links = new StatueLinkTable();
        static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);
        static readonly HashSet<long> _skipLogged = new HashSet<long>();
        static bool _installed;

        public static void Install(Session s)
        {
            if (_installed) return; _installed = true;
            Mod.Node.On<StatueLink>(Side.Host, (c, m) => HostReceive(c, m));
            Mod.Node.On<StatueLink>(Side.Guest, (c, m) => Apply(m));
            s.ServerStarted += Reset; s.ServerStopped += Reset; s.ClientJoined += Reset; s.ClientDisconnected += _ => Reset();
            // a joining guest learns which slimes are stone right now (its replicas of them must stay hidden)
            Mod.Snapshots.Register(Lycoreia.Snapshot.SnapStep.SlimeBehavior, ctx =>
            {
                int n = 0;
                foreach (var (statue, slime) in StatuesInWorld()) { ctx.Send(new StatueLink { StatueId = statue, SlimeId = slime, Stone = true }); n++; }
                if (n > 0) Mod.Diagnostics?.Record("actors", "statue-links-snapshot", $"count={n}; peer={ctx.Peer?.PlayerId}");
            });
        }

        static void Reset() { Links.Clear(); _skipLogged.Clear(); _hidden.Clear(); }
        static readonly HashSet<long> _hidden = new HashSet<long>();                                    // slime objects this seat hid; only those are restored

        static IEnumerable<(long statue, long slime)> StatuesInWorld()
        {
            var list = new List<(long, long)>(); var seen = new HashSet<long>(); var m = GameState.Model; if (m == null) return list;
            foreach (var p in Links.All()) if (seen.Add(p.statue)) list.Add(p);                         // statues owned by other guests (replicas carry no reference)
            try
            {
                foreach (var kv in m.identifiables)
                {
                    var sf = kv.Value?.TryCast<StatueFormModel>(); if (sf == null) continue;
                    long slime = sf.referencedActorId.Value; if (slime != 0 && seen.Add(sf.actorId.Value)) list.Add((sf.actorId.Value, slime));
                }
            }
            catch (Exception e) { Rl.Warn("scan", "statue scan failed: " + e.Message); }
            return list;
        }

        static bool MayTransform(Component c, long id, string what)
        {
            if (!HookGuard.Active) return true;
            var l = ActorLink.Of(c);
            bool ok = StatueRules.MayTransform(true, l, l && l.LocallyOwned, BehaviourAuthority.ShouldRun(c));
            if (!ok && _skipLogged.Count < 4096 && _skipLogged.Add(id))
                Mod.Diagnostics?.Record("actors", "statue-transform-skipped", $"what={what}; id={id}; owner={l?.CurrentOwnerId}");
            return ok;
        }

        // ------------------------------------------------------------------ owner side: native ran, tell the others
        internal static void SendStone(long statueId, long slimeId)
        {
            Links.Note(statueId, slimeId);
            Send(new StatueLink { StatueId = statueId, SlimeId = slimeId, Stone = true });
            Log.Info($"ringtail {slimeId} turned to stone (statue {statueId})");
        }

        internal static void SendRelease(long statueId, long slimeId)
        {
            Links.Forget(statueId);
            Send(new StatueLink { StatueId = statueId, SlimeId = slimeId, Stone = false });
            Log.Info($"ringtail {slimeId} left its stone form (statue {statueId})");
        }

        static void Send(StatueLink m)
        {
            try { if (Mod.IsHost) Mod.Node.SendToAll(m); else if (Mod.IsGuest) Mod.Node.SendToHost(m); }
            catch (Exception e) { Rl.Warn("send", "statue link send failed: " + e.Message); }
            Mod.Diagnostics?.Record("actors", "statue-link-sent", $"statue={m.StatueId}; slime={m.SlimeId}; stone={m.Stone}");
        }

        // ------------------------------------------------------------------ receivers
        static Verdict HostReceive(Ctx c, StatueLink m)
        {
            var t = Mod.Actors?.Table; if (t == null) return Verdict.Reject;
            bool ownsStatue = t.TryGet(m.StatueId, out var st) && st.Owner == c.SenderId;
            bool ownsSlime = t.TryGet(m.SlimeId, out var sl) && sl.Owner == c.SenderId;
            // a release follows the statue's own destroy (already out of the table): the slime must still be known
            bool ok = m.Stone ? ownsStatue || ownsSlime : ownsStatue || ownsSlime || (Links.TryGet(m.StatueId, out var linked) && linked == m.SlimeId);
            if (!ok) { Rl.Warn("reject", $"statue link from {c.SenderId} rejected (statue {m.StatueId}, slime {m.SlimeId})"); return Verdict.Reject; }
            Apply(m); return Verdict.Relay;
        }

        static void Apply(StatueLink m)
        {
            Mod.Diagnostics?.Record("actors", "statue-link-applied", $"statue={m.StatueId}; slime={m.SlimeId}; stone={m.Stone}");
            if (m.Stone) { Links.Note(m.StatueId, m.SlimeId); Hide(m.SlimeId); }
            else { Links.Forget(m.StatueId); Restore(m.SlimeId); }
        }

        /// <summary>The same native removal as BecomeStatue: the slime's object goes, its model (and table entry) stays.</summary>
        static void Hide(long slimeId)
        {
            var go = GameActorWorld.ObjectOf(slimeId); if (go == null) return;
            GameActorWorld.InternalDepth++;
            try { Destroyer.Destroy(go, 0f, "Lycoreia.statue", true, false); ActorRuntime.Forget(slimeId); if (_hidden.Count < 4096) _hidden.Add(slimeId); }
            catch (Exception e) { Rl.Warn("hide", $"could not hide ringtail {slimeId}: {e.Message}"); }
            finally { GameActorWorld.InternalDepth--; }
        }

        /// <summary>The same native step as Unstatue: the slime's object comes back from its kept model.</summary>
        static void Restore(long slimeId)
        {
            if (!_hidden.Remove(slimeId)) return;                                                        // never wake a slime this seat did not hide (unloaded regions)
            var model = GameActorWorld.Find(slimeId)?.TryCast<ActorModel>(); if (model == null || model.GetGameObject() != null) return;
            GameActorWorld.InternalDepth++; long alloc = ActorReconciler.AllocatorBefore();
            try { InstantiationHelpers.InstantiateActorFromModel(model); }                       // a replica comes up kinematic (ActorFromModelHook)
            catch (Exception e) { Rl.Warn("restore", $"could not restore ringtail {slimeId}: {e.Message}"); }
            finally { ActorReconciler.AllocatorAfter(alloc, slimeId); GameActorWorld.InternalDepth--; }
        }

        /// <summary>A replica created (snapshot, region load) for a slime another seat holds in stone form stays hidden.</summary>
        internal static void AfterReplicaInstantiated(long id)
        {
            if (!Links.IsStone(id)) return;
            Hide(id);
            Mod.Diagnostics?.Record("actors", "statue-replica-hidden", $"slime={id}");
        }

        // ------------------------------------------------------------------ hooks
        [HarmonyPatch(typeof(TurnToStoneInDaylight), nameof(TurnToStoneInDaylight.BecomeStatue))]
        static class BecomeStatueHook
        {
            static bool Prefix(TurnToStoneInDaylight __instance)
            {
                try { return MayTransform(__instance, ActorLink.IdOf(__instance.gameObject), "stone"); }
                catch (Exception e) { HookGuard.Fail("BecomeStatue", e); return true; }
            }
        }

        [HarmonyPatch(typeof(RingtailStatue), nameof(RingtailStatue.SetReferencedActorModel))]
        static class StatueReferenceHook
        {
            static void Postfix(RingtailStatue __instance, ActorModel actorModel)
            {
                try
                {
                    if (!HookGuard.Active || Applying.Active || actorModel == null || __instance._model == null) return;
                    long statue = __instance._model.actorId.Value, slime = actorModel.actorId.Value;
                    var l = ActorLink.Of(__instance); if (!l || !l.LocallyOwned) return;          // only the seat that owns the statue speaks for it
                    SendStone(statue, slime);
                }
                catch (Exception e) { HookGuard.Fail("RingtailStatue.SetReferencedActorModel", e); }
            }
        }

        [HarmonyPatch(typeof(RingtailStatue), nameof(RingtailStatue.Unstatue))]
        static class UnstatueHook
        {
            static bool Prefix(RingtailStatue __instance, out (long statue, long slime) __state)
            {
                __state = (0, 0);
                try
                {
                    if (!HookGuard.Active) return true;
                    var model = __instance._model; if (model == null) return true;
                    long statue = model.actorId.Value;
                    if (!MayTransform(__instance, statue, "release")) return false;
                    long slime = model.referencedActorId.Value;
                    if (slime == 0 || GameActorWorld.Find(slime) == null)
                    {
                        // a replica statue (created from the table) has no reference: take it from the owner's link
                        if (!Links.TryGet(statue, out slime) || GameActorWorld.Find(slime) == null)
                        {
                            Rl.Warn("unknown:" + statue, $"statue {statue} does not know its ringtail; it stays stone");
                            return false;
                        }
                        model.referencedActorId = new ActorId(slime);
                    }
                    __state = (statue, slime);
                    return true;
                }
                catch (Exception e) { HookGuard.Fail("RingtailStatue.Unstatue", e); return true; }
            }

            static void Postfix((long statue, long slime) __state)
            {
                try { if (__state.statue != 0 && HookGuard.Active) SendRelease(__state.statue, __state.slime); }
                catch (Exception e) { HookGuard.Fail("RingtailStatue.Unstatue.post", e); }
            }
        }
    }
}

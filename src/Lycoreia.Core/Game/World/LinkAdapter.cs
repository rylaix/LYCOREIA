// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.SceneManagement;
using Lycoreia.Game.Hooks;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Il2CppMonomiPark.SlimeRancher.World.Teleportation;
using Lycoreia.Owned;
using Lycoreia.World;
using UnityEngine;

namespace Lycoreia.Game.World
{
    /// <summary>
    /// Teleporter links and paired gadgets over the live game. Links are published from TeleporterModel.AddDestination; unlinks are
    /// detected by watching the links we recorded (TeleporterModel.RemoveDestination is never patched: native access violation). Received
    /// links are reconciled into the models every 0.5 s while the registry or source gadget is still loading. Warp depot / linked cannon
    /// partners come from the host's pair map, never from proximity. Not synced yet: cannon fire timers, teleporter FX.
    /// </summary>
    public sealed class LinkAdapter
    {
        public static LinkAdapter Instance; public readonly TeleporterLinks Teleporters; public readonly GadgetLinks Gadgets;
        public const double ReconcileEvery = 0.5, PairScanEvery = 1.0;
        readonly HashSet<(string, string)> _seenLocally = new HashSet<(string, string)>(); readonly Dictionary<(string, string), double> _localGrace = new Dictionary<(string, string), double>();
        readonly HashSet<string> _puzzleNodes = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string, bool> _activation = new Dictionary<string, bool>(StringComparer.Ordinal);
        readonly Session _session;
        double _nextReconcile, _nextPairScan; static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);

        public static void Install(Session s, Lycoreia.Snapshot.SnapshotHost snap) { if (Instance == null) Instance = new LinkAdapter(s, snap); }
        LinkAdapter(Session s, Lycoreia.Snapshot.SnapshotHost snap)
        {
            _session = s;
            s.ClientJoined += () => _activation.Clear();
            s.ServerStarted += () => _activation.Clear();
            s.Node.On<TeleporterActivation>(Side.Guest, (c, m) =>
            {
                if (!string.IsNullOrWhiteSpace(m.NodeId)) _activation[m.NodeId] = m.Waiting;
                ReconcileActivation();
            });
            snap.Register(Lycoreia.Snapshot.SnapStep.TeleporterLinks, ctx =>
            {
                ReconcileActivation();
                foreach (var pair in _activation) ctx.Send(new TeleporterActivation { NodeId = pair.Key, Waiting = pair.Value });
            });
            Teleporters = new TeleporterLinks(s, snap) { EndpointLoaded = id => Teleporter(id) != null || Node(id) != null, OnUnlink = ApplyUnlink };
            Gadgets = new GadgetLinks(s, snap) { Authorize = AuthorizePair, OnPair = RepairPair };
            s.ClientJoined += () => _seenLocally.Clear(); s.ServerStarted += () => { _seenLocally.Clear(); SeedHostLinks(); };
        }

        // ---------------------------------------------------------------- teleporters
        static GameModel G => GameState.Model;
        static TeleporterModel Teleporter(string id) { var g = G; return g?.teleporters != null && id != null && g.teleporters.TryGetValue(id, out var t) ? t : null; }
        static TeleporterNodeModel Node(string id) { var g = G; return g?.teleporterNodes != null && id != null && g.teleporterNodes.TryGetValue(id, out var n) ? n : null; }
        static List<string> DestinationIds(TeleporterModel t) { var l = new List<string>(); try { if (t?.DestinationNodes != null) foreach (var n in t.DestinationNodes) if (n != null) l.Add(n.NodeId); } catch { } return l; }

        public void OnLocalAddDestination(TeleporterModel t, TeleporterNodeModel node, SceneGroup group)
        {
            if (t == null || node == null || Mod.IsGuest && !Teleporters.SnapshotApplied) return; _seenLocally.Add((t.Id, node.NodeId)); if (!Applying.Active) _localGrace[(t.Id, node.NodeId)] = Time.unscaledTimeAsDouble + 5;
            if (!Applying.Active && (!Mod.IsGuest || IsGadgetSource(t))) Teleporters.LocalLink(t.Id, node.NodeId, SceneGroups.IdOf(group));
        }
        static bool IsGadgetSource(TeleporterModel model)
        {
            if (model?.SourceNodeModel == null) return false;
            foreach (var node in SceneObjectCache.All<TeleporterNode>())
                if (node != null && node.NodeId == model.SourceNodeModel.NodeId && node._gadget != null) return true;
            return false;
        }
        void SeedHostLinks()
        {
            var g = G; if (g?.teleporters == null) return;
            try { foreach (var kv in g.teleporters) foreach (var d in DestinationIds(kv.Value)) { _seenLocally.Add((kv.Key, d)); Teleporters.LocalLink(kv.Key, d, SceneGroups.IdOf(kv.Value.GetDestinationSceneGroup(Node(d)))); } }
            catch (Exception e) { Log.Warn("teleporter seed: " + e.Message); }
        }
        void ApplyUnlink(string src, string dst)
        {
            try
            {
                _seenLocally.Remove((src, dst)); var t = Teleporter(src); var n = Node(dst);
                if (t != null && n != null && DestinationIds(t).Contains(dst)) using (Applying.Enter()) t.RemoveDestination(n);       // calling it is fine; patching it is not
            }
            catch (Exception e) { Rl.Warn("tp-unlink", $"teleporter unlink {src}->{dst} failed: {e.Message}"); }
        }
        /// <summary>0.5 s: received links missing from loaded models are added; recorded links that vanished from a model are unlinks.</summary>
        void Reconcile()
        {
            var g = G; if (g == null) return;
            foreach (var kv in Teleporters.Links.ToList())
            {
                var t = Teleporter(kv.Key); if (t == null) continue;                                       // source gadget still loading: retry
                var dests = DestinationIds(t);
                if (dests.Contains(kv.Value.dest)) { _seenLocally.Add((kv.Key, kv.Value.dest)); continue; }
                if (_seenLocally.Contains((kv.Key, kv.Value.dest)) && (!Mod.IsGuest || IsGadgetSource(t))) { _seenLocally.Remove((kv.Key, kv.Value.dest)); Teleporters.LocalUnlink(kv.Key, kv.Value.dest); continue; }   // observed removal
                try
                {
                    var n = Node(kv.Value.dest) ?? g.GetOrCreateTeleporterNodeModel(kv.Value.dest); var grp = SceneGroups.Get(kv.Value.scene);
                    if (n == null) continue;
                    using (Applying.Enter()) t.AddDestination(n, grp);
                    _seenLocally.Add((kv.Key, kv.Value.dest)); Teleporters.OnEndpointLoaded(kv.Key);
                }
                catch (Exception e) { Rl.Warn("tp-link-" + kv.Key, $"teleporter link {kv.Key}->{kv.Value.dest} failed: {e.Message}"); }
            }
            // a guest's own save may hold links the host does not have: after the snapshot they are removed (the host map is authoritative)
            if (Mod.IsGuest && Teleporters.SnapshotApplied && g.teleporters != null)
                foreach (var kv in g.teleporters)
                    foreach (var d in DestinationIds(kv.Value).ToList())
                        if ((!Teleporters.Links.TryGetValue(kv.Key, out var l) || l.dest != d) && !(_localGrace.TryGetValue((kv.Key, d), out var until) && until > Time.unscaledTimeAsDouble)) ApplyUnlink(kv.Key, d);   // own new link: wait for the host's answer
        }

        // ---------------------------------------------------------------- paired gadgets
        static GadgetModel Gadget(long id) { var g = G; return g != null && g.identifiables.TryGetValue(new ActorId(id), out var m) && m != null ? m.TryCast<GadgetModel>() : null; }
        static GadgetPairKind KindOf(GadgetModel m) => m == null ? GadgetPairKind.Unsupported : m.TryCast<WarpDepotModel>() != null ? GadgetPairKind.WarpDepot : m.TryCast<LinkedCannonModel>() != null ? GadgetPairKind.LinkedCannon : GadgetPairKind.Unsupported;
        bool? AuthorizePair(long a, long b, string sender)
        {
            var ma = Gadget(a); var mb = Gadget(b); if (ma == null || mb == null) return null;                // spawns travel on another lane: defer
            return GadgetLinkAuthorizationPolicy.AreCompatible(KindOf(ma), KindOf(mb), ma.ident != null && mb.ident != null && ma.ident.ReferenceId == mb.ident.ReferenceId);
        }
        void RepairPair(long a, long b)
        {
            try
            {
                var da = Gadget(a)?.TryCast<WarpDepotModel>(); var db = Gadget(b)?.TryCast<WarpDepotModel>(); if (da == null || db == null) return;
                using (Applying.Enter()) { if (da.linkedModel == null || da.linkedModel.Pointer != db.Pointer) da.linkedModel = db; if (db.linkedModel == null || db.linkedModel.Pointer != da.Pointer) db.linkedModel = da; }
            }
            catch (Exception e) { Rl.Warn("gadget-pair", $"pair {a}<->{b} repair failed: {e.Message}"); }
        }
        /// <summary>The partner LinkDestroyer and the depot use: the host's pair map, never proximity (null: let the game decide).</summary>
        public static GadgetModel PartnerFromMap(GadgetModel self)
        {
            var a = Instance; if (a == null || !Mod.IsNetworkActive || self == null) return null;
            long p = a.Gadgets.PartnerOf(self.actorId.Value); return p != 0 ? Gadget(p) : null;
        }
        void ScanPairs()
        {
            var g = G; if (g == null) return; var alive = new HashSet<long>();
            foreach (var model in g.AllGadgets())
            {
                var d = model?.TryCast<WarpDepotModel>(); if (d == null) continue; long id = d.actorId.Value; alive.Add(id);
                var l = d.linkedModel; if (l != null) Gadgets.LocalPair(id, l.actorId.Value);
            }
            if (Mod.IsHost) foreach (var id in Gadgets.Pairs.Keys.ToList()) if (Gadget(id) == null) Gadgets.GadgetDestroyed(id);   // model gone, not just unloaded
            Gadgets.Revalidate();
        }

        // Link presence does not encode the native external-activation gate. Keep this
        // gate separate from transient arrival/trigger-exit cooldowns on each player.
        void ReconcileActivation()
        {
            if (Mod.IsGuest && Teleporters.SnapshotApplied) ReconcilePuzzleActivation();
            foreach (var node in SceneObjectCache.All<TeleporterNode>())
            {
                if (node == null || string.IsNullOrWhiteSpace(node.NodeId)) continue;
                if (_session.IsHost)
                {
                    bool waiting = node._waitForExternalActivation;
                    if (_activation.TryGetValue(node.NodeId, out var old) && old == waiting) continue;
                    _activation[node.NodeId] = waiting;
                    _session.Node.SendToAll(new TeleporterActivation { NodeId = node.NodeId, Waiting = waiting });
                    Mod.Diagnostics?.Record("world", "teleporter-activation-published", $"id={node.NodeId}; waiting={waiting}; authority=host");
                }
                else if (!_puzzleNodes.Contains(node.NodeId) && _activation.TryGetValue(node.NodeId, out var waiting) && node._waitForExternalActivation != waiting)
                {
                    using (Applying.Enter()) { node._waitForExternalActivation = waiting; node.UpdateFX(); }
                    Mod.Diagnostics?.Record("world", "teleporter-activation-applied", $"id={node.NodeId}; waiting={waiting}; authority=host");
                }
            }
        }

        // The game only activates PuzzleTeleportLock when its slots become filled;
        // NotifySlotChanged has no reverse branch. Restoring an unfilled host save
        // must therefore close the gate explicitly, including guest-only save keys.
        void ReconcilePuzzleActivation()
        {
            _puzzleNodes.Clear();
            foreach (var puzzle in SceneObjectCache.All<PuzzleTeleportLock>())
            {
                if (puzzle == null || puzzle.Teleporter == null) continue;
                _puzzleNodes.Add(puzzle.Teleporter.NodeId);
                using (Applying.Enter())
                {
                    if (puzzle._slots != null) foreach (var slot in puzzle._slots)
                    {
                        if (slot == null || slot._model == null) continue;
                        var key = WorldRuntime.Slots.Adapter.KeyOf(slot._model);
                        bool filled = key != null && WorldRuntime.Slots.System.Confirmed(key).GetValueOrDefault() > 0;
                        if (slot._model.filled != filled) { slot._model.filled = filled; slot._model.NotifyParticipants(); }
                    }
                    if (puzzle._depositors != null) foreach (var depositor in puzzle._depositors)
                    {
                        if (depositor == null || depositor._model == null) continue;
                        var key = WorldRuntime.Depositors.Adapter.KeyOf(depositor._model);
                        int amount = (int)WorldRuntime.Depositors.System.Confirmed(key).GetValueOrDefault();
                        if (depositor._model.AmountDeposited != amount) { depositor._model.AmountDeposited = amount; depositor._model.NotifyParticipants(); }
                    }
                    bool waiting = !puzzle.ShouldUnlock();
                    var node = puzzle.Teleporter;
                    if (node._waitForExternalActivation == waiting) continue;
                    node._waitForExternalActivation = waiting; node.UpdateFX();
                    Mod.Diagnostics?.Record("world", "teleporter-puzzle-reconciled", $"id={node.NodeId}; waiting={waiting}; authority=host-slots");
                }
            }
        }

        public bool AllowExternalActivation(TeleporterNode node)
        {
            if (!Mod.IsGuest || Applying.Active) return true;
            if (!Teleporters.SnapshotApplied || node == null) return false;
            ReconcilePuzzleActivation();
            if (_puzzleNodes.Contains(node.NodeId)) return !node._waitForExternalActivation;
            return !_activation.TryGetValue(node.NodeId, out var waiting) || !waiting;
        }

        public void Pump(double now)
        {
            if (now >= _nextReconcile) { _nextReconcile = now + ReconcileEvery; try { Reconcile(); ReconcileActivation(); } catch (Exception e) { Rl.Warn("tp-reconcile", "teleporter reconcile failed: " + e.Message); } }
            if (now >= _nextPairScan) { _nextPairScan = now + PairScanEvery; try { ScanPairs(); } catch (Exception e) { Rl.Warn("pair-scan", "gadget pair scan failed: " + e.Message); } }
        }
    }

    [HarmonyPatch(typeof(TeleporterNode), nameof(TeleporterNode.RegisterSelf))]
    static class TeleporterObjectRegistered { static void Postfix() => SceneObjectCache.Invalidate<TeleporterNode>(); }
    [HarmonyPatch(typeof(TeleporterNode), nameof(TeleporterNode.DeregisterSelf))]
    static class TeleporterObjectRemoved { static void Postfix() => SceneObjectCache.Invalidate<TeleporterNode>(); }

    [HarmonyPatch(typeof(TeleporterNode), nameof(TeleporterNode.ExternalActivate))]
    static class TeleporterExternalActivationHook
    {
        static bool Prefix(TeleporterNode __instance)
        {
            try { return LinkAdapter.Instance == null || LinkAdapter.Instance.AllowExternalActivation(__instance); }
            catch (Exception e) { HookGuard.Fail("TeleporterNode.ExternalActivate", e); return !Mod.IsGuest; }
        }
    }

    [HarmonyPatch(typeof(TeleporterModel), nameof(TeleporterModel.AddDestination))]
    static class TeleporterAddDestinationHook
    {
        static void Postfix(TeleporterModel __instance, TeleporterNodeModel nodeModel, SceneGroup sceneGroup)
        { try { if (WH.On) LinkAdapter.Instance?.OnLocalAddDestination(__instance, nodeModel, sceneGroup); } catch (Exception e) { HookGuard.Fail("TeleporterModel.AddDestination", e); } }
    }
    [HarmonyPatch(typeof(WarpDepotModel), nameof(WarpDepotModel.GetLinkedGadget))]
    static class WarpDepotPartnerHook
    {
        static bool Prefix(WarpDepotModel __instance, ref GadgetModel __result) { try { var p = LinkAdapter.PartnerFromMap(__instance); if (p == null) return true; __result = p; return false; } catch (Exception e) { HookGuard.Fail("WarpDepotModel.GetLinkedGadget", e); return true; } }
    }
    [HarmonyPatch(typeof(LinkedCannonModel), nameof(LinkedCannonModel.GetLinkedGadget))]
    static class CannonPartnerHook
    {
        static bool Prefix(LinkedCannonModel __instance, ref GadgetModel __result) { try { var p = LinkAdapter.PartnerFromMap(__instance); if (p == null) return true; __result = p; return false; } catch (Exception e) { HookGuard.Fail("LinkedCannonModel.GetLinkedGadget", e); return true; } }
    }
}

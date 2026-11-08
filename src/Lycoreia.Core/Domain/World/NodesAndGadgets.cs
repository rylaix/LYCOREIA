// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Lycoreia.Domain;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.Snapshot;

namespace Lycoreia.World
{
    // =================================================================== resource nodes
    public enum NodeApply { Nothing, Place, MarkEmpty, Remove }

    /// <summary>
    /// Resource nodes (ore, brine spouts, ...) are rolled per spawner by each seat's ResourceNodeDirector: definition, prefab variant, despawn
    /// time and the resources it yields are random, so two seats grow different nodes. The host's GameModel record is the truth; a seat that
    /// rolls a node for a spawner the host has no node on may keep it (first writer wins), otherwise the host's record replaces it.
    /// </summary>
    public static class NodeRules
    {
        /// <summary>ResourceNode.NodeState.HARVESTED (NONE, READY, HARVESTING, HARVESTED, DESPAWNING); the adapter checks it at start.</summary>
        public const int Harvested = 3;

        public static bool SamePlacement(in NodePlacement a, in NodePlacement b)
            => a.HasNode == b.HasNode && (!a.HasNode || (a.Definition == b.Definition && a.VariantIndex == b.VariantIndex && Math.Abs(a.DespawnAtWorldTime - b.DespawnAtWorldTime) < 1e-3));

        /// <summary>What a receiver does with an incoming record, given what its game holds for that spawner.</summary>
        public static NodeApply Plan(bool known, in NodePlacement cur, in NodePlacement inc)
        {
            if (!inc.HasNode) return known && cur.HasNode ? NodeApply.Remove : NodeApply.Nothing;
            if (!known || !SamePlacement(cur, inc)) return NodeApply.Place;
            return inc.State == Harvested && cur.State != Harvested ? NodeApply.MarkEmpty : NodeApply.Nothing;
        }

        /// <summary>Host decision on a guest's report. A placement is kept only where the host has no node; a harvest only on the same node.</summary>
        public static bool HostAccepts(bool hostKnown, in NodePlacement host, in NodePlacement guest, bool emptied)
        {
            if (!Valid(guest)) return false;
            if (emptied) return hostKnown && host.HasNode && SamePlacement(host, guest);
            return guest.HasNode && (!hostKnown || !host.HasNode);
        }

        public static bool Valid(in NodePlacement p)
            => Lycoreia.Owned.ResourceNodeSyncPolicy.IsValidNodeId(p.Id) && p.Id.Length <= 256 && (!p.HasNode || (!string.IsNullOrEmpty(p.Definition) && p.VariantIndex >= 0 && !double.IsNaN(p.DespawnAtWorldTime)));
    }

    /// <summary>The game side of resource nodes. <see cref="Apply"/> returns false while the model table does not exist yet (retried).</summary>
    public interface INodeWorld
    {
        bool Ready { get; }
        bool TryRead(string id, out NodePlacement p);
        IEnumerable<NodePlacement> ReadAll();
        bool Apply(NodeApply action, in NodePlacement p);
    }

    public sealed class ResourceNodeSync
    {
        readonly Session _s; readonly INodeWorld _w;
        readonly Dictionary<string, NodePlacement> _pending = new Dictionary<string, NodePlacement>(StringComparer.Ordinal);
        public bool SnapshotApplied { get; private set; }
        /// <summary>Diagnostics sink: (event, details).</summary>
        public Action<string, string> Trace = (e, d) => { };

        public ResourceNodeSync(Session s, SnapshotHost snap, INodeWorld w)
        {
            _s = s; _w = w;
            s.Node.On<NodeChange>(Side.Host, HostReceive);
            s.Node.On<NodeChange>(Side.Guest, (c, m) => Receive(m.P, "live"));
            snap?.Register(SnapStep.ResourceNodes, ctx => { var m = new ResourceNodePlacements(); foreach (var p in _w.ReadAll()) if (NodeRules.Valid(p)) m.Items.Add(p); ctx.Send(m); });
            s.Node.On<ResourceNodePlacements>(Side.Guest, (c, m) => ApplySnapshot(m.Items));
            s.ClientDisconnected += _ => { _pending.Clear(); SnapshotApplied = false; };
            s.ServerStarted += () => { _pending.Clear(); SnapshotApplied = false; };
        }

        Verdict HostReceive(Ctx c, NodeChange m)
        {
            if (!HostAuthority.Require(c)) return Verdict.Reject;
            bool known = _w.TryRead(m.P.Id ?? "", out var host);
            if (!NodeRules.HostAccepts(known, host, m.P, m.Emptied))
            {
                Trace("node-refused", $"id={m.P.Id}; from={c.SenderId}; emptied={m.Emptied}; host={Describe(known, host)}; guest={Describe(true, m.P)}");
                if (known) c.Node.SendTo(c.Sender, new NodeChange { P = host });     // the sender adopts the host's record
                return Verdict.Reject;
            }
            var canonical = m.P; if (m.Emptied) { canonical = host; canonical.State = NodeRules.Harvested; }
            using (Applying.Enter()) _w.Apply(m.Emptied ? NodeApply.MarkEmpty : NodeApply.Place, canonical);
            Trace(m.Emptied ? "node-emptied" : "node-placed", $"id={canonical.Id}; by={c.SenderId}; def={canonical.Definition}; variant={canonical.VariantIndex}");
            c.Node.SendToAll(new NodeChange { P = canonical });
            return Verdict.Reject;
        }

        void Receive(in NodePlacement p, string why)
        {
            if (!NodeRules.Valid(p)) return;
            if (!_w.Ready) { _pending[p.Id] = p; return; }
            bool known = _w.TryRead(p.Id, out var cur); var plan = NodeRules.Plan(known, cur, p);
            if (plan == NodeApply.Nothing) return;
            bool ok; using (Applying.Enter()) ok = _w.Apply(plan, p);
            if (!ok) { _pending[p.Id] = p; return; }
            Trace("node-applied", $"id={p.Id}; action={plan}; why={why}; before={Describe(known, cur)}; after={Describe(true, p)}");
        }

        void ApplySnapshot(List<NodePlacement> items)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal); int n = 0;
            foreach (var p in items) { if (!NodeRules.Valid(p)) continue; ids.Add(p.Id); Receive(p, "snapshot"); n++; }
            SnapshotApplied = true;
            // nodes this guest's own save holds where the host has no record at all: offered to the host (it keeps them when it has none)
            int offered = 0;
            if (_w.Ready) foreach (var p in _w.ReadAll().ToList()) if (p.HasNode && !ids.Contains(p.Id) && NodeRules.Valid(p)) { _s.Node.SendToHost(new NodeChange { P = p }); offered++; }
            Trace("node-snapshot", $"records={n}; offered={offered}; pending={_pending.Count}");
        }

        /// <summary>Retries records whose model table was not ready.</summary>
        public void Pump()
        {
            if (_pending.Count == 0 || !_w.Ready) return;
            var l = _pending.Values.ToList(); _pending.Clear(); foreach (var p in l) Receive(p, "pending");
        }

        /// <summary>A node this seat's director rolled (or that despawned): the host publishes it, a guest offers it to the host.</summary>
        public void LocalPlacement(in NodePlacement p)
        {
            if (Applying.Active || !NodeRules.Valid(p)) return;
            if (_s.IsHost) { _s.Node.SendToAll(new NodeChange { P = p }); Trace("node-published", $"id={p.Id}; has={p.HasNode}; def={p.Definition}"); }
            else if (SnapshotApplied && p.HasNode) _s.Node.SendToHost(new NodeChange { P = p });   // despawns run on every seat at the same world time
        }

        /// <summary>A node this seat harvested (its vacuum drove it): every other seat shows it empty, nobody else spawns its resources.</summary>
        public void LocalEmptied(in NodePlacement p)
        {
            if (Applying.Active || !NodeRules.Valid(p) || !p.HasNode) return;
            var q = p; q.State = NodeRules.Harvested;
            if (_s.IsHost) _s.Node.SendToAll(new NodeChange { P = q }); else if (SnapshotApplied) _s.Node.SendToHost(new NodeChange { P = q, Emptied = true });
        }

        /// <summary>Guests never roll nodes for a spawner the host already has a node on (the host's record would replace it anyway).</summary>
        public static bool GuestMayRoll(bool isGuest, bool snapshotApplied, bool hostHasNode) => !isGuest || (snapshotApplied && !hostHasNode);

        public static string Describe(bool known, in NodePlacement p) => !known ? "unknown" : !p.HasNode ? "none" : $"{p.Definition}#{p.VariantIndex}/s{p.State}";
    }

    // =================================================================== placed gadgets and decorations
    /// <summary>Reconcile rules for gadgets (no owner: any connected player may place or pick up, notes 4.6 rule 7).</summary>
    public static class GadgetRules
    {
        public const float PrePlacedMatch = 0.75f;

        public static bool Valid(in GadgetRecord r)
            => r.TypeId > 0 && Finite(r.Position) && Finite(r.Euler) && (r.PrePlaced || r.Id > 0);
        static bool Finite(Vec3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        /// <summary>A guest places gadgets with ids from its own block only (the host's allocator owns everything else).</summary>
        public static bool HostAcceptsPlacement(in GadgetRecord r, string sender, bool idTaken)
            => Valid(r) && !r.PrePlaced && !idTaken && Lycoreia.Owned.ActorSpawnPublicationPolicy.CanClientPublish(r.Id, sender, true);

        /// <summary>
        /// Join reconcile of player-placed gadgets: create what the host has and this seat lacks (or holds as another type), remove this
        /// seat's own placed gadgets the host does not have. Pre-placed gadgets belong to the scene and are left alone.
        /// </summary>
        public static (List<GadgetRecord> create, List<GadgetRecord> remove) SnapshotPlan(IEnumerable<GadgetRecord> host, IEnumerable<GadgetRecord> local)
        {
            var mine = new Dictionary<long, GadgetRecord>(); foreach (var r in local) if (!r.PrePlaced) mine[r.Id] = r;
            var create = new List<GadgetRecord>(); var remove = new List<GadgetRecord>(); var keep = new HashSet<long>();
            foreach (var h in host)
            {
                if (h.PrePlaced || !Valid(h)) continue; keep.Add(h.Id);
                if (mine.TryGetValue(h.Id, out var l) && l.TypeId == h.TypeId) continue;
                if (mine.ContainsKey(h.Id)) remove.Add(mine[h.Id]);
                create.Add(h);
            }
            foreach (var l in mine.Values) if (!keep.Contains(l.Id)) remove.Add(l);
            return (create, remove);
        }

        /// <summary>The local gadget a removal names: same id, or for a pre-placed one the nearest pre-placed gadget of that type.</summary>
        public static GadgetRecord? Match(in GadgetRecord removed, IEnumerable<GadgetRecord> local)
        {
            GadgetRecord? best = null; float bestD = PrePlacedMatch * PrePlacedMatch;
            foreach (var l in local)
            {
                if (!removed.PrePlaced) { if (l.Id == removed.Id) return l; continue; }
                if (!l.PrePlaced || l.TypeId != removed.TypeId) continue;
                float dx = l.Position.X - removed.Position.X, dy = l.Position.Y - removed.Position.Y, dz = l.Position.Z - removed.Position.Z, d = dx * dx + dy * dy + dz * dz;
                if (d <= bestD) { bestD = d; best = l; }
            }
            return best;
        }
    }

    /// <summary>Remote inventory count increases for gadget definitions must also enter the game's Recent category event history.</summary>
    public static class InventoryRecentPolicy
    {
        public static bool ShouldRecordGadgetObtained(bool isGadgetDefinition, int before, int after)
            => isGadgetDefinition && after > before;
    }

    public interface IGadgetWorld
    {
        bool Ready { get; }
        IEnumerable<GadgetRecord> All();
        bool Exists(long id);
        bool Create(in GadgetRecord r);
        bool Remove(in GadgetRecord local);
    }

    public sealed class GadgetSync
    {
        readonly Session _s; readonly IGadgetWorld _w;
        readonly List<(bool place, GadgetRecord r)> _pending = new List<(bool, GadgetRecord)>();
        readonly Dictionary<long, double> _removed = new Dictionary<long, double>(); readonly Func<double> _now;
        const double TombstoneSeconds = 60;
        public Action<string, string> Trace = (e, d) => { };

        public GadgetSync(Session s, SnapshotHost snap, IGadgetWorld w, Func<double> now)
        {
            _s = s; _w = w; _now = now;
            s.Node.On<GadgetPlaced>(Side.Host, (c, m) =>
            {
                if (!HostAuthority.Require(c)) return Verdict.Reject;
                if (!GadgetRules.HostAcceptsPlacement(m.G, c.SenderId, _w.Exists(m.G.Id) || Tombstoned(m.G.Id))) { Trace("gadget-refused", $"id={m.G.Id}; type={m.G.TypeId}; from={c.SenderId}"); return Verdict.Reject; }
                Place(m.G, "guest:" + c.SenderId); return Verdict.Relay;
            });
            s.Node.On<GadgetPlaced>(Side.Guest, (c, m) => { if (GadgetRules.Valid(m.G)) Place(m.G, "host"); });
            s.Node.On<GadgetRemoved>(Side.Host, (c, m) => { if (!HostAuthority.Require(c) || !GadgetRules.Valid(m.G)) return Verdict.Reject; Remove(m.G, "guest:" + c.SenderId); return Verdict.Relay; });
            s.Node.On<GadgetRemoved>(Side.Guest, (c, m) => { if (GadgetRules.Valid(m.G)) Remove(m.G, "host"); });
            snap?.Register(SnapStep.Gadgets, ctx => { var m = new InitialGadgets(); foreach (var r in _w.All()) if (!r.PrePlaced && GadgetRules.Valid(r)) m.Items.Add(r); ctx.Send(m); });
            s.Node.On<InitialGadgets>(Side.Guest, (c, m) => ApplySnapshot(m.Items));
            s.ClientDisconnected += _ => { _pending.Clear(); _removed.Clear(); };
            s.ServerStarted += () => { _pending.Clear(); _removed.Clear(); };
        }

        bool Tombstoned(long id) => id > 0 && _removed.TryGetValue(id, out var t) && _now() - t < TombstoneSeconds;

        void Place(in GadgetRecord r, string from)
        {
            if (Tombstoned(r.Id)) return;
            if (!_w.Ready) { _pending.Add((true, r)); return; }
            if (_w.Exists(r.Id)) return;
            bool ok; using (Applying.Enter()) ok = _w.Create(r);
            Trace(ok ? "gadget-created" : "gadget-create-failed", $"id={r.Id}; type={r.TypeId}; scene={r.Scene}; from={from}");
        }

        void Remove(in GadgetRecord r, string from)
        {
            if (r.Id > 0) _removed[r.Id] = _now();
            if (!_w.Ready) { _pending.Add((false, r)); return; }
            var local = GadgetRules.Match(r, _w.All());
            if (local == null) { Trace("gadget-remove-nomatch", $"id={r.Id}; type={r.TypeId}; preplaced={r.PrePlaced}; from={from}"); return; }
            bool ok; using (Applying.Enter()) ok = _w.Remove(local.Value);
            Trace(ok ? "gadget-removed" : "gadget-remove-failed", $"id={r.Id}; local={local.Value.Id}; type={r.TypeId}; preplaced={r.PrePlaced}; from={from}");
        }

        void ApplySnapshot(List<GadgetRecord> host)
        {
            if (!_w.Ready) { foreach (var r in host) _pending.Add((true, r)); return; }
            var (create, remove) = GadgetRules.SnapshotPlan(host, _w.All());
            int made = 0, gone = 0;
            using (Applying.Enter())
            {
                foreach (var r in remove) if (_w.Remove(r)) gone++;
                foreach (var r in create) if (_w.Create(r)) made++;
            }
            Trace("gadget-snapshot", $"host={host.Count}; created={made}/{create.Count}; removed-local={gone}/{remove.Count}");
        }

        public void Pump()
        {
            if (_pending.Count == 0 || !_w.Ready) return;
            var l = _pending.ToArray(); _pending.Clear();
            foreach (var (place, r) in l) if (place) Place(r, "pending"); else Remove(r, "pending");
        }

        /// <summary>A gadget this seat placed: published (guests to the host, which relays).</summary>
        public void LocalPlaced(in GadgetRecord r)
        {
            if (Applying.Active || !GadgetRules.Valid(r) || r.PrePlaced) return;
            if (_s.IsHost) _s.Node.SendToAll(new GadgetPlaced { G = r }); else _s.Node.SendToHost(new GadgetPlaced { G = r });
            Trace("gadget-published", $"id={r.Id}; type={r.TypeId}; scene={r.Scene}");
        }

        /// <summary>A gadget this seat picked up, stored or demolished (its model is gone, not just unloaded).</summary>
        public void LocalRemoved(in GadgetRecord r)
        {
            if (Applying.Active || !GadgetRules.Valid(r)) return;
            if (r.Id > 0) _removed[r.Id] = _now();
            if (_s.IsHost) _s.Node.SendToAll(new GadgetRemoved { G = r }); else _s.Node.SendToHost(new GadgetRemoved { G = r });
            Trace("gadget-removal-published", $"id={r.Id}; type={r.TypeId}; preplaced={r.PrePlaced}");
        }
    }
}

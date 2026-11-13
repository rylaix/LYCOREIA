// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Text;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Lycoreia.Actors;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using UnityEngine;

namespace Lycoreia.Game
{
    /// <summary>
    /// Keeps the actor table and the local game in agreement:
    /// host load rule (register every saved actor at server start, then any actor the game created without the spawn hook),
    /// actor-id blocks (the local allocator never leaves this peer's block), guest orphans (published when inside the block,
    /// dropped when they came from the private save allocator) and a periodic census written to diagnostics for desync analysis.
    /// </summary>
    public sealed class ActorReconciler : IFramePump
    {
        public string Name => "actor-reconcile";
        const double SweepEvery = 2.0, CensusEvery = 10.0;
        static readonly OrphanClock Orphans = new OrphanClock();
        static readonly RateLimitedLog Rl = new RateLimitedLog(null, 15_000);
        static bool _seedPending; static long _guestBlockStart = -1;
        double _nextSweep; long _censusSlot; bool _wasActive;
        static readonly OrphanClock MissingClock = new OrphanClock(); static readonly Dictionary<long, double> _recreatedAt = new Dictionary<long, double>();

        public static void Install(Session s)
        {
            s.ServerStarted += () => { _seedPending = true; Orphans.Clear(); };
            s.ClientJoined += () => { Orphans.Clear(); _guestBlockStart = -1; };
            s.ClientDisconnected += _ => { Orphans.Clear(); _guestBlockStart = -1; };
            s.ServerStopped += () => { _seedPending = false; Orphans.Clear(); };
        }

        public void Pump()
        {
            if (GameState.Loading) return;
            var a = Mod.Actors; bool active = a != null && Mod.IsNetworkActive && GameState.Ready;
            if (!active) { if (_wasActive) Orphans.Clear(); _wasActive = false; return; }
            _wasActive = true;
            double now = Time.unscaledTimeAsDouble;
            if (a.IsHost && _seedPending) { _seedPending = false; Seed(a, now); _nextSweep = now + SweepEvery; }
            // the census runs on wall-clock 10 s marks so both seats' dumps describe the same moment (offline diff)
            long slot = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond / (long)CensusEvery; bool census = slot != _censusSlot;
            if (now >= _nextSweep || census) { _nextSweep = now + SweepEvery; if (census) _censusSlot = slot; Sweep(a, now, census); }
        }

        // ------------------------------------------------------------------ host load rule
        /// <summary>Host types must exist before anything is registered (the registry used to be built only when a guest joined).</summary>
        public static void EnsureHostTypes() { if (Mod.IsHost && ActorTypes.Count == 0) ActorTypes.BuildRegistry(); }

        static void Seed(ActorSync a, double now)
        {
            EnsureHostTypes(); RepairAllocator(a);
            int live = 0, sleeping = 0, skipped = 0;
            foreach (var im in ActorModels())
            {
                var e = EntryOf(im, out bool isLive, out bool skip); if (skip || e == null) { skipped++; continue; }
                if (a.HostRegisterExisting(e, isLive)) { if (isLive) live++; else sleeping++; }
            }
            Log.Info($"host registered {live + sleeping} saved actors ({live} live and host-owned, {sleeping} in unloaded regions and unowned; {skipped} skipped)");
            Mod.Diagnostics?.Record("actors", "host-seed", $"live={live}; unloaded={sleeping}; skipped={skipped}; table={a.Table.Count}");
        }

        /// <summary>Every actor model of the scene (no gadgets, no drones: those are separate model classes).</summary>
        static List<ActorModel> ActorModels()
        {
            var list = new List<ActorModel>(); var m = GameState.Model; if (m == null) return list;
            try { foreach (var kv in m.identifiables) { var am = kv.Value?.TryCast<ActorModel>(); if (am != null) list.Add(am); } }
            catch (Exception e) { Rl.Warn("enum", "actor model scan failed: " + e.Message); }
            return list;
        }

        /// <summary>The table entry for a game model; <paramref name="skip"/> for the player and types the registry does not know.</summary>
        static ActorEntry EntryOf(ActorModel im, out bool live, out bool skip)
        {
            live = false; skip = true;
            try
            {
                var t = im.ident; if (t == null || t.IsPlayer) return null;
                int typeId = ActorTypes.IdOf(t); if (typeId == 0) return null;
                var go = im.GetGameObject(); live = go != null;
                Vector3 p = live ? go.transform.position : im.lastPosition; Quaternion r = live ? go.transform.rotation : im.lastRotation;
                skip = false;
                return new ActorEntry { Id = im.actorId.Value, TypeId = typeId, Scene = SceneGroups.IdOf(im.sceneGroup), Position = GameActorWorld.W(p), Rotation = new Quat(r.x, r.y, r.z, r.w), Kind = SpawnKind.Normal };
            }
            catch (Exception e) { Rl.Warn("entry", "actor entry failed: " + e.Message); return null; }
        }

        // ------------------------------------------------------------------ actor-id blocks
        static ActorIdProvider Provider { get { try { return GameState.Model?._actorIdProvider; } catch { return null; } } }

        /// <summary>Guest: the host assigned our block in the snapshot; the allocator continues inside it from now on.</summary>
        public static void SetGuestBlock(long start)
        {
            _guestBlockStart = start;
            var a = Mod.Actors; if (a != null) RepairAllocator(a);
        }

        /// <summary>Moves the local allocator back into this peer's block when anything (a save load, a replica id claim) moved it out.</summary>
        public static void RepairAllocator(ActorSync a)
        {
            try
            {
                var prov = Provider; if (prov == null || a == null || string.IsNullOrEmpty(a.LocalId)) return;
                if (a.IsGuest && _guestBlockStart < 0) return;                                            // block not assigned yet
                var block = ActorIdBlock.For(a.LocalId, a.IsHost);
                long next = prov._nextActorId, highest = block.start - 1;
                var m = GameState.Model; if (m != null) foreach (var kv in m.identifiables) { long id = kv.Key.Value; if (ActorIdBlock.Contains(block, id) && id > highest) highest = id; }
                if (a.IsGuest && ActorIdBlock.Contains(block, _guestBlockStart) && _guestBlockStart - 1 > highest) highest = _guestBlockStart - 1;   // host-assigned floor (past tombstoned ids)
                long fixedNext = ActorIdBlock.Repair(next, block, highest);
                if (fixedNext != next)
                {
                    prov._nextActorId = fixedNext;
                    Log.Info($"actor id allocator moved from {next} to {fixedNext} (block {block.start}..{block.end - 1})");
                    Mod.Diagnostics?.Record("actors", "allocator-repaired", $"from={next}; to={fixedNext}; block={block.start}");
                }
            }
            catch (Exception e) { Rl.Warn("alloc", "allocator repair failed: " + e.Message); }
        }

        /// <summary>Wraps a replica creation: a native id claim may push the allocator to the replica's id (another player's block); undo that.</summary>
        public static long AllocatorBefore() { try { return Provider?._nextActorId ?? -1; } catch { return -1; } }
        public static void AllocatorAfter(long before, long replicaId)
        {
            if (before < 0) return;
            try
            {
                var prov = Provider; if (prov == null) return; long after = prov._nextActorId; if (after == before) return;
                var a = Mod.Actors; if (a == null) return; var block = ActorIdBlock.For(a.LocalId, a.IsHost);
                if (ActorIdBlock.Contains(block, after)) return;
                prov._nextActorId = before;
                Rl.Warn("claim", $"replica {replicaId} moved the actor id allocator to {after}; restored {before}");
                Mod.Diagnostics?.Record("actors", "allocator-claimed", $"replica={replicaId}; moved-to={after}; restored={before}");
            }
            catch { }
        }

        // ------------------------------------------------------------------ sweep + census
        static readonly List<LocalActorFact> _facts = new List<LocalActorFact>();
        static readonly Dictionary<long, ActorModel> _byId = new Dictionary<long, ActorModel>();

        static void Sweep(ActorSync a, double now, bool census)
        {
            if (a.IsHost) EnsureHostTypes();
            RepairAllocator(a);
            _facts.Clear(); _byId.Clear();
            foreach (var im in ActorModels())
            {
                try
                {
                    var t = im.ident; if (t == null || t.IsPlayer) continue;
                    long id = im.actorId.Value; var go = im.GetGameObject(); var f = new LocalActorFact { Id = id, TypeId = ActorTypes.IdOf(t), Live = go != null, TypeName = t.name };
                    if (go != null) { f.Position = GameActorWorld.W(go.transform.position); var body = go.GetComponent<Rigidbody>(); f.HasBody = body != null; f.Kinematic = body != null && body.isKinematic; f.Hibernating = BodyMode.IsHibernating(go); f.NativeHold = f.Kinematic && BodyMode.NativeHold(go); }
                    else f.Position = GameActorWorld.W(im.lastPosition);
                    _facts.Add(f); _byId[id] = im;
                }
                catch { }
            }
            var report = ActorCensus.Build(a.Table.All, _facts, a.LocalId, a.IsPlayerConnected, now);
            HandleOrphans(a, report, now);
            HandleMissing(a, report, now);
            FixReplicaBodies(a, report);
            FixFrozenOwned(a, report, now);
            if (census) WriteCensus(a, report);
        }

        static void HandleOrphans(ActorSync a, CensusReport r, double now)
        {
            var current = new HashSet<long>(); int registered = 0, dropped = 0;
            var block = ActorIdBlock.For(a.LocalId, a.IsHost); bool applied = a.IsHost || (Mod.Guest != null && Mod.Guest.Applied);
            var keep = VacuumCapture.IrreversibleIds();
            foreach (var o in r.Orphans)
            {
                current.Add(o.Id);
                bool tomb = a.Table.IsTombstoned(o.Id), prot = keep != null && keep.Contains(o.Id);
                var act = ActorCensus.Decide(a.IsHost, applied, Orphans.FirstSeen(o.Id, now), now, false, tomb, prot, ActorIdBlock.Contains(block, o.Id));
                if (act == OrphanAction.Register && _byId.TryGetValue(o.Id, out var im))
                {
                    var e = EntryOf(im, out bool live, out bool skip); if (skip || e == null) continue;
                    bool ok = a.IsHost ? a.HostRegisterExisting(e, live) : a.PublishSpawn(e);
                    if (ok) { registered++; Mod.Diagnostics?.Record("actors", "orphan-registered", $"id={o.Id}; type={o.TypeName}; live={live}"); }
                }
                else if (act == OrphanAction.Drop && _byId.TryGetValue(o.Id, out var dm))
                {
                    dropped++; Mod.Diagnostics?.Record("actors", "orphan-dropped", $"id={o.Id}; type={o.TypeName}; live={o.Live}; tombstoned={tomb}");
                    DropInternal(dm);
                }
            }
            Orphans.Retain(current);
            if (registered + dropped > 0) Log.Info($"actor reconcile: {registered} unannounced actors registered, {dropped} local-only actors dropped");
        }

        /// <summary>
        /// Table entries with no local model: the game removed it on a path no destroy hook saw. Our own actor is gone for real, so the
        /// removal is broadcast; someone else's replica is re-created from the table (at most every 30 s per actor).
        /// </summary>
        static void HandleMissing(ActorSync a, CensusReport r, double now)
        {
            var cur = new HashSet<long>(r.Missing); int published = 0, recreated = 0;
            foreach (var id in r.Missing)
            {
                if (a.IsGuest && VacuumCapture.Machine.OwnsConsumeRemoval(id)) continue;
                if (now - MissingClock.FirstSeen(id, now) < ActorCensus.OrphanGraceSeconds || !a.Table.TryGet(id, out var e) || e.IsGadget) continue;
                if (!GameActorWorld.Instantiable(e.TypeId)) continue;                                   // table-only entry by design (type without prefab)
                if (e.Owner == a.LocalId) { if (a.PublishDestroy(id)) { published++; Mod.Diagnostics?.Record("actors", "missing-owned-destroyed", $"id={id}; type={e.TypeId}"); } continue; }
                if (_recreatedAt.TryGetValue(id, out var t) && now - t < 30) continue;
                _recreatedAt[id] = now; if (_recreatedAt.Count > 4096) _recreatedAt.Clear();
                bool ok = new GameActorWorld().CreateReplica(e); if (ok) { recreated++; a.ApplyOwnershipAgain(e); }
                Mod.Diagnostics?.Record("actors", "missing-replica-recreated", $"id={id}; type={e.TypeId}; owner={e.Owner}; ok={ok}");
            }
            MissingClock.Retain(cur);
            if (published + recreated > 0) Log.Info($"actor reconcile: {published} natively removed own actors broadcast, {recreated} lost replicas re-created");
        }

        static void DropInternal(ActorModel im)
        {
            GameActorWorld.InternalDepth++;
            try { var go = im.GetGameObject(); if (go != null) Destroyer.DestroyActor(go, "Lycoreia.orphan", true); else GameState.Model?.DestroyIdentifiableModel(im); ActorRuntime.Forget(im.actorId.Value); }
            catch (Exception e) { Rl.Warn("drop", "orphan drop failed: " + e.Message); }
            finally { GameActorWorld.InternalDepth--; }
        }

        /// <summary>A replica whose region loaded after it was created comes up with a dynamic body; non-owners hold it kinematic.</summary>
        static void FixReplicaBodies(ActorSync a, CensusReport r)
        {
            // owned-but-kinematic is only reported: the game itself holds produce on plants, held and stored actors kinematic
            if (r.DynamicReplicas.Count == 0) return;
            foreach (var id in r.DynamicReplicas) if (!ActorRuntime.Motion.IsPredicted(id) && a.Table.TryGet(id, out var e)) ReplicaBodies.Apply(id, e, false);
            Mod.Diagnostics?.Record("actors", "bodies-fixed", $"dynamic-replicas={r.DynamicReplicas.Count}; frozen-owned={r.FrozenOwned.Count}");
        }

        static readonly FrozenOwnedRepair _frozen = new FrozenOwnedRepair();

        /// <summary>
        /// An owned, awake actor the game is not holding but whose body stayed kinematic: nobody simulates it and the owner's own vacuum
        /// cannot pull it (it only became vacuumable after a resync rebuilt it). Made dynamic again; before/after in actors/frozen-owned-fixed.
        /// </summary>
        static void FixFrozenOwned(ActorSync a, CensusReport r, double now)
        {
            foreach (var id in _frozen.Update(r.FrozenOwned, now))
            {
                try
                {
                    if (ActorRuntime.Motion.IsPredicted(id) || !a.Table.TryGet(id, out var e) || e.Owner != a.LocalId) continue;
                    var go = GameActorWorld.ObjectOf(id); if (go == null || BodyMode.IsHibernating(go) || BodyMode.NativeHold(go)) continue;
                    var body = go.GetComponent<Rigidbody>(); if (body == null || !body.isKinematic) continue;
                    BodyMode.Set(go, body, false);
                    Mod.Diagnostics?.Record("actors", "frozen-owned-fixed", $"id={id}; type={e.TypeId}; name={go.name}; before=kinematic; after={(body.isKinematic ? "kinematic" : "dynamic")}; pos={GameActorWorld.W(go.transform.position)}");
                }
                catch (Exception ex) { Rl.Warn("frozen-fix", "frozen owned fix failed: " + ex.Message); }
            }
        }

        static void WriteCensus(ActorSync a, CensusReport r)
        {
            var d = Mod.Diagnostics; if (d == null) return;
            d.Record("actors", "census", r.Summary() + "; " + r.Samples());
#if DEEPLOG
            // full per-actor dump for offline two-seat diffs (rewrite/tools/actor_diff.py), split under the detail limit
            var facts = new Dictionary<long, LocalActorFact>(); foreach (var f in _facts) facts[f.Id] = f;
            var sb = new StringBuilder(); int part = 0; string stamp = Time.unscaledTimeAsDouble.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            foreach (var e in a.Table.All)
            {
                if (e.IsGadget) continue;
                LocalActorFact? f = facts.TryGetValue(e.Id, out var ff) ? ff : null;
                if (sb.Length > 3600) { d.Record("actors", "census-dump", $"t={stamp}; part={part++}; " + sb); sb.Clear(); }
                if (sb.Length > 0) sb.Append(';'); sb.Append(ActorCensus.DumpLine(e, f));
            }
            d.Record("actors", "census-dump", $"t={stamp}; part={part}; last=1; " + sb);
            WriteTypeCensus(d, stamp);
#endif
        }

#if DEEPLOG
        static void WriteTypeCensus(Lycoreia.DiagnosticsWriter d, string stamp)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var f in _facts)
            {
                string name = (f.TypeName ?? "?").Replace(';', '_').Replace(',', '_');
                string key = f.TypeId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + name;
                counts.TryGetValue(key, out int count); counts[key] = count + 1;
            }
            var rows = new List<string>(counts.Count);
            foreach (var pair in counts) rows.Add(pair.Key + "=" + pair.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            rows.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder(); int part = 0;
            foreach (var row in rows)
            {
                if (sb.Length + row.Length + 1 > 3400)
                {
                    d.Record("actors", "census-types", $"t={stamp}; part={part++}; " + sb); sb.Clear();
                }
                if (sb.Length > 0) sb.Append(';'); sb.Append(row);
            }
            d.Record("actors", "census-types", $"t={stamp}; part={part}; last=1; " + sb);
            // issue3/issue4: players rarely send diagnostics.jsonl; the per-type growth goes to latest.log once a minute
            var byName = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var f in _facts) { string n = f.TypeName ?? "?"; byName.TryGetValue(n, out int c); byName[n] = c + 1; }
            double now = Time.unscaledTimeAsDouble;
            if (now >= _nextTypeLog)
            {
                _nextTypeLog = now + 60;
                Log.Info($"[census] actors={_facts.Count} table={Mod.Actors?.Table.Count ?? 0}; " + TypeGrowth.Describe(byName, _lastTypeCounts, 10));
                _lastTypeCounts = byName;
            }
        }
        static double _nextTypeLog; static Dictionary<string, int> _lastTypeCounts;
#endif
    }

    /// <summary>Applies the owner/non-owner body mode to an actor that just became live (region load) or drifted.</summary>
    public static class ReplicaBodies
    {
        public static void Apply(long id, ActorEntry e, bool locallyOwned)
        {
            try
            {
                var go = GameActorWorld.ObjectOf(id); if (go == null) return;
                var body = go.GetComponent<Rigidbody>(); if (body != null) BodyMode.Set(go, body, !locallyOwned);
                if (!locallyOwned)
                {
                    go.transform.SetPositionAndRotation(new Vector3(e.Position.X, e.Position.Y, e.Position.Z), new Quaternion(e.Rotation.X, e.Rotation.Y, e.Rotation.Z, e.Rotation.W));
                    ActorRuntime.Motion.Teleport(id, e.Position, e.Rotation);
                }
            }
            catch { }
        }
    }
}

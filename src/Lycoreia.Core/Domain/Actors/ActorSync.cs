// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Lycoreia.Domain;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.Owned;
using Lycoreia.Snapshot;

namespace Lycoreia.Actors
{
    /// <summary>The game side of actor sync. Hooks and the real world implement it; tests use a fake.</summary>
    public interface IActorWorld : ISpawnWorld
    {
        bool TypeKnown(int typeId);
        bool CreateReplica(ActorEntry e);                  // model plus object (model only when the region is not loaded)
        void DestroyReplica(long actorId);                 // internal removal: must never announce
        void ApplyPose(ActorEntry e);
        void ApplyState(ActorState s);
        void ApplyLocalOwnership(ActorEntry e, bool locallyOwned);
        bool IsHibernating(long actorId);
        bool IsProtectedRanchSlime(long actorId);
        CaptureFacts GetCaptureFacts(long actorId, string playerId);
        bool RemoveForConsume(long actorId);
        bool HostHoldsActor(long actorId);
        float DistanceToActor(string playerId, long actorId, float fallback);
        float MaxVacuumDistance { get; }
    }

    /// <summary>
    /// Actor message handling on both sides plus the local-origin publishing API used by the hooks.
    /// Host: authoritative (ownership, epochs, budget, capture, destroys). Guest: replicas, ordered through the delta buffer.
    /// </summary>
    public sealed class ActorSync : IArbiterWorld
    {
        readonly Session _s; readonly SnapshotHost _snap; readonly IActorWorld _w;
        public readonly ActorTable Table = new ActorTable();
        readonly Dictionary<long, ActorState> _produceState = new Dictionary<long, ActorState>();
        public readonly HostSpawnBudget Budget; public readonly OwnershipArbiter Arbiter; public readonly CaptureHost Captures; public readonly SpawnBackoff Backoff = new SpawnBackoff();
        public ActorDeltaBuffer Buffer;                                 // guest side; null on a pure host
        readonly RateLimitedLog _rl; readonly Func<double> _now;
        public Action<long> OnGuestSpawnRefused = id => { };           // host destroyed our fresh spawn
        /// <summary>Diagnostics sink (event, details); the game layer routes it to diagnostics.jsonl. Never throws into sync code.</summary>
        public Action<string, string> Trace = (ev, details) => { };
        void T(string ev, string details) { try { Trace(ev, details); } catch { } }

        public ActorSync(Session s, SnapshotHost snap, IActorWorld w, Func<double> nowSeconds, ActorDeltaBuffer buffer = null)
        {
            _s = s; _snap = snap; _w = w; _now = nowSeconds; Buffer = buffer; _rl = new RateLimitedLog(s.Node.Clock, 30_000);
            Budget = new HostSpawnBudget(w, nowSeconds); Arbiter = new OwnershipArbiter(this); Captures = new CaptureHost(nowSeconds, s.Node.Clock);
            if (Buffer != null) Buffer.ActorKnown = id => Table.TryGet(id, out _);
            var n = s.Node;
            n.On<ActorSpawn>(Side.Host, HostSpawn); n.On<ActorSpawn>(Side.Guest, (c, m) => Guest(ActorDeltaKind.ActorSpawn, m.ActorId, m.Rev, m, null, GuestSpawn));
            n.On<ActorDestroy>(Side.Host, HostDestroy); n.On<ActorDestroy>(Side.Guest, (c, m) => Guest(ActorDeltaKind.ActorDestroy, m.ActorId, m.Rev, m, null, GuestDestroy));
            n.On<ActorUpdate>(Side.Host, HostUpdate); n.On<ActorUpdate>(Side.Guest, (c, m) => Guest(ActorDeltaKind.ActorUpdate, m.ActorId, m.Rev, m, null, GuestUpdate));
            n.On<ActorState>(Side.Host, HostState); n.On<ActorState>(Side.Guest, (c, m) => Guest(ActorDeltaKind.ActorState, m.ActorId, m.Rev, m, null, GuestState));
            n.On<ActorTransfer>(Side.Host, HostTransfer); n.On<ActorTransfer>(Side.Guest, (c, m) =>
            {
                // An idempotent consume acknowledgement must reach the request
                // machine even when its world revision is already a tombstone.
                if (m.CaptureCommit && m.CaptureApproved && m.ConsumesActor) OnCaptureReply?.Invoke(m);
                Guest(ActorDeltaKind.ActorTransfer, m.ActorId, m.Rev, m, m.NewOwner, GuestTransfer);
            });
            n.On<ActorUnload>(Side.Host, HostUnload); n.On<ActorUnload>(Side.Guest, (c, m) => Guest(ActorDeltaKind.ActorUnload, m.ActorId, m.Rev, m, null, GuestUnload));
            n.On<CaptureAbort>(Side.Host, HostCaptureAbort);
            s.ClientRemoved += OnClientRemoved;
            s.ServerStarted += Reset; s.ClientDisconnected += _ => Reset();
        }

        public void Reset() { Arbiter.Reset(); Budget.Reset(); Captures.Reset(); Backoff.Reset(); Table.ClearDeferred(); _produceState.Clear(); }

        string Me => _s.LocalId;
        Revision Stamp() => new Revision { Has = true, Value = _snap.NextWorldRevision() };

        // =================================================================== host side
        Verdict HostSpawn(Ctx c, ActorSpawn m)
        {
            string sender = c.SenderId;
            var kind = m.Kind;
            if (!ActorSpawnPublicationPolicy.CanClientPublish(m.ActorId, sender, true)) { LogReject("spawn outside the sender's id block", m.ActorId, sender); T("spawn-rejected", $"id={m.ActorId}; type={m.TypeId}; from={sender}; reason=id-block"); if (!Table.TryGet(m.ActorId, out _)) SendDestroyTo(c.Sender, m.ActorId, false); return Verdict.Reject; }   // the sender drops its orphan (never when the id names a shared actor)
            if (!_w.TypeKnown(m.TypeId)) { LogReject("spawn of an unknown type", m.ActorId, sender); T("spawn-rejected", $"id={m.ActorId}; type={m.TypeId}; from={sender}; reason=unknown-type"); return Verdict.Reject; }
            if (Table.IsTombstoned(m.ActorId) && !Table.TryGet(m.ActorId, out _)) { LogReject("spawn of a destroyed id", m.ActorId, sender); T("spawn-rejected", $"id={m.ActorId}; type={m.TypeId}; from={sender}; reason=tombstoned"); SendDestroyTo(c.Sender, m.ActorId, false); return Verdict.Reject; }   // a consumed/destroyed actor never comes back; the sender drops its ghost
            if (kind == SpawnKind.Gadget)
            {
                if (Table.TryGet(m.ActorId, out var held) && held.TypeId == m.TypeId) return Verdict.Reject;   // the host really holds it: keep it
                SendDestroyTo(c.Sender, m.ActorId, gadget: true);                                                // tell the origin to remove its orphan gadget model
                return Verdict.Reject;
            }
            var d = Budget.Check(sender, m.TypeId, m.Position, kind == SpawnKind.VacShot, kind == SpawnKind.Ranch);
            if (d != SpawnDecision.Allow) { T("spawn-rejected", $"id={m.ActorId}; type={m.TypeId}; from={sender}; reason={d}"); SendDestroyTo(c.Sender, m.ActorId, false); return Verdict.Reject; }  // the refused sender destroys the actor
            var e = EntryFrom(m); e.Owner = sender; e.Epoch = 0;
            if (Table.TryGet(m.ActorId, out var reused)) { T("id-reused", $"id={m.ActorId}; old-owner={reused.Owner}; old-type={reused.TypeId}; new-type={m.TypeId}; from={sender}"); _w.DestroyReplica(m.ActorId); }   // id reuse: clear the old replica and epoch history
            Table.Add(e);
            if (!_w.CreateReplica(e)) { RemoveFromTable(m.ActorId); return Verdict.Reject; }
            _w.ApplyLocalOwnership(e, false);
            m.Rev = Stamp(); e.LastRevision = m.Rev.Value; e.LastOwnerHeardAt = _now();
            return Verdict.Relay;
        }

        void SendDestroyTo(Peer p, long id, bool gadget) => _s.Node.SendTo(p, new ActorDestroy { ActorId = id, OwnerToken = "", Rev = new Revision() });

        Verdict HostDestroy(Ctx c, ActorDestroy m)
        {
            string sender = c.SenderId;
            if (!Table.TryGet(m.ActorId, out var e)) return Verdict.Reject;                                    // unknown ids are never relayed
            if (!e.IsGadget)
            {
                if (e.Owner != sender) { RateLog($"destroy-rejected:{sender}", $"destroy of {m.ActorId} rejected: sender {sender} is not the owner"); T("destroy-rejected", $"id={m.ActorId}; from={sender}; owner={e.Owner}"); return Verdict.Reject; }
                if (!string.IsNullOrEmpty(m.OwnerToken) && m.OwnerToken != sender) return Verdict.Reject;
                if (_snap.DestroyGuardActive(sender) && _w.IsProtectedRanchSlime(m.ActorId)) { RateLog("destroy-guard", $"destroy of protected ranch slime {m.ActorId} refused during resync guard"); return Verdict.Reject; }
            }
            // any connected player may pick up (destroy) a placed gadget
            m.Rev = Stamp(); RemoveFromTable(m.ActorId, m.Rev.Value); _w.DestroyReplica(m.ActorId);
            return Verdict.Relay;
        }

        Verdict HostUpdate(Ctx c, ActorUpdate m)
        {
            string sender = c.SenderId;
            if (!Table.TryGet(m.ActorId, out var e)) return Verdict.Reject;
            bool owner = e.Owner == sender;
            var reservation = Captures.ReservationFor(e.Id);
            if (reservation != null && reservation.Player != sender && owner) return Verdict.Reject;
            if (!owner && !PullAccepted(sender, e, m)) { RateLog($"update-rejected:{sender}", $"update of {m.ActorId} rejected: not from the owner"); return Verdict.Reject; }
            if (owner && !OwnershipEpochPolicy.ShouldAcceptUpdate(m.Epoch > 0, m.Epoch, e.Epoch)) return Verdict.Reject;
            bool heldBefore = e.HeldByOwner; Vec3 positionBefore = e.Position;
            if (owner) { e.LastOwnerHeardAt = _now(); e.ReleasedByOwner = false; e.HeldByOwner = m.Held; } else m.Held = false;
            Apply(e, m); m.Rev = Stamp();
            if (owner && heldBefore != e.HeldByOwner) T("held-state", $"id={e.Id}; type={e.TypeId}; owner={sender}; held={e.HeldByOwner}; from={positionBefore}; to={e.Position}");
            return Verdict.Relay;
        }

        /// <summary>Approved consume pull: a guest streams samples of an actor the host (or another peer) still owns.</summary>
        bool PullAccepted(string sender, ActorEntry e, ActorUpdate m)
        {
            var r = Captures.ReservationFor(e.Id);
            if (r == null || r.Player != sender || r.Committed || r.ExpectedOwner != (e.Owner ?? "")) return false;
            if (m.Epoch != e.Epoch) return false;
            if (!Finite(m.Position) || !Finite(m.Rotation) || !Finite(m.Velocity)) return false;
            if (m.Rotation.X * m.Rotation.X + m.Rotation.Y * m.Rotation.Y + m.Rotation.Z * m.Rotation.Z + m.Rotation.W * m.Rotation.W < 0.25f) return false;
            float range = ActorCaptureTransferPolicy.GetMaximumVacuumAcquisitionDistance(_w.MaxVacuumDistance);
            return float.IsFinite(range) && _w.DistanceToActor(sender, e.Id, float.PositiveInfinity) <= range &&
                _w.TryPlayerPosition(sender, out var player) && HostSpawnBudget.Dist(player, m.Position) <= range;
        }
        static bool Finite(Vec3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
        static bool Finite(Quat q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);

        void Apply(ActorEntry e, ActorUpdate m) { e.Position = m.Position; e.Rotation = m.Rotation; e.Velocity = m.Velocity; _w.ApplyPose(e); }

        Verdict HostState(Ctx c, ActorState m)
        {
            if (!Table.TryGet(m.ActorId, out var e) || e.Owner != c.SenderId) return Verdict.Reject;
            if (m.Kind == ActorStateKind.Produce) { e.Attached = m.Attached; _produceState[m.ActorId] = _s.Node.Clone(m); }
            _w.ApplyState(m); m.Rev = Stamp(); return Verdict.Relay;
        }

        Verdict HostUnload(Ctx c, ActorUnload m)
        {
            string sender = c.SenderId;
            if (!Table.TryGet(m.ActorId, out var e) || e.Owner != sender) return Verdict.Reject;       // sender always from the endpoint, never the payload
            m.SenderId = sender; e.ReleasedByOwner = true; e.HeldByOwner = false; Arbiter.EvaluateNow(m.ActorId, (float)_now());
            m.Rev = Stamp(); return Verdict.Relay;
        }

        Verdict HostTransfer(Ctx c, ActorTransfer m)
        {
            string sender = c.SenderId;
            if (m.Interest) { if (Table.TryGet(m.ActorId, out _)) Arbiter.RegisterInterest(sender, m.ActorId, m.InterestDistance, (float)_now()); return Verdict.Reject; }   // a claim only; no reply, never relayed
            if (m.CaptureCommit) { Table.TryGet(m.ActorId, out var target); return HostCaptureCommit(c, m, target); }
            if (!Table.TryGet(m.ActorId, out var e)) return Verdict.Reject;
            if (m.CaptureIntent) return HostCapturePrepare(c, m, e);
            // plain transfer: compare-and-swap on the expected previous owner
            string cur = e.Owner ?? "";
            if (sender != _s.LocalId && m.NewOwner != sender) { LogReject("transfer to someone else by a guest", m.ActorId, sender); return Verdict.Reject; }
            if (m.HasExpected)
            {
                if ((m.ExpectedPreviousOwner ?? "") != cur) { RepairTo(c.Sender, e); return Verdict.Reject; }                  // raced a newer transfer: repair reply to the sender alone
            }
            else if (cur.Length > 0 && _s.Node.FindByPlayer(cur) != null) { RepairTo(c.Sender, e); return Verdict.Reject; }   // legacy: only if unowned or the owner disconnected
            GrantAndBroadcast(e, m.NewOwner, null);
            return Verdict.Reject;                                                                                             // already broadcast with the canonical epoch
        }

        Verdict HostCapturePrepare(Ctx c, ActorTransfer m, ActorEntry e)
        {
            if (m.CaptureRequestId == Guid.Empty) return Verdict.Reject;
            string sender = c.SenderId;
            // nobody simulates the actor (unowned, owner gone or released, or the host's own copy asleep): hand it to the requester at
            // once, so its own vacuum pulls a locally owned actor. A full capture would need a live host copy and was denied while the
            // host was elsewhere (the guest then gave up for 30 s).
            // Produce still on its plant is the same: the requester's own game must detach it from its joint, which a frozen replica
            // never does (2026-11-02 run: approved crops half popped out and snapped back, vacuumable only point blank).
            bool handOver = OwnerFree(e) || (e.Attached && !e.HeldByOwner);
            if (m.HasExpected && (m.ExpectedPreviousOwner ?? "") == (e.Owner ?? "") && handOver && !Captures.HasReservation(m.ActorId))
            {
                float range = ActorCaptureTransferPolicy.GetMaximumVacuumAcquisitionDistance(_w.MaxVacuumDistance);
                float d = _w.DistanceToActor(sender, m.ActorId, float.PositiveInfinity);
                if (float.IsFinite(range) && d <= range)
                {
                    T("capture-free-grant", $"id={m.ActorId}; by={sender}; owner={e.Owner}; dist={d:0.0}");
                    if (!Captures.ReserveGranted(sender, m, sender)) { _s.Node.SendTo(c.Sender, ApprovalReply(m, false)); return Verdict.Reject; }
                    GrantAndBroadcast(e, sender, null);
                    _s.Node.SendTo(c.Sender, ApprovalReply(m, true)); return Verdict.Reject;
                }
            }
            var facts = _w.GetCaptureFacts(m.ActorId, sender);
            if (e.Owner == sender && !facts.Live)
            {
                // The authoritative owner has the live copy; the host may only
                // retain its model/table while that region is asleep.
                facts.Live = facts.NativeReadable = facts.HasVacuumable = facts.CanCapture = true;
                facts.Captive = false; facts.ActorScene = facts.PlayerScene = e.Scene;
                float fallback = _w.TryPlayerPosition(sender, out var player) ? HostSpawnBudget.Dist(player, e.Position) : float.PositiveInfinity;
                facts.PlayerToActor = _w.DistanceToActor(sender, e.Id, fallback);
            }
            if (e.Owner == sender && !_w.HostHoldsActor(e.Id)) facts.Captive = false;
            if (e.HeldByOwner && e.Owner != sender && ((IArbiterWorld)this).IsConnected(e.Owner)) facts.Captive = true;   // in another player's vacuum: never pulled out of it
            var o = Captures.Prepare(sender, m, e.Owner, facts, out var reason);
            T("capture-prepare", $"id={m.ActorId}; by={sender}; owner={e.Owner}; consumes={m.ConsumesActor}; outcome={o}; reason={reason}; live={facts.Live}; captive={facts.Captive}; dist={facts.PlayerToActor:0.0}; max={facts.MaxVacuumDistance:0.0}");
            if (o == CaptureOutcome.Approved || o == CaptureOutcome.Replayed) _s.Node.SendTo(c.Sender, ApprovalReply(m, true));
            else { _s.Node.SendTo(c.Sender, ApprovalReply(m, false)); RateLog($"capture-reject:{reason}", $"capture of {m.ActorId} by {sender} rejected: {reason}"); }
            return Verdict.Reject;
        }

        /// <summary>No connected peer simulates the actor: unowned, owner disconnected or released, or the host's own copy is asleep.</summary>
        bool OwnerFree(ActorEntry e) => string.IsNullOrEmpty(e.Owner) || !IsPlayerConnected(e.Owner) || e.ReleasedByOwner || (e.Owner == Me && _w.IsHibernating(e.Id));

        static ActorTransfer ApprovalReply(ActorTransfer m, bool approved) => new ActorTransfer
        {
            ActorId = m.ActorId, CaptureIntent = true, CaptureApproved = approved, CaptureRequestId = m.CaptureRequestId, ConsumesActor = m.ConsumesActor,
            NewOwner = m.NewOwner, ExpectedPreviousOwner = m.ExpectedPreviousOwner, HasExpected = m.HasExpected
        };

        Verdict HostCaptureCommit(Ctx c, ActorTransfer m, ActorEntry e)
        {
            string sender = c.SenderId;
            bool eligible = e != null;
            var o = Captures.Commit(sender, m, e?.Owner ?? "", eligible, out var res, out bool consumes, out var replay);
            T("capture-commit", $"id={m.ActorId}; by={sender}; owner={e?.Owner}; consumes={m.ConsumesActor}; outcome={o}; eligible={eligible}");
            if (o == CaptureOutcome.Replayed)
            {
                _s.Node.SendTo(c.Sender, new ActorTransfer { ActorId = m.ActorId, CaptureCommit = true, CaptureApproved = true, CaptureRequestId = m.CaptureRequestId, ConsumesActor = replay.consumed, NewOwner = sender, Epoch = replay.epoch, Rev = new Revision { Has = true, Value = replay.rev } });
                return Verdict.Reject;
            }
            if (o != CaptureOutcome.Committed) { _s.Node.SendTo(c.Sender, new ActorTransfer { ActorId = m.ActorId, CaptureCommit = true, CaptureApproved = false, CaptureRequestId = m.CaptureRequestId }); return Verdict.Reject; }
            if (consumes)
            {
                // remove that exact live actor and its model first, then confirm; on failure restore ownership and reject
                if (!_w.RemoveForConsume(m.ActorId)) { Captures.Release(m.ActorId); _s.Node.SendTo(c.Sender, new ActorTransfer { ActorId = m.ActorId, CaptureCommit = true, CaptureApproved = false, CaptureRequestId = m.CaptureRequestId }); return Verdict.Reject; }
                uint ep = Table.HostGrant(m.ActorId, sender); long rev = _snap.NextWorldRevision();
                RemoveFromTable(m.ActorId, rev);
                Captures.RememberCommit(sender, m, rev, ep, true);
                _s.Node.SendToAll(new ActorTransfer { ActorId = m.ActorId, CaptureCommit = true, CaptureApproved = true, CaptureRequestId = m.CaptureRequestId, ConsumesActor = true, NewOwner = sender, Epoch = ep, Rev = new Revision { Has = true, Value = rev } });
                return Verdict.Reject;
            }
            uint epoch = Table.HostGrant(m.ActorId, sender); e.HeldByOwner = true; long revision = _snap.NextWorldRevision(); _w.ApplyLocalOwnership(e, sender == Me);
            Captures.RememberCommit(sender, m, revision, epoch, false); Arbiter.NoteGrant(m.ActorId, (float)_now());
            _s.Node.SendToAll(new ActorTransfer { ActorId = m.ActorId, CaptureCommit = true, CaptureApproved = true, CaptureRequestId = m.CaptureRequestId, ConsumesActor = false, NewOwner = sender, Epoch = epoch, Rev = new Revision { Has = true, Value = revision } });
            return Verdict.Reject;
        }

        void HostCaptureAbort(Ctx c, CaptureAbort m)
        {
            var o = Captures.Abort(c.SenderId, m);
            if (o == CaptureOutcome.AbortRefused && Captures.ReservationFor(m.ActorId) != null) RateLog("abort-refused", $"capture abort for {m.ActorId} by {c.SenderId} refused");
        }

        /// <summary>Host decision to hand an actor over: bumps the epoch, applies locally, broadcasts the canonical result.</summary>
        public void GrantAndBroadcast(ActorEntry e, string newOwner, string previousOwnerHint)
        {
            string prev = e.Owner; bool released = e.ReleasedByOwner;
            var world = (IArbiterWorld)this;
            float fromDistance = string.IsNullOrEmpty(prev) ? float.PositiveInfinity : world.DistanceToActor(prev, e.Id, float.PositiveInfinity);
            float toDistance = world.DistanceToActor(newOwner, e.Id, float.PositiveInfinity);
            uint epoch = Table.HostGrant(e.Id, newOwner); long rev = _snap.NextWorldRevision();
            T("grant", $"id={e.Id}; type={e.TypeId}; from={prev}; to={newOwner}; epoch={epoch}; released={released}; from-dist={fromDistance:0.0}; to-dist={toDistance:0.0}");
            e.LastRevision = rev; _w.ApplyLocalOwnership(e, newOwner == Me); Arbiter.NoteGrant(e.Id, (float)_now());
            _s.Node.SendToAll(new ActorTransfer { ActorId = e.Id, NewOwner = newOwner, ExpectedPreviousOwner = prev ?? "", HasExpected = true, Epoch = epoch, Rev = new Revision { Has = true, Value = rev } });
        }

        void RepairTo(Peer p, ActorEntry e) => _s.Node.SendTo(p, new ActorTransfer { ActorId = e.Id, NewOwner = e.Owner, ExpectedPreviousOwner = e.Owner, HasExpected = true, Epoch = e.Epoch, Rev = new Revision { Has = e.LastRevision > 0, Value = e.LastRevision } });

        void OnClientRemoved(Peer p)
        {
            // reassign unowned actors of the leaving player: owner cleared so the arbiter hands them to the nearest peer
            int n = 0; foreach (var e in Table.All.Where(a => a.Owner == p.PlayerId).ToList()) { e.Owner = ""; e.ReleasedByOwner = true; n++; }
            T("owner-left", $"player={p.PlayerId}; released={n}");
        }

        // =================================================================== guest side
        void Guest<T>(ActorDeltaKind kind, long id, Revision rev, T m, string transferOwner, Func<T, bool> apply) where T : Msg
        {
            if (Buffer == null) { apply(m); return; }
            var copy = _s.Node.Clone(m);                                      // the received message is recycled after this handler returns
            if (!Buffer.TryDefer(kind, id, rev.Has, rev.Value, () => apply(copy), transferOwner)) Apply(kind, id, rev, copy, transferOwner, apply);
        }
        void Apply<T>(ActorDeltaKind kind, long id, Revision rev, T copy, string transferOwner, Func<T, bool> apply) where T : Msg
        {
            if (rev.Has && !Buffer.ShouldApply(kind, id, rev.Value)) return;
            if (apply(copy) && rev.Has) Buffer.RecordApplied(kind, id, rev.Value, transferOwner);
        }

        bool GuestSpawn(ActorSpawn m)
        {
            if (!_w.TypeKnown(m.TypeId)) { Log.Warn($"spawn of unknown type {m.TypeId} skipped"); T("spawn-skipped", $"id={m.ActorId}; type={m.TypeId}; reason=unknown-type"); return false; }
            if (Buffer != null && m.Rev.Has && Table.WasDestroyed(m.ActorId, m.Rev.Value)) return false;
            var e = EntryFrom(m);
            T("spawn-received", $"id={e.Id}; type={e.TypeId}; kind={e.Kind}; owner={e.Owner}; scene={e.Scene}; rev={m.Rev.Value}");
            if (Buffer != null && m.Rev.Has) e.Owner = Buffer.OwnerForSpawn(m.ActorId, m.Rev.Value, m.OwnerId);    // a newer transfer may already be known
            if (Table.TryGet(m.ActorId, out var reused)) { T("id-reused", $"id={m.ActorId}; old-owner={reused.Owner}; old-type={reused.TypeId}; new-type={m.TypeId}"); _w.DestroyReplica(m.ActorId); }
            Table.Add(e); e.LastRevision = m.Rev.Value;
            if (!_w.CreateReplica(e)) { RemoveFromTable(m.ActorId); T("spawn-skipped", $"id={m.ActorId}; type={m.TypeId}; reason=replica-failed"); return false; }
            _w.ApplyLocalOwnership(e, e.Owner == Me);
            if (Table.TryResolveDeferred(m.ActorId, e.Owner, out var destroyedAt)) { RemoveFromTable(m.ActorId, destroyedAt); _w.DestroyReplica(m.ActorId); }
            return true;
        }

        bool GuestDestroy(ActorDestroy m)
        {
            if (!Table.TryGet(m.ActorId, out var e)) { if (m.Rev.Has) RemoveFromTable(m.ActorId, m.Rev.Value); return true; }
            if (!e.IsGadget && ActorDestroyOrderingPolicy.ShouldDefer(m.OwnerToken, e.Owner)) { Table.Defer(m.ActorId, m.OwnerToken, m.Rev.Value); return false; }   // owner token ahead of what we know
            T("destroy-received", $"id={m.ActorId}; owner={e.Owner}; token={m.OwnerToken}; rev={m.Rev.Value}");
            RemoveFromTable(m.ActorId, m.Rev.Value); _w.DestroyReplica(m.ActorId);
            if (!m.Rev.Has) T("spawn-refused", $"id={m.ActorId}; owner={e.Owner}");                  // a direct (unstamped) destroy is the host refusing our spawn
            OnGuestSpawnRefused(m.ActorId);                                  // the spawn budget treats a destroy of our own fresh spawn as a host refusal
            Backoff.OnHostRefusal(m.ActorId, _now());
            return true;
        }

        bool GuestUpdate(ActorUpdate m)
        {
            if (!Table.TryGet(m.ActorId, out var e)) return false;
            if (e.Owner == Me) return false;                                 // we simulate it; owner echoes are ignored
            if (!OwnershipEpochPolicy.ShouldAcceptUpdate(m.Epoch > 0, m.Epoch, e.Epoch)) return false;
            bool heldBefore = e.HeldByOwner; Vec3 positionBefore = e.Position;
            e.LastOwnerHeardAt = _now(); e.ReleasedByOwner = false; e.HeldByOwner = m.Held; Apply(e, m);
            if (heldBefore != e.HeldByOwner) T("held-state", $"id={e.Id}; type={e.TypeId}; owner={e.Owner}; held={e.HeldByOwner}; from={positionBefore}; to={e.Position}");
            return true;
        }

        bool GuestState(ActorState m) { if (Table.TryGet(m.ActorId, out var e) && e.Owner != Me) { if (m.Kind == ActorStateKind.Produce) { e.Attached = m.Attached; _produceState[m.ActorId] = _s.Node.Clone(m); } _w.ApplyState(m); return true; } return false; }

        bool GuestTransfer(ActorTransfer m)
        {
            if (!Table.TryGet(m.ActorId, out var e)) return false;
            if (m.CaptureIntent && !m.CaptureCommit) { OnCaptureReply?.Invoke(m); return false; }          // prepare reply: approved or rejected
            if (m.CaptureCommit && !m.CaptureApproved) { OnCaptureReply?.Invoke(m); return false; }
            if (m.CaptureCommit && m.ConsumesActor && m.CaptureApproved) { RemoveFromTable(m.ActorId, m.Rev.Value); _w.DestroyReplica(m.ActorId); return true; }
            var d = Table.ApplyOwner(m.ActorId, m.NewOwner, m.Epoch);
            if (d != OwnershipEpochDecision.Apply)
            {
                if (m.CaptureCommit && m.CaptureApproved && e.Owner == m.NewOwner && e.Epoch == m.Epoch)
                { e.HeldByOwner = true; OnCaptureReply?.Invoke(m); }
                return false;
            }
            e.LastRevision = Math.Max(e.LastRevision, m.Rev.Value); _w.ApplyLocalOwnership(e, e.Owner == Me);
            if (Table.TryResolveDeferred(m.ActorId, e.Owner, out var destroyedAt)) { RemoveFromTable(m.ActorId, destroyedAt); _w.DestroyReplica(m.ActorId); }
            if (m.CaptureCommit) OnCaptureReply?.Invoke(m);
            return true;
        }
        public event Action<ActorTransfer> OnCaptureReply;

        bool GuestUnload(ActorUnload m)
        {
            if (!Table.TryGet(m.ActorId, out var e) || e.Owner != m.SenderId) return false;
            e.ReleasedByOwner = true; e.HeldByOwner = false;
            if (_w.IsHibernating(m.ActorId)) { _w.ApplyLocalOwnership(e, false); }
            // awake: record the release and express interest (no direct claim)
            else SendInterest(e, float.PositiveInfinity);
            return true;
        }

        /// <summary>Guest to host (or host locally): interest claim carrying the sender's distance.</summary>
        public void SendInterest(ActorEntry e, float distance)
        {
            if (_s.IsHost) Arbiter.RegisterInterest(Me, e.Id, distance, (float)_now());
            else if (_s.IsGuest) _s.Node.SendToHost(new ActorTransfer { ActorId = e.Id, Interest = true, InterestDistance = distance, NewOwner = Me });
        }

        // =================================================================== local-origin publishing (called by hooks)
        /// <summary>A locally created actor: mark owned, record, broadcast. A guest's spawn goes to the host which relays it.</summary>
        public bool PublishSpawn(ActorEntry e)
        {
            if (!_s.IsHost && !_s.Connected) return false;
            if (Applying.Active) return false;                               // created because of a received message
            if (!GuestMayPublish(e.Id)) { T("spawn-unpublishable", $"id={e.Id}; type={e.TypeId}"); return false; }   // outside our id block: the host would refuse it
            e.Owner = Me; e.Epoch = 0; Table.Add(e);
            var m = SpawnOf(e);
            T("spawn-published", $"id={e.Id}; type={e.TypeId}; kind={e.Kind}; owner={Me}; scene={e.Scene}; pos={e.Position}");
            if (_s.IsHost) { m.Rev = Stamp(); e.LastRevision = m.Rev.Value; _s.Node.SendToAll(m); }
            else { Backoff.NoteLocalSpawn(e.Id, e.Position, _now(), wild: e.Kind == SpawnKind.Normal); _s.Node.SendToHost(m); }
            return true;
        }

        /// <summary>
        /// A guest may only publish ids inside the block the host assigned it (ids from its private save allocator would be refused and
        /// the actor would exist on that guest alone). The host may publish any id.
        /// </summary>
        public bool GuestMayPublish(long id) => !_s.IsGuest || ActorSpawnPublicationPolicy.CanClientPublish(id, Me, true);

        /// <summary>
        /// Host load rule: registers an actor that exists in the host's game but was never announced (saved actors at server start,
        /// actors the game registered without the spawn hook). Owned by the host when its object is live, otherwise unowned so the
        /// arbiter hands it to whoever is near when its region loads. Broadcast to connected guests; joiners get it in the snapshot.
        /// </summary>
        public bool HostRegisterExisting(ActorEntry e, bool live)
        {
            if (!_s.IsHost || e == null || e.Id <= 0 || e.IsGadget || Table.TryGet(e.Id, out _) || Table.IsTombstoned(e.Id)) return false;
            e.Owner = live ? Me : ""; e.Epoch = 0; e.ReleasedByOwner = false; Table.Add(e);
            var m = SpawnOf(e); m.Rev = Stamp(); e.LastRevision = m.Rev.Value; e.LastOwnerHeardAt = _now();
            _s.Node.SendToAll(m);
            return true;
        }

        static ActorSpawn SpawnOf(ActorEntry e) => new ActorSpawn { ActorId = e.Id, Position = e.Position, Rotation = e.Rotation, TypeId = e.TypeId, SceneGroup = e.Scene, Kind = e.Kind, OwnerId = e.Owner ?? "", HasSlime = e.HasSlime, Slime = e.Slime, HasMaterial = e.HasMaterial, MaterialIndex = e.MaterialIndex, HasCharge = e.HasCharge, ChargeTime = e.ChargeTime };

        /// <summary>A gameplay destroy by the owner: broadcast once with the owner token. Non-owners never destroy.</summary>
        public bool PublishDestroy(long actorId)
        {
            if (!Table.TryGet(actorId, out var e) || Applying.Active) return false;
            if (!e.IsGadget && e.Owner != Me) return false;
            var m = new ActorDestroy { ActorId = actorId, OwnerToken = e.IsGadget ? "" : Me };
            if (_s.IsHost) { m.Rev = Stamp(); RemoveFromTable(actorId, m.Rev.Value); _s.Node.SendToAll(m); } else { RemoveFromTable(actorId); _s.Node.SendToHost(m); }
            return true;
        }

        /// <summary>Owner transform sample; sent when changed, else every 10th tick as keep-alive staggered by id.</summary>
        public static bool ShouldSendTransform(bool changed, long tick, long actorId) => changed || (tick + (actorId % 10)) % 10 == 0;

        public void SendOwnedUpdate(ActorEntry e)
        {
            var m = new ActorUpdate { ActorId = e.Id, Position = e.Position, Rotation = e.Rotation, Velocity = e.Velocity, Epoch = e.Epoch, Held = e.HeldByOwner };
            if (_s.IsHost) { m.Rev = Stamp(); _s.Node.SendToAll(m, Delivery.Unreliable); } else _s.Node.SendToHost(m, Delivery.Unreliable);
        }

        /// <summary>Owner state (emotions, produce cycle, plort flags); also force-sent about every 15 s by the runtime.</summary>
        public bool SendOwnedState(ActorState m)
        {
            if (!Table.TryGet(m.ActorId, out var e) || e.Owner != Me || Applying.Active) return false;
            if (m.Kind == ActorStateKind.Produce) { e.Attached = m.Attached; _produceState[m.ActorId] = _s.Node.Clone(m); }
            if (_s.IsHost) { m.Rev = Stamp(); _s.Node.SendToAll(m); } else _s.Node.SendToHost(m);
            return true;
        }

        /// <summary>The owner's region hibernated or unloaded the actor: tell the others it is released (they express interest).</summary>
        public bool SendUnload(long actorId)
        {
            if (!Table.TryGet(actorId, out var e) || e.Owner != Me) return false;
            e.ReleasedByOwner = true; e.HeldByOwner = false; T("unload", $"id={actorId}");
            var m = new ActorUnload { ActorId = actorId, SenderId = Me };
            if (_s.IsHost) { m.Rev = Stamp(); _s.Node.SendToAll(m); Arbiter.EvaluateNow(actorId, (float)_now()); } else _s.Node.SendToHost(m);
            return true;
        }

        /// <summary>Host-only removal for /cleanup: a destroy for everyone that bypasses owner checks (the owner token is the current owner, so nobody defers it).</summary>
        public bool HostForceDestroy(long actorId)
        {
            if (!_s.IsHost || !Table.TryGet(actorId, out var e)) return false;
            var m = new ActorDestroy { ActorId = actorId, OwnerToken = e.IsGadget ? "" : e.Owner ?? "", Rev = Stamp() };
            RemoveFromTable(actorId, m.Rev.Value); _w.DestroyReplica(actorId); _s.Node.SendToAll(m);
            return true;
        }

        /// <summary>Re-applies the local body mode after the game layer re-created a lost replica.</summary>
        public void ApplyOwnershipAgain(ActorEntry e)
        {
            _w.ApplyLocalOwnership(e, e.Owner == Me);
            ReplayProduceState(e.Id);
        }
        ActorEntry RemoveFromTable(long id, long revision = 0)
        {
            _produceState.Remove(id);
            return Table.Remove(id, revision);
        }
        public void ReplayProduceState(long id)
        {
            if (Table.TryGet(id, out _) && _produceState.TryGetValue(id, out var state)) _w.ApplyState(state);
        }

        /// <summary>Capture protocol sends of the guest capture client.</summary>
        public void SendCapturePull(long actorId, Vec3 position, Quat rotation, Vec3 velocity)
        {
            if (!_s.IsGuest || !Table.TryGet(actorId, out var e) || e.Owner == Me) return;
            _s.Node.SendToHost(new ActorUpdate { ActorId = actorId, Epoch = e.Epoch, Position = position, Rotation = rotation, FullRotation = true, Velocity = velocity });
        }
        public void SendCapture(ActorTransfer m) { if (_s.IsGuest) _s.Node.SendToHost(m); }
        public void SendCaptureAbort(long actorId, Guid requestId) { if (_s.IsGuest) _s.Node.SendToHost(new CaptureAbort { ActorId = actorId, RequestId = requestId }); }
        public bool IsHost => _s.IsHost; public bool IsGuest => _s.IsGuest; public string LocalId => Me;
        public bool IsPlayerConnected(string id) => !string.IsNullOrEmpty(id) && (id == Me || _s.Node.FindByPlayer(id) != null);

        // =================================================================== snapshot
        public List<ActorRecord> BuildSnapshot()
        {
            var list = new List<ActorRecord>(Table.Count);
            foreach (var e in Table.All.OrderBy(a => a.Id))
                list.Add(new ActorRecord { ActorId = e.Id, Position = e.Position, Rotation = e.Rotation, TypeId = e.TypeId, SceneGroup = e.Scene, OwnerId = e.Owner ?? "", Epoch = e.Epoch, HasSlime = e.HasSlime, Slime = e.Slime, HasMaterial = e.HasMaterial, MaterialIndex = e.MaterialIndex, HasCharge = e.HasCharge, ChargeTime = e.ChargeTime });
            return list;
        }

        public void RegisterSnapshotWriter(double worldTime)
        {
            _snap.Register(SnapStep.Actors, ctx => ctx.SendActors(BuildSnapshot(), StartingIdFor(Table, ctx.Peer.PlayerId), worldTime));
        }
        public static long ActorStart(string playerId) => (long)Identity.ShortId(playerId) * Identity.ActorIdOffset;
        /// <summary>Where a joining guest's allocator starts: after every id of its block the host still holds or has tombstoned, so a rejoin never
        /// reuses a consumed id (HostSpawn refuses tombstoned ids).</summary>
        public static long StartingIdFor(ActorTable table, string playerId) { var b = ActorIdBlock.For(playerId, false); return Math.Max(b.start, table.HighestIn(b) + 1); }

        /// <summary>Guest apply: internal rebuild (never announces) from a received snapshot.</summary>
        public ApplyOutcome ApplySnapshot(ActorSnapshot snap) { ClearForSnapshot(); return SpawnFromSnapshot(snap); }

        /// <summary>Step 2 of the guest apply: clears the table and destroys existing actors (internal, never sent). Returns how many were destroyed.</summary>
        public int ClearForSnapshot()
        {
            int n = 0; foreach (var e in Table.All.ToList()) { try { _w.DestroyReplica(e.Id); n++; } catch { } }
            Table.Clear(); return n;
        }

        /// <summary>Step 4: spawns the snapshot actors with their owners and epochs.</summary>
        public ApplyOutcome SpawnFromSnapshot(ActorSnapshot snap)
        {
            bool failure = false;
            foreach (var r in snap.Actors)
            {
                if (!_w.TypeKnown(r.TypeId) || r.ActorId <= 0) continue;             // expected skip
                var e = new ActorEntry { Id = r.ActorId, Owner = r.OwnerId ?? "", Epoch = r.Epoch, TypeId = r.TypeId, Scene = r.SceneGroup, Position = r.Position, Rotation = r.Rotation, HasSlime = r.HasSlime, Slime = r.Slime, HasMaterial = r.HasMaterial, MaterialIndex = r.MaterialIndex, HasCharge = r.HasCharge, ChargeTime = r.ChargeTime };
                Table.Add(e); e.Epoch = r.Epoch;
                try { if (!_w.CreateReplica(e)) { RemoveFromTable(r.ActorId); failure = true; } else _w.ApplyLocalOwnership(e, e.Owner == Me); }
                catch (Exception ex) { Log.Warn("snapshot actor failed: " + ex.Message); failure = true; }
            }
            return failure ? ApplyOutcome.RuntimeFailure : ApplyOutcome.Success;
        }

        // =================================================================== helpers
        static ActorEntry EntryFrom(ActorSpawn m) => new ActorEntry
        {
            Id = m.ActorId, Owner = m.OwnerId ?? "", TypeId = m.TypeId, Scene = m.SceneGroup, Kind = m.Kind, IsGadget = m.Kind == SpawnKind.Gadget,
            Position = m.Position, Rotation = m.Rotation, HasSlime = m.HasSlime, Slime = m.Slime, HasMaterial = m.HasMaterial, MaterialIndex = m.MaterialIndex, HasCharge = m.HasCharge, ChargeTime = m.ChargeTime
        };
        void LogReject(string what, long id, string sender) => RateLog("reject:" + what, $"{what}: actor {id} from {sender}");
        void RateLog(string key, string msg) => _rl.Warn(key, msg);

        // ---- IArbiterWorld
        bool IArbiterWorld.ActorExists(long id) => Table.TryGet(id, out _);
        string IArbiterWorld.OwnerOf(long id) => Table.OwnerOf(id);
        bool IArbiterWorld.IsConnected(string id) => id == Me || _s.Node.FindByPlayer(id) != null;
        bool IArbiterWorld.WasReleasedByOwner(long id, string owner) => Table.TryGet(id, out var e) && e.ReleasedByOwner && e.Owner == owner;
        bool IArbiterWorld.HasCaptureReservation(long id) => Captures.HasReservation(id);
        bool IArbiterWorld.HostHoldsActor(long id) => _w.HostHoldsActor(id);
        bool IArbiterWorld.OwnerHoldsActor(long id) => Table.TryGet(id, out var e) && e.HeldByOwner;
        bool IArbiterWorld.IsPinned(long id) => Table.TryGet(id, out var e) && e.Attached && !e.ReleasedByOwner;   // only while the owner simulates its plant
        float IArbiterWorld.DistanceToActor(string p, long id, float fb) => _w.DistanceToActor(p, id, fb);
        string IArbiterWorld.HostId => Me;
        void IArbiterWorld.GrantOwnership(long id, string newOwner, string prev) { if (Table.TryGet(id, out var e)) GrantAndBroadcast(e, newOwner, prev); }
    }
}

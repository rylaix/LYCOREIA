// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using Il2CppMonomiPark.SlimeRancher.Slime;
using Il2CppMonomiPark.SlimeRancher.Slime.Dervish;
using Il2CppMonomiPark.SlimeRancher.Slime.Slumber;
using Il2CppMonomiPark.SlimeRancher.Slime.Tangle;
using Lycoreia.Game;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.Snapshot;
using UnityEngine;

namespace Lycoreia.Owned.Shared.Managers;

/// <summary>Owner-published, revisioned behavior state for the four slime behaviors with remote presentation.</summary>
internal static class NetworkSlimeBehaviorManager
{
    const int MaxCached = 4096, MaxPending = 256, MaxSnapshotEntries = 12, MaxRetries = 10;
    readonly record struct Key(SlimeBehaviorKind Kind, long ActorId);
    sealed class Pending { internal SlimeBehaviorEntry Entry; internal int Attempts; internal float At; }
    sealed class OwnerRequest { internal string Sender = ""; internal SlimeBehaviorEntry Entry; internal float QueuedAt; }
    static readonly Dictionary<Key, SlimeBehaviorEntry> Latest = new();
    static readonly Dictionary<Key, SlimeBehaviorEntry> LastPublished = new();
    static readonly Dictionary<long, string> PublishedOwner = new();
    static readonly Dictionary<Key, Pending> PendingApply = new();
    static readonly Dictionary<(Key key, string sender), OwnerRequest> PendingOwner = new();
    static readonly Queue<Key> Order = new();
    static readonly Dictionary<long, float> ActiveYawns = new();
    static readonly List<long> YawnScratch = new();
    static readonly List<Key> PendingScratch = new();
    [ThreadStatic] static bool _applyingState;
    internal static bool ApplyingState => _applyingState;

    internal static void Register()
    {
        Mod.Node.On<DervishCyclone>(Side.Host, (c, m) => HostReceive(c, m));
        Mod.Node.On<SloomberYawn>(Side.Host, (c, m) => HostReceive(c, m));
        Mod.Node.On<TangleVineGrapple>(Side.Host, (c, m) => HostReceive(c, m));
        Mod.Node.On<StalkConsumableTarget>(Side.Host, (c, m) => HostReceive(c, m));
        Mod.Node.On<DervishCyclone>(Side.Guest, (c, m) => GuestReceive(c, m));
        Mod.Node.On<SloomberYawn>(Side.Guest, (c, m) => GuestReceive(c, m));
        Mod.Node.On<TangleVineGrapple>(Side.Guest, (c, m) => GuestReceive(c, m));
        Mod.Node.On<StalkConsumableTarget>(Side.Guest, (c, m) => GuestReceive(c, m));
        Mod.Node.On<InitialSlimeBehavior>(Side.Guest, (c, m) => ApplySnapshot(m));
        Mod.Snapshots.Register(SnapStep.SlimeBehavior, SendSnapshot);
        Mod.Session.ServerStarted += Reset;
        Mod.Session.ClientJoined += Reset;
        Mod.Session.ClientDisconnected += _ => Reset();
    }

    internal static void Pump()
    {
        float now = Time.unscaledTime;
        if (!Mod.IsNetworkActive) { Reset(); return; }
        if (ActiveYawns.Count > 0)
        {
        YawnScratch.Clear(); YawnScratch.AddRange(ActiveYawns.Keys);
        foreach (var id in YawnScratch)
            if (Mod.Actors == null || !Mod.Actors.Table.TryGet(id, out _)) ActiveYawns.Remove(id);
            else if (now - ActiveYawns[id] > 30f)
            {
                ActiveYawns.Remove(id);
                var actor = ActorLink.ById(id);
                if (actor && actor.LocallyOwned) Publish(new SlimeBehaviorEntry { Kind = SlimeBehaviorKind.Sloomber, ActorId = id, Active = false });
                else if (Mod.IsHost && Mod.Actors != null && Mod.Actors.Table.TryGet(id, out _))
                {
                    var key = new Key(SlimeBehaviorKind.Sloomber, id);
                    if (!Latest.TryGetValue(key, out var current) || current.Rev < long.MaxValue)
                    {
                        var clear = new SlimeBehaviorEntry { Kind = SlimeBehaviorKind.Sloomber, ActorId = id, Active = false, ForceClear = true, Rev = NextRevision(key) };
                        Remember(key, clear); QueueApply(clear); Broadcast(clear);
                        Trace("behavior-yawn-timeout", $"actor={id}; rev={clear.Rev}");
                    }
                }
            }
        }
        PumpOwnerRequests(now);
        if (PendingApply.Count == 0) return;
        PendingScratch.Clear(); PendingScratch.AddRange(PendingApply.Keys);
        foreach (var key in PendingScratch)
        {
            if (!PendingApply.TryGetValue(key, out var pending) || now < pending.At) continue;
            if (TryApply(pending.Entry, out var ready) && ready) { PendingApply.Remove(key); continue; }
            if (++pending.Attempts >= MaxRetries) { PendingApply.Remove(key); Trace("behavior-apply-expired", $"kind={key.Kind}; actor={key.ActorId}; attempts={pending.Attempts}"); }
            else pending.At = now + Math.Min(2f, 0.1f * (1 << Math.Min(4, pending.Attempts)));
        }
    }

    static void Reset() { Latest.Clear(); LastPublished.Clear(); PublishedOwner.Clear(); PendingApply.Clear(); PendingOwner.Clear(); Order.Clear(); ActiveYawns.Clear(); }

    internal static void Publish(DervishSlimeSpin spin)
    {
        if (!spin) return;
        bool active = spin._cyclone != null && spin._mode == DervishSlimeSpin.Mode.CYCLONE;
        int size = active ? (int)spin.GetCycloneSize() : 0;
        Publish(new SlimeBehaviorEntry { Kind = SlimeBehaviorKind.Dervish, ActorId = ActorLink.IdOf(spin.gameObject), Active = active, Size = size, FloatDir = GameActorWorld.W(spin._floatDir) });
    }

    internal static void Publish(GroundVine vine, GameObject target, bool active)
    {
        if (!vine) return;
        if (active && target) ActorRuntime.NoteActivity(target);
        Publish(new SlimeBehaviorEntry { Kind = SlimeBehaviorKind.Tangle, ActorId = ActorLink.IdOf(vine.gameObject), TargetId = active ? ActorLink.IdOf(target) : 0, Active = active });
    }

    internal static void Publish(StalkConsumable stalk, GameObject target)
    {
        if (!stalk) return;
        Publish(new SlimeBehaviorEntry { Kind = SlimeBehaviorKind.Stalk, ActorId = ActorLink.IdOf(stalk.gameObject), TargetId = ActorLink.IdOf(target) });
    }

    internal static void Yawn(InitiateYawn yawn, bool active)
    {
        if (!yawn) return;
        long id = ActorLink.IdOf(yawn.gameObject);
        if (id <= 0) return;
        if (active) ActiveYawns[id] = Time.unscaledTime; else ActiveYawns.Remove(id);
        Publish(new SlimeBehaviorEntry { Kind = SlimeBehaviorKind.Sloomber, ActorId = id, Active = active });
    }

    static void Publish(SlimeBehaviorEntry state)
    {
        if (!Mod.IsNetworkActive || Applying.Active || ApplyingState || state.ActorId <= 0) return;
        var actor = ActorLink.ById(state.ActorId);
        if (!actor || !actor.LocallyOwned) return;
        var key = new Key(state.Kind, state.ActorId);
        if (!PublishedOwner.TryGetValue(state.ActorId, out var owner) || owner != actor.CurrentOwnerId)
        {
            LastPublished.Remove(key);
            PublishedOwner[state.ActorId] = actor.CurrentOwnerId;
        }
        if (LastPublished.TryGetValue(key, out var published) && Same(published, state)) return;
        LastPublished[key] = state;
        if (Mod.IsHost && Latest.TryGetValue(key, out var old) && Same(old, state)) return;
        state.Rev = 0;
        if (Mod.IsHost)
        {
            if (Latest.TryGetValue(key, out var current) && current.Rev == long.MaxValue) { Trace("behavior-revision-exhausted", $"kind={state.Kind}; actor={state.ActorId}"); return; }
            state.Rev = NextRevision(key); Remember(key, state); Broadcast(state);
        }
        else SendToHost(state);
    }

    static Verdict HostReceive(Ctx c, DervishCyclone m)
    {
        var s = new SlimeBehaviorEntry { Kind = SlimeBehaviorKind.Dervish, ActorId = m.ActorId, Active = m.Active, Size = m.Size, FloatDir = m.FloatDir };
        return AcceptClient(c, s, m.Rev, v => { m.Rev = v; Mod.Node.SendToAll(m); });
    }
    static Verdict HostReceive(Ctx c, SloomberYawn m)
    {
        var s = new SlimeBehaviorEntry { Kind = SlimeBehaviorKind.Sloomber, ActorId = m.ActorId, Active = m.Active };
        return AcceptClient(c, s, m.Rev, v => { m.Rev = v; m.ForceClear = false; if (m.Active) ActiveYawns[m.ActorId] = Time.unscaledTime; else ActiveYawns.Remove(m.ActorId); Mod.Node.SendToAll(m); });
    }
    static Verdict HostReceive(Ctx c, TangleVineGrapple m)
    {
        var s = new SlimeBehaviorEntry { Kind = SlimeBehaviorKind.Tangle, ActorId = m.ActorId, TargetId = m.TargetId, Active = m.Active };
        return AcceptClient(c, s, m.Rev, v => { m.Rev = v; Mod.Node.SendToAll(m); });
    }
    static Verdict HostReceive(Ctx c, StalkConsumableTarget m)
    {
        var s = new SlimeBehaviorEntry { Kind = SlimeBehaviorKind.Stalk, ActorId = m.ActorId, TargetId = m.TargetId };
        return AcceptClient(c, s, m.Rev, v => { m.Rev = v; Mod.Node.SendToAll(m); });
    }

    static Verdict AcceptClient(Ctx c, SlimeBehaviorEntry s, long incoming, Action<long> relay)
    {
        if (!Valid(s) || incoming != 0 || Mod.Actors == null) return Verdict.Reject;
        if (!Mod.Actors.Table.TryGet(s.ActorId, out var actor) || (s.TargetId > 0 && !Mod.Actors.Table.TryGet(s.TargetId, out _)))
        {
            DeferClient(c.SenderId, s);
            return Verdict.Reject;
        }
        if (actor.Owner != c.SenderId) return Verdict.Reject;
        var key = new Key(s.Kind, s.ActorId);
        if (Latest.TryGetValue(key, out var previous) && Same(previous, s)) return Verdict.Reject;
        if (Latest.TryGetValue(key, out previous) && previous.Rev == long.MaxValue) { Trace("behavior-revision-exhausted", $"kind={s.Kind}; actor={s.ActorId}"); return Verdict.Reject; }
        s.Rev = NextRevision(key); Remember(key, s); QueueApply(s); relay(s.Rev);
        Trace("behavior-relayed", $"kind={s.Kind}; actor={s.ActorId}; owner={c.SenderId}; rev={s.Rev}; active={s.Active}; target={s.TargetId}");
        return Verdict.Reject;
    }

    static void DeferClient(string sender, SlimeBehaviorEntry s)
    {
        if (string.IsNullOrEmpty(sender)) return;
        var key = new Key(s.Kind, s.ActorId); var requestKey = (key, sender);
        if (PendingOwner.TryGetValue(requestKey, out var old)) { old.Entry = s; return; }
        if (PendingOwner.Count >= MaxPending) { Trace("behavior-request-refused", $"kind={s.Kind}; actor={s.ActorId}; sender={sender}; reason=pending-limit"); return; }
        PendingOwner[requestKey] = new OwnerRequest { Sender = sender, Entry = s, QueuedAt = Time.unscaledTime };
        Trace("behavior-request-queued", $"kind={s.Kind}; actor={s.ActorId}; sender={sender}");
    }

    static void PumpOwnerRequests(float now)
    {
        if (PendingOwner.Count == 0 || Mod.Actors == null) return;
        foreach (var item in new List<KeyValuePair<(Key key, string sender), OwnerRequest>>(PendingOwner))
        {
            var request = item.Value; var s = request.Entry;
            if (now - request.QueuedAt > 10f || Mod.Node.FindByPlayer(request.Sender) == null)
            { PendingOwner.Remove(item.Key); continue; }
            if (!Mod.Actors.Table.TryGet(s.ActorId, out var actor)) continue;
            if (actor.Owner != request.Sender) { PendingOwner.Remove(item.Key); continue; }
            if (s.TargetId > 0 && !Mod.Actors.Table.TryGet(s.TargetId, out _)) continue;
            PendingOwner.Remove(item.Key);
            var stateKey = new Key(s.Kind, s.ActorId);
            if (Latest.TryGetValue(stateKey, out var old) && (old.Rev == long.MaxValue || Same(old, s))) continue;
            s.Rev = NextRevision(stateKey); Remember(stateKey, s); QueueApply(s);
            if (s.Kind == SlimeBehaviorKind.Sloomber) { if (s.Active) ActiveYawns[s.ActorId] = now; else ActiveYawns.Remove(s.ActorId); }
            Broadcast(s);
            Trace("behavior-request-replayed", $"kind={s.Kind}; actor={s.ActorId}; owner={request.Sender}; rev={s.Rev}");
        }
    }

    static void GuestReceive(Ctx c, DervishCyclone m) => Receive(new SlimeBehaviorEntry { Kind = SlimeBehaviorKind.Dervish, ActorId = m.ActorId, Active = m.Active, Size = m.Size, FloatDir = m.FloatDir, Rev = m.Rev });
    static void GuestReceive(Ctx c, SloomberYawn m) => Receive(new SlimeBehaviorEntry { Kind = SlimeBehaviorKind.Sloomber, ActorId = m.ActorId, Active = m.Active, ForceClear = m.ForceClear, Rev = m.Rev });
    static void GuestReceive(Ctx c, TangleVineGrapple m) => Receive(new SlimeBehaviorEntry { Kind = SlimeBehaviorKind.Tangle, ActorId = m.ActorId, TargetId = m.TargetId, Active = m.Active, Rev = m.Rev });
    static void GuestReceive(Ctx c, StalkConsumableTarget m) => Receive(new SlimeBehaviorEntry { Kind = SlimeBehaviorKind.Stalk, ActorId = m.ActorId, TargetId = m.TargetId, Rev = m.Rev });

    static void Receive(SlimeBehaviorEntry s)
    {
        if (!Valid(s) || s.Rev <= 0) return;
        var key = new Key(s.Kind, s.ActorId);
        if (Latest.TryGetValue(key, out var old) && s.Rev <= old.Rev) return;
        Remember(key, s); QueueApply(s);
    }

    static void QueueApply(SlimeBehaviorEntry s)
    {
        var key = new Key(s.Kind, s.ActorId);
        if (TryApply(s, out var ready) && ready) { PendingApply.Remove(key); return; }
        if (PendingApply.Count >= MaxPending && !PendingApply.ContainsKey(key)) { Trace("behavior-apply-refused", $"kind={s.Kind}; actor={s.ActorId}; reason=pending-limit"); return; }
        PendingApply[key] = new Pending { Entry = s, Attempts = 1, At = Time.unscaledTime + 0.1f };
        Trace("behavior-apply-queued", $"kind={s.Kind}; actor={s.ActorId}; rev={s.Rev}");
    }

    static void ApplySnapshot(InitialSlimeBehavior m)
    {
        foreach (var s in m.Entries) Receive(s);
    }

    static void SendSnapshot(SnapshotContext ctx)
    {
        if (!Mod.IsHost) return;
        ObserveOwnedBehaviorForSnapshot();
        var entries = new List<SlimeBehaviorEntry>();
        foreach (var kv in Latest)
            if (Mod.Actors.Table.TryGet(kv.Key.ActorId, out _) && Valid(kv.Value)) { var snapshot = kv.Value; if (snapshot.Kind == SlimeBehaviorKind.Sloomber && !snapshot.Active) snapshot.ForceClear = true; entries.Add(snapshot); }
        entries.Sort((a, b) => a.ActorId != b.ActorId ? a.ActorId.CompareTo(b.ActorId) : a.Kind.CompareTo(b.Kind));
        for (int i = 0; i < entries.Count; i += MaxSnapshotEntries)
        {
            var m = new InitialSlimeBehavior();
            for (int j = i; j < Math.Min(entries.Count, i + MaxSnapshotEntries); j++) m.Entries.Add(entries[j]);
            ctx.Send(m, lane: Lane.Snapshot);
        }
    }

    static void ObserveOwnedBehaviorForSnapshot()
    {
        if (Mod.Actors == null) return;
        foreach (var e in Mod.Actors.Table.All)
        {
            if (e.IsGadget || e.Owner != Mod.LocalId) continue;
            var go = GameActorWorld.ObjectOf(e.Id); if (!go) continue;
            try
            {
                var stalk = go.GetComponent<StalkConsumable>();
                if (stalk) SaveSnapshotState(new SlimeBehaviorEntry { Kind = SlimeBehaviorKind.Stalk, ActorId = e.Id, TargetId = ActorLink.IdOf(stalk._target) });
                var vine = go.GetComponent<GroundVine>();
                if (vine)
                {
                    long targetId = ActorLink.IdOf(vine._target);
                    SaveSnapshotState(new SlimeBehaviorEntry { Kind = SlimeBehaviorKind.Tangle, ActorId = e.Id, TargetId = targetId, Active = targetId > 0 });
                }
                var spin = go.GetComponent<DervishSlimeSpin>();
                if (spin)
                {
                    bool active = spin._cyclone != null && spin._mode == DervishSlimeSpin.Mode.CYCLONE;
                    SaveSnapshotState(new SlimeBehaviorEntry { Kind = SlimeBehaviorKind.Dervish, ActorId = e.Id, Active = active, Size = active ? (int)spin.GetCycloneSize() : 0, FloatDir = GameActorWorld.W(spin._floatDir) });
                }
            }
            catch (Exception ex) { Trace("behavior-snapshot-read-failed", $"actor={e.Id}; error={ex.GetType().Name}:{ex.Message}"); }
        }
    }

    static void SaveSnapshotState(SlimeBehaviorEntry state)
    {
        if (!Valid(state)) return;
        var key = new Key(state.Kind, state.ActorId);
        if (Latest.TryGetValue(key, out var old) && Same(old, state)) return;
        if (Latest.TryGetValue(key, out old) && old.Rev == long.MaxValue) return;
        state.Rev = NextRevision(key); Remember(key, state);
    }

    static bool TryApply(SlimeBehaviorEntry s, out bool ready)
    {
        ready = false;
        var go = GameActorWorld.ObjectOf(s.ActorId);
        if (!go) return false;
        var actor = ActorLink.ById(s.ActorId);
        if (actor && actor.LocallyOwned && !(s.Kind == SlimeBehaviorKind.Sloomber && s.ForceClear)) { ready = true; return true; }
        bool wasApplying = _applyingState;
        _applyingState = true;
        try
        {
            using (Applying.Enter())
            {
                switch (s.Kind)
                {
                    case SlimeBehaviorKind.Dervish:
                    {
                        var spin = go.GetComponent<DervishSlimeSpin>(); if (!spin) return false;
                        if (s.Active)
                        {
                            if (!Enum.IsDefined(typeof(DervishSlimeSpin.CycloneSize), s.Size)) { Trace("behavior-apply-invalid", $"kind={s.Kind}; actor={s.ActorId}; size={s.Size}"); ready = true; return true; }
                            spin._floatDir = new Vector3(s.FloatDir.X, s.FloatDir.Y, s.FloatDir.Z);
                            if (!spin._cyclone) { var cyclone = spin.SpawnCyclone((DervishSlimeSpin.CycloneSize)s.Size); if (!cyclone) return false; spin._cyclone = cyclone; }
                            spin._mode = DervishSlimeSpin.Mode.CYCLONE;
                        }
                        else { if (spin._currActorVortexer) { spin._currActorVortexer.StartFade(); var align = spin._currActorVortexer.GetComponent<KeepAlignedUnderActor>(); if (align) align.enabled = false; } spin._cyclone = null; spin._mode = DervishSlimeSpin.Mode.NONE; }
                        ready = true; return true;
                    }
                    case SlimeBehaviorKind.Sloomber:
                    {
                        var yawn = go.GetComponent<InitiateYawn>(); if (!yawn || yawn._yawnComponentReactables == null) return false;
                        foreach (var reactable in yawn._yawnComponentReactables) if (reactable != null) { if (s.Active) reactable.BeginYawn(); else reactable.EndYawn(); }
                        if (!s.Active && !s.ForceClear) yawn.DoYawn();
                        ready = true; return true;
                    }
                    case SlimeBehaviorKind.Stalk:
                    {
                        var stalk = go.GetComponent<StalkConsumable>(); if (!stalk) return false;
                        var target = s.TargetId > 0 ? GameActorWorld.ObjectOf(s.TargetId) : null;
                        if (s.TargetId > 0 && !target) return false;
                        stalk.SetTarget(target); ready = true; return true;
                    }
                    case SlimeBehaviorKind.Tangle:
                    {
                        var vine = go.GetComponent<GroundVine>(); if (!vine) return false;
                        if (!s.Active) { vine.Release(); ready = true; return true; }
                        var target = s.TargetId > 0 ? GameActorWorld.ObjectOf(s.TargetId) : null;
                        if (!target || !vine._vinePrefab) return false;
                        if (vine._target == target) { ready = true; return true; }
                        if (GroundVine.IsGrabbed(target)) return false;
                        ready = vine.MaybeGrapple(target); return ready;
                    }
                }
            }
        }
        catch (Exception e) { Trace("behavior-apply-failed", $"kind={s.Kind}; actor={s.ActorId}; error={e.GetType().Name}:{e.Message}"); }
        finally { _applyingState = wasApplying; }
        return false;
    }

    static long NextRevision(Key key) => Latest.TryGetValue(key, out var old) ? (old.Rev == long.MaxValue ? long.MaxValue : old.Rev + 1) : 1;
    static void Remember(Key key, SlimeBehaviorEntry s)
    {
        if (!Latest.ContainsKey(key))
        {
            if (Latest.Count >= MaxCached)
            {
                var first = Order.Dequeue(); Latest.Remove(first); LastPublished.Remove(first); PendingApply.Remove(first);
                bool actorStillCached = false; foreach (var existing in Latest.Keys) if (existing.ActorId == first.ActorId) { actorStillCached = true; break; }
                if (!actorStillCached) PublishedOwner.Remove(first.ActorId);
            }
            Order.Enqueue(key);
        }
        Latest[key] = s;
    }
    static bool Same(SlimeBehaviorEntry a, SlimeBehaviorEntry b) => a.Kind == b.Kind && a.ActorId == b.ActorId && a.TargetId == b.TargetId && a.Active == b.Active && a.Size == b.Size && a.FloatDir.X == b.FloatDir.X && a.FloatDir.Y == b.FloatDir.Y && a.FloatDir.Z == b.FloatDir.Z;
    static bool Valid(SlimeBehaviorEntry s) => s.ActorId > 0 && s.TargetId >= 0 && Enum.IsDefined(typeof(SlimeBehaviorKind), s.Kind) && s.Size >= 0 &&
        (!s.ForceClear || (s.Kind == SlimeBehaviorKind.Sloomber && !s.Active)) &&
        (!s.Active || s.Kind != SlimeBehaviorKind.Tangle || s.TargetId > 0) &&
        (!s.Active || s.Kind != SlimeBehaviorKind.Dervish || Enum.IsDefined(typeof(DervishSlimeSpin.CycloneSize), s.Size)) &&
        float.IsFinite(s.FloatDir.X) && float.IsFinite(s.FloatDir.Y) && float.IsFinite(s.FloatDir.Z) &&
        (double)s.FloatDir.X * s.FloatDir.X + (double)s.FloatDir.Y * s.FloatDir.Y + (double)s.FloatDir.Z * s.FloatDir.Z <= 10000.0;
    static void Broadcast(SlimeBehaviorEntry s)
    {
        switch (s.Kind)
        {
            case SlimeBehaviorKind.Dervish: Mod.Node.SendToAll(new DervishCyclone { ActorId = s.ActorId, Active = s.Active, Size = s.Size, FloatDir = s.FloatDir, Rev = s.Rev }); break;
            case SlimeBehaviorKind.Sloomber: Mod.Node.SendToAll(new SloomberYawn { ActorId = s.ActorId, Active = s.Active, ForceClear = s.ForceClear, Rev = s.Rev }); break;
            case SlimeBehaviorKind.Tangle: Mod.Node.SendToAll(new TangleVineGrapple { ActorId = s.ActorId, TargetId = s.TargetId, Active = s.Active, Rev = s.Rev }); break;
            case SlimeBehaviorKind.Stalk: Mod.Node.SendToAll(new StalkConsumableTarget { ActorId = s.ActorId, TargetId = s.TargetId, Rev = s.Rev }); break;
        }
    }
    static void SendToHost(SlimeBehaviorEntry s)
    {
        switch (s.Kind)
        {
            case SlimeBehaviorKind.Dervish: Mod.Node.SendToHost(new DervishCyclone { ActorId = s.ActorId, Active = s.Active, Size = s.Size, FloatDir = s.FloatDir }); break;
            case SlimeBehaviorKind.Sloomber: Mod.Node.SendToHost(new SloomberYawn { ActorId = s.ActorId, Active = s.Active }); break;
            case SlimeBehaviorKind.Tangle: Mod.Node.SendToHost(new TangleVineGrapple { ActorId = s.ActorId, TargetId = s.TargetId, Active = s.Active }); break;
            case SlimeBehaviorKind.Stalk: Mod.Node.SendToHost(new StalkConsumableTarget { ActorId = s.ActorId, TargetId = s.TargetId }); break;
        }
    }
    static void Trace(string ev, string details) => Mod.Diagnostics?.Record("slime-behavior", ev, details);
}

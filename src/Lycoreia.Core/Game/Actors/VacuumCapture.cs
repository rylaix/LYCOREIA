// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher;
using Il2CppMonomiPark.SlimeRancher.Player.PlayerItems;
using Lycoreia.Actors;
using Lycoreia.Game.Hooks;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using UnityEngine;

namespace Lycoreia.Game
{
    /// <summary>
    /// Guest vacuum of an actor someone else owns: prepare, native action (local prediction), commit; the host never changes the owner
    /// on prepare. The host vacuuming a remote-owned actor decides on the spot (it is the authority) unless a guest holds a reservation.
    /// </summary>
    public static class VacuumCapture
    {
        public static readonly GuestCaptureMachine Machine = new GuestCaptureMachine();
        static ActorSync _bound;
        static readonly HashSet<long> _predicted = new HashSet<long>();
        static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);
        static double _nextPull;
        static double Now => Time.unscaledTimeAsDouble;

        /// <summary>The replica the local vacuum currently drives (diagnostics), or null.</summary>
        public static ActorLink ActiveActor { get { foreach (var id in _predicted) { var l = ActorLink.ById(id); if (l) return l; } return null; } }

        public static void Reset() { Machine.Reset(); foreach (var id in _predicted.ToList()) StopPrediction(id); _predicted.Clear(); }

        static void Bind()
        {
            var a = Mod.Actors; if (a == null || ReferenceEquals(a, _bound)) return;
            if (_bound != null) _bound.OnCaptureReply -= OnReply;
            _bound = a; a.OnCaptureReply += OnReply;
        }

        static bool VacActive(VacuumItem v) { try { return v != null && v.isActiveAndEnabled && v._vacPressed && v._vacMode == VacuumItem.VacMode.VAC; } catch { return false; } }

        /// <summary>Drives sends, retries, timeouts, prediction upkeep and held-capture confirmation. Called once per frame.</summary>
        public static void Pump(double now)
        {
            Bind(); var a = Mod.Actors; if (a == null || !a.IsGuest) { if (Machine.PendingCount > 0 || _predicted.Count > 0) Reset(); return; }
            string me = a.LocalId;
            foreach (var act in Machine.Pump(now))
            {
                var c = act.Capture;
                switch (act.Kind)
                {
                    case CaptureActionKind.SendPrepare: a.SendCapture(Machine.PrepareMessage(c, me)); break;
                    case CaptureActionKind.SendCommit: a.SendCapture(Machine.CommitMessage(c, me)); break;
                    case CaptureActionKind.SendAbort: a.SendCaptureAbort(c.ActorId, c.RequestId); StopPrediction(c.ActorId); break;
                    case CaptureActionKind.GaveUp: StopPrediction(c.ActorId); Rl.Info("gave-up:" + act.Reason, $"capture of {c.ActorId} gave up: {act.Reason}"); Mod.Diagnostics?.Record("vacuum", "capture-gave-up", $"id={c.ActorId}; reason={act.Reason}; phase={c.Phase}; native={c.NativeConfirmed}; commitSent={c.CommitSent}"); break;
                }
            }
            var vac = GameActorWorld.LocalVacuum; bool active = VacActive(vac);
            foreach (var id in _predicted.ToList())
            {
                var c = Machine.Get(id); var go = GameActorWorld.ObjectOf(id);
                bool keep = go != null && c != null && c.Phase == CapturePhase.Approved && (active || c.Irreversible || c.NativeConfirmed);
                if (!keep) { StopPrediction(id); if (c != null && !c.Irreversible && !c.NativeConfirmed) { var ab = Machine.Cancel(id, "vacuum released"); if (ab.HasValue) a.SendCaptureAbort(id, ab.Value.Capture.RequestId); } }
            }
            if (now >= _nextPull)
            {
                _nextPull = now + 0.05;
                foreach (var id in _predicted)
                {
                    if (Machine.Get(id) is not { Phase: CapturePhase.Approved }) continue;
                    var go = GameActorWorld.ObjectOf(id); if (go == null) continue;
                    var t = go.transform; var q = t.rotation; var body = go.GetComponent<Rigidbody>();
                    a.SendCapturePull(id, GameActorWorld.W(t.position), new Quat(q.x, q.y, q.z, q.w), body != null ? GameActorWorld.W(body.velocity) : Vec3.Zero);
                }
            }
            // held (non-consume) captures: the native captive state confirms a successful light-vac pull
            foreach (var id in _predicted.ToList())
            {
                var c = Machine.Get(id); if (c == null || c.Consumes || c.NativeConfirmed) continue;
                var v = GameActorWorld.ObjectOf(id)?.GetComponent<Vacuumable>();
                try
                {
                    // IsCaptive is the game's state for a successful light-vac pull. The joint's connectedBody
                    // may be null for a valid player-owned joint, so do not wait for that implementation detail.
                    if (v != null && (v.IsCaptive() || (vac != null && vac._held == v.gameObject && v.IsHeld()) ||
                        v.Size != VacuumableSize.NORMAL && v.Pending && active) && Machine.NoteNativeConfirmed(id, now))
                        Mod.Diagnostics?.Record("vacuum", "capture-native-confirmed", $"id={id}; mode=held");
                }
                catch { }
            }
            if (!active) foreach (var ab in Machine.CancelAllExcept(0, true, "vacuum released")) a.SendCaptureAbort(ab.Capture.ActorId, ab.Capture.RequestId);
        }

        static void OnReply(ActorTransfer m)
        {
            if (!Machine.OnReply(m, Now, out var c)) return;
            Mod.Diagnostics?.Record("vacuum", "capture-reply", $"id={m.ActorId}; commit={m.CaptureCommit}; approved={m.CaptureApproved}; consumes={m.ConsumesActor}; request={m.CaptureRequestId}; owner={m.NewOwner}; epoch={m.Epoch}; native={c.NativeConfirmed}; destroyed={c.DestroyObserved}");
            if (m.CaptureCommit || !m.CaptureApproved) { StopPrediction(m.ActorId); return; }      // final result, or a denial
            StartPrediction(m.ActorId);                                                             // approved: the local vacuum may now drive the replica
        }

        static void StartPrediction(long id)
        {
            var l = ActorLink.ById(id); if (!l) return;
            if (!VacActive(GameActorWorld.LocalVacuum) || !CaptureEligible(l, out _)) return;
            _predicted.Add(id); l.SetLocalVacuumPrediction(true);
            var c = Machine.Get(id);
            Mod.Diagnostics?.Record("vacuum", "capture-prediction-started", $"id={id}; request={c?.RequestId}; owner={l.CurrentOwnerId}; consumes={c?.Consumes}; authority=host-reservation");
        }
        static void StopPrediction(long id)
        {
            if (!_predicted.Remove(id)) return;
            var l = ActorLink.ById(id);
            if (l && !l.LocallyOwned)
            {
                try
                {
                    var vac = GameActorWorld.LocalVacuum;
                    var v = l.GetComponent<Vacuumable>();
                    if (vac != null && vac._held == l.gameObject) vac.ClearHeld();
                    if (v != null && v.IsCaptive()) v.Release();
                    Mod.Diagnostics?.Record("vacuum", "capture-prediction-released", $"id={id}; owner={l.CurrentOwnerId}; authority=host");
                }
                catch (Exception e) { Rl.Warn("release-prediction", "capture prediction release: " + e.Message); }
            }
            if (l) l.SetLocalVacuumPrediction(false); else ActorRuntime.Motion.SetPredicted(id, false);
        }

        /// <summary>Capturable, not captive, finite distance within the native maximum range.</summary>
        static bool CaptureEligible(ActorLink l, out bool consumes)
        {
            consumes = false;
            try
            {
                var v = l.GetComponent<Vacuumable>(); if (v == null || !v.CanCapture() || v.IsCaptive() && !_predicted.Contains(l.Id)) return false;
                consumes = v.Size == VacuumableSize.NORMAL;                                         // larger actors are held, not stored
                var vac = GameActorWorld.LocalVacuum; if (vac == null || vac.VacOrigin == null) return false;
                float d = Vector3.Distance(vac.VacOrigin.transform.position, l.transform.position);
                return float.IsFinite(d) && d <= ActorCaptureTransferPolicyFacade.MaxRange(vac.MaxVacDist);
            }
            catch { return false; }
        }

        // ================================================================== hooks
        /// <summary>The objects currently in the vacuum cone: ask for captures of remote-owned ones (guest) or take them (host).</summary>
        static int _lastFrame = -1; static double _nextConeLog;
        public static void OnVacuumTargets(VacuumItem vac, Il2CppSystem.Collections.Generic.HashSet<GameObject> inVac)
        {
            var a = Mod.Actors; if (a == null || !Mod.IsNetworkActive || inVac == null || !VacActive(vac)) return;
            // the local player's vacuum only. The vacpack is NOT parented under SceneContext.Player (a transform-parent test rejected
            // every pass in game, 2026-11-02 run: no capture was ever asked for), so compare with the player's own VacuumItem.
            if (!IsLocalVacuum(vac)) return;
            int frame = Time.frameCount; if (frame == _lastFrame) return; _lastFrame = frame;          // several native passes hand us the cone; once per frame
            double now = Now;
            if (now >= _nextConeLog && inVac.Count > 0) { _nextConeLog = now + 2; LogCone(inVac); }
            foreach (var go in inVac)
            {
                var l = ActorLink.Of(go); if (!l || a.IsHost && l.LocallyOwned || l.Entry is not { } e || e.IsGadget) continue;
                if (!CaptureEligible(l, out bool consumes)) { NoteSkip(l, e, "ineligible"); continue; }
                if (e.HeldByOwner && e.Owner != a.LocalId && a.IsPlayerConnected(e.Owner)) { NoteSkip(l, e, "held-by-owner"); continue; }   // in the owner's vacuum: never pulled out of it
                if (a.IsHost)
                {
                    if (a.Captures.HasReservation(l.Id)) { NoteSkip(l, e, "guest-reservation"); continue; }
                    Mod.Diagnostics?.Record("vacuum", "host-take", $"id={l.Id}; from={e.Owner}; type={e.TypeId}; consumes={consumes}");
                    a.GrantAndBroadcast(e, a.LocalId, e.Owner);                                       // the host decides for itself
                    continue;
                }
                Machine.NoteCandidate(l.Id, e.Scene, now);
                var r = Machine.Ask(l.Id, e.Owner ?? "", consumes, now, out _);
                if (r == CaptureAskResult.Queued) Mod.Diagnostics?.Record("vacuum", "capture-asked", $"id={l.Id}; owner={e.Owner}; type={e.TypeId}; consumes={consumes}");
                else if (r != CaptureAskResult.AlreadyPending) NoteSkip(l, e, "ask-" + r);
                if (r == CaptureAskResult.AlreadyPending && Machine.Get(l.Id) is { Phase: CapturePhase.Approved } && !_predicted.Contains(l.Id)) StartPrediction(l.Id);
            }
        }

        static bool IsLocalVacuum(VacuumItem vac)
        {
            try { var mine = GameActorWorld.LocalVacuum; return mine != null && vac != null && mine.Pointer == vac.Pointer; }
            catch { return false; }
        }

        static void LogCone(Il2CppSystem.Collections.Generic.HashSet<GameObject> inVac)
        {
            try
            {
                var sb = new System.Text.StringBuilder(); int n = 0;
                foreach (var go in inVac)
                {
                    if (go == null || n++ >= 12) continue; long id = ActorLink.IdOf(go); var l = ActorLink.Of(go);
                    sb.Append(go.name).Append(':').Append(id).Append(':').Append(!l ? "unlinked" : l.LocallyOwned ? "own" : "remote:" + l.CurrentOwnerId).Append(' ');
                }
                Mod.Diagnostics?.Record("vacuum", "cone", $"count={inVac.Count}; {sb}");
            }
            catch { }
        }

        static readonly Dictionary<long, double> _skipLogged = new Dictionary<long, double>();
        /// <summary>Why a remote-owned actor in the local vacuum cone was not asked for (diagnostics, once per actor per 10 s).</summary>
        static void NoteSkip(ActorLink l, ActorEntry e, string why)
        {
            try
            {
                double now = Now; if (_skipLogged.TryGetValue(l.Id, out var t) && now - t < 10) return; _skipLogged[l.Id] = now; if (_skipLogged.Count > 2048) _skipLogged.Clear();
                var v = l.GetComponent<Vacuumable>(); var vac = GameActorWorld.LocalVacuum; var rc = l.GetComponent<ResourceCycle>(); var body = l.GetComponent<Rigidbody>();
                float d = vac != null && vac.VacOrigin != null ? Vector3.Distance(vac.VacOrigin.transform.position, l.transform.position) : -1;
                Mod.Diagnostics?.Record("vacuum", "capture-skipped", $"why={why}; id={l.Id}; owner={e.Owner}; type={e.TypeId}; held={e.HeldByOwner}; vacuumable={v != null}; canCapture={(v != null && v.CanCapture())}; captive={(v != null && v.IsCaptive())}; size={(v != null ? v.Size.ToString() : "-")}; " +
                    $"dist={d:0.0}; max={(vac != null ? vac.MaxVacDist : -1):0.0}; kinematic={(body != null && body.isKinematic)}; stage={(rc != null ? rc.GetState().ToString() : "-")}; attached={e.Attached}; released={e.ReleasedByOwner}");
            }
            catch { }
        }

        /// <summary>Capture / force-joint gate: a remote-owned replica may be joined to the local vacuum only with an approved capture.</summary>
        public static bool AllowJoint(Vacuumable v)
        {
            try
            {
                if (!Mod.IsNetworkActive || v == null) return true;
                var l = ActorLink.Of(v); if (!l) return true;
                if (Mod.IsHost && Mod.Actors.Captures.HasReservation(l.Id)) return false;
                if (Mod.IsHost) return true;
                if (l.LocallyOwned && l.Entry.HeldByOwner && (v.IsHeld() || v.IsCaptive()) && Machine.Get(l.Id) == null) return true;
                if (!VacActive(GameActorWorld.LocalVacuum) && l.LocallyOwned) return true;
                return Machine.Get(l.Id) is { Phase: CapturePhase.Approved };
            }
            catch (Exception e) { HookGuard.Fail("Vacuumable.Capture", e); return true; }
        }

        /// <summary>Native consume of a vacuumed object (into ammo). Remote-owned: only with an approved consume capture; arms the destroy permit.</summary>
        public static bool BeforeConsume(GameObject go, out long armed)
        {
            armed = 0;
            try
            {
                if (go == null) return true;
                if (Mod.IsHost && Mod.Actors != null && Mod.Actors.Captures.HasReservation(ActorLink.IdOf(go))) return false;
                if (!Mod.IsGuest) return true;
                var l = ActorLink.Of(go); if (!l) return true;
                var c = Machine.Get(l.Id);
                if (c == null && l.LocallyOwned) Machine.Ask(l.Id, l.CurrentOwnerId, true, Now, out c);
                if (c == null || c.Phase != CapturePhase.Approved) return false;                    // not approved yet: the replica stays put
                if (!c.Consumes)
                {
                    var native = go.GetComponent<Vacuumable>();
                    // The native method also drives non-storable GIANT pulls.
                    // Let that path run without granting a consume destroy permit.
                    return native != null && Machine.AllowsHeldNativePass(l.Id, native.Size == VacuumableSize.NORMAL);
                }
                if (!Machine.TryArmConsume(l.Id, Now)) return false;
                armed = l.Id; return true;
            }
            catch (Exception e) { HookGuard.Fail("ConsumeVacuumable", e); return true; }
        }

        public static void AfterConsume(long armed, bool failed)
        {
            if (armed == 0) return;
            try
            {
                bool removed = !failed && GameActorWorld.ObjectOf(armed) == null;
                // Native consume may start an animation/coroutine and destroy the actor after this method returns.
                // Keep its short-lived permit armed so the delayed destroy can pass the ownership guard.
                if (removed) { Machine.OnConsumeFinished(armed, true, Now); _predicted.Remove(armed); }
                else if (failed) Machine.OnConsumeFinished(armed, false, Now);
            }
            catch (Exception e) { HookGuard.Fail("ConsumeVacuumable.after", e); }
        }

        /// <summary>ConsumeExistingJointed has no object parameter; its target is the vacuum's current held actor.</summary>
        public static bool BeforeConsumeExistingJointed(VacuumItem vacuum, out long armed)
        {
            GameObject held = null;
            try { held = vacuum != null ? vacuum._held : null; } catch { }
            return BeforeConsume(held, out armed);
        }

        /// <summary>The destroy hook asks: is this exact local destroy of a non-owned actor the awaited native consume? Never announced.</summary>
        public static bool AllowConsumeDestroy(long id)
        {
            if (!Mod.IsGuest) return false;
            if (!Machine.AllowConsumeDestroy(id, Mod.Session.Connected, Now, false)) return false;
            Machine.ObserveDestroy(id); Machine.OnConsumeFinished(id, true, Now); ActorRuntime.Forget(id); return true;
        }

        /// <summary>A world resync keeps irreversible captures and aborts only the others, one abort per actor.</summary>
        public static List<long> IrreversibleIds() { var l = new List<long>(); foreach (var id in Machine.PendingIds()) if (Machine.Get(id) is { Irreversible: true }) l.Add(id); return l; }

        public static void OnResync()
        {
            var a = Mod.Actors; foreach (var ab in Machine.OnResync()) { a?.SendCaptureAbort(ab.Capture.ActorId, ab.Capture.RequestId); StopPrediction(ab.Capture.ActorId); }
        }
    }

    static class ActorCaptureTransferPolicyFacade
    {
        public static float MaxRange(float nativeMax) => Lycoreia.Owned.ActorCaptureTransferPolicy.GetMaximumVacuumAcquisitionDistance(nativeMax);
    }
}

namespace Lycoreia.Game.Hooks
{
    [HarmonyPatch(typeof(VacuumItem), nameof(VacuumItem.UpdateHeld))]
    static class VacuumUpdateHeldHook
    {
        static void Prefix(VacuumItem __instance, Il2CppSystem.Collections.Generic.HashSet<GameObject> inVac)
        { try { VacuumCapture.OnVacuumTargets(__instance, inVac); } catch (Exception e) { HookGuard.Fail("VacuumItem.UpdateHeld", e); } }
    }

    /// <summary>The per-frame vacuum pass (UpdateHeld may only run while something is held: in game no capture was ever asked for).</summary>
    [HarmonyPatch(typeof(VacuumItem), nameof(VacuumItem.Consume))]
    static class VacuumConsumePassHook
    {
        static void Prefix(VacuumItem __instance, Il2CppSystem.Collections.Generic.HashSet<GameObject> inVac)
        { try { VacuumCapture.OnVacuumTargets(__instance, inVac); } catch (Exception e) { HookGuard.Fail("VacuumItem.Consume", e); } }
    }

    [HarmonyPatch(typeof(VacuumItem), nameof(VacuumItem.UpdateHud))]
    static class VacuumHudPassHook
    {
        static void Prefix(VacuumItem __instance, Il2CppSystem.Collections.Generic.HashSet<GameObject> inVac)
        { try { VacuumCapture.OnVacuumTargets(__instance, inVac); } catch (Exception e) { HookGuard.Fail("VacuumItem.UpdateHud", e); } }
    }

    [HarmonyPatch(typeof(Vacuumable), nameof(Vacuumable.Capture))]
    static class VacuumableCaptureHook { static bool Prefix(Vacuumable __instance) => VacuumCapture.AllowJoint(__instance); }

    [HarmonyPatch(typeof(Vacuumable), nameof(Vacuumable.Hold))]
    static class VacuumableHoldHook { static bool Prefix(Vacuumable __instance) => VacuumCapture.AllowJoint(__instance); }

    [HarmonyPatch(typeof(VacuumItem), nameof(VacuumItem.ForceJoint))]
    static class VacuumForceJointHook { static bool Prefix(Vacuumable vacuumable) => VacuumCapture.AllowJoint(vacuumable); }

    [HarmonyPatch(typeof(VacuumItem), nameof(VacuumItem.ConsumeVacuumable))]
    static class VacuumConsumeHook
    {
        static bool Prefix(GameObject gameObj, out long __state) => VacuumCapture.BeforeConsume(gameObj, out __state);
        static Exception Finalizer(long __state, Exception __exception) { VacuumCapture.AfterConsume(__state, __exception != null); return __exception; }
    }

    // Slimes and plorts that have already been pulled into the jointed/held path are consumed here.
    // Without this gate their local destroy is suppressed, while the host still owns the original actor.
    [HarmonyPatch(typeof(VacuumItem), nameof(VacuumItem.ConsumeExistingJointed))]
    static class VacuumConsumeExistingJointedHook
    {
        static bool Prefix(VacuumItem __instance, out long __state) => VacuumCapture.BeforeConsumeExistingJointed(__instance, out __state);
        static Exception Finalizer(long __state, Exception __exception) { VacuumCapture.AfterConsume(__state, __exception != null); return __exception; }
    }
}

// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using Il2CppKinematicCharacterController;
using Il2CppMonomiPark.SlimeRancher.Player.CharacterController;
using UnityEngine;

namespace Lycoreia.Game.Avatars
{
    /// <summary>Pure boost-jump rules.</summary>
    public static class BoostRules
    {
        public const float Factor = 1.55f, MinJumpSpeed = 2f, Grace = 0.25f, MaxBoostedSpeed = 30f;
        /// <summary>A jump that starts within <see cref="Grace"/> s of standing on a friend, once per take-off.</summary>
        public static bool ShouldBoost(double now, double lastOnFriend, bool grounded, float verticalSpeed, bool boostedThisAir)
            => !grounded && !boostedThisAir && now - lastOnFriend <= Grace && verticalSpeed >= MinJumpSpeed;
        /// <summary>About 2.4x the jump height (height grows with the square of the take-off speed).</summary>
        public static float Boosted(float verticalSpeed) => Math.Min(verticalSpeed * Factor, MaxBoostedSpeed);
    }

    /// <summary>Pure squash curve of the avatar stood on (visual only): press down fast, spring back with a small overshoot, exactly 1 at the end.</summary>
    public static class SquashCurve
    {
        public const float Duration = 0.4f, Press = 0.1f, Depth = 0.22f;
        /// <summary>Vertical scale factor at time t seconds after the jump.</summary>
        public static float Y(float t)
        {
            if (t <= 0f || t >= Duration) return 1f;
            float k;
            if (t < Press) { float u = t / Press; k = u * u * (3f - 2f * u); }
            else { float s = t - Press; k = (float)(Math.Exp(-s / 0.07) * Math.Cos(s * 18.0)); }
            return 1f - Depth * k;
        }
        /// <summary>Horizontal factor: widens as it flattens (roughly volume preserving).</summary>
        public static float Xz(float t) => 1f + (1f - Y(t)) * 0.5f;
    }

    public sealed class AvatarBodies : IFramePump
    {
        public string Name => "avatar-bodies";
        public const string BodyName = "Lycoreia.PlayerBody";
        static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);
        static readonly Dictionary<string, CapsuleCollider> _bodies = new Dictionary<string, CapsuleCollider>();
        static readonly HashSet<int> _bodyIds = new HashSet<int>();
        static int _layer = -1; static IntPtr _layerFor;
        string _lastFriendId; double _lastOnFriend = double.NegativeInfinity; bool _boosted, _wasGrounded; static bool _toldStanding;

        public static bool IsBody(Collider c) { try { return c != null && _bodyIds.Contains(c.GetInstanceID()); } catch { return false; } }

        public void Pump()
        {
            var pl = SceneContext.Instance != null ? SceneContext.Instance.Player : null;
            EnsureSync();
            if (pl == null || !Mod.IsNetworkActive) { EndSquash(); EndNudge(); if (_bodies.Count > 0) Clear(); return; }
            TickSquash(); TickNudge();
            var cc = pl.GetComponent<SRCharacterController>(); var motor = cc != null ? cc.Motor : null; if (motor == null) return;
            EnsureBodies(motor);
            Boost(cc, motor);
        }

        // ---- boost-fx message (cosmetic): sent on a local boost, played here for boosts seen from other seats
        static Lycoreia.Players.BoostFxSync _sync; static Lycoreia.Session _syncFor;

        sealed class BoostWorld : Lycoreia.Players.IBoostFxWorld { public void PlayBoostFx(Lycoreia.Net.Messages.BoostFx fx) => PlayRemoteBoost(fx); }

        static void EnsureSync()
        {
            var s = Mod.Session; if (s == null || ReferenceEquals(_syncFor, s)) return;
            try
            {
                _syncFor = s; _sync = new Lycoreia.Players.BoostFxSync(s, new BoostWorld(), () => Time.unscaledTimeAsDouble);
                _sync.Trace += (ev, detail) => { Mod.Diagnostics?.Record("players", "boost-fx", $"{ev}; {detail}; sent={_sync.Sent}; received={_sync.Received}; played={_sync.Played}; rejected={_sync.Rejected}"); Log.Info($"boost-fx {ev}: {detail}"); };
            }
            catch (Exception e) { Rl.Warn("boost-fx", "boost-fx sync failed: " + e.Message); }
        }

        /// <summary>A boost seen from another seat: puff and sound at the jumper's feet, squash of the stood-on avatar (or a small camera dip when that is us).</summary>
        static void PlayRemoteBoost(Lycoreia.Net.Messages.BoostFx fx)
        {
            try
            {
                var at = new Vector3(fx.Position.X, fx.Position.Y, fx.Position.Z);
                Lycoreia.Owned.Fx.NativeFx.Puff(at);
                Lycoreia.Owned.Fx.NativeFx.PlayCue(Lycoreia.Owned.Fx.FxCue.Jump, at);
                if (!string.IsNullOrEmpty(fx.StoodOnId) && fx.StoodOnId == Mod.LocalId) { _nudgeStart = Time.time; }
                else StartSquash(fx.StoodOnId);
            }
            catch (Exception e) { Rl.Warn("boost-fx", "remote boost FX failed: " + e.Message); }
        }

        // the local first-person camera dips a few centimetres when a friend boost-jumps off us (removed exactly again; the offset ends at 0)
        static float _nudgeStart = -1f; static Vector3 _nudgeApplied;

        static void TickNudge()
        {
            if (_nudgeStart < 0f && _nudgeApplied == Vector3.zero) return;
            try
            {
                var cam = Camera.main; if (cam == null) { _nudgeStart = -1f; _nudgeApplied = Vector3.zero; return; }
                var t = cam.transform; t.localPosition -= _nudgeApplied; _nudgeApplied = Vector3.zero;
                if (_nudgeStart < 0f) return;
                float e = Time.time - _nudgeStart; if (e >= SquashCurve.Duration) { _nudgeStart = -1f; return; }
                _nudgeApplied = new Vector3(0f, -(1f - SquashCurve.Y(e)) * 0.25f, 0f); t.localPosition += _nudgeApplied;
            }
            catch (Exception e) { Rl.Warn("nudge", "camera nudge failed: " + e.Message); _nudgeStart = -1f; _nudgeApplied = Vector3.zero; }
        }

        static void EndNudge() { if (_nudgeStart < 0f && _nudgeApplied == Vector3.zero) return; _nudgeStart = -1f; TickNudge(); }

        // ---- squash of the avatar stood on (visual only: the remote avatar's model scale; the body capsule is counter-scaled so its world size is unchanged)
        static RemoteAvatar _sqAvatar; static string _sqId; static Vector3 _sqRoot, _sqBody; static Transform _sqBodyT; static float _sqStart; static float _sqY = 1f;

        static void StartSquash(string id)
        {
            try
            {
                EndSquash();
                if (id == null || !RemoteAvatars.All.TryGetValue(id, out var a) || a?.Root == null || !_bodies.TryGetValue(id, out var cap) || cap == null) return;
                _sqAvatar = a; _sqId = id; _sqRoot = a.transform.localScale; _sqBodyT = cap.transform; _sqBody = _sqBodyT.localScale; _sqStart = Time.time; _sqY = 1f;
            }
            catch (Exception e) { Rl.Warn("squash", "squash start failed: " + e.Message); _sqAvatar = null; }
        }

        static void TickSquash()
        {
            if (_sqAvatar == null) return;
            try
            {
                if (_sqAvatar.Root == null || _sqBodyT == null) { _sqAvatar = null; _sqY = 1f; return; }   // avatar gone: nothing to restore
                float t = Time.time - _sqStart;
                if (t >= SquashCurve.Duration) { EndSquash(); return; }
                float y = SquashCurve.Y(t), xz = SquashCurve.Xz(t);
                _sqAvatar.transform.localScale = new Vector3(_sqRoot.x * xz, _sqRoot.y * y, _sqRoot.z * xz);
                _sqBodyT.localScale = new Vector3(_sqBody.x / xz, _sqBody.y / y, _sqBody.z / xz);
                _sqY = y;
            }
            catch (Exception e) { Rl.Warn("squash", "squash failed: " + e.Message); EndSquash(); }
        }

        /// <summary>Restores the exact original scales (model and body capsule). Safe to call anytime.</summary>
        static void EndSquash()
        {
            if (_sqAvatar == null) return;
            try
            {
                if (_sqAvatar.Root != null) _sqAvatar.transform.localScale = _sqRoot;
                if (_sqBodyT != null) _sqBodyT.localScale = _sqBody;
            }
            catch { }
            _sqAvatar = null; _sqBodyT = null; _sqY = 1f;
        }

        static float SquashOf(Transform avatar) => _sqAvatar != null && _sqAvatar.transform == avatar ? _sqY : 1f;

        static void Clear() { foreach (var b in _bodies.Values) try { if (b != null) UnityEngine.Object.Destroy(b.gameObject); } catch { } _bodies.Clear(); _bodyIds.Clear(); }

        /// <summary>A layer the motor collides with and can stand on: the player's own capsule layer first (vacuum rays already ignore it).</summary>
        static int PickLayer(KinematicCharacterMotor motor)
        {
            if (_layerFor == motor.Pointer) return _layer;                                     // also caches "none found" (no per-frame scan)
            int collide = motor.CollidableLayers.value, stable = motor.StableGroundLayers.value, own = motor.Capsule != null ? motor.Capsule.gameObject.layer : -1;
            int pick = -1;
            if (own >= 0 && (collide & (1 << own)) != 0 && (stable & (1 << own)) != 0) pick = own;
            if (pick < 0) for (int l = 0; l < 32 && pick < 0; l++) { var n = LayerMask.LayerToName(l) ?? ""; if ((collide & stable & (1 << l)) != 0 && (n.Contains("Player") || n.Contains("Character"))) pick = l; }
            if (pick < 0) { int d = LayerMask.NameToLayer("Default"); if (d >= 0 && (collide & stable & (1 << d)) != 0) pick = d; }
            _layer = pick; _layerFor = motor.Pointer;
            Log.Info($"player bodies on layer {pick} ({(pick >= 0 ? LayerMask.LayerToName(pick) : "none")}); own capsule layer {own}; collidable mask 0x{collide:X8}, stable 0x{stable:X8}");
            return pick;
        }

        static void EnsureBodies(KinematicCharacterMotor motor)
        {
            int layer = PickLayer(motor); if (layer < 0) return;
            foreach (var kv in RemoteAvatars.All)
            {
                var a = kv.Value; if (a?.Root == null) continue;
                if (_bodies.TryGetValue(kv.Key, out var existing) && existing != null) { Fit(existing, motor, a.transform); continue; }
                try
                {
                    var go = new GameObject(BodyName); go.layer = layer; go.transform.SetParent(a.transform, false);
                    var cap = go.AddComponent<CapsuleCollider>(); Fit(cap, motor, a.transform);
                    _bodies[kv.Key] = cap; _bodyIds.Add(cap.GetInstanceID());
                    Log.Info($"player body for {kv.Key}: capsule r={cap.radius:0.00} h={cap.height:0.00} center={cap.center} (the local player's own capsule)");
                }
                catch (Exception e) { Rl.Warn("body", "player body failed: " + e.Message); }
            }
            if (_bodies.Count > RemoteAvatars.All.Count)
                foreach (var id in new List<string>(_bodies.Keys)) if (!RemoteAvatars.All.ContainsKey(id)) { try { _bodyIds.Remove(_bodies[id].GetInstanceID()); UnityEngine.Object.Destroy(_bodies[id].gameObject); } catch { } _bodies.Remove(id); }
        }

        /// <summary>Exactly the local player's capsule (the avatar root sits where the remote player's transform is).</summary>
        static void Fit(CapsuleCollider cap, KinematicCharacterMotor motor, Transform avatar)
        {
            // the avatar root is scaled (AvatarTemplate.Scale): divide so the world size is exactly the player's capsule
            float s = avatar != null ? Math.Max(1e-3f, avatar.lossyScale.y / SquashOf(avatar)) : 1f;
            float r = motor.CapsuleRadius / s, h = motor.CapsuleHeight / s; var c = motor.CharacterTransformToCapsuleCenter / s;
            if (Math.Abs(cap.radius - r) > 1e-3f) cap.radius = r; if (Math.Abs(cap.height - h) > 1e-3f) cap.height = h;
            if ((cap.center - c).sqrMagnitude > 1e-6f) cap.center = c; cap.direction = 1;
        }

        static string FriendIdOf(Collider c)
        {
            try { int id = c.GetInstanceID(); foreach (var kv in _bodies) if (kv.Value != null && kv.Value.GetInstanceID() == id) return kv.Key; } catch { }
            return null;
        }

        void Boost(SRCharacterController cc, KinematicCharacterMotor motor)
        {
            double now = Time.timeAsDouble; bool grounded = motor.GroundingStatus.IsStableOnGround || motor.GroundingStatus.FoundAnyGround;
            if (grounded && IsBody(motor.GroundingStatus.GroundCollider))
            {
                if (_lastOnFriend < 0 && !_toldStanding) { _toldStanding = true; Log.Info("standing on another player (boost jump ready)"); }
                _lastOnFriend = now; _lastFriendId = FriendIdOf(motor.GroundingStatus.GroundCollider);
            }
            if (grounded) _boosted = false;
            var v = motor.BaseVelocity;
            if (BoostRules.ShouldBoost(now, _lastOnFriend, grounded, v.y, _boosted))
            {
                _boosted = true; float boosted = BoostRules.Boosted(v.y);
                motor.BaseVelocity = new Vector3(v.x, boosted, v.z);
                Log.Info($"boost jump off a friend: {v.y:0.0} -> {boosted:0.0} m/s");
                Mod.Diagnostics?.Record("players", "boost-jump", $"from={v.y:0.00}; to={boosted:0.00}");
                try
                {
                    var feet = cc.transform.position;   // presentation only: puff, native jump sound, squash of the player stood on (then published as a BoostFx message for the other seats)
                    Lycoreia.Owned.Fx.NativeFx.Puff(feet);
                    Lycoreia.Owned.Fx.NativeFx.PlayCue(Lycoreia.Owned.Fx.FxCue.Jump, feet);
                    StartSquash(_lastFriendId);
                    _sync?.Publish(_lastFriendId, new Lycoreia.Net.Vec3(feet.x, feet.y, feet.z), boosted);   // every other seat plays it too
                }
                catch (Exception e) { Rl.Warn("boost-fx", "boost jump FX failed: " + e.Message); }
            }
            _wasGrounded = grounded;
        }
    }
}

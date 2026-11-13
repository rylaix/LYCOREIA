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

    public sealed class AvatarBodies : IFramePump
    {
        public string Name => "avatar-bodies";
        public const string BodyName = "Lycoreia.PlayerBody";
        static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);
        static readonly Dictionary<string, CapsuleCollider> _bodies = new Dictionary<string, CapsuleCollider>();
        static readonly HashSet<int> _bodyIds = new HashSet<int>();
        static int _layer = -1; static IntPtr _layerFor;
        double _lastOnFriend = double.NegativeInfinity; bool _boosted, _wasGrounded; static bool _toldStanding;

        public static bool IsBody(Collider c) { try { return c != null && _bodyIds.Contains(c.GetInstanceID()); } catch { return false; } }

        public void Pump()
        {
            var pl = SceneContext.Instance != null ? SceneContext.Instance.Player : null;
            if (pl == null || !Mod.IsNetworkActive) { if (_bodies.Count > 0) Clear(); return; }
            var cc = pl.GetComponent<SRCharacterController>(); var motor = cc != null ? cc.Motor : null; if (motor == null) return;
            EnsureBodies(motor);
            Boost(cc, motor);
        }

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
            float s = avatar != null ? Math.Max(1e-3f, avatar.lossyScale.y) : 1f;
            float r = motor.CapsuleRadius / s, h = motor.CapsuleHeight / s; var c = motor.CharacterTransformToCapsuleCenter / s;
            if (Math.Abs(cap.radius - r) > 1e-3f) cap.radius = r; if (Math.Abs(cap.height - h) > 1e-3f) cap.height = h;
            if ((cap.center - c).sqrMagnitude > 1e-6f) cap.center = c; cap.direction = 1;
        }

        void Boost(SRCharacterController cc, KinematicCharacterMotor motor)
        {
            double now = Time.timeAsDouble; bool grounded = motor.GroundingStatus.IsStableOnGround || motor.GroundingStatus.FoundAnyGround;
            if (grounded && IsBody(motor.GroundingStatus.GroundCollider))
            {
                if (_lastOnFriend < 0 && !_toldStanding) { _toldStanding = true; Log.Info("standing on another player (boost jump ready)"); }
                _lastOnFriend = now;
            }
            if (grounded) _boosted = false;
            var v = motor.BaseVelocity;
            if (BoostRules.ShouldBoost(now, _lastOnFriend, grounded, v.y, _boosted))
            {
                _boosted = true; float boosted = BoostRules.Boosted(v.y);
                motor.BaseVelocity = new Vector3(v.x, boosted, v.z);
                Log.Info($"boost jump off a friend: {v.y:0.0} -> {boosted:0.0} m/s");
                Mod.Diagnostics?.Record("players", "boost-jump", $"from={v.y:0.00}; to={boosted:0.00}");
            }
            _wasGrounded = grounded;
        }
    }
}

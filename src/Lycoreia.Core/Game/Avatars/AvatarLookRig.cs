// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using UnityEngine;

namespace Lycoreia.Game.Avatars
{
    /// <summary>Pure split of the remote look pitch over the upper body (degrees; positive looks down, like the camera's euler x).</summary>
    public static class LookRules
    {
        public const float MaxPitch = 70f, ChestShare = 0.3f, NeckShare = 0.2f, HeadShare = 0.5f;
        /// <summary>Arm hold: full pitch up to ArmKnee, then only ArmSlope of the rest (70 down -> 41), so the hands stay out of the torso.</summary>
        public const float ArmKnee = 25f, ArmSlope = 0.35f, ArmClear = 0.30f, ForeGripAlong = 0.7f, ForeGripBelow = 0.05f;
        public static float ArmPitch(float pitch)
        {
            if (!float.IsFinite(pitch)) return 0f;
            float p = Math.Clamp(pitch, -MaxPitch, MaxPitch); return p > ArmKnee ? ArmKnee + (p - ArmKnee) * ArmSlope : p;
        }
        public static (float chest, float neck, float head) Split(float pitch)
        {
            if (!float.IsFinite(pitch)) return (0, 0, 0);
            float p = Math.Clamp(pitch, -MaxPitch, MaxPitch); return (p * ChestShare, p * NeckShare, p * HeadShare);
        }
        /// <summary>Look direction from the body's forward and right axes and the pitch.</summary>
        public static Vector3 LookDir(Vector3 forward, Vector3 right, float pitch) => Quaternion.AngleAxis(Math.Clamp(float.IsFinite(pitch) ? pitch : 0, -MaxPitch, MaxPitch), right) * forward;
    }

    /// <summary>
    /// Runs in LateUpdate after the animator (the locomotion controller animates legs and body only, it has no head look and no
    /// third-person vac hold): bends chest, neck and head by the remote look pitch and holds the vac in the right hand along the look
    /// direction (two-bone IK for the arm, then the hand turned so the vac's own tank-to-barrel axis points where the player looks).
    /// The left arm (idle hand rig) and the legs (kick rig) belong to the owned rigs and are not touched.
    /// </summary>
    public sealed class AvatarLookRig
    {
        readonly Transform _root, _chest, _neck, _head, _shoulder, _elbow, _hand, _barrel, _tank, _grip, _lShoulder, _lElbow, _lHand, _lIndex, _lMiddle, _lLittle;
        readonly Renderer _barrelR, _gripR;
        float _weight;
        public bool Ready => _root != null && _head != null;
        public bool HasVac => _shoulder != null && _elbow != null && _hand != null && _barrel != null && _tank != null;

        public AvatarLookRig(Animator a, Transform root)
        {
            _root = root;
            _chest = a.GetBoneTransform(HumanBodyBones.UpperChest) ?? a.GetBoneTransform(HumanBodyBones.Chest) ?? a.GetBoneTransform(HumanBodyBones.Spine);
            _neck = a.GetBoneTransform(HumanBodyBones.Neck); _head = a.GetBoneTransform(HumanBodyBones.Head);
            _lShoulder = a.GetBoneTransform(HumanBodyBones.LeftUpperArm); _lElbow = a.GetBoneTransform(HumanBodyBones.LeftLowerArm); _lHand = a.GetBoneTransform(HumanBodyBones.LeftHand);
            _lIndex = a.GetBoneTransform(HumanBodyBones.LeftIndexProximal); _lMiddle = a.GetBoneTransform(HumanBodyBones.LeftMiddleProximal); _lLittle = a.GetBoneTransform(HumanBodyBones.LeftLittleProximal);
            _shoulder = a.GetBoneTransform(HumanBodyBones.RightUpperArm); _elbow = a.GetBoneTransform(HumanBodyBones.RightLowerArm); _hand = a.GetBoneTransform(HumanBodyBones.RightHand);
            if (_hand != null) foreach (var t in _hand.GetComponentsInChildren<Transform>(true))
                {
                    var n = (t.name ?? "").ToLowerInvariant(); if (!t.gameObject.activeInHierarchy) continue;
                    if (_barrel == null && n.Contains("vac_barrel") && !n.Contains("ring")) _barrel = t;
                    if (_tank == null && n.Contains("vac_tank")) _tank = t;
                    if (_grip == null && n.Contains("vac_grip")) _grip = t;
                }
            _barrelR = _barrel != null ? _barrel.GetComponent<Renderer>() : null; _gripR = _grip != null ? _grip.GetComponent<Renderer>() : null;
            Log.Info($"avatar look rig: chest={_chest?.name} neck={_neck?.name} head={_head?.name} arm={_shoulder?.name}/{_elbow?.name}/{_hand?.name} vac barrel={_barrel?.name} tank={_tank?.name} grip={_grip?.name} left={_lShoulder?.name}/{_lElbow?.name}/{_lHand?.name}");
        }

        public void Pose(float pitch, float dt)
        {
            if (!Ready) return;
            _weight = Mathf.MoveTowards(_weight, 1f, dt * 5f);                                   // eases in on the first frames after (re)showing
            var right = _root.right; var (c, n, h) = LookRules.Split(pitch);
            // the bend is relative to the animator's pose: undo last frame's bend first when the animator did not rewrite the bone this
            // frame (a paused/skipped animator or a rig without head curves made the head turn further every frame: the "head spin")
            _chestG.Restore(_chest); _neckG.Restore(_neck); _headG.Restore(_head);
            _chestG.Bend(_chest, c * _weight, right); _neckG.Bend(_neck, n * _weight, right); _headG.Bend(_head, h * _weight, right);
            if (!HasVac) return;
            float s = _root.lossyScale.y; var look = LookRules.LookDir(_root.forward, right, pitch); var up = Vector3.Cross(look, right);
            var sp = _shoulder.position;
            // Looking down: the hold point must not follow the full pitch into the torso. The arm takes a softened pitch, the drop
            // below the look line fades, and the hand stays at least ArmClear in front of the chest; the vac itself still aims at `look`.
            float down = Mathf.Clamp01(pitch / LookRules.MaxPitch);
            var armLook = LookRules.LookDir(_root.forward, right, LookRules.ArmPitch(pitch)); var armUp = Vector3.Cross(armLook, right);
            var target = sp + armLook * (0.42f * s) - right * (0.06f * s) - armUp * (0.14f * s * (1f - 0.6f * down));
            if (_chest != null) { float f = Vector3.Dot(target - _chest.position, _root.forward), min = LookRules.ArmClear * s; if (f < min) target += _root.forward * (min - f); }
            var hint = sp + right * (0.25f * s) - _root.up * (0.35f * s);
            var before = (_shoulder.rotation, _elbow.rotation, _hand.rotation);
            RemoteAvatar.SolveTwoBoneIk(_shoulder, _elbow, _hand, target, hint);
            var axis = _barrel.position - _tank.position;
            if (axis.sqrMagnitude > 1e-6f) _hand.rotation = Quaternion.FromToRotation(axis.normalized, look) * _hand.rotation;
            if (_weight < 1f) { _shoulder.rotation = Quaternion.Slerp(before.Item1, _shoulder.rotation, _weight); _elbow.rotation = Quaternion.Slerp(before.Item2, _elbow.rotation, _weight); _hand.rotation = Quaternion.Slerp(before.Item3, _hand.rotation, _weight); }
        }

        /// <summary>
        /// After the owned idle rig (it curls the left fingers): the left hand takes a fore-grip under the front of the vac, between the
        /// handle and the barrel tip, palm up, fingers pointing across the barrel so the curl wraps them round it. The right hand keeps the
        /// handle; the two hands no longer share one point.
        /// </summary>
        public void PoseLeft()
        {
            if (!Ready || !HasVac || _lShoulder == null || _lElbow == null || _lHand == null || _weight <= 0f) return;
            float s = _root.lossyScale.y; var right = _root.right;
            var axis = _barrel.position - _tank.position; if (axis.sqrMagnitude < 1e-6f) return; axis.Normalize();
            var vacUp = Vector3.Cross(axis, right); if (vacUp.sqrMagnitude < 1e-6f) vacUp = _root.up; vacUp.Normalize();
            var front = _barrelR != null ? _barrelR.bounds.center : _barrel.position;
            var back = _gripR != null ? _gripR.bounds.center : (_grip != null ? _grip.position : _tank.position);
            var contact = Vector3.Lerp(back, front, LookRules.ForeGripAlong) - vacUp * (LookRules.ForeGripBelow * s);
            var fingers = (axis * 0.6f + right * 0.8f).normalized;                                   // across the underside, toward the far side
            var wrist = contact - fingers * (0.07f * s) - vacUp * (0.02f * s);
            var hint = _lShoulder.position - right * (0.3f * s) - _root.up * (0.35f * s);
            var before = (_lShoulder.rotation, _lElbow.rotation, _lHand.rotation);
            RemoteAvatar.SolveTwoBoneIk(_lShoulder, _lElbow, _lHand, wrist, hint);
            OrientLeftHand(fingers, vacUp);
            if (_weight < 1f) { _lShoulder.rotation = Quaternion.Slerp(before.Item1, _lShoulder.rotation, _weight); _lElbow.rotation = Quaternion.Slerp(before.Item2, _lElbow.rotation, _weight); _lHand.rotation = Quaternion.Slerp(before.Item3, _lHand.rotation, _weight); }
        }

        /// <summary>Turns the left wrist so the fingers point along <paramref name="fingers"/> and the palm (the side the fingers curl to,
        /// measured from the rig, so no axis convention is assumed) faces <paramref name="palm"/>.</summary>
        void OrientLeftHand(Vector3 fingers, Vector3 palm)
        {
            if (_lMiddle == null) return;
            var dir = _lMiddle.position - _lHand.position; if (dir.sqrMagnitude < 1e-8f) return;
            _lHand.rotation = Quaternion.FromToRotation(dir.normalized, fingers) * _lHand.rotation;
            if (_lIndex == null || _lLittle == null) return;
            dir = (_lMiddle.position - _lHand.position).normalized;
            var across = (_lIndex.position - _lLittle.position).normalized * AvatarPose.FingerCurlSign;
            var curlTo = Vector3.ProjectOnPlane(Quaternion.AngleAxis(30f, across) * dir - dir, dir);
            var want = Vector3.ProjectOnPlane(palm, dir);
            if (curlTo.sqrMagnitude < 1e-8f || want.sqrMagnitude < 1e-8f) return;
            _lHand.rotation = Quaternion.AngleAxis(Vector3.SignedAngle(curlTo, want, dir), dir) * _lHand.rotation;
        }

        public void Hidden() { _weight = 0f; _chestG = default; _neckG = default; _headG = default; }

        BoneBend _chestG, _neckG, _headG;
        /// <summary>One procedurally bent bone: remembers the animator's rotation and what we wrote, so a frame the animator skipped is undone, never stacked.</summary>
        struct BoneBend
        {
            Quaternion _base, _written; bool _has;
            public void Restore(Transform bone)
            {
                if (bone == null || !_has) return;
                if (Quaternion.Angle(bone.localRotation, _written) < 0.01f) bone.localRotation = _base;     // still our value: the animator did not write it
                _has = false;
            }
            public void Bend(Transform bone, float degrees, Vector3 axis)
            {
                if (bone == null) return;
                _base = bone.localRotation;
                if (Math.Abs(degrees) > 0.01f) bone.rotation = Quaternion.AngleAxis(degrees, axis) * bone.rotation;
                _written = bone.localRotation; _has = true;
            }
        }
    }
}

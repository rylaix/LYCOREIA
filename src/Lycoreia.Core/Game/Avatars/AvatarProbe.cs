// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Lycoreia.Game.Avatars
{
    /// <summary>
    /// One-shot dumps (controller parameters, layers, clips; template component inventory; skinned-mesh bone coverage) and a change log of
    /// the local body animator's parameters and playing clips, so the remote avatar can be driven with what the game really uses.
    /// Everything goes to latest.log with the prefix "[anim-probe]".
    /// </summary>
    public sealed class AvatarProbe : IFramePump
    {
        public string Name => "avatar-probe";
        const int MaxChangeLines = 1200; const double SampleEvery = 0.25;
        static bool _controllerDumped, _templateDumped; static readonly HashSet<string> _avatarDumped = new HashSet<string>();
        readonly Dictionary<string, string> _last = new Dictionary<string, string>(); int _lines; double _next;

        static void P(string s) => Log.Info("[anim-probe] " + s);

        public void Pump()
        {
            double now = Time.unscaledTimeAsDouble; if (now < _next) return; _next = now + SampleEvery;
            try
            {
                if (!_templateDumped && AvatarTemplate.Prefab != null) { _templateDumped = true; DumpInventory("template", AvatarTemplate.Prefab); }
                var local = AvatarTemplate.LocalBodyAnimator(); if (local == null) return;
                if (!_controllerDumped) { _controllerDumped = true; DumpController("local body", local); DumpBones("local body", local); }
                foreach (var kv in RemoteAvatars.All)
                {
                    var a = kv.Value; if (!a || a.animator == null || !a.hasAnimationController || !_avatarDumped.Add(kv.Key)) continue;
                    DumpInventory("remote avatar " + kv.Key, a.Root); DumpController("remote avatar " + kv.Key, a.animator); DumpBones("remote avatar " + kv.Key, a.animator);
                }
                if (_lines < MaxChangeLines) SampleChanges(local, now, "local");
                foreach (var kv in RemoteAvatars.All) { var ra = kv.Value; if (_lines < MaxChangeLines && ra && ra.animator != null) SampleChanges(ra.animator, now, "remote " + kv.Key + (ra.animator.enabled ? "" : " (disabled)")); }
            }
            catch (Exception e) { if (_lines++ < MaxChangeLines) P("probe failed: " + e.Message); }
        }

        static void DumpController(string who, Animator a)
        {
            var c = a.runtimeAnimatorController;
            P($"{who}: controller={(c != null ? c.name : "none")} avatar={(a.avatar != null ? a.avatar.name : "none")} human={a.isHuman} layers={a.layerCount} rootMotion={a.applyRootMotion} culling={a.cullingMode} enabled={a.enabled} speed={a.speed}");
            foreach (var p in a.parameters) P($"  param {p.name} {p.type} default f={p.defaultFloat} i={p.defaultInt} b={p.defaultBool}");
            for (int i = 0; i < a.layerCount; i++)
            {
                var clips = a.GetCurrentAnimatorClipInfo(i); var st = a.GetCurrentAnimatorStateInfo(i);
                P($"  layer {i} '{a.GetLayerName(i)}' weight={a.GetLayerWeight(i):0.00} stateHash={st.fullPathHash} clips=[{string.Join(",", clips.Select(x => x.clip != null ? x.clip.name + "@" + x.weight.ToString("0.00") : "?"))}]");
            }
            if (c != null) foreach (var clip in c.animationClips) if (clip != null) P($"  clip {clip.name} length={clip.length:0.00} loop={clip.isLooping} humanMotion={clip.humanMotion}");
        }

        /// <summary>Do the animator's humanoid bones belong to the visible skinned meshes? (A pose on other bones never shows.)</summary>
        static void DumpBones(string who, Animator a)
        {
            var root = a.transform; var mapped = new Dictionary<Transform, string>();
            for (int b = 0; b < (int)HumanBodyBones.LastBone; b++) { try { var t = a.GetBoneTransform((HumanBodyBones)b); if (t != null) mapped[t] = ((HumanBodyBones)b).ToString(); } catch { } }
            P($"{who}: {mapped.Count} humanoid bones mapped; hips={(a.GetBoneTransform(HumanBodyBones.Hips) is { } h ? Path(h, root) : "none")}");
            var smrs = a.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var parentSmrs = a.transform.root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (parentSmrs.Length != smrs.Length) P($"{who}: {parentSmrs.Length - smrs.Length} skinned meshes are outside the animator's hierarchy");
            foreach (var r in parentSmrs)
            {
                if (r == null) continue; var bones = r.bones; int n = bones != null ? bones.Length : 0, hit = 0, under = 0;
                if (bones != null) foreach (var t in bones) { if (t == null) continue; if (mapped.ContainsKey(t)) hit++; if (t.IsChildOf(root)) under++; }
                P($"  smr {Path(r.transform, a.transform.root)} active={r.gameObject.activeInHierarchy} enabled={r.enabled} layer={r.gameObject.layer} bones={n} humanoid-hits={hit} under-animator={under} rootBone={(r.rootBone != null ? Path(r.rootBone, a.transform.root) : "none")}");
            }
        }

        static void DumpInventory(string who, GameObject go)
        {
            var counts = new Dictionary<string, int>(); var notable = new List<string>();
            foreach (var an in go.GetComponentsInChildren<Animator>(true)) if (an != null) P($"  animator@{Path(an.transform, go.transform)} enabled={an.enabled} controller={(an.runtimeAnimatorController != null ? an.runtimeAnimatorController.name : "none")} human={an.isHuman}");
            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue; string t = c.GetIl2CppType().Name; counts[t] = counts.TryGetValue(t, out var n) ? n + 1 : 1;
                if (t.Contains("Joint") || t.Contains("Constraint") || t.Contains("Particle") || t == "Rigidbody" || t == "MeshRenderer" || t.Contains("Light") || t.Contains("Audio"))
                    if (notable.Count < 60) notable.Add($"{t}@{Path(c.transform, go.transform)}");
            }
            P($"{who}: components " + string.Join(", ", counts.OrderByDescending(k => k.Value).Select(k => k.Key + "=" + k.Value)));
            foreach (var s in notable) P("  " + s);
        }

        void SampleChanges(Animator a, double now, string who)
        {
            var sb = new StringBuilder();
            foreach (var p in AnimatorBindings.For(a))
            {
                string v = p.type switch { AnimatorControllerParameterType.Float => a.GetFloat(p.nameHash).ToString("0.00"), AnimatorControllerParameterType.Int => a.GetInteger(p.nameHash).ToString(), AnimatorControllerParameterType.Bool => a.GetBool(p.nameHash) ? "1" : "0", _ => "" };
                if (p.type == AnimatorControllerParameterType.Trigger) continue;
                string key = who + "p:" + p.name; if (!_last.TryGetValue(key, out var old) || old != v) { _last[key] = v; sb.Append(' ').Append(p.name).Append('=').Append(v); }
            }
            for (int i = 0; i < a.layerCount; i++)
            {
                var clips = a.GetCurrentAnimatorClipInfo(i); string v = string.Join("+", clips.Select(x => x.clip != null ? x.clip.name : "?")) + $"|w{a.GetLayerWeight(i):0.0}";
                string key = who + "l:" + i; if (!_last.TryGetValue(key, out var old) || old != v) { _last[key] = v; sb.Append(" L").Append(i).Append('=').Append(v); }
            }
            if (sb.Length > 0) { _lines++; P($"{who} t={now:0.00}:{sb}"); }
        }

        static string Path(Transform t, Transform root) { var s = t.name; for (var p = t.parent; p != null && p != root; p = p.parent) s = p.name + "/" + s; return s; }
    }
}

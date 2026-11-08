// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.Regions;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.Players;
using UnityEngine;

namespace Lycoreia.Game.Avatars
{
    /// <summary>
    /// The hidden, persistent avatar template (scale 0.85), cloned from the main-menu player model; a capsule when none is found.
    /// The menu model carries a generic rig: remote avatars and the first-person legs get the local player's humanoid avatar and
    /// animation controller once the gameplay player exists (<see cref="Retarget"/>).
    /// </summary>
    public static class AvatarTemplate
    {
        public const float Scale = 0.85f, LabelHeight = 3f;
        public const double CaptureWindowSeconds = 30, CaptureRetrySeconds = 0.5;
        static GameObject _template;
        static double _captureUntil = -1, _nextCaptureTry;
        public static bool Ready => _template != null;
        /// <summary>The template object (the owned first-person legs copy their leg mesh from it).</summary>
        public static GameObject Prefab => _template;

        /// <summary>Scene "MainMenuEnvironment" loaded: try now, then keep retrying from the frame pump for a while (the menu model may activate later).</summary>
        public static void BeginCapture()
        {
            if (_template != null) return;
            _captureUntil = Time.unscaledTimeAsDouble + CaptureWindowSeconds; _nextCaptureTry = 0;
            PumpCapture();
        }

        /// <summary>From OnUpdate: one capture attempt every 0.5 s inside the window; the candidate list is logged once if it never succeeds.</summary>
        public static void PumpCapture()
        {
            if (_template != null || _captureUntil < 0) return;
            double now = Time.unscaledTimeAsDouble; if (now < _nextCaptureTry) return; _nextCaptureTry = now + CaptureRetrySeconds;
            bool last = now >= _captureUntil;
            if (CaptureFromMainMenu(logCandidates: last) || last) _captureUntil = -1;
        }

        /// <summary>Clone the humanoid-looking player model of the loaded scenes (inactive objects included) and strip its behaviours. True when captured.</summary>
        public static bool CaptureFromMainMenu(bool logCandidates = true)
        {
            if (_template != null) return true;
            try
            {
                Animator best = null; int bestScore = int.MinValue; var seen = logCandidates ? new List<(int, string)>() : null;
                foreach (var a in Resources.FindObjectsOfTypeAll<Animator>())
                {
                    if (a == null) continue; var go = a.gameObject; var scene = go.scene;
                    if (!scene.IsValid() || !scene.isLoaded || (go.name ?? "").StartsWith("Lycoreia.", StringComparison.Ordinal)) continue;   // assets and our own objects
                    int score = Score(a, out string path);
                    seen?.Add((score, $"{path} [{scene.name}] active={go.activeInHierarchy} human={a.avatar != null && a.avatar.isHuman}"));
                    if (score > bestScore) { bestScore = score; best = a; }
                }
                if (best == null || bestScore < MinScore)
                {
                    if (logCandidates)
                    {
                        Log.Warn("avatar template: no player model in the main menu; remote players use a capsule");
                        foreach (var c in seen.OrderByDescending(c => c.Item1).Take(12)) Log.Info($"  avatar candidate score={c.Item1}: {c.Item2}");
                    }
                    return false;
                }

                // Instantiate under an inactive holder so none of the menu model's own behaviours wake up in the copy.
                var holder = new GameObject("Lycoreia.AvatarTemplateHolder"); holder.SetActive(false);
                var clone = UnityEngine.Object.Instantiate(best.gameObject, holder.transform); clone.name = "Lycoreia.AvatarTemplate"; clone.SetActive(false);
                clone.transform.SetParent(null, false); UnityEngine.Object.Destroy(holder);
                foreach (var b in clone.GetComponentsInChildren<MonoBehaviour>(true)) if (b != null) UnityEngine.Object.Destroy(b);   // keep only the model and its animator
                clone.transform.localPosition = Vector3.zero; clone.transform.localRotation = Quaternion.identity; clone.transform.localScale = Vector3.one * Scale;
                UnityEngine.Object.DontDestroyOnLoad(clone); _template = clone;
                Score(best, out string chosen);
                Log.Info($"avatar template captured from {chosen} (score {bestScore}, humanoid={best.avatar != null && best.avatar.isHuman}, controller={(best.runtimeAnimatorController != null ? best.runtimeAnimatorController.name : "none")})");
                return true;
            }
            catch (Exception e) { Log.Warn("avatar template capture failed: " + e.Message); return false; }
        }

        const int MinScore = 40;
        /// <summary>Beatrix by name first; otherwise the richest skinned humanoid-looking rig. Slimes and props score low.</summary>
        static int Score(Animator a, out string path)
        {
            path = PathOf(a.transform); var lower = path.ToLowerInvariant();
            var skinned = a.GetComponentsInChildren<SkinnedMeshRenderer>(true); if (skinned == null || skinned.Length == 0) return int.MinValue / 2;
            int bones = 0; foreach (var r in skinned) if (r != null && r.bones != null) bones = Math.Max(bones, r.bones.Length);
            int score = Math.Min(bones, 120);
            if (lower.Contains("beatrix")) score += 1000;
            if (a.avatar != null && a.avatar.isHuman) score += 200;
            if (lower.Contains("slime") || lower.Contains("largo") || lower.Contains("gordo") || lower.Contains("chick") || lower.Contains("hen")) score -= 500;
            foreach (var t in a.GetComponentsInChildren<Transform>(true))
            {
                var n = (t.name ?? "").ToLowerInvariant();
                if (n.Contains("spine") || n.Contains("hips") || n.Contains("pelvis")) { score += 60; break; }
            }
            return score;
        }
        static string PathOf(Transform t) { var s = t.name; for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s; return s; }

        /// <summary>The local gameplay player's humanoid body animator (null in the menu or while loading).</summary>
        static Animator _localBody;
        static IntPtr _localBodyPlayer;
        public static Animator LocalBodyAnimator()
        {
            try
            {
                if (GameState.Loading) return null;
                var pl = SceneContext.Instance != null ? SceneContext.Instance.Player : null; if (pl == null) return null;
                if (_localBodyPlayer == pl.Pointer && _localBody != null) return _localBody;
                var a = pl.GetComponent<Animator>();
                if (!IsUsableHumanoid(a)) foreach (var c in pl.GetComponentsInChildren<Animator>(true)) if (IsUsableHumanoid(c)) { a = c; break; }
                if (IsUsableHumanoid(a)) { _localBodyPlayer = pl.Pointer; return _localBody = a; }
            }
            catch { }
            return null;
        }
        static bool IsUsableHumanoid(Animator a) => a != null && a.avatar != null && a.avatar.isHuman && a.runtimeAnimatorController != null;

        /// <summary>Gives a copy of the menu model the local player's humanoid avatar and controller (the menu clips barely bind to it). True when it is humanoid afterwards.</summary>
        public static bool Retarget(Animator target)
        {
            if (target == null) return false;
            var src = LocalBodyAnimator();
            if (src != null)
            {
                if (target.avatar != src.avatar) target.avatar = src.avatar;
                if (target.runtimeAnimatorController != src.runtimeAnimatorController) target.runtimeAnimatorController = src.runtimeAnimatorController;
                target.Rebind();
            }
            return IsUsableHumanoid(target);
        }

        public static GameObject Create(string name)
        {
            GameObject go;
            if (_template != null)
            {
                go = UnityEngine.Object.Instantiate(_template); StripMenuProps(go); go.SetActive(true);
                foreach (var c in go.GetComponentsInChildren<Collider>(true)) if (c != null) c.enabled = false;         // remote players never block vac rays or bodies
                foreach (var b in go.GetComponentsInChildren<Rigidbody>(true)) if (b != null) b.isKinematic = true;
                foreach (var r in go.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (r != null) r.updateWhenOffscreen = true;   // procedural poses leave the bind-pose bounds
                foreach (var an in go.GetComponentsInChildren<Animator>(true)) if (an != null) { an.applyRootMotion = false; an.cullingMode = AnimatorCullingMode.AlwaysAnimate; }
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Capsule); go.transform.localScale = new Vector3(0.7f, 0.9f, 0.7f) * Scale;
                var col = go.GetComponent<Collider>(); if (col != null) UnityEngine.Object.Destroy(col);
            }
            go.name = "Lycoreia.Avatar." + name; UnityEngine.Object.DontDestroyOnLoad(go);
            return go;
        }

        /// <summary>
        /// Names (lower case, substring) of the main-menu scene's props on the Beatrix model: coffee mug with liquid and steam, sandwich,
        /// slimepedia (on the hip and the reading one), the physics vacpac hose, crumbs FX and the loose copies at the rig root.
        /// The vac in the right hand (vacAttach/vacStandard) stays: it is what a vacuuming player holds.
        /// </summary>
        static readonly string[] PropNames = { "mug", "coffee", "steam", "sandwich", "slimepedia", "fx crumbs", "vacpac_ground", "mesh_vacpac_hose" };

        /// <summary>
        /// Removes what only made sense in the menu scene: props, joints and constraints (a physics chain on a kinematic copy only
        /// fights the pose), extra rigid bodies and particles, and every Animator except the root one (the menu's own controller would
        /// keep writing its idle pose onto the same bones as the retargeted humanoid controller).
        /// </summary>
        public static void StripMenuProps(GameObject go)
        {
            int props = 0, comps = 0, anims = 0; var root = go.transform; var keepAnim = go.GetComponent<Animator>() ?? go.GetComponentInChildren<Animator>(true);
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t == root) continue; string n = (t.name ?? "").ToLowerInvariant();
                bool looseVac = n.StartsWith("vacstandard") && t.parent != null && t.parent.name == "rig_new";            // the menu's spare vac lying at the rig root
                if (Array.Exists(PropNames, p => n.Contains(p)) || looseVac) { t.gameObject.SetActive(false); props++; }
            }
            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue; string tn = c.GetIl2CppType().Name;
                if (tn.EndsWith("Joint") || tn.EndsWith("Constraint") || tn == "ParticleSystemRenderer") { UnityEngine.Object.Destroy(c); comps++; }
            }
            foreach (var a in go.GetComponentsInChildren<Animator>(true))
                if (a != null && a != keepAnim) { a.enabled = false; anims++; Log.Info($"avatar template: disabled extra animator on {a.name} ({(a.runtimeAnimatorController != null ? a.runtimeAnimatorController.name : "no controller")})"); }
            if (!_stripLogged) { _stripLogged = true; Log.Info($"avatar template: {props} menu props hidden, {comps} joints/constraints/particle renderers removed, {anims} extra animators disabled; driving animator {(keepAnim != null ? keepAnim.name : "none")}"); }
        }
        static bool _stripLogged;

        static Font _font; static bool _fontTried;
        /// <summary>Unity 6 ships "LegacyRuntime.ttf" as the built-in font; a TextMesh without a font draws nothing.</summary>
        public static Font LabelFont()
        {
            if (_fontTried) return _font; _fontTried = true;
            foreach (var n in new[] { "LegacyRuntime.ttf", "Arial.ttf" }) { try { _font = Resources.GetBuiltinResource<Font>(n); } catch { } if (_font != null) break; }
            return _font;
        }
    }

    /// <summary>One remote player's avatar: model, nametag and smoothed pose. Owned partials add idle, kick and hand rigs.</summary>
    public sealed partial class RemoteAvatar
    {
        public readonly string PlayerId; public string Name; public readonly GameObject Root; public readonly Transform transform;
        internal readonly Animator animator; readonly TextMesh _label; readonly Transform _labelRoot;
        public readonly AvatarSmoother Smoother = new AvatarSmoother();
        public Vector3 MapPosition;
        public int SceneGroup = -1; public bool HasState; public bool Moving, Sprinting; public int Airborne; public float MoveX, MoveZ, SpeedX, SpeedZ;
        public Color Colour = Color.white;
        bool _shown;                    // the root starts inactive; the first visible tick shows it
        bool _rigReady; double _nextRigTry; AvatarLookRig _look; int _cullingMask = int.MinValue; Renderer[] _renderers = Array.Empty<Renderer>();
        // base surface the owned rig partials use
        internal Transform leftArmUpper, leftArmLower, leftHand;
        internal bool IsLocal => false;
        internal bool hasAnimationController => animator != null && animator.runtimeAnimatorController != null && animator.isHuman;
        internal RemoteAvatar model => this;
        internal int AirborneState => Airborne;
        internal int GetInstanceID() => Root != null ? Root.GetInstanceID() : 0;
        public T GetComponentInChildren<T>() where T : Component => Root != null ? Root.GetComponentInChildren<T>() : null;
        /// <summary>False once the avatar's object is gone, like a destroyed Unity object.</summary>
        public static implicit operator bool(RemoteAvatar a) => a != null && a.Root != null;

        public RemoteAvatar(string id, string name)
        {
            PlayerId = id; Name = name ?? "Player"; Root = AvatarTemplate.Create(id); transform = Root.transform;
            animator = Root.GetComponent<Animator>() ?? Root.GetComponentInChildren<Animator>(true);        // the one StripMenuProps kept enabled
            _renderers = Root.GetComponentsInChildren<Renderer>(true);
            var labelGo = new GameObject("Lycoreia.Nametag"); _labelRoot = labelGo.transform; _labelRoot.SetParent(transform, false); _labelRoot.localPosition = Vector3.up * AvatarTemplate.LabelHeight / AvatarTemplate.Scale;
            _label = labelGo.AddComponent<TextMesh>(); _label.text = Name; _label.anchor = TextAnchor.MiddleCenter; _label.alignment = TextAlignment.Center; _label.characterSize = 0.06f; _label.fontSize = 48; _label.color = Colour;
            var font = AvatarTemplate.LabelFont(); if (font != null) { _label.font = font; var mr = labelGo.GetComponent<MeshRenderer>(); if (mr != null) mr.sharedMaterial = font.material; }
            Root.SetActive(false);
        }

        /// <summary>Once the local player exists: humanoid avatar and controller from the local body, then the bone refs and the owned rigs.</summary>
        void EnsureRig(double now)
        {
            if (_rigReady || animator == null || now < _nextRigTry) return; _nextRigTry = now + 1.0;
            if (!AvatarTemplate.Retarget(animator) && !hasAnimationController) return;
            leftArmUpper = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm); leftArmLower = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm); leftHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            _rigReady = true;
            try { _look = new AvatarLookRig(animator, transform); } catch (Exception e) { Log.Debug("avatar look rig: " + e.Message); }
            try { OwnedSetup(); } catch (Exception e) { Log.Debug("avatar rig setup: " + e.Message); }
            Log.Info($"avatar of {PlayerId}: humanoid rig ready ({animator.runtimeAnimatorController?.name})");
        }

        /// <summary>Puts the model on layers the gameplay camera draws (the menu model's layers may be culled in game).</summary>
        void ApplyLayers(Camera cam)
        {
            if (cam == null || cam.cullingMask == _cullingMask) return; _cullingMask = cam.cullingMask;
            int def = LayerMask.NameToLayer("Default");
            foreach (var r in _renderers)
            {
                if (r == null) continue; int layer = Lycoreia.Owned.RemotePlayerRenderLayerPolicy.ResolveRendererLayer(r.gameObject.layer, _cullingMask, def);
                if (layer >= 0) r.gameObject.layer = layer;
            }
        }

        public void SetName(string name) { Name = name ?? Name; if (_label != null) _label.text = Lycoreia.Game.Prefs.StreamerMode && PlayerId != Mod.LocalId ? "Player" : Name; }
        public string ColourHex;
        public void SetColour(Color c) { Colour = c; if (_label != null) _label.color = c; }
        /// <summary>Applies a "#RRGGBB" profile colour (alpha is always opaque); true when it changed.</summary>
        public bool SetColourHex(string hex)
        {
            if (hex == ColourHex || !ProfileColour.TryParse(hex, out var r, out var g, out var b)) return false;
            ColourHex = hex; SetColour(new Color(r / 255f, g / 255f, b / 255f, 1f)); return true;
        }

        public void Push(PlayerUpdate u, double now)
        {
            MapPosition = new Vector3(u.Position.X, u.Position.Y, u.Position.Z);
            Smoother.Push(now, u.Position, u.BodyYaw, u.LookPitch);
            SceneGroup = u.SceneGroup; Moving = u.Moving; Sprinting = u.Sprinting; Airborne = u.Airborne; MoveX = u.MoveX; MoveZ = u.MoveZ; SpeedX = u.SpeedX; SpeedZ = u.SpeedZ; HasState = true;
        }

        public void Tick(double now, float dt, int localScene, Camera cam)
        {
            bool visible = HasState && AvatarRules.Visible(SceneGroup, localScene);
            if (visible != _shown) { _shown = visible; Root.SetActive(visible); if (!visible) _look?.Hidden(); }   // toggled only on change (never per frame)
            TickGadgetGhost(now, visible);
            if (!visible) return;
            Smoother.Step(now, dt); var p = Smoother.Position;
            transform.SetPositionAndRotation(new Vector3(p.X, p.Y, p.Z), Quaternion.Euler(0, Smoother.Yaw, 0));
            EnsureRig(now); ApplyLayers(cam);
            if (animator != null) DriveAnimator();
            if (cam != null && _labelRoot != null)
            {
                float d = Vector3.Distance(cam.transform.position, _labelRoot.position);
                _labelRoot.localScale = Vector3.one * AvatarRules.NametagScale(d) / AvatarTemplate.Scale;
                _labelRoot.rotation = Quaternion.LookRotation(_labelRoot.position - cam.transform.position);
            }
        }
        static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);

        /// <summary>LateUpdate, after the animator wrote this frame's pose: the procedural rigs (kick, idle, fingers) pose bones on top of it.</summary>
        public void LateTick(float dt)
        {
            if (!_shown || !_rigReady || Root == null) return;
            try { _look?.Pose(Smoother.Pitch, dt); } catch (Exception e) { Rl.Warn("avatar-look", "avatar look rig: " + e.Message); }
            try { OwnedTick(dt); } catch (Exception e) { Rl.Warn("avatar-owned", "avatar rig: " + e.Message); }
            try { _look?.PoseLeft(); } catch (Exception e) { Rl.Warn("avatar-owned", "avatar rig: " + e.Message); }
        }

        // BeaBody_controller parameters (logged from the game, [anim-probe] 2026-11-02): HorizontalMovement, ForwardMovement, Yaw, Height
        // (floats), Moving, Sprinting (bools), AirborneState (int), HorizontalSpeed, ForwardSpeed (floats). The sender mirrors its own
        // animator's values, so they are written as received. Only parameters the controller has are written.
        void DriveAnimator()
        {
            try
            {
                if (animator.runtimeAnimatorController == null || !animator.enabled) return;
                foreach (var prm in AnimatorBindings.For(animator))
                {
                    switch (prm.name)
                    {
                        case "Moving": if (prm.type == AnimatorControllerParameterType.Bool) animator.SetBool(prm.nameHash, Moving); break;
                        case "Sprinting": if (prm.type == AnimatorControllerParameterType.Bool) animator.SetBool(prm.nameHash, Sprinting); break;
                        case "AirborneState": if (prm.type == AnimatorControllerParameterType.Int) animator.SetInteger(prm.nameHash, Airborne); break;
                        case "HorizontalMovement": if (prm.type == AnimatorControllerParameterType.Float) animator.SetFloat(prm.nameHash, MoveX, 0.08f, Time.unscaledDeltaTime); break;
                        case "ForwardMovement": if (prm.type == AnimatorControllerParameterType.Float) animator.SetFloat(prm.nameHash, MoveZ, 0.08f, Time.unscaledDeltaTime); break;
                        case "HorizontalSpeed": if (prm.type == AnimatorControllerParameterType.Float) animator.SetFloat(prm.nameHash, SpeedX, 0.08f, Time.unscaledDeltaTime); break;
                        case "ForwardSpeed": if (prm.type == AnimatorControllerParameterType.Float) animator.SetFloat(prm.nameHash, SpeedZ, 0.08f, Time.unscaledDeltaTime); break;
                    }
                }
            }
            catch (Exception e) { Rl.Warn("avatar-anim", "avatar animator: " + e.Message); }
        }

        /// <summary>
        /// Two-bone IK (upper, lower, end): rotates the upper bone so the chain reaches towards <paramref name="target"/> with the knee/elbow
        /// bending towards <paramref name="hint"/>, then the lower bone so the end lands on the target (law of cosines). Lengths are kept.
        /// </summary>
        internal static void SolveTwoBoneIk(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 hint)
        {
            if (upper == null || lower == null || end == null) return;
            Vector3 a = upper.position, b = lower.position, c = end.position;
            float ab = Vector3.Distance(a, b), bc = Vector3.Distance(b, c); if (ab < 1e-4f || bc < 1e-4f) return;
            Vector3 toT = target - a; float d = Mathf.Clamp(toT.magnitude, Mathf.Abs(ab - bc) + 1e-3f, ab + bc - 1e-3f); if (toT.sqrMagnitude < 1e-8f) return;
            Vector3 dir = toT.normalized;
            Vector3 bendDir = hint - a; bendDir -= Vector3.Project(bendDir, dir); if (bendDir.sqrMagnitude < 1e-8f) { bendDir = (b - a) - Vector3.Project(b - a, dir); if (bendDir.sqrMagnitude < 1e-8f) bendDir = Vector3.Cross(dir, Vector3.right); }
            bendDir.Normalize();
            float cosA = Mathf.Clamp((ab * ab + d * d - bc * bc) / (2f * ab * d), -1f, 1f); float angA = Mathf.Acos(cosA);
            Vector3 wantB = a + (dir * Mathf.Cos(angA) + bendDir * Mathf.Sin(angA)) * ab;
            upper.rotation = Quaternion.FromToRotation(b - a, wantB - a) * upper.rotation;
            Vector3 wantC = a + dir * d; b = lower.position; c = end.position;
            lower.rotation = Quaternion.FromToRotation(c - b, wantC - b) * lower.rotation;
        }

        public void Destroy() { try { OwnedDestroy(); } catch { } if (Root != null) UnityEngine.Object.Destroy(Root); }

        // hooks the owned partials implement (idle life, kick, hand rig); empty defaults keep the core independent of them
        partial void OwnedSetup();
        partial void OwnedTick(float dt);
        partial void OwnedDestroy();
    }

    public sealed partial class RemoteAvatars
    {
        /// <summary>
        /// Copies the local body animator's own locomotion parameters into the update (the game computes them from input and the
        /// character controller); the velocity-derived values stay as the fallback when the animator is missing or lacks a parameter.
        /// </summary>
        static void MirrorLocalAnimator(PlayerUpdate m)
        {
            var a = AvatarTemplate.LocalBodyAnimator(); if (a == null) return;
            foreach (var p in AnimatorBindings.For(a))
            {
                switch (p.name)
                {
                    case "HorizontalMovement" when p.type == AnimatorControllerParameterType.Float: m.MoveX = a.GetFloat(p.nameHash); break;
                    case "ForwardMovement" when p.type == AnimatorControllerParameterType.Float: m.MoveZ = a.GetFloat(p.nameHash); break;
                    case "HorizontalSpeed" when p.type == AnimatorControllerParameterType.Float: m.SpeedX = a.GetFloat(p.nameHash); break;
                    case "ForwardSpeed" when p.type == AnimatorControllerParameterType.Float: m.SpeedZ = a.GetFloat(p.nameHash); break;
                    case "Moving" when p.type == AnimatorControllerParameterType.Bool: m.Moving = a.GetBool(p.nameHash); break;
                    case "Sprinting" when p.type == AnimatorControllerParameterType.Bool: m.Sprinting = a.GetBool(p.nameHash); break;
                    case "AirborneState" when p.type == AnimatorControllerParameterType.Int: m.Airborne = a.GetInteger(p.nameHash); break;
                }
            }
        }
    }

    /// <summary>The roster's avatars: created on join (never for the local id), removed on leave, kept across scene loads; never recreated by a resync.</summary>
    public sealed partial class RemoteAvatars : IFramePump, IPlayerWorld
    {
        public string Name => "avatars";
        public static readonly Dictionary<string, RemoteAvatar> All = new Dictionary<string, RemoteAvatar>();
        public static PlayerSync Sync;
        /// <summary>The installed roster (test rigs create avatars through it).</summary>
        public static RemoteAvatars Current;
        /// <summary>The LateUpdate half of the roster (owned rigs after the animator).</summary>
        public readonly IFramePump Late = new LatePump();
        sealed class LatePump : IFramePump
        {
            public string Name => "avatars-late";
            public void Pump() { if (All.Count == 0) return; float dt = Time.unscaledDeltaTime; foreach (var a in All.Values) a.LateTick(dt); }
        }
        public const double SendEvery = 0.05; double _nextSend; PlayerUpdate _out = new PlayerUpdate();
        static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);
        public static event Action<PlayerPing> PingShown; public static event Action<PlayerFx> FxShown; public static event Action<PlayerKick> KickApplied;

        public static RemoteAvatars Install(Session s)
        {
            var r = new RemoteAvatars(); Current = r; Sync = new PlayerSync(s, r, () => Time.unscaledTimeAsDouble);
            s.PlayerAdded += p => { if (p != null && p.Id != s.LocalId) r.Ensure(p.Id, p.Name); };
            s.ProfileChanged += p => { if (p != null && All.TryGetValue(p.Id, out var av)) av.SetColourHex(p.ColourHex); };
            Mod.Snapshots?.Register(Lycoreia.Snapshot.SnapStep.VacPalettes, ctx => { foreach (var profile in s.KnownProfiles()) ctx.Send(profile); });
            s.PlayerRemoved += p => { if (p != null) r.Remove(p.Id); };
            s.ClientDisconnected += _ => r.Clear(); s.ServerStopped += r.Clear;
            return r;
        }

        public RemoteAvatar Ensure(string id, string name)
        {
            if (string.IsNullOrEmpty(id) || id == Mod.LocalId) return null;
            if (All.TryGetValue(id, out var a) && a.Root != null) { a.SetName(name); return a; }
            try { a = new RemoteAvatar(id, name); a.SetColourHex(Mod.Session?.ColourOf(id)); All[id] = a; Lycoreia.Game.Players_Bridge.Set(id, a.transform); return a; }
            catch (Exception e) { Log.Warn($"could not create the avatar of {id}: {e.Message}"); return null; }
        }
        public void Remove(string id) { if (All.Remove(id, out var a)) a.Destroy(); Lycoreia.Game.Players_Bridge.Set(id, null); }
        public void Clear() { foreach (var id in All.Keys.ToList()) Remove(id); }

        // ---- IPlayerWorld
        public bool HasAvatar(string id) => All.ContainsKey(id) || (Mod.Session != null && Mod.Session.Players.ContainsKey(id) && Ensure(id, Mod.Session.Players[id].Name) != null);
        public void ShowUpdate(PlayerUpdate u) { if (u.PlayerId != Mod.LocalId && (All.TryGetValue(u.PlayerId, out var a) || (a = Ensure(u.PlayerId, null)) != null)) a.Push(u, Time.unscaledTimeAsDouble); }
        public void ShowGadgetGhost(GadgetGhost g) { if (g?.PlayerId != null && g.PlayerId != Mod.LocalId && (All.TryGetValue(g.PlayerId, out var a) || (a = Ensure(g.PlayerId, null)) != null)) a.ShowGadgetGhost(g); }
        public void ShowPing(PlayerPing p) { if (p.PlayerId != null && All.TryGetValue(p.PlayerId, out var a)) { try { a.PlayPoint(new Vector3(p.Position.X, p.Position.Y, p.Position.Z)); } catch { } } PingShown?.Invoke(p); }
        public void ShowFx(PlayerFx fx) => FxShown?.Invoke(fx);
        public void ApplyKick(PlayerKick k) { if (k.KickerId != null && All.TryGetValue(k.KickerId, out var a)) { try { a.PlayKick(); } catch (Exception e) { Rl.Warn("kick-anim", "kick animation: " + e.Message); } } KickApplied?.Invoke(k); }
        public bool TryActorPosition(long actorId, out Vec3 position) { var go = GameActorWorld.ObjectOf(actorId); if (go != null) { position = GameActorWorld.W(go.transform.position); return true; } position = default; return false; }

        public static int LocalSceneGroup()
        {
            try { var g = SystemContext.Instance?.SceneLoader?._currentSceneGroup; return g != null ? SceneGroups.IdOf(g) : -1; }
            catch { return -1; }
        }

        string _sentColour; int _sentGen = -1; Session _sentSession;
        /// <summary>Sends the local colour on join and whenever the setting changes; applies stored colours to avatars created before their profile arrived.</summary>
        void ProfilePump()
        {
            var s = Mod.Session; if (s == null || !Mod.IsNetworkActive || s.LocalId == null) return;
            if (s.IsGuest && !s.Joined) return;
            var colour = ProfileColour.Normalize(Prefs.Get<string>(Settings.UsernameColor)) ?? "#FFFFFF";
            if (colour != _sentColour || s.Generation != _sentGen || !ReferenceEquals(s, _sentSession))
            {
                _sentColour = colour; _sentGen = s.Generation; _sentSession = s; s.SendProfile(colour);
                Mod.Diagnostics?.Record("players", "profile-sent", $"colour={colour}");
            }
            foreach (var a in All.Values) { var hex = s.ColourOf(a.PlayerId); if (hex != null && a.SetColourHex(hex)) Mod.Diagnostics?.Record("players", "profile-applied", $"player={a.PlayerId}; colour={hex}"); }
        }

        public void Pump()
        {
            try { ProfilePump(); } catch (Exception e) { Rl.Warn("profile", "profile sync: " + e.Message); }
            RemoteMapMarkers.UpdateRadar();
            if (All.Count == 0 && !Mod.IsNetworkActive) return;
            double now = Time.unscaledTimeAsDouble; float dt = Time.unscaledDeltaTime; int scene = LocalSceneGroup(); var cam = Camera.main;
            foreach (var a in All.Values) { try { a.Tick(now, dt, scene, cam); } catch (Exception e) { Rl.Warn("avatar-tick", "avatar update failed: " + e.Message); } }
            GadgetGhostAdapter.Pump();
            if (Mod.IsNetworkActive && now >= _nextSend) { _nextSend = now + SendEvery; SendLocal(scene); }
        }

        /// <summary>The local player's pose, look and movement inputs at a fixed rate (any peer to the host, the host relays).</summary>
        void SendLocal(int scene)
        {
            try
            {
                var pl = SceneContext.Instance?.Player; if (pl == null || Mod.LocalId == null) return;
                var t = pl.transform; var cam = Camera.main; var body = pl.GetComponent<Rigidbody>(); var cc = pl.GetComponent<CharacterController>();
                Vector3 vel = cc != null ? cc.velocity : body != null ? body.velocity : Vector3.zero; var local = t.InverseTransformDirection(vel);
                float pitch = cam != null ? cam.transform.eulerAngles.x : 0f; if (pitch > 180f) pitch -= 360f;
                var m = new PlayerUpdate
                {
                    PlayerId = Mod.LocalId, Position = GameActorWorld.W(t.position), BodyYaw = t.eulerAngles.y, LookYaw = cam != null ? cam.transform.eulerAngles.y : t.eulerAngles.y, LookPitch = pitch,
                    Airborne = cc != null && !cc.isGrounded ? 1 : 0, MoveX = Mathf.Clamp(local.x, -1, 1), MoveZ = Mathf.Clamp(local.z, -1, 1), SpeedX = local.x, SpeedZ = local.z,
                    Moving = new Vector2(vel.x, vel.z).sqrMagnitude > 0.04f, Sprinting = new Vector2(vel.x, vel.z).sqrMagnitude > 49f, SceneGroup = scene
                };
                MirrorLocalAnimator(m);
                if (Mod.IsHost) Sync?.NoteHostPosition(m.Position);
                Mod.Node.SendAuto(m);
            }
            catch (Exception e) { Rl.Warn("local-send", "local player update failed: " + e.Message); }
        }
    }
}

namespace Lycoreia.Game
{
    /// <summary>Keeps <see cref="Players.Avatars"/> (used by the arbiter, spawn leader and capture range checks) in step with the avatar roster.</summary>
    static class Players_Bridge
    {
        public static void Set(string id, Transform t) { if (id == null) return; if (t == null) Lycoreia.Game.Players.Avatars.Remove(id); else Lycoreia.Game.Players.Avatars[id] = t; }
    }
}

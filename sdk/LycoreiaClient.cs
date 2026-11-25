// LycoreiaClient.cs - drop-in client for the Lycoreia co-op API (API v1).
// SPDX-License-Identifier: MIT  (this file only; see sdk/LICENSE. Lycoreia itself is under its own license.)
//
// Copy this file into your mod. It finds Lycoreia at runtime with ONE reflection lookup (type "Lycoreia.Api.LycoreiaApi" in the
// assembly "Lycoreia") and does nothing when Lycoreia is not installed: Role is Offline, CheatsAllowed is true, PublishSpawn returns
// false, events never fire. No reference to Lycoreia.dll is needed, so players without Lycoreia can use your mod.
// Main thread only. Nothing here throws.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using UnityEngine;

namespace LycoreiaSdk
{
    public enum LycoreiaRole { Offline = 0, Host = 1, Guest = 2 }

    public sealed class LycoreiaPlayerInfo
    {
        public string Id = "", Name = "";
        public Color Color = Color.white;
        public bool IsHost, IsLocal;
        /// <summary>Avatar / player transform; null while not spawned in this scene.</summary>
        public Transform Transform;
    }

    /// <summary>Result of <see cref="LycoreiaClient.BeginCheat"/>. Dispose it when the action is done.</summary>
    public sealed class LycoreiaCheat : IDisposable
    {
        public bool Allowed { get; internal set; } = true;
        /// <summary>Why not (player's language); null when allowed.</summary>
        public string Reason { get; internal set; }
        internal IDisposable Inner;
        public void Dispose() { try { Inner?.Dispose(); } catch { } Inner = null; }
    }

    public static class LycoreiaClient
    {
        /// <summary>The API version this file was written for.</summary>
        public const int ClientApiVersion = 1;

        /// <summary>True once Lycoreia (API v1 or newer) was found.</summary>
        public static bool Available { get { Bind(); return _api != null; } }
        /// <summary>Lycoreia's API version (0 when not installed).</summary>
        public static int ApiVersion { get { Bind(); return _apiVersion; } }
        /// <summary>Lycoreia build ("2.1.0"); "" when not installed.</summary>
        public static string ModVersion => Get(_modVersion, "");

        public static LycoreiaRole Role { get { Bind(); try { return _role == null ? LycoreiaRole.Offline : (LycoreiaRole)Convert.ToInt32(_role()); } catch { return LycoreiaRole.Offline; } } }
        public static bool InSession => Role != LycoreiaRole.Offline;
        /// <summary>Offline (or without Lycoreia): true. Host: its "Allow cheats" setting. Guest: what the host allowed.</summary>
        public static bool CheatsAllowed => Get(_cheatsAllowed, true);
        /// <summary>Text for the player when cheats are off; null when allowed.</summary>
        public static string CheatsDeniedReason => Get<string>(_cheatsDenied, null);
        /// <summary>Key for per-session data (same for every session with this host); "" offline.</summary>
        public static string SessionKey => Get(_sessionKey, "");
        public static string LocalPlayerId => Get(_localId, "");
        public static bool StreamerMode => Get(_streamer, false);
        /// <summary>The player is typing into Lycoreia's chat or a menu field: ignore your hotkeys.</summary>
        public static bool TypingOpen => Get(_typing, false);
        /// <summary>A Lycoreia overlay owns the screen: keep your menu closed.</summary>
        public static bool OverlayOpen => Get(_overlay, false);

        public static List<LycoreiaPlayerInfo> Players
        {
            get
            {
                var list = new List<LycoreiaPlayerInfo>();
                Bind();
                if (_players == null) return list;
                try
                {
                    if (!(_players() is System.Collections.IEnumerable items)) return list;
                    foreach (var p in items)
                    {
                        if (p == null) continue;
                        var t = p.GetType();
                        list.Add(new LycoreiaPlayerInfo
                        {
                            Id = Prop<string>(t, p, "Id") ?? "",
                            Name = Prop<string>(t, p, "Name") ?? "",
                            Color = Prop<Color>(t, p, "Color"),
                            IsHost = Prop<bool>(t, p, "IsHost"),
                            IsLocal = Prop<bool>(t, p, "IsLocal"),
                            Transform = Prop<Transform>(t, p, "Transform"),
                        });
                    }
                }
                catch { }
                return list;
            }
        }

        public static event Action<LycoreiaRole> RoleChanged;
        public static event Action<bool> CheatsAllowedChanged;
        public static event Action<LycoreiaPlayerInfo> PlayerJoined;
        public static event Action<string> PlayerLeft;

        /// <summary>Optional: name your mod in Lycoreia's diagnostics. Call it once at start (safe without Lycoreia).</summary>
        public static void Register(string modName, string modVersion)
        {
            _regName = modName; _regVersion = modVersion;
            Bind();
            if (_api != null) DoRegister();
        }

        /// <summary>Share an actor you created with InstantiateActorModel + InstantiateActorFromModel. False offline / without Lycoreia.</summary>
        public static bool PublishSpawn(GameObject actor) => PublishSpawn(actor, out _);

        public static bool PublishSpawn(GameObject actor, out string reason)
        {
            reason = null; Bind();
            if (_publish == null) { reason = "Lycoreia is not installed"; return false; }
            try
            {
                var args = new object[] { actor, null };
                bool ok = (bool)_publish.Invoke(null, args);
                reason = args[1] as string;
                return ok;
            }
            catch (Exception e) { reason = e.GetBaseException().Message; return false; }
        }

        /// <summary>
        /// using (var c = LycoreiaClient.BeginCheat("currency")) { if (!c.Allowed) { Show(c.Reason); return; } ... }
        /// Without Lycoreia: always allowed.
        /// </summary>
        public static LycoreiaCheat BeginCheat(string kind)
        {
            var result = new LycoreiaCheat(); Bind();
            if (_beginCheat == null) return result;
            try
            {
                var scope = _beginCheat.Invoke(null, new object[] { kind });
                if (scope != null)
                {
                    var t = scope.GetType();
                    result.Allowed = Prop<bool>(t, scope, "Allowed");
                    result.Reason = Prop<string>(t, scope, "Reason");
                    result.Inner = scope as IDisposable;
                }
            }
            catch { result.Allowed = CheatsAllowed; result.Reason = CheatsDeniedReason; }
            return result;
        }

        // ------------------------------------------------------------ binding

        static Type _api; static int _apiVersion; static bool _given; static float _nextTry;
        static Func<object> _role, _players;
        static Func<bool> _cheatsAllowed, _streamer, _typing, _overlay;
        static Func<string> _cheatsDenied, _sessionKey, _localId, _modVersion;
        static MethodInfo _publish, _beginCheat, _register;
        static string _regName, _regVersion;

        static void Bind()
        {
            if (_api != null || _given) return;
            float now; try { now = Time.unscaledTime; } catch { now = 0f; }
            if (now < _nextTry) return;
            _nextTry = now + 3f;                                             // Lycoreia may load after this mod: retry every 3 s
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Lycoreia");
                var api = asm?.GetType("Lycoreia.Api.LycoreiaApi");
                if (api == null) return;
                var versionField = api.GetField("Version", BindingFlags.Public | BindingFlags.Static);
                int version = versionField != null ? Convert.ToInt32(versionField.GetRawConstantValue()) : 0;
                if (version < ClientApiVersion) { _given = true; Debug.LogWarning($"[LycoreiaClient] Lycoreia API v{version} is older than v{ClientApiVersion}: co-op integration off."); return; }

                _role = Getter<object>(api, "Role"); _players = Getter<object>(api, "Players");
                _cheatsAllowed = Getter<bool>(api, "CheatsAllowed"); _streamer = Getter<bool>(api, "StreamerMode");
                _typing = Getter<bool>(api, "TypingOpen"); _overlay = Getter<bool>(api, "OverlayOpen");
                _cheatsDenied = Getter<string>(api, "CheatsDeniedReason"); _sessionKey = Getter<string>(api, "SessionKey");
                _localId = Getter<string>(api, "LocalPlayerId"); _modVersion = Getter<string>(api, "ModVersion");
                _publish = api.GetMethod("PublishSpawn", new[] { typeof(GameObject), typeof(string).MakeByRefType() });
                _beginCheat = api.GetMethod("BeginCheat", new[] { typeof(string) });
                _register = api.GetMethod("Register", new[] { typeof(string), typeof(string) });
                Subscribe(api, "RoleChanged", v => Raise(RoleChanged, (LycoreiaRole)Convert.ToInt32(v)));
                Subscribe(api, "CheatsAllowedChanged", v => Raise(CheatsAllowedChanged, (bool)v));
                Subscribe(api, "PlayerJoined", v => { var info = Players.FirstOrDefault(p => p.Id == Prop<string>(v.GetType(), v, "Id")); if (info != null) Raise(PlayerJoined, info); });
                Subscribe(api, "PlayerLeft", v => Raise(PlayerLeft, v as string));
                _api = api; _apiVersion = version;
                if (_regName != null) DoRegister();
            }
            catch (Exception e) { _given = true; Debug.LogWarning("[LycoreiaClient] binding failed: " + e.Message); }
        }

        static void DoRegister() { try { _register?.Invoke(null, new object[] { _regName ?? "?", _regVersion ?? "?" }); } catch { } }

        static Func<T> Getter<T>(Type api, string name)
        {
            var getter = api.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
            if (getter == null) return null;
            return () => (T)getter.Invoke(null, null);
        }

        /// <summary>Subscribes an Action&lt;object&gt; to a static Action&lt;T&gt; event whatever T is (T may be a Lycoreia type).</summary>
        static void Subscribe(Type api, string name, Action<object> handler)
        {
            var ev = api.GetEvent(name, BindingFlags.Public | BindingFlags.Static);
            if (ev?.EventHandlerType == null) return;
            var arg = ev.EventHandlerType.GetMethod("Invoke").GetParameters()[0].ParameterType;
            var p = Expression.Parameter(arg, "value");
            var body = Expression.Invoke(Expression.Constant(handler), Expression.Convert(p, typeof(object)));
            ev.AddEventHandler(null, Expression.Lambda(ev.EventHandlerType, body, p).Compile());
        }

        static void Raise<T>(Action<T> ev, T value)
        {
            if (ev == null) return;
            foreach (Action<T> d in ev.GetInvocationList()) { try { d(value); } catch (Exception e) { Debug.LogWarning("[LycoreiaClient] event handler failed: " + e.Message); } }
        }

        static T Get<T>(Func<T> f, T fallback) { Bind(); if (f == null) return fallback; try { return f(); } catch { return fallback; } }

        static T Prop<T>(Type t, object o, string name)
        {
            try { var v = t.GetProperty(name)?.GetValue(o); return v is T x ? x : default; } catch { return default; }
        }
    }
}

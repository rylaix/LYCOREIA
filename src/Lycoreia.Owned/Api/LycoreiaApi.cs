// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
// Lycoreia public API for other mods (v1, 2026-11-24). Everything here is a stable contract: names, signatures and meaning do not change
// within an API version. Other mods should bind it through sdk/LycoreiaClient.cs (one reflection lookup, no hard reference), never
// through Lycoreia's internal types. Main thread only; nothing here throws.
using System;
using System.Collections.Generic;
using Lycoreia.Game;
using Lycoreia.Game.Avatars;
using Lycoreia.Owned.Components.UI;
using Lycoreia.Text;
using UnityEngine;

namespace Lycoreia.Api
{
    /// <summary>This seat's part in a co-op session.</summary>
    public enum LycoreiaRole { Offline = 0, Host = 1, Guest = 2 }

    /// <summary>One player of the session (the local one included). Snapshot values, taken when Players was read.</summary>
    public sealed class LycoreiaPlayer
    {
        /// <summary>Stable player id (PLAYER_...). Use it as a key; never show it.</summary>
        public string Id { get; internal set; } = "";
        /// <summary>Display name; "Player" for other players while the local player has streamer mode on.</summary>
        public string Name { get; internal set; } = "";
        public Color Color { get; internal set; } = Color.white;
        public bool IsHost { get; internal set; }
        public bool IsLocal { get; internal set; }
        /// <summary>The avatar (remote players) or the player object (local player); null while not spawned in this scene.</summary>
        public Transform Transform { get; internal set; }
    }

    /// <summary>
    /// Returned by <see cref="LycoreiaApi.BeginCheat"/>. Check <see cref="Allowed"/>; if false, do not perform the action and show
    /// <see cref="Reason"/>. Dispose it when the action is done (a using block).
    /// </summary>
    public sealed class LycoreiaCheatScope : IDisposable
    {
        public bool Allowed { get; internal set; }
        /// <summary>Why it is not allowed, in the player's language (en/ru/es); null when allowed.</summary>
        public string Reason { get; internal set; }
        public string Kind { get; internal set; } = "";
        bool _disposed;
        public void Dispose() { if (_disposed) return; _disposed = true; LycoreiaApi.EndCheat(this); }
    }

    public static class LycoreiaApi
    {
        /// <summary>API contract version. Additions keep the number; a breaking change raises it.</summary>
        public const int Version = 1;
        /// <summary>Lycoreia build, e.g. "2.1.0-rc9".</summary>
        public static string ModVersion => Lycoreia.ModVersion.Value;

        // ------------------------------------------------------------ session state

        public static LycoreiaRole Role
        {
            get
            {
                try
                {
                    var s = Mod.Session; if (s == null) return LycoreiaRole.Offline;
                    if (s.IsHost) return LycoreiaRole.Host;
                    if (s.IsGuest && s.Connected && s.Joined) return LycoreiaRole.Guest;
                }
                catch { }
                return LycoreiaRole.Offline;
            }
        }

        /// <summary>True while hosting or joined as a guest.</summary>
        public static bool InSession => Role != LycoreiaRole.Offline;

        /// <summary>
        /// Whether cheats are allowed right now: always true offline; the host's "Allow Cheats" setting while hosting; the rule the host
        /// approved this guest with while a guest.
        /// </summary>
        public static bool CheatsAllowed
        {
            get
            {
                try
                {
                    var s = Mod.Session;
                    switch (Role)
                    {
                        case LycoreiaRole.Host: return s.AllowCheats;
                        case LycoreiaRole.Guest: return s.HostAllowsCheats;
                        default: return true;
                    }
                }
                catch { return false; }
            }
        }

        /// <summary>Text for the player when cheats are off (en/ru/es), null when allowed.</summary>
        public static string CheatsDeniedReason
        {
            get
            {
                if (CheatsAllowed) return null;
                return Role == LycoreiaRole.Host
                    ? CoopText.Pick("Cheats are off for this session. Turn on \"Allow cheats\" in Lycoreia settings and host again.",
                        "Читы в этой сессии выключены. Включите «Разрешить читы» в настройках Lycoreia и запустите сервер заново.",
                        "Los trucos están desactivados en esta sesión. Activa \"Permitir trucos\" en los ajustes de Lycoreia y vuelve a crear la partida.")
                    : CoopText.Pick("The host has not allowed cheats.", "Хост не разрешил читы.", "El anfitrión no ha permitido los trucos.");
            }
        }

        /// <summary>
        /// A key for storing per-session data (markers, presets): "" offline, otherwise derived from the host's player id (the same for
        /// every session with that host). Only letters, digits, '_' and '-'.
        /// </summary>
        public static string SessionKey
        {
            get
            {
                try
                {
                    if (!InSession) return "";
                    var raw = Mod.Session?.HostId ?? "";
                    var chars = new System.Text.StringBuilder(raw.Length);
                    foreach (var c in raw) chars.Append(char.IsLetterOrDigit(c) && c < 128 || c == '_' || c == '-' ? c : '_');
                    return chars.Length == 0 ? "" : "lyc_" + chars;
                }
                catch { return ""; }
            }
        }

        /// <summary>The local player's id; "" offline.</summary>
        public static string LocalPlayerId { get { try { return InSession ? Mod.LocalId ?? "" : ""; } catch { return ""; } } }

        /// <summary>The local player's streamer mode: hide other players' names.</summary>
        public static bool StreamerMode { get { try { return Prefs.StreamerMode; } catch { return false; } } }

        /// <summary>The player is typing into a Lycoreia field (chat, menu text box): do not react to hotkeys.</summary>
        public static bool TypingOpen { get { try { return ChatHud.IsOpen || InputGate.Blocked; } catch { return false; } } }

        /// <summary>A Lycoreia overlay owns the screen (chat, multiplayer menu, co-op wheel, what's new popup): keep your menu closed.</summary>
        public static bool OverlayOpen
        {
            get
            {
                try { return ChatHud.IsOpen || InputGate.Blocked || LycoreiaMenu.IsOpen || CoopWheel.Busy || WhatsNewPopup.IsOpen; }
                catch { return false; }
            }
        }

        /// <summary>Everyone in the session, the local player first. Empty offline.</summary>
        public static IReadOnlyList<LycoreiaPlayer> Players
        {
            get
            {
                var list = new List<LycoreiaPlayer>();
                try
                {
                    var s = Mod.Session; if (s == null || !InSession) return list;
                    bool hide = StreamerMode; string me = Mod.LocalId;
                    if (me != null && s.Players.TryGetValue(me, out var local))
                        list.Add(new LycoreiaPlayer { Id = me, Name = local.Name ?? "", IsHost = local.IsHost, IsLocal = true, Color = Hex(s.ColourOf(me)), Transform = LocalTransform() });
                    foreach (var p in s.Players.Values)
                    {
                        if (p == null || p.Id == me) continue;
                        RemoteAvatars.All.TryGetValue(p.Id, out var av);
                        Transform tr = null; try { if (av != null && av.Root != null) tr = av.transform; } catch { }
                        list.Add(new LycoreiaPlayer { Id = p.Id, Name = hide ? "Player" : p.Name ?? "", IsHost = p.IsHost, IsLocal = false, Color = av != null ? av.Colour : Hex(s.ColourOf(p.Id)), Transform = tr });
                    }
                }
                catch { }
                return list;
            }
        }

        // ------------------------------------------------------------ events (raised on the main thread, once per change)

        /// <summary>Offline / Host / Guest changed.</summary>
        public static event Action<LycoreiaRole> RoleChanged;
        /// <summary><see cref="CheatsAllowed"/> changed.</summary>
        public static event Action<bool> CheatsAllowedChanged;
        /// <summary>A player joined the session (not raised for the local player).</summary>
        public static event Action<LycoreiaPlayer> PlayerJoined;
        /// <summary>A player left; the argument is their id.</summary>
        public static event Action<string> PlayerLeft;

        // ------------------------------------------------------------ actions

        /// <summary>
        /// Optional: tell Lycoreia which mod is using the API (shown in Lycoreia's diagnostics). Returns <see cref="Version"/>.
        /// </summary>
        public static int Register(string modName, string modVersion)
        {
            try
            {
                modName = Clean(modName); modVersion = Clean(modVersion);
                if (Users.Add(modName + " " + modVersion))
                {
                    Log.Info($"API: {modName} {modVersion} uses Lycoreia API v{Version}");
                    Mod.Diagnostics?.Record("api", "register", $"mod={modName}; version={modVersion}; api={Version}");
                }
            }
            catch { }
            return Version;
        }

        /// <summary>
        /// Shares an actor your mod created with the other players. Actors created through InstantiationHelpers.InstantiateActor are
        /// shared automatically; actors made with GameModel.InstantiateActorModel + InstantiationHelpers.InstantiateActorFromModel are
        /// not, so call this right after creating one. Safe to call twice and offline (returns false offline). While cheats are allowed
        /// the actor skips Lycoreia's spawn caps (crowded-area and chicken limits).
        /// </summary>
        public static bool PublishSpawn(GameObject actor) => PublishSpawn(actor, out _);

        /// <summary><see cref="PublishSpawn(GameObject)"/> with the reason when it returns false.</summary>
        public static bool PublishSpawn(GameObject actor, out string reason)
        {
            reason = null;
            try
            {
                if (!InSession) { reason = "not in a session"; return false; }
                if (actor == null) { reason = "no object"; return false; }
                var id = actor.GetComponent<Il2Cpp.IdentifiableActor>()?._model?.actorId.Value;
                if (id.HasValue && Mod.Actors != null && Mod.Actors.Table.TryGet(id.Value, out _)) return true;   // already shared
                bool ok = Lycoreia.Game.Hooks.InstantiateActorHook.Publish(actor, CheatsAllowed, out reason);
                Mod.Diagnostics?.Record("api", ok ? "spawn-published" : "spawn-refused", $"id={id}; cheat={ActiveKinds()}; reason={reason}");
                return ok;
            }
            catch (Exception e) { reason = "error: " + e.Message; return false; }
        }

        /// <summary>
        /// Start a cheat action (spawning, money, unlocks, teleports, god mode...). kind is a short name for logs ("spawn", "currency").
        /// Use: <c>using (var c = LycoreiaApi.BeginCheat("currency")) { if (!c.Allowed) { Show(c.Reason); return; } AddMoney(); }</c>
        /// API v1 checks the session rule on this seat and records the action in Lycoreia's diagnostics.
        /// </summary>
        public static LycoreiaCheatScope BeginCheat(string kind)
        {
            var scope = new LycoreiaCheatScope { Kind = Clean(kind) };
            try
            {
                scope.Allowed = CheatsAllowed;
                scope.Reason = scope.Allowed ? null : CheatsDeniedReason;
                if (scope.Allowed && InSession) { ActiveCheats++; Kinds.Add(scope.Kind); }
                if (InSession) Mod.Diagnostics?.Record("api", scope.Allowed ? "cheat" : "cheat-denied", $"kind={scope.Kind}; role={Role}");
            }
            catch { scope.Allowed = false; scope.Reason = "Lycoreia API error"; }
            return scope;
        }

        internal static void EndCheat(LycoreiaCheatScope scope)
        {
            if (scope.Allowed && ActiveCheats > 0) { ActiveCheats--; Kinds.Remove(scope.Kind); }
        }

        // ------------------------------------------------------------ internals

        static int ActiveCheats;
        static readonly List<string> Kinds = new List<string>();
        static readonly HashSet<string> Users = new HashSet<string>();
        static LycoreiaRole _lastRole; static bool _lastCheats = true; static readonly HashSet<string> _lastPlayers = new HashSet<string>();
        static string ActiveKinds() => Kinds.Count == 0 ? "-" : string.Join(",", Kinds);

        static string Clean(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "?";
            var t = s.Trim(); if (t.Length > 40) t = t.Substring(0, 40);
            return t.Replace(";", ",").Replace("\n", " ").Replace("\r", " ");
        }

        static Color Hex(string hex)
        {
            if (!string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex.StartsWith("#") ? hex : "#" + hex, out var c)) return c;
            return Color.white;
        }

        static Transform LocalTransform()
        {
            try { var p = Il2Cpp.SceneContext.Instance?.Player; return p != null ? p.transform : null; } catch { return null; }
        }

        /// <summary>Raises the events. Called every frame by Lycoreia.</summary>
        internal static void Pump()
        {
            var role = Role;
            if (role != _lastRole) { _lastRole = role; if (role == LycoreiaRole.Offline) { ActiveCheats = 0; Kinds.Clear(); } Raise(RoleChanged, role); }
            var cheats = CheatsAllowed;
            if (cheats != _lastCheats) { _lastCheats = cheats; Raise(CheatsAllowedChanged, cheats); }
            if (PlayerJoined == null && PlayerLeft == null && _lastPlayers.Count == 0) return;
            var now = new HashSet<string>();
            try { var s = Mod.Session; if (s != null && role != LycoreiaRole.Offline) foreach (var id in s.Players.Keys) if (id != Mod.LocalId) now.Add(id); } catch { }
            if (now.SetEquals(_lastPlayers)) return;
            List<LycoreiaPlayer> players = null;
            foreach (var id in now)
                if (!_lastPlayers.Contains(id))
                {
                    players ??= new List<LycoreiaPlayer>(Players);
                    var p = players.Find(x => x.Id == id);
                    if (p != null) Raise(PlayerJoined, p);
                }
            foreach (var id in _lastPlayers) if (!now.Contains(id)) Raise(PlayerLeft, id);
            _lastPlayers.Clear(); _lastPlayers.UnionWith(now);
        }

        static void Raise<T>(Action<T> ev, T arg)
        {
            if (ev == null) return;
            foreach (Action<T> d in ev.GetInvocationList())
            {
                try { d(arg); }
                catch (Exception e) { Log.Warn($"API: a {typeof(T).Name} event subscriber ({d.Method.DeclaringType?.Assembly.GetName().Name}) failed: {e.Message}"); }
            }
        }
    }
}

// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections;
using Lycoreia.Net;

namespace Lycoreia.Compat
{
    public static class ModEnv
    {
        public static object StartCoroutine(IEnumerator routine) => Lycoreia.Game.Coroutines.Start(routine);
        public static bool inGame => Lycoreia.Game.GameState.Ready;
        public static string LocalID => Mod.LocalId;
        public static bool HandlingPacket { get => Applying.Active; set => Applying.Force(value); }
        public static GameModel GameState => Lycoreia.Game.GameState.Model;
        /// <summary>The remote avatar template (owned first-person legs copy their leg mesh from it).</summary>
        public static UnityEngine.GameObject PlayerPrefab => Lycoreia.Game.Avatars.AvatarTemplate.Prefab;
        /// <summary>Remote avatars' root objects by player id (a fresh view each call).</summary>
        public static System.Collections.Generic.Dictionary<string, UnityEngine.GameObject> PlayerObjects
        { get { var d = new System.Collections.Generic.Dictionary<string, UnityEngine.GameObject>(); foreach (var kv in Lycoreia.Game.Avatars.RemoteAvatars.All) if (kv.Value.Root != null) d[kv.Key] = kv.Value.Root; return d; } }
    }

    public static class OwnedLog
    {
        public static void LogMessage(string m, LogTarget t = LogTarget.Main) => Log.Info(m, t);
        public static void LogWarning(string m, LogTarget t = LogTarget.Main) => Log.Warn(m, t);
        public static void LogError(string m, LogTarget t = LogTarget.Main) => Log.Error(m, t);
        public static void LogDebug(string m, LogTarget t = LogTarget.Main) => Log.Debug(m, t);
        public static void LogDiagnostic(string category, string ev, string details = "") => Mod.Diagnostics?.Record(category, ev, details);
        static readonly Lycoreia.RateLimitedLog Rl = new Lycoreia.RateLimitedLog(null, 10_000);
        public static void LogDiagnostic(string category, string ev, string details, string rateKey, int perKeyMilliseconds)
        { if (new Lycoreia.RateLimitedLog(null, perKeyMilliseconds).ShouldEmit(rateKey, out _) && Rl != null) Mod.Diagnostics?.Record(category, ev, details); }
    }
    public enum OwnedLogTarget { Main, Sensitive, Both, Neither }
}

namespace Lycoreia.Compat
{
    public static class DiagLog
    {
        public static bool DiagnosticsEnabled => Mod.Diagnostics?.Enabled == true;
        public static void LogDiagnostic(string category, string ev, string details = "") => Mod.Diagnostics?.Record(category, ev, details);
        static readonly System.Collections.Generic.Dictionary<string, long> Last = new();
        public static void LogDiagnostic(string category, string ev, string details, string rateKey, int minimumIntervalMilliseconds = 1000)
        {
            int perKeyMilliseconds = minimumIntervalMilliseconds; long now = Environment.TickCount64;
            lock (Last) { if (Last.TryGetValue(rateKey, out var t) && now - t < perKeyMilliseconds) return; Last[rateKey] = now; }
            Mod.Diagnostics?.Record(category, ev, details);
        }
    }
}

namespace Lycoreia.Compat
{
    /// <summary>Old entry-point facade: only the members Lycoreia files read.</summary>
    public static class ModFacade
    {
        public static bool IsNetworkActive => Mod.IsNetworkActive;
        public static string CoopJoinGame => Lycoreia.Game.Prefs.Get<string>(Settings.JoinGame);
        public static bool FriendsCanVacMe => Lycoreia.Game.Prefs.FriendsCanVacMe;
        public static bool FriendsCanKickMe => Lycoreia.Game.Prefs.FriendsCanKickMe;
        public static void SendToAllOrServer(Msg m) => Mod.Node?.SendAuto(m);
        public static void SetConfigValue<T>(string key, T value) => Lycoreia.Game.Prefs.Set(key, value);
        public static string Username => Lycoreia.Game.Prefs.Get<string>(Settings.Username);
        public static string RawUsername => Username;
        public static string UsernameColor => Lycoreia.Game.Prefs.Get<string>(Settings.UsernameColor);
        public static bool StreamerMode => Lycoreia.Game.Prefs.StreamerMode;
        public static bool DisableAchievements => Lycoreia.Game.Prefs.Get<bool>(Settings.DisableAchievements);
        public static bool AllowCheats => Lycoreia.Game.Prefs.AllowCheats;
        public static string SavedHostPort => Lycoreia.Game.Prefs.Get<int>(Settings.HostPort).ToString();
        public static string SavedConnectPort => Lycoreia.Game.Prefs.Get<int>(Settings.RecentPort).ToString();
        public static string SavedConnectIP => Lycoreia.Game.Prefs.Get<string>(Settings.RecentIp);
        public static class Server
        {
            public static bool IsRunning => Mod.IsHost;
            public static void SendToAll(Msg m) { if (Mod.IsHost) Mod.Node?.SendToAll(m); }
            public static void SendToClient(Msg m, Peer p) { if (Mod.IsHost && p != null) Mod.Node?.SendTo(p, m); }
            public static void SendToAllExcept(Msg m, Peer p) { if (Mod.IsHost) Mod.Node?.SendToAllExcept(m, p); }
            /// <summary>Raised when hosting starts (forwards to Session.ServerStarted).</summary>
            public static event Action OnServerStarted { add { Mod.Session.ServerStarted += value; } remove { Mod.Session.ServerStarted -= value; } }
            public static class ClientManager
            {
                /// <summary>A player left (forwards to Session.PlayerRemoved).</summary>
                public static event Action<PlayerInfo> OnClientRemoved { add { Mod.Session.PlayerRemoved += value; } remove { Mod.Session.PlayerRemoved -= value; } }
                public sealed class ClientView { public string PlayerId; }
                /// <summary>True while the peer is still the registered connection of its player.</summary>
                public static bool TryGetClient(Peer p, out ClientView client)
                {
                    client = null; if (p == null || !p.Registered || string.IsNullOrEmpty(p.PlayerId) || Mod.Node?.FindByPlayer(p.PlayerId) != p) return false;
                    client = new ClientView { PlayerId = p.PlayerId }; return true;
                }
            }
            public static int Port => Lycoreia.Game.Prefs.Get<int>(Settings.HostPort);
            public static void Close() => Mod.Session?.StopServer();
            public static class ReSyncManager { public static void SynchronizeAll() => Mod.Snapshots?.ResyncAll(); public static void RequestResync() => Mod.Guest?.RequestResync(); }
        }
        public static class Client
        {
            public static bool IsConnected => Mod.Session != null && Mod.Session.Connected;
            public static bool IsConnecting => Mod.Session != null && Mod.Session.Connecting;
            public static bool IsJoined => Mod.Session != null && Mod.Session.Joined;
            public static void SendPacket(Msg m) { if (Mod.IsGuest) Mod.Node?.SendToHost(m); }
            /// <summary>Guest joined (initial approve applied). The argument is unused (kept for the owned call sites' signature).</summary>
            public static event Action<object> OnConnected { add { Mod.Session.ClientJoined += () => value(null); } remove { } }
            public static event Action OnDisconnected { add { Mod.Session.ClientDisconnected += _ => value(); } remove { } }
            public static void Disconnect() => Mod.Session?.Disconnect();
        }
    }
}

namespace Lycoreia.Compat
{
    public static class Roster
    {
        public static PlayerInfo GetPlayer(string id) => id != null && Mod.Session != null && Mod.Session.Players.TryGetValue(id, out var p) ? p : null;
        /// <summary>The player's selected colour; a stable hash colour until their profile arrives.</summary>
        public static UnityEngine.Color ColorOf(PlayerInfo p) { if (p?.Id == null) return UnityEngine.Color.white; if (Lycoreia.Net.Messages.ProfileColour.TryParse(Mod.Session?.ColourOf(p.Id), out var cr, out var cg, out var cb)) return new UnityEngine.Color(cr / 255f, cg / 255f, cb / 255f, 1f); uint h = 2166136261; foreach (char ch in p.Id) h = (h ^ ch) * 16777619; return UnityEngine.Color.HSVToRGB((h % 360) / 360f, 0.55f, 1f); }
        public static System.Collections.Generic.IEnumerable<PlayerInfo> GetAllPlayers() => Mod.Session != null ? Mod.Session.Players.Values : System.Linq.Enumerable.Empty<PlayerInfo>();
    }

    /// <summary>The console and chat commands' view of the game (ICommandEnv over the live mod).</summary>
    public sealed class CommandEnv : ICommandEnv
    {
        public static readonly CommandEnv Instance = new CommandEnv();
        public Session Session => Mod.Session;
        public Lycoreia.Snapshot.SnapshotHost Snapshots => Mod.Snapshots;
        public bool InSession => Lycoreia.Game.GameState.Ready;
        public bool ChatFocused => false;
        public bool AllowCheatsSetting => Lycoreia.Game.Prefs.AllowCheats;
        public void CloseMenu() { }
        public void OpenManualCodeDialog() { }
        public void StartAutoHost() => Lycoreia.Owned.Components.UI.MultiplayerUI.Instance?.NativeHostAutomatic();
        public void ToggleGui() { }
        public void ToggleChat() { }
        public void ShowLocalChat(string username, string text) => Lycoreia.Owned.Components.UI.MultiplayerUI.Instance?.ShowLocalChat(username, text);
        public void AddSprinkles(int count) => Lycoreia.Game.GameCommands.AddSprinkles(count);
        public void RemoveFirewallExceptions() { foreach (var p in (Lycoreia.Game.Prefs.Get<string>(Settings.FirewallExceptions) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)) if (int.TryParse(p, out var port)) new NetshFirewall().Remove(port); }
        public void BackupSave() => ShowLocalChat("Lycoreia", Lycoreia.Game.GameCommands.BackupSave());
        public string Cleanup(bool now) => Lycoreia.Game.GameCommands.Cleanup(now);
    }
}

namespace Lycoreia.Compat
{
    /// <summary>Colour helpers the owned menu uses.</summary>
    public static class ColorUtil
    {
        public static bool IsValidHexColor(string value) => value != null && System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9A-Fa-f]{6}$");
        public static string RandomHexColor() { var r = new System.Random(); return $"{r.Next(40, 256):X2}{r.Next(40, 256):X2}{r.Next(40, 256):X2}"; }
    }
}

namespace Lycoreia.Compat
{
    /// <summary>Wire vector conversions for owned files (the core wire types are Unity-free).</summary>
    public static class VecConvert
    {
        public static Vec3 ToWire(this UnityEngine.Vector3 v) => new Vec3(v.x, v.y, v.z);
        public static UnityEngine.Vector3 ToUnity(this Vec3 v) => new UnityEngine.Vector3(v.X, v.Y, v.Z);
    }
}

namespace Lycoreia.Compat
{
    /// <summary>Read-only views of the actor table for owned diagnostics (fresh dictionaries per call; deeplog/test builds only use them).</summary>
    public static class ActorView
    {
        public static System.Collections.Generic.Dictionary<long, IdentifiableModel> Actors
        {
            get
            {
                var d = new System.Collections.Generic.Dictionary<long, IdentifiableModel>(); var g = Lycoreia.Game.GameState.Model; var a = Mod.Actors;
                if (g == null || a == null) return d;
                foreach (var e in a.Table.All) if (g.identifiables.TryGetValue(new ActorId(e.Id), out var m) && m != null) d[e.Id] = m;
                return d;
            }
        }
        public static System.Collections.Generic.Dictionary<long, string> OwnershipByActorId
        { get { var d = new System.Collections.Generic.Dictionary<long, string>(); if (Mod.Actors != null) foreach (var e in Mod.Actors.Table.All) d[e.Id] = e.Owner ?? ""; return d; } }
        public static System.Collections.Generic.Dictionary<int, IdentifiableType> ActorTypes
        {
            get
            {
                var d = new System.Collections.Generic.Dictionary<int, IdentifiableType>();
                foreach (var t in Resources.FindObjectsOfTypeAll<IdentifiableType>()) { int id = Lycoreia.Game.ActorTypes.IdOf(t); if (id != 0) d[id] = t; }
                return d;
            }
        }
    }
}

namespace Lycoreia.Compat
{
    /// <summary>The owned files' view of the actor type registry (ids are Lycoreia.Game.ActorTypes ids).</summary>
    public static class ActorManager
    {
        public static readonly TypeView ActorTypes = new TypeView();
        public sealed class TypeView
        {
            public bool TryGetValue(int id, out IdentifiableType type) => Lycoreia.Game.ActorTypes.TryGetType(id, out type);
            public System.Collections.Generic.IEnumerable<IdentifiableType> Values
            {
                get
                {
                    var t = GameContext.Instance?.LookupDirector?._identifiableTypeByRefId; if (t == null) yield break;
                    foreach (var kv in t) if (kv.Value != null) yield return kv.Value;
                }
            }
        }
    }
    /// <summary>Persistent ids of identifiable types for owned files.</summary>
    public static class NetworkActorManager { public static int GetPersistentID(IdentifiableType t) => Lycoreia.Game.ActorTypes.IdOf(t); }
}

namespace Lycoreia.Compat
{
    /// <summary>The owned vacuum traces' view of the local capture prediction (now Lycoreia.Game.VacuumCapture).</summary>
    public static class LocalActorVacuumPredictionManager { public static Lycoreia.Game.ActorLink ActiveActor => Lycoreia.Game.VacuumCapture.ActiveActor; }
}

namespace Lycoreia.Compat
{
    /// <summary>The owned gordo reward recovery's view of the host reward ledger (Lycoreia.World.GordoRewardLedger).</summary>
    public static class NetworkGordoSlimeManager
    {
        static Lycoreia.World.GordoRewardLedger L => Lycoreia.Game.World.WorldRuntime.GordoRewards;
        public static bool HasPendingHostReward(string id) => L.HasPending(id);
        public static bool HasGivenRewards(string id) => L.HasGiven(id);
        public static bool IsRewardGenerationUncertain(string id) => L.IsUncertain(id);
        public static bool IsRewardGenerationInProgress(string id) => L.InProgress(id);
    }
}

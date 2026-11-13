// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.IO;
using Lycoreia.Net;
using Lycoreia.Snapshot;

namespace Lycoreia
{
    /// <summary>
    /// Composition root: builds and owns the core objects once. Game code reaches the network only through this
    /// class (session state, node, snapshots), never through its own globals.
    /// </summary>
    public static class Mod
    {
        public static MessageRegistry Registry { get; private set; }
        public static Node Node { get; private set; }
        public static Session Session { get; private set; }
        public static SnapshotHost Snapshots { get; private set; }
        public static GuestSnapshotController Guest { get; private set; }
        public static DiagnosticsWriter Diagnostics { get; private set; }
        public static PacketWindow Packets { get; private set; }
        public static AutoHost AutoHost { get; private set; }
        /// <summary>Actor sync over the real game world; created by the game layer once the game context exists.</summary>
        public static Lycoreia.Actors.ActorSync Actors { get; set; }
        public static ChatDedup Chat { get; } = new ChatDedup();
        public static string DataFolder { get; private set; }
        public static bool Initialised => Session != null;
        /// <summary>Game version as reported by Unity (e.g. "1.3.0 [build]"), and a user-facing warning when it is not the supported one.</summary>
        public static string GameVersion { get; internal set; } = "";
        public static string GameVersionWarning { get; internal set; }
        /// <summary>Result of the startup update check (null until it finished; Kind Unknown when no source answered).</summary>
        public static UpdateStatus Update { get; internal set; }
        /// <summary>Older game builds are not supported: multiplayer is blocked and the menu only explains why.</summary>
        public static bool GameUnsupported { get; internal set; }

        public static bool IsHost => Session != null && Session.IsHost;
        public static bool IsGuest => Session != null && Session.IsGuest;
        /// <summary>True while hosting or connected. Safe before init: patches can fire during the first scene load.</summary>
        public static bool IsNetworkActive => Session != null && (Session.IsHost || Session.Connected);
        public static string LocalId => Session?.LocalId;

        /// <summary>Resolves the data folder (migrating a legacy folder once), builds the object graph.</summary>
        public static void Init(string userDataRoot, Func<long> clock = null, bool diagnostics = false)
        {
            if (Session != null) return;
            DataFolder = PrepareDataFolder(userDataRoot);
            Log.Open(DataFolder);
            Registry = MessageRegistry.FromAssembly(typeof(Mod).Assembly);
            Node = new Node(Registry, clock);
            Session = new Session(Node);
            Snapshots = new SnapshotHost(Session);
            Guest = new GuestSnapshotController(Session);
            Diagnostics = new DiagnosticsWriter(DataFolder) { HostId = () => Session.HostId ?? "", LocalId = () => Session.LocalId ?? "", Role = () => Session.IsHost ? "host" : Session.IsGuest ? "client" : "offline" };
            Packets = new PacketWindow(Diagnostics, t => Registry.Get(t)?.Name ?? ("type" + t), Node.Clock);
            Node.SlowDispatch += (name, ms, queued) => Diagnostics?.Record("performance", "slow-dispatch-handler", $"type={name}; ms={ms:0.00}; queued={queued}");
            Node.PacketSeen += (t, b, p, sent) => { Packets.Seen(t, b, p, sent); if (Log.PacketSizes) Log.Debug($"{(sent ? "sent" : "got")} {Registry.Get(t)?.Name} {b} bytes"); };
            Node.Rejected += Packets.Rejected;
            if (diagnostics) Diagnostics.Start(BuildInfo.Version);
            AutoHost = new AutoHost(new UpnpGateway(), new NetshFirewall()) { PortStillHosted = p => Session.IsHost };
            Session.ServerStopped += () => AutoHost.ReleaseAsync();
        }

        /// <summary>"Lycoreia" under the user data folder.</summary>
        public static string PrepareDataFolder(string userDataRoot)
        {
            string target = Path.Combine(userDataRoot, BuildInfo.DataFolder);
            Directory.CreateDirectory(target); return target;
        }

        /// <summary>Quit order: log, (Discord shut down by the caller), UPnP release blocking up to 8 s, close server, disconnect client, flush diagnostics.</summary>
        public static void Quit(Action shutdownDiscord = null)
        {
            Log.Info("application quitting"); Watchdog.Stop(); Diagnostics?.Record("runtime", "application-quitting", "shutdown requested");
            try { shutdownDiscord?.Invoke(); } catch (Exception e) { Log.Warn("discord shutdown: " + e.Message); }
            AutoHost?.ReleaseBlocking();
            Session?.StopServer();
            Session?.Disconnect(null, true);
            Diagnostics?.Flush();
        }
    }
}

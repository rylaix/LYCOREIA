// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Lycoreia.Net;
using Lycoreia.Net.Messages;

namespace Lycoreia.Snapshot
{
    /// <summary>The 33 steps sent to a joining or resyncing client, in order.</summary>
    public enum SnapStep
    {
        Approve = 1, LandPlots, GardenOwnership, Gordos, Switches, Refinery, Pedia, Map, AccessDoors, ResourceNodes, VacPalettes, Progression,
        TreasurePods, Upgrades, DroneResources, Actors, SlimeBehavior, Gadgets, GadgetLinks, TeleporterLinks, Prices, Shop, Weather, PuzzleSlots,
        PlortDepositors, PrismaBarriers, DroneCloud, Components, Conversations, RaisedEvents, Tornadoes, Sanctuary, PrismaDisruption, StabilizingSpawner
    }

    public sealed class SnapshotContext
    {
        public Node Node; public Peer Peer; public long SyncId; public bool Initial; public long WorldRevision;
        public void Send(Msg m, Delivery? mode = null, Lane? lane = null) => Node.SendTo(Peer, m, mode, lane);
        /// <summary>Sends a long actor list as pages of PageSize actors sharing the sync id.</summary>
        public void SendActors(IReadOnlyList<ActorRecord> actors, long startingActorId, double worldTime, int pageSize = ActorPaging.DefaultPageSize)
        {
            foreach (var page in ActorPaging.Split(actors, SyncId, startingActorId, worldTime, WorldRevision, pageSize)) Send(page, lane: Lane.Snapshot);
        }
    }

    public static class ActorPaging
    {
        public const int DefaultPageSize = 200;
        public static IEnumerable<InitialActors> Split(IReadOnlyList<ActorRecord> actors, long syncId, long startingId, double worldTime, long worldRevision, int pageSize = DefaultPageSize)
        {
            int pages = Math.Max(1, (actors.Count + pageSize - 1) / pageSize);
            for (int p = 0; p < pages; p++)
            {
                var m = new InitialActors { SyncId = syncId, Page = p, PageCount = pages };
                if (p == 0) { m.StartingActorId = startingId; m.WorldTime = worldTime; m.WorldRevision = new Revision { Has = worldRevision > 0, Value = worldRevision }; }
                for (int i = p * pageSize; i < Math.Min(actors.Count, (p + 1) * pageSize); i++) m.Actors.Add(actors[i]);
                yield return m;
            }
        }
    }

    /// <summary>Host orchestration: owns step order, sync ids, the world revision, the destroy guard and resync cooldowns.</summary>
    public sealed class SnapshotHost
    {
        public const long DestroyGuardMs = 8000, ClientResyncCooldownMs = 120_000, ResyncAllCooldownMs = 5000;
        readonly Session _session; readonly Func<long> _clock;
        readonly Dictionary<SnapStep, List<Action<SnapshotContext>>> _writers = new Dictionary<SnapStep, List<Action<SnapshotContext>>>();
        readonly Dictionary<string, long> _guardUntil = new Dictionary<string, long>();
        readonly Dictionary<string, long> _lastResync = new Dictionary<string, long>();
        long _lastResyncAll = -ResyncAllCooldownMs - 1;
        long _worldRevision;
        public Action<Peer> SendTypeRegistry = p => { };
        public readonly List<SnapStep> LastRun = new List<SnapStep>();

        public SnapshotHost(Session s)
        {
            _session = s; _clock = s.Node.Clock;
            s.Approved += OnApproved;
            s.ResyncRequested += OnResyncRequested;
            s.ClientRemoved += p => { if (p.PlayerId != null) { _guardUntil.Remove(p.PlayerId); _lastResync.Remove(p.PlayerId); } };
        }

        public long WorldRevision => _worldRevision;
        /// <summary>Host-wide, strictly increasing revision stamped on actor mutations.</summary>
        public long NextWorldRevision() => System.Threading.Interlocked.Increment(ref _worldRevision);

        public void Register(SnapStep step, Action<SnapshotContext> writer)
        {
            if (!_writers.TryGetValue(step, out var l)) _writers[step] = l = new List<Action<SnapshotContext>>();
            l.Add(writer);
        }

        void OnApproved(Peer peer, bool initial)
        {
            try { SendTypeRegistry(peer); } catch (Exception e) { Log.Error("type registry", e); }
            Run(peer, initial);
        }

        /// <summary>Sends the full ordered snapshot. Arms the destroy guard first.</summary>
        public void Run(Peer peer, bool initial)
        {
            ArmDestroyGuard(peer.PlayerId);
            var ctx = new SnapshotContext { Node = _session.Node, Peer = peer, Initial = initial, WorldRevision = _worldRevision };
            LastRun.Clear();
            foreach (SnapStep step in Enum.GetValues(typeof(SnapStep)))
            {
                LastRun.Add(step);
                try
                {
                    if (step == SnapStep.Approve)
                    {
                        var a = initial ? _session.NewResyncApprove(peer, peer.SyncId) : _session.NewResyncApprove(peer);
                        ctx.SyncId = a.SyncId; ctx.Send(a, lane: Lane.Important); continue;
                    }
                    if (_writers.TryGetValue(step, out var list)) foreach (var w in list) w(ctx);
                }
                catch (Exception e) { Log.Error($"snapshot step {step} failed", e); }
            }
            peer.SyncId = ctx.SyncId;
        }

        // ---- destroy guard
        public void ArmDestroyGuard(string playerId) { if (playerId != null) _guardUntil[playerId] = _clock() + DestroyGuardMs; }
        public bool DestroyGuardActive(string playerId) => playerId != null && _guardUntil.TryGetValue(playerId, out var t) && _clock() < t;

        // ---- resync
        public const string ClientCooldownText = "Resync is on cooldown. Please wait before requesting another resync.";
        public const string AllCooldownText = "Resync all is on cooldown. Please wait before resyncing all players.";

        void OnResyncRequested(Peer peer, bool automatic)
        {
            // the guest's own bounded retries after a failed apply bypass the manual cooldown
            if (!(automatic && TryAutomaticResync(peer.PlayerId)) && !TryClientResync(peer.PlayerId)) { Reply(peer, ClientCooldownText); return; }
            Run(peer, false);
            Reply(peer, "Your client has been resynced.");
        }

        /// <summary>One per client per 2 minutes.</summary>
        public bool TryClientResync(string playerId)
        {
            long now = _clock();
            if (_lastResync.TryGetValue(playerId, out var t) && now - t < ClientResyncCooldownMs) return false;
            _lastResync[playerId] = now; return true;
        }

        readonly Dictionary<string, long> _lastAuto = new Dictionary<string, long>();
        bool TryAutomaticResync(string id)
        {
            long now = _clock(); if (_lastAuto.TryGetValue(id, out var t) && now - t < 1000) return false;
            _lastAuto[id] = now; return true;
        }

        /// <summary>Host command: resync everyone, 5 s cooldown. Returns the chat line to show the host.</summary>
        public string ResyncAll()
        {
            long now = _clock();
            if (now - _lastResyncAll < ResyncAllCooldownMs) return AllCooldownText;
            _lastResyncAll = now;
            foreach (var p in _session.Node.RegisteredGuests.ToList()) Run(p, false);
            _session.BroadcastSystem("The server issued a resync for all players.");
            return null;
        }

        void Reply(Peer p, string text) => _session.Node.SendTo(p, new ChatMessage { Username = "", Text = text, MessageId = Guid.NewGuid().ToString("N"), Kind = ChatKind.System });
    }
}

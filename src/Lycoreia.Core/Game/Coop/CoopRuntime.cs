// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.Economy;
using Il2CppMonomiPark.SlimeRancher.Player.PlayerItems;
using Il2CppMonomiPark.SlimeRancher.Slime;
using Lycoreia.Coop;
using Lycoreia.Game.Hooks;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using UnityEngine;

namespace Lycoreia.Game.Coop
{
    public sealed class CoopRuntime : IFramePump
    {
        public string Name => "coop-board";
        public static readonly CoopBoard Board = new CoopBoard();
        /// <summary>Bumped on every board change (the menu redraws on it).</summary>
        public static int Revision { get; private set; }
        /// <summary>The recap of the last session that ended on this seat (shown in the menu afterwards).</summary>
        public static List<(string name, string line)> LastRecap = new List<(string, string)>();
        static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);
        static readonly Dictionary<CoopStat, long> _pending = new Dictionary<CoopStat, long>();
        static readonly List<string> _notices = new List<string>();
        static bool _dirty, _persistDirty, _installed; static string _loadedFor; static double _nextReport, _nextBroadcast, _nextTick, _nextSave;

        public static void Install(Session s)
        {
            if (_installed) return; _installed = true;
            s.Node.On<CoopOpMsg>(Side.Host, (c, m) => { HostApply(c.SenderId, CoopWire.FromMsg(m)); return Verdict.Accept; });
            s.Node.On<CoopBoardMsg>(Side.Guest, (c, m) => OnBoard(m));
            Mod.Snapshots.Register(Lycoreia.Snapshot.SnapStep.Components, ctx => ctx.Send(CoopWire.ToMsg(Board)));
            s.ServerStarted += () => { Board.ClearAll(); _loadedFor = null; Changed(false); };
            s.ServerStopped += EndSession;
            s.ClientJoined += () => { Board.ClearAll(); Changed(false); };
            s.ClientDisconnected += _ => EndSession();
        }

        static void EndSession()
        {
            try
            {
                var lines = Board.Recap.Select(p => (NameOf(p.Key), RecapText.Line(p.Value))).ToList();
                if (lines.Count > 0) { LastRecap = lines; foreach (var (n, l) in lines) Log.Info($"session recap: {n}: {l}"); }
                if (Mod.IsHost && _persistDirty) Save();
            }
            catch (Exception e) { Rl.Warn("end", "session recap failed: " + e.Message); }
            Board.ClearAll(); _loadedFor = null; _pending.Clear(); _notices.Clear(); Changed(false); CoopWorld.Reset();
        }

        public static string NameOf(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return "Someone";
            try { if (Mod.Session != null && Mod.Session.Players.TryGetValue(playerId, out var p) && !string.IsNullOrWhiteSpace(p.Name)) return Prefs.StreamerMode && playerId != Mod.LocalId ? "Player" : p.Name; } catch { }
            return playerId == Mod.LocalId ? "You" : "Player";
        }
        static int PlayerCount() { try { return Math.Max(1, Mod.Session?.Players.Count ?? 1); } catch { return 1; } }
        static long NowUtc() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        static void Changed(bool persist) { Revision++; _dirty = true; if (persist) _persistDirty = true; }

        // ---------------------------------------------------------------- operations (UI and stat sources call these)
        public static void Do(CoopOp op)
        {
            if (!Mod.IsNetworkActive) return;
            if (Mod.IsHost) HostApply(Mod.LocalId, op); else Mod.Node.SendToHost(CoopWire.ToMsg(op));
        }

        /// <summary>A stat that happened on this seat (batched every 2 s).</summary>
        public static void Report(CoopStat stat, long amount)
        {
            if (!Mod.IsNetworkActive || amount <= 0) return;
            _pending.TryGetValue(stat, out var v); _pending[stat] = Math.Min(v + amount, CoopBoard.MaxReport);
        }

        static void HostApply(string player, CoopOp op)
        {
            try
            {
                var r = Board.Apply(player, NameOf(player), op, NowUtc(), PlayerCount(), player == Mod.LocalId);
                if (!r.Changed) return;
                bool persist = op.Kind != CoopOpKind.Report && op.Kind != CoopOpKind.ExpeditionStart;
                Changed(persist || r.RewardMoney > 0);
                _notices.AddRange(r.Notices);
                if (r.RewardMoney > 0 && !Lycoreia.Game.World.WorldRuntime.HostGrantMoney(r.RewardMoney)) Rl.Warn("reward", "expedition reward could not be paid");
                if (op.Kind != CoopOpKind.Report) Mod.Diagnostics?.Record("coop", "op", $"kind={op.Kind}; id={op.Id}; from={player}; notices={r.Notices.Count}");
            }
            catch (Exception e) { Rl.Warn("apply", "co-op board: " + e.Message); }
        }

        static void OnBoard(CoopBoardMsg m)
        {
            try { CoopWire.Apply(Board, m); Changed(false); foreach (var n in m.Notices) Notice(n); }
            catch (Exception e) { Rl.Warn("board", "co-op board apply failed: " + e.Message); }
        }

        static void Notice(string text) { if (string.IsNullOrWhiteSpace(text)) return; try { Mod.Session?.RaiseNotice(text); } catch { } Log.Info("[co-op] " + text); }

        // ---------------------------------------------------------------- pump
        public void Pump()
        {
            if (!Mod.IsNetworkActive) return;
            double now = Time.unscaledTimeAsDouble;
            if (Mod.IsHost) HostLoadIfNeeded();
            if (now >= _nextReport && _pending.Count > 0)
            {
                _nextReport = now + 2;
                foreach (var kv in _pending.ToList()) Do(new CoopOp { Kind = CoopOpKind.Report, Id = (int)kv.Key, Arg = (int)Math.Min(kv.Value, CoopBoard.MaxReport) });
                _pending.Clear();
            }
            if (!Mod.IsHost) { CoopWorld.Pump(); return; }
            if (now >= _nextTick) { _nextTick = now + 1; var r = Board.Tick(NowUtc()); if (r.Changed) { Changed(true); _notices.AddRange(r.Notices); } }
            if (_dirty && now >= _nextBroadcast)
            {
                _nextBroadcast = now + 0.25; _dirty = false;
                var msg = CoopWire.ToMsg(Board); msg.Notices = _notices.ToList();
                foreach (var n in _notices) Notice(n);
                _notices.Clear();
                Mod.Node.SendToAll(msg);
            }
            if (_persistDirty && now >= _nextSave) { _nextSave = now + 2; Save(); }
            CoopWorld.Pump();
        }

        static string SaveName() { try { return GameContext.Instance?.AutoSaveDirector?.CurrentSaveGameName(); } catch { return null; } }
        static string PathFor(string save) => Path.Combine(Lycoreia.Owned.Shared.Utils.ModPaths.DataDirectory, "Coop", CoopWire.FileNameFor(save));

        static void HostLoadIfNeeded()
        {
            if (!GameState.Ready || GameState.Loading) return;
            var save = SaveName(); if (string.IsNullOrEmpty(save) || save == _loadedFor) return;
            _loadedFor = save; var path = PathFor(save);
            try
            {
                Board.ClearAll();                                                                   // another save: never carry the old board into its file
                if (File.Exists(path) && CoopWire.FromJson(Board, File.ReadAllText(path)))
                    Log.Info($"co-op board loaded for save {save}: {Board.Jobs.Count} jobs, {Board.Notes.Count} notes, {Board.History.Count} past expeditions");
                Changed(false);
            }
            catch (Exception e) { Rl.Warn("load", "co-op board load failed: " + e.Message); }
        }

        static void Save()
        {
            if (_loadedFor == null) return; _persistDirty = false;
            try
            {
                var path = PathFor(_loadedFor); Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + ".tmp"; File.WriteAllText(tmp, CoopWire.ToJson(Board)); File.Move(tmp, path, true);
            }
            catch (Exception e) { Rl.Warn("save", "co-op board save failed: " + e.Message); }
        }

        // ---------------------------------------------------------------- where am I: anchors for jobs and notes
        /// <summary>The nearest corral/plot (12 m) or Gordo (15 m) to the local player; else the player's own position.</summary>
        public static (string anchor, string label, Vec3 position, int scene) Here()
        {
            var pl = SceneContext.Instance?.Player; if (pl == null) return ("", "here", default, Avatars.RemoteAvatars.LocalSceneGroup());
            var t = At(pl.transform.position, 12f, 15f); return (t.anchor, t.label, t.position, t.scene);
        }

        /// <summary>What is at a world point (the co-op wheel's aim): the nearest plot within <paramref name="plotRange"/> or Gordo within
        /// <paramref name="gordoRange"/>; else the spot itself. <c>kind</c>: corral, coop, garden, silo, pond, incinerator, plot, gordo or spot.</summary>
        public static (string anchor, string label, string kind, Vec3 position, int scene) At(Vector3 point, float plotRange = 8f, float gordoRange = 10f)
        {
            int scene = Avatars.RemoteAvatars.LocalSceneGroup(); string anchor = "", label = "this spot", kind = "spot"; Vector3 at = point; float best = float.MaxValue;
            try
            {
                foreach (var loc in SceneObjectCache.All<LandPlotLocation>())
                {
                    if (loc == null || !loc.gameObject.activeInHierarchy) continue; float d = Vector3.Distance(point, loc.transform.position);
                    if (d < plotRange && d < best)
                    {
                        best = d; anchor = "plot:" + loc.Id; at = loc.transform.position + Vector3.up * 2.5f;
                        var lp = loc.GetComponentInChildren<LandPlot>(); kind = lp != null ? PlotName(lp.TypeId) : "plot"; label = "this " + kind;
                    }
                }
                foreach (var eat in SceneObjectCache.All<GordoEat>())
                {
                    if (eat == null || !eat.gameObject.activeInHierarchy) continue; float d = Vector3.Distance(point, eat.transform.position);
                    if (d >= gordoRange || d >= best) continue;
                    var key = Lycoreia.Game.World.WorldRuntime.GordoKey(eat.GordoModel);
                    if (key != null) { best = d; anchor = "gordo:" + key; at = eat.transform.position + Vector3.up * 4f; kind = "gordo"; label = "this Gordo"; }
                }
            }
            catch (Exception e) { Rl.Warn("here", "anchor search failed: " + e.Message); }
            if (anchor.Length == 0) at = point + Vector3.up * 2.2f;
            return (anchor, label, kind, GameActorWorld.W(at), scene);
        }

        static string PlotName(LandPlot.Id id) => id switch
        {
            LandPlot.Id.CORRAL => "corral", LandPlot.Id.COOP => "coop", LandPlot.Id.GARDEN => "garden", LandPlot.Id.SILO => "silo", LandPlot.Id.POND => "pond", LandPlot.Id.INCINERATOR => "incinerator", _ => "plot",
        };
    }

    // ---------------------------------------------------------------- stat sources (native events on the seat where they happen)
    [HarmonyPatch(typeof(PlortEconomyDirector), nameof(PlortEconomyDirector.RegisterSold))]
    static class CoopPlortSoldStat
    {
        static void Postfix(int count) { try { if (Mod.IsNetworkActive && !Applying.Active && count > 0) CoopRuntime.Report(CoopStat.PlortsSold, count); } catch { } }
    }

    [HarmonyPatch(typeof(VacuumItem), nameof(VacuumItem.ConsumeVacuumable))]
    static class CoopVacuumStat
    {
        static readonly HashSet<long> _counted = new HashSet<long>();
        static void Prefix(GameObject gameObj)
        {
            try
            {
                if (!Mod.IsNetworkActive || gameObj == null) return;
                // ConsumeVacuumable runs again for the same object until it is really gone (2.1 test: "1109 food gathered" in 7 minutes):
                // count each actor once
                long key = ActorLink.IdOf(gameObj); if (key == 0) key = -gameObj.GetInstanceID();
                if (!_counted.Add(key)) return; if (_counted.Count > 8192) { _counted.Clear(); _counted.Add(key); }
                var ident = gameObj.GetComponent<IdentifiableActor>()?._model?.ident; if (ident == null) return;
                if (ident.TryCast<SlimeDefinition>() != null) CoopRuntime.Report(CoopStat.SlimesVacuumed, 1);
                else if (gameObj.GetComponent<ResourceCycle>() != null || ident.IsAnimal) CoopRuntime.Report(CoopStat.ProduceVacuumed, 1);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(TreasurePod), nameof(TreasurePod.Activate))]
    static class CoopPodStat
    {
        static void Prefix(TreasurePod __instance, out int __state) { __state = -1; try { if (__instance != null) __state = (int)__instance.CurrState; } catch { } }
        /// <summary>Counts only an activation that really opened the pod in this call (a guest's gated Activate does not run natively).</summary>
        static void Postfix(TreasurePod __instance, int __state)
        {
            try
            {
                if (!Mod.IsNetworkActive || Applying.Active || __instance == null || __state < 0 || (int)__instance.CurrState == __state) return;
                var pl = SceneContext.Instance?.Player; if (pl == null || Vector3.Distance(pl.transform.position, __instance.transform.position) > 8f) return;   // the player who opened it
                CoopRuntime.Report(CoopStat.PodsOpened, 1);
            }
            catch { }
        }
    }
}

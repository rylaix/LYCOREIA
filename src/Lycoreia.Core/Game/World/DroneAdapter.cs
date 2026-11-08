// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Il2CppMonomiPark.SlimeRancher.Drone;
using Il2CppMonomiPark.SlimeRancher.Drone.Cloud;
using Lycoreia.Game.Hooks;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.World;
using DroneTask = Lycoreia.Net.Messages.DroneTask;
using UnityEngine;

namespace Lycoreia.Game.World
{
    /// <summary>
    /// Drones over the live game: one lease owner simulates each drone (ranch movement, explorer program); everyone else freezes its
    /// own simulation and follows the owner's pose stream. Tasks and fuel from non-owners travel as intents to the owner. The drone
    /// cloud is one host-authoritative pool. Not synced yet: drone ammo, animator parameters, explorer resource stores, cloud extraction.
    /// </summary>
    public sealed class DroneAdapter : IDroneWorld
    {
        public static DroneAdapter Instance; public readonly DroneLeases Leases; public readonly DroneCloudSync Cloud;
        public const double PoseEvery = 0.1, LeaseEvery = 1.0, ClaimDelay = 2.0, ClaimRetry = 3.0;
        public const byte NoTask = 255;
        readonly Dictionary<long, double> _loadedSince = new Dictionary<long, double>(); readonly Dictionary<long, double> _claimedAt = new Dictionary<long, double>();
        readonly Dictionary<long, (Vector3 p, Quaternion r)> _poseTarget = new Dictionary<long, (Vector3, Quaternion)>();
        double _nextPose, _nextLease; static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);

        public static void Install(Session s, Lycoreia.Snapshot.SnapshotHost snap)
        {
            if (Instance != null) return; Instance = new DroneAdapter(s, snap);
        }
        DroneAdapter(Session s, Lycoreia.Snapshot.SnapshotHost snap)
        {
            Leases = new DroneLeases(s, this, snap) { OnTask = ApplyTask, OnBattery = ApplyBattery };
            Cloud = new DroneCloudSync(s, snap) { OnAmount = ApplyCloud };
            s.Node.On<DroneUpdate>(Side.Both, (c, m) => { if (!Leases.ShouldSimulate(m.StationId)) _poseTarget[m.StationId] = (new Vector3(m.Position.X, m.Position.Y, m.Position.Z), new Quaternion(m.Rotation.X, m.Rotation.Y, m.Rotation.Z, m.Rotation.W)); });
            s.ServerStarted += () => { Leases.Clear(); Cloud.Clear(); _poseTarget.Clear(); Leases.HostClaimAll(AllStationIds()); SeedCloud(); };
            s.ClientJoined += () => { Leases.Clear(); Cloud.Clear(); _poseTarget.Clear(); };
            s.PlayerRemoved += p => { if (p != null) Leases.ForgetPlayer(p.Id); };
        }

        // ---------------------------------------------------------------- model access
        static GameModel G => GameState.Model;
        static DroneStationGadgetModel Station(long id) { try { var g = G; return g != null && g.identifiables.TryGetValue(new ActorId(id), out var m) && m != null ? m.TryCast<DroneStationGadgetModel>() : null; } catch { return null; } }
        static IEnumerable<long> AllStationIds()
        {
            var g = G; var list = new List<long>(); if (g == null) return list;
            try { foreach (var model in g.AllGadgets()) if (model != null && model.TryCast<DroneStationGadgetModel>() != null) list.Add(model.actorId.Value); } catch (Exception e) { Rl.Warn("drone-enum", "drone station scan failed: " + e.Message); }
            return list;
        }
        static DroneStation StationObject(long id) { var m = Station(id); var go = m?.GetGameObject(); return go != null && go.activeInHierarchy ? go.GetComponent<DroneStation>() : null; }
        static GameObject DroneObject(DroneStation st) { if (st == null) return null; if (st.RanchDrone != null) return st.RanchDrone.gameObject; if (st.ExplorerDrone != null) return st.ExplorerDrone.gameObject; return null; }
        public bool DroneExists(long id) => Station(id) != null;
        public bool DroneHibernated(long id) => StationObject(id) == null;
        static TimeDirector Td => SceneContext.Instance != null ? SceneContext.Instance.TimeDirector : null;
        static float PerHour() { try { var d = SceneContext.Instance?.DroneDirector; return d != null ? d.EnergyDepletedPerHour : 1f; } catch { return 1f; } }
        static DroneCloudModel CloudModel { get { try { return SceneContext.Instance?.DroneDirector?.GetCloudModel(); } catch { return null; } } }

        // ---------------------------------------------------------------- simulation gate (hooks)
        public static bool MaySimulate(long stationId) { var a = Instance; return a == null || !Mod.IsNetworkActive || stationId == 0 || a.Leases.ShouldSimulate(stationId); }

        // ---------------------------------------------------------------- tasks
        public static DroneTask Describe(long station, DroneTaskData d)
        {
            var m = new DroneTask { ActorId = station, Target = NoTask };
            if (d == null) return m;
            m.Target = (byte)(int)d.TargetType; m.Source = (byte)(int)d.SourceType; m.Sink = (byte)(int)d.SinkType; m.TargetIdent = ActorTypes.IdOf(d.TargetIdentType);
            return m;
        }
        /// <summary>A local task assignment (UI). The owner publishes it; anyone else sends it as an intent (the owner's answer overrides).</summary>
        public void OnLocalTask(DroneStation st)
        {
            if (Applying.Active || st == null || st._model == null) return;
            long id = st._model.actorId.Value; var m = Describe(id, st.GetCurrentTaskData());
            if (Leases.ShouldSimulate(id)) { if (Mod.IsHost) Mod.Node.SendToAll(m); else Mod.Node.SendToHost(m); }
            else { m.Intent = true; Mod.Node.SendToHost(m); }
        }
        void ApplyTask(DroneTask m)
        {
            try
            {
                var st = StationObject(m.ActorId); if (st == null) return;                         // unloaded: the owner (who has it loaded) is the truth; it republishes on load
                using (Applying.Enter())
                {
                    if (m.Target == NoTask) { st.AssignTaskNone(); return; }
                    ActorTypes.TryGetType(m.TargetIdent, out var ident);
                    if (st.GetDroneType() == DroneType.EXPLORER_DRONE)
                    {
                        foreach (var t in st.GatherExplorerPossibleTaskTargets()) if (Matches(t, m.Target, ident)) { st.AssignExplorerTask(t, false); return; }
                        return;
                    }
                    foreach (var t in st.GatherRanchPossibleTaskTargets())
                    {
                        if (!Matches(t, m.Target, ident)) continue;
                        foreach (var src in st.GatherRanchPossibleTaskSources(t))
                        {
                            if ((byte)(int)src.SourceType != m.Source) continue;
                            foreach (var sink in st.GatherRanchPossibleTaskSinks(t, src.SourceType)) if ((byte)(int)sink.SinkType == m.Sink) { st.AssignRanchTask(t, src, sink, false); return; }
                        }
                    }
                    Rl.Warn("drone-task-" + m.ActorId, $"drone {m.ActorId}: task {m.Target}/{m.Source}/{m.Sink} not available here");
                }
            }
            catch (Exception e) { Rl.Warn("drone-task", "drone task apply failed: " + e.Message); }
        }
        static bool Matches(DroneTaskTarget t, byte type, IdentifiableType ident) => t != null && (byte)(int)t.TargetType == type && (ident == null || t.TargetIdent == null || t.TargetIdent.ReferenceId == ident.ReferenceId);

        // ---------------------------------------------------------------- battery
        public static int EnergyDepth;
        public void OnLocalEnergyAdded(DroneStationGadgetModel model, float perHour, float value)
        {
            if (Applying.Active || model == null || value == 0) return;
            long id = model.actorId.Value;
            if (Leases.ShouldSimulate(id)) PublishEnergy(id, model, perHour);
            else Mod.Node.SendToHost(new DroneBattery { ActorId = id, Charge = value, DepletedPerHour = perHour, Intent = true });
        }
        void PublishEnergy(long id, DroneStationGadgetModel model, float perHour)
        {
            var td = Td; if (td == null) return;
            var m = new DroneBattery { ActorId = id, Charge = model.GetCurrEnergy(td, perHour), DepletedPerHour = perHour };
            if (Mod.IsHost) Mod.Node.SendToAll(m); else Mod.Node.SendToHost(m);
        }
        void ApplyBattery(DroneBattery m)
        {
            try
            {
                var model = Station(m.ActorId); var td = Td; if (model == null || td == null || !float.IsFinite(m.Charge) || !float.IsFinite(m.DepletedPerHour) || m.DepletedPerHour <= 0) return;
                using (Applying.Enter()) { if (m.Intent) model.AddEnergy(td, m.DepletedPerHour, m.Charge); else model.SetEnergy(td, m.DepletedPerHour, m.Charge); }
                if (m.Intent) PublishEnergy(m.ActorId, model, m.DepletedPerHour);             // the owner answers an intent with its canonical charge
            }
            catch (Exception e) { Rl.Warn("drone-battery", "drone battery apply failed: " + e.Message); }
        }

        // ---------------------------------------------------------------- cloud
        void SeedCloud()
        {
            try { var c = CloudModel; if (c?._amounts == null) return; foreach (var kv in c._amounts) if (kv.Key != null) Cloud.Seed(ActorTypes.IdOf(kv.Key), kv.Value); }
            catch (Exception e) { Log.Warn("drone cloud seed: " + e.Message); }
        }
        public void OnLocalCloudChanged(DroneCloudModel model, IdentifiableType type)
        {
            if (Applying.Active || model == null || type == null) return;
            int id = ActorTypes.IdOf(type); if (id > 0) Cloud.LocalChanged(id, model.GetAmount(type));
        }
        void ApplyCloud(int typeId, int amount)
        {
            try { var c = CloudModel; if (c == null || !ActorTypes.TryGetType(typeId, out var t)) return; if (c.GetAmount(t) != amount) using (Applying.Enter()) c.SetAmount(t, amount); }
            catch (Exception e) { Rl.Warn("drone-cloud", "drone cloud apply failed: " + e.Message); }
        }

        // ---------------------------------------------------------------- frame pump: leases (1 Hz), owner pose (10 Hz), follower smoothing
        public void Pump(double now)
        {
            if (now >= _nextLease) { _nextLease = now + LeaseEvery; PumpLeases(now); }
            bool sendPose = now >= _nextPose; if (sendPose) _nextPose = now + PoseEvery;
            foreach (var id in new List<long>(Leases.Stations))
            {
                var st = StationObject(id); var drone = DroneObject(st); if (drone == null) continue;
                if (Leases.ShouldSimulate(id))
                {
                    if (!sendPose) continue; var t = drone.transform;
                    var m = new DroneUpdate { StationId = id, Position = new Vec3(t.position.x, t.position.y, t.position.z), Rotation = new Quat(t.rotation.x, t.rotation.y, t.rotation.z, t.rotation.w), AtStation = st._model != null && st._model.IsDroneAtStation != null && st._model.IsDroneAtStation.Value };
                    if (Mod.IsHost) Mod.Node.SendToAll(m); else Mod.Node.SendToHost(m);
                }
                else if (_poseTarget.TryGetValue(id, out var target))
                {
                    var t = drone.transform; float k = Mathf.Clamp01(Time.unscaledDeltaTime * 10f);
                    t.position = (t.position - target.p).sqrMagnitude > 100f ? target.p : Vector3.Lerp(t.position, target.p, k);
                    t.rotation = Quaternion.Slerp(t.rotation, target.r, k);
                }
            }
        }
        void PumpLeases(double now)
        {
            var s = Mod.Session; if (s == null) return;
            foreach (var id in AllStationIds())
            {
                bool loaded = StationObject(id) != null;
                if (loaded) { if (!_loadedSince.ContainsKey(id)) _loadedSince[id] = now; } else _loadedSince.Remove(id);
                string owner = Leases.OwnerOf(id); bool connected = !string.IsNullOrEmpty(owner) && s.Players.ContainsKey(owner);
                var a = LeasePlanner.Decide(owner, s.LocalId, loaded, connected, loaded ? now - _loadedSince[id] : 0, ClaimDelay);
                if (a == LeaseAction.Release) Leases.Release(id);
                else if (a == LeaseAction.Claim && (!_claimedAt.TryGetValue(id, out var at) || now - at >= ClaimRetry)) { _claimedAt[id] = now; Leases.Claim(id); }
            }
        }
    }

    // ---------------------------------------------------------------- hooks: non-owners never run drone simulation; local changes publish
    [HarmonyPatch(typeof(RanchDrone), nameof(RanchDrone.RegistryFixedUpdate))]
    static class RanchDroneFreezeHook
    {
        static bool Prefix(RanchDrone __instance) { try { var m = __instance.GetModel(); return m == null || DroneAdapter.MaySimulate(m.StationId.Value); } catch (Exception e) { HookGuard.Fail("RanchDrone.RegistryFixedUpdate", e); return true; } }
    }
    [HarmonyPatch(typeof(ExplorerDroneMovementManager), nameof(ExplorerDroneMovementManager.FixedUpdate))]
    static class ExplorerDroneFreezeHook
    {
        static bool Prefix(ExplorerDroneMovementManager __instance) { try { var m = __instance._stationModel; return m == null || DroneAdapter.MaySimulate(m.actorId.Value); } catch (Exception e) { HookGuard.Fail("ExplorerDroneMovementManager.FixedUpdate", e); return true; } }
    }
    [HarmonyPatch(typeof(DroneStationGadgetModel), nameof(DroneStationGadgetModel.UpdateProgram))]
    static class ExplorerProgramFreezeHook
    {
        static bool Prefix(DroneStationGadgetModel __instance) { try { return DroneAdapter.MaySimulate(__instance.actorId.Value); } catch (Exception e) { HookGuard.Fail("DroneStationGadgetModel.UpdateProgram", e); return true; } }
    }
    [HarmonyPatch]
    static class DroneTaskHook
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(DroneStation), nameof(DroneStation.AssignRanchTask));
            yield return AccessTools.Method(typeof(DroneStation), nameof(DroneStation.AssignExplorerTask));
            yield return AccessTools.Method(typeof(DroneStation), nameof(DroneStation.AssignTaskNone));
        }
        static void Postfix(DroneStation __instance) { try { if (WH.On && DroneAdapter.Instance != null) DroneAdapter.Instance.OnLocalTask(__instance); } catch (Exception e) { HookGuard.Fail("DroneStation.AssignTask", e); } }
    }
    [HarmonyPatch(typeof(DroneStationGadgetModel), nameof(DroneStationGadgetModel.AddEnergy))]
    static class DroneEnergyHook
    {
        static void Postfix(DroneStationGadgetModel __instance, float energyDepletedPerHour, float value) { try { if (WH.On && DroneAdapter.Instance != null) DroneAdapter.Instance.OnLocalEnergyAdded(__instance, energyDepletedPerHour, value); } catch (Exception e) { HookGuard.Fail("DroneStationGadgetModel.AddEnergy", e); } }
    }
    [HarmonyPatch]
    static class DroneCloudHook
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(DroneCloudModel), nameof(DroneCloudModel.AddAmount));
            yield return AccessTools.Method(typeof(DroneCloudModel), nameof(DroneCloudModel.SetAmount));
        }
        static void Postfix(DroneCloudModel __instance, IdentifiableType type) { try { if (WH.On && DroneAdapter.Instance != null) DroneAdapter.Instance.OnLocalCloudChanged(__instance, type); } catch (Exception e) { HookGuard.Fail("DroneCloudModel.amount", e); } }
    }
}

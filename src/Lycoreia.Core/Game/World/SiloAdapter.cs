// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Lycoreia.Game.Hooks;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.World;

namespace Lycoreia.Game.World
{
    public sealed class SiloAdapter
    {
        public static SiloAdapter Instance;
        const double HostEvery = 0.5, GuestEvery = 0.25;
        static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);
        readonly Session _s;
        readonly Dictionary<string, List<SiloSlot>> _sent = new Dictionary<string, List<SiloSlot>>();      // host: last broadcast per storage
        readonly Dictionary<string, List<SiloSlot>> _baseline = new Dictionary<string, List<SiloSlot>>();  // guest: last host state applied
        double _next;

        public static void Install(Session s, Lycoreia.Snapshot.SnapshotHost snap) { if (Instance == null) Instance = new SiloAdapter(s, snap); }

        SiloAdapter(Session s, Lycoreia.Snapshot.SnapshotHost snap)
        {
            _s = s;
            s.Node.On<SiloState>(Side.Guest, (c, m) => OnState(m));
            s.Node.On<SiloDelta>(Side.Host, (c, m) => OnDelta(c, m));
            snap.Register(Lycoreia.Snapshot.SnapStep.LandPlots, ctx =>
            {
                int n = 0; foreach (var (plot, set, am) in Storages()) { ctx.Send(Make(plot, set, Read(am))); n++; }
                Log.Info($"plot storage snapshot sent: {n} storages");
            });
            s.ServerStarted += Clear; s.ServerStopped += Clear; s.ClientJoined += Clear; s.ClientDisconnected += _ => Clear();
        }
        void Clear() { _sent.Clear(); _baseline.Clear(); }

        // ---------------------------------------------------------------- game model
        static IEnumerable<(string plot, string set, AmmoModel am)> Storages()
        {
            var list = new List<(string, string, AmmoModel)>(); var plots = GameState.LandPlots; if (plots == null) return list;
            foreach (var kv in plots)
            {
                var silo = kv.Value?.siloAmmo; if (silo == null || kv.Key == null) continue;
                foreach (var s in silo) if (s.Value != null && s.Key != null) list.Add((kv.Key, s.Key, s.Value));
            }
            return list;
        }
        static AmmoModel Find(string plot, string set)
        {
            var plots = GameState.LandPlots; if (plots == null || plot == null || !plots.TryGetValue(plot, out var m) || m?.siloAmmo == null) return null;
            return set != null && m.siloAmmo.TryGetValue(set, out var am) ? am : null;
        }
        static List<SiloSlot> Read(AmmoModel am)
        {
            var list = new List<SiloSlot>(); var slots = am?.Slots; if (slots == null) return list;
            foreach (var sl in slots) { if (sl == null) { list.Add(default); continue; } list.Add(new SiloSlot(sl.Id != null ? ActorTypes.IdOf(sl.Id) : 0, sl.Count)); }
            return list;
        }
        /// <summary>Writes slot values into the model (the silo UI and the vacuum read the model).</summary>
        static void Write(AmmoModel am, IReadOnlyList<SiloSlot> want)
        {
            var slots = am?.Slots; if (slots == null) return;
            for (int i = 0; i < Math.Min(slots.Length, want.Count); i++)
            {
                var sl = slots[i]; if (sl == null) continue; var w = want[i];
                int have = sl.Id != null ? ActorTypes.IdOf(sl.Id) : 0;
                if (w.Empty) { if (sl.Id != null || sl.Count != 0) { sl.Clear(); am.OnOnAmmoChanged(sl); } continue; }
                if (!ActorTypes.TryGetType(w.Type, out var type)) continue;
                if (have == w.Type && sl.Count == w.Count) continue;
                sl.Id = type; sl.Count = w.Count; am.OnOnAmmoChanged(sl);
            }
        }
        static SiloState Make(string plot, string set, List<SiloSlot> slots)
            => new SiloState { Plot = plot, Set = set, Slots = slots.Select(x => new SiloSlotWire { Type = x.Empty ? 0 : x.Type, Count = x.Empty ? 0 : x.Count }).ToList() };
        static List<SiloSlot> FromWire(SiloState m) => m.Slots.Select(x => new SiloSlot(x.Type, x.Count)).ToList();

        // ---------------------------------------------------------------- pump
        public void Pump(double now)
        {
            if (!Mod.IsNetworkActive || !GameState.Ready || GameState.Loading || now < _next) return;
            _next = now + (_s.IsHost ? HostEvery : GuestEvery);
            if (_s.IsHost) HostBroadcastChanges(); else if (Mod.Guest != null && Mod.Guest.Applied) GuestSendChanges(null);
        }

        void HostBroadcastChanges()
        {
            foreach (var (plot, set, am) in Storages())
            {
                var cur = Read(am); var key = SiloLedger.Key(plot, set);
                if (_sent.TryGetValue(key, out var last) && SiloLedger.SameState(last, cur)) continue;
                bool first = last == null; _sent[key] = cur;
                if (first) continue;                                         // first sight after start: the snapshot already carried it
                _s.Node.SendToAll(Make(plot, set, cur));
                Mod.Diagnostics?.Record("world", "silo-state-sent", $"plot={plot}; set={set}; slots={string.Join(",", cur)}");
            }
        }

        /// <summary>Guest: local changes since the last host state go to the host. <paramref name="only"/> limits it to one storage.</summary>
        void GuestSendChanges(string only)
        {
            foreach (var (plot, set, am) in Storages())
            {
                var key = SiloLedger.Key(plot, set); if (only != null && key != only) continue;
                if (!_baseline.TryGetValue(key, out var baseline)) continue;                       // no host state yet: nothing to compare with
                var cur = Read(am); var changes = SiloLedger.Diff(baseline, cur); if (changes.Count == 0) continue;
                foreach (var ch in changes) _s.Node.SendToHost(new SiloDelta { Plot = plot, Set = set, Slot = ch.Slot, Type = ch.Type, Delta = ch.Delta });
                _baseline[key] = cur;                                                              // optimistic until the host answers
                Mod.Diagnostics?.Record("world", "silo-delta-sent", $"plot={plot}; set={set}; changes={changes.Count}");
            }
        }

        void OnState(SiloState m)
        {
            try
            {
                var key = SiloLedger.Key(m.Plot, m.Set); var am = Find(m.Plot, m.Set);
                if (am == null) { Rl.Warn("silo-missing:" + key, $"plot storage {key} from the host does not exist here"); return; }
                if (_baseline.ContainsKey(key)) GuestSendChanges(key);                            // never lose a local change made since the last state
                var want = FromWire(m); using (Applying.Enter()) Write(am, want);
                _baseline[key] = Read(am);
            }
            catch (Exception e) { Rl.Warn("silo-state", "plot storage apply failed: " + e.Message); }
        }

        Verdict OnDelta(Ctx c, SiloDelta m)
        {
            try
            {
                var am = Find(m.Plot, m.Set); var slots = am?.Slots;
                if (slots == null || m.Slot < 0 || m.Slot >= slots.Length || slots[m.Slot] == null) return Verdict.Reject;
                var cur = Read(am); var before = cur[m.Slot];
                cur[m.Slot] = SiloLedger.Apply(before, new SiloChange { Slot = m.Slot, Type = m.Type, Delta = m.Delta }, slots[m.Slot].MaxCount);
                if (!cur[m.Slot].Equals(before)) Write(am, cur);
                // the broadcast pump sends the new state (also back to the sender, which corrects a refused change)
                var key = SiloLedger.Key(m.Plot, m.Set); _sent.Remove(key); _sent[key] = new List<SiloSlot>();
                Mod.Diagnostics?.Record("world", "silo-delta-applied", $"plot={m.Plot}; set={m.Set}; slot={m.Slot}; type={m.Type}; delta={m.Delta}; from={c.SenderId}; before={before}; after={cur[m.Slot]}");
            }
            catch (Exception e) { Rl.Warn("silo-delta", "plot storage change failed: " + e.Message); }
            return Verdict.Accept;
        }
    }

    // ---------------------------------------------------------------- native writers that must run once (on the host)
    static class StorageAuthority
    {
        public static bool HostOnly => !Mod.IsNetworkActive || Mod.IsHost;
    }

    /// <summary>The corral plort collector: host only. Its consume of a guest-owned plort removes the plort for everyone (HostConsume).</summary>
    [HarmonyPatch(typeof(PlortCollector), nameof(PlortCollector.FixedUpdate))]
    static class PlortCollectorHostOnly
    {
        static bool Prefix(out bool __state)
        {
            __state = false; if (!StorageAuthority.HostOnly) return false;
            if (Mod.IsNetworkActive) { SlimeFoodAuthorityHook.HostConsumeDepth++; __state = true; }
            return true;
        }
        static Exception Finalizer(Exception __exception, bool __state) { if (__state && SlimeFoodAuthorityHook.HostConsumeDepth > 0) SlimeFoodAuthorityHook.HostConsumeDepth--; return __exception; }
    }
    [HarmonyPatch(typeof(PlortCollector), nameof(PlortCollector.StartCollection))]
    static class PlortCollectorStartHostOnly { static bool Prefix() => StorageAuthority.HostOnly; }
    [HarmonyPatch(typeof(PlortCollector), nameof(PlortCollector.FastForward))]
    static class PlortCollectorFastForwardHostOnly { static bool Prefix() => StorageAuthority.HostOnly; }

    /// <summary>The auto-feeder takes food out of storage and spawns it as actors: host only (the guest sees the spawned food as replicas).</summary>
    [HarmonyPatch(typeof(SlimeFeeder), nameof(SlimeFeeder.FixedUpdate))]
    static class SlimeFeederHostOnly { static bool Prefix() => StorageAuthority.HostOnly; }
    [HarmonyPatch(typeof(SlimeFeeder), nameof(SlimeFeeder.UpdateToTime))]
    static class SlimeFeederFastForwardHostOnly { static bool Prefix() => StorageAuthority.HostOnly; }
}

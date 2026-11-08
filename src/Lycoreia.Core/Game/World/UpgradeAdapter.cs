// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.DataModel;
using Lycoreia.Net;
using Lycoreia.World;
using UnityEngine;

namespace Lycoreia.Game.World
{
    /// <summary>
    /// Upgrade levels by the definition's unique id. Guests apply levels through the game's own level-up path (so dependent state such
    /// as node harvesting refreshes) under the applying flag. Guest purchases are not wired yet: price and charge refuse.
    /// </summary>
    public sealed class UpgradeAdapter : IUpgradeWorld
    {
        Dictionary<int, UpgradeDefinition> _defs;
        static UpgradeModel Model { get { try { return GameState.Model?.player?.upgradeModel; } catch { return null; } } }
        UpgradeDefinition Def(int id)
        {
            if (_defs == null || !_defs.ContainsKey(id)) { _defs = new Dictionary<int, UpgradeDefinition>(); foreach (var d in Resources.FindObjectsOfTypeAll<UpgradeDefinition>()) if (d != null) _defs[d.UniqueId] = d; }
            return _defs.TryGetValue(id, out var x) ? x : null;
        }
        public IEnumerable<int> AllIds() { Def(int.MinValue); return _defs.Keys.ToList(); }
        public int Level(int id) { try { var m = Model; var d = Def(id); return m != null && d != null ? m.GetUpgradeLevel(d) : -1; } catch { return -1; } }
        public int PriceFor(int id, int level) => int.MaxValue;
        public int Currency => 0;
        public bool TryCharge(int amount) => false;
        public void SetLevel(int id, int level)
        {
            try
            {
                var m = Model; var d = Def(id); if (m == null || d == null) return;
                using (Applying.Enter())
                {
                    int guard = 16; while (m.GetUpgradeLevel(d) < level && guard-- > 0) if (!m.IncrementUpgradeLevel(d)) break;   // the game's own level-up path
                    if (m.GetUpgradeLevel(d) != level) m.SetUpgradeLevel(d, level);
                }
            }
            catch (Exception e) { Log.Warn($"upgrade {id} -> {level}: {e.Message}"); }
        }

        public static UpgradeSync Install(Session s, Lycoreia.Snapshot.SnapshotHost snap)
        {
            var a = new UpgradeAdapter(); var sync = new UpgradeSync(s, a, snap) { AllIds = a.AllIds };
            Hooks.UpgradeLevelHook.LevelChanged += d => { if (d != null) sync.HostLocalChanged(d.UniqueId); };
            return sync;
        }
    }
}

// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher;
using Lycoreia.Actors;
using Lycoreia.Net;
using UnityEngine;

namespace Lycoreia.Game
{
    /// <summary>The game halves of /cleanup, addsprinkles and backupsave.</summary>
    public static class GameCommands
    {
        static CleanupPlan _preview;

        /// <summary>/cleanup: a preview (count and per-type summary); "now" plans again and removes 40 per frame with a destroy for everyone.</summary>
        public static string Cleanup(bool now)
        {
            var a = Mod.Actors; if (a == null || !Mod.IsHost) return "Only the host can run /cleanup.";
            if (ActorRuntime.CleanupPending > 0) return $"Cleanup is already running ({ActorRuntime.CleanupPending} left).";
            var w = new GameActorWorld(); bool ranchKnown = GameActorWorld.Ranch.Count > 0;
            var items = a.Table.All.Select(e => new CleanupItem(e.Id, e.TypeId, e.Scene, e.Position, w.ClassOf(e.TypeId), gadget: e.IsGadget, ranch: GameActorWorld.Ranch.Contains(e.Position) == true, conservatory: IsConservatory(e.Scene))).ToList();
            var plan = Actors.Cleanup.Plan(items, ranchKnown);
            if (!now)
            {
                _preview = plan;
                if (plan.Remove.Count == 0) return "Cleanup: nothing to remove.";
                return $"Cleanup would remove {plan.Remove.Count} actors: " + string.Join(", ", plan.Summary.Take(8).Select(s => $"{TypeName(s.TypeId)} x{s.Count}")) + ". Run /cleanup now to remove them.";
            }
            ActorRuntime.QueueCleanup(plan.Remove);
            Log.Info($"cleanup: removing {plan.Remove.Count} actors (preview said {_preview?.Remove.Count.ToString() ?? "n/a"})");
            Mod.Diagnostics?.Record("actor", "cleanup", $"remove={plan.Remove.Count}; total={plan.Total}; ranchKnown={ranchKnown}");
            return plan.Remove.Count == 0 ? "Cleanup: nothing to remove." : $"Cleanup: removing {plan.Remove.Count} actors.";
        }

        static bool IsConservatory(int scene) { try { var g = SceneGroups.Get(scene); return g != null && (g.name ?? "").Contains("Conservatory", StringComparison.OrdinalIgnoreCase); } catch { return false; } }
        static string TypeName(int typeId) => ActorTypes.TryGetType(typeId, out var t) && t != null ? t.name : "type " + typeId;

        /// <summary>addsprinkles &lt;n&gt;: spawns sprinkles at the player (cheat; the router checks the setting).</summary>
        public static void AddSprinkles(int count)
        {
            try
            {
                var player = SceneContext.Instance?.Player; var model = GameState.Model; if (player == null || model == null) { Log.Warn("addsprinkles: not in a game"); return; }
                IdentifiableType type = null;
                foreach (var t in Resources.FindObjectsOfTypeAll<IdentifiableType>()) if (t != null && t.prefab != null && (t.name ?? "").Contains("Sprinkle", StringComparison.OrdinalIgnoreCase)) { type = t; break; }
                if (type == null) { Log.Warn("addsprinkles: no sprinkle type found"); return; }
                var group = player.GetComponent<Il2CppMonomiPark.SlimeRancher.Regions.RegionMember>()?.Regions is { Count: > 0 } rs ? rs[0].SceneGroup : null;
                if (group == null) { Log.Warn("addsprinkles: player scene group unknown"); return; }
                count = Math.Clamp(count, 1, 500); var at = player.transform.position + player.transform.forward * 2f + Vector3.up;
                for (int i = 0; i < count; i++)
                    InstantiationHelpers.InstantiateActor(type.prefab, group, at + UnityEngine.Random.insideUnitSphere * 0.75f, Quaternion.identity, false, default, default, null, false, false);
                Log.Info($"added {count} {type.name}");
            }
            catch (Exception e) { Log.Error("addsprinkles failed", e); }
        }

        /// <summary>backupsave: copies the save files into the data folder's SaveBackups (one folder per backup), then prunes when enabled.</summary>
        public static string BackupSave(string reason = "manual")
        {
            try
            {
                string saves = Application.persistentDataPath; if (string.IsNullOrEmpty(saves) || !Directory.Exists(saves)) return "Save folder not found.";
                var files = Directory.GetFiles(saves, "*.sav", SearchOption.AllDirectories);
                if (files.Length == 0) return "No save files found.";
                string root = Path.Combine(Mod.DataFolder, "SaveBackups"), target = Path.Combine(root, $"{DateTime.Now:yyyyMMdd-HHmmss}-{reason}");
                foreach (var f in files)
                {
                    string rel = Path.GetRelativePath(saves, f), dst = Path.Combine(target, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)); File.Copy(f, dst, true);
                }
                if (Prefs.Get<bool>(Settings.DeleteOldBackups)) Prune(root, Math.Max(1, Prefs.Get<int>(Settings.BackupsToKeep)));
                Log.Info($"save backup ({reason}): {files.Length} files -> {target}");
                return $"Backed up {files.Length} save files.";
            }
            catch (Exception e) { Log.Error("save backup failed", e); return "Save backup failed: " + e.Message; }
        }

        public static void Prune(string root, int keep)
        {
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.GetDirectories(root).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).Skip(keep))
                try { Directory.Delete(d, true); } catch (Exception e) { Log.Warn("could not delete old backup: " + e.Message); }
        }

        /// <summary>Automatic backups when hosting starts and when a client joins, unless switched off.</summary>
        public static void AutoBackup(string reason)
        {
            if (!Prefs.Get<bool>(Settings.AutoBackups) || Prefs.Get<bool>(Settings.SkipHostConnectBackups)) return;
            BackupSave(reason);
        }
    }
}

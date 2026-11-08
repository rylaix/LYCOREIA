// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections;
using Il2Cpp;
using Lycoreia.Actors;
using Lycoreia.Snapshot;
using UnityEngine;

namespace Lycoreia.Game
{
    /// <summary>Guest apply: wait for the player and the end of scene loads (180 s), clear, wait 2 frames if anything was destroyed, spawn.</summary>
    public sealed class GameSnapshotApplier : IActorSnapshotApplier
    {
        public const float WaitLimitSeconds = 180f;
        readonly ActorSync _sync;
        public GameSnapshotApplier(ActorSync sync) { _sync = sync; }

        public void Apply(ActorSnapshot snapshot, Func<bool> stillCurrent, Action<ApplyOutcome> done)
        {
            Coroutines.Start(Run(snapshot, stillCurrent, done));
        }

        IEnumerator Run(ActorSnapshot snap, Func<bool> current, Action<ApplyOutcome> done)
        {
            float until = Time.unscaledTime + WaitLimitSeconds;
            while (SceneContext.Instance == null || SceneContext.Instance.Player == null || SceneLoading())
            {
                if (!current() || Time.unscaledTime > until) { done(ApplyOutcome.Stale); yield break; }   // gave up, or a newer snapshot / a disconnect made this one stale
                yield return null;
            }
            if (!current()) { done(ApplyOutcome.Stale); yield break; }
            ApplyOutcome outcome; int destroyed;
            GameActorWorld.InternalDepth++;
            VacuumCapture.OnResync();                                                                      // irreversible captures survive; the others abort per actor
            int clearedTable, clearedLocal;
            try { clearedTable = _sync.ClearForSnapshot(); clearedLocal = GameActorWorld.ClearLocalActors(VacuumCapture.IrreversibleIds()); destroyed = clearedTable + clearedLocal; }
            finally { GameActorWorld.InternalDepth--; }
            ActorReconciler.SetGuestBlock(snap.StartingActorId);                                         // our own spawns use the block the host assigned from now on
            if (destroyed > 0) { yield return null; yield return null; }                                  // live changes stay queued in the delta buffer meanwhile
            if (!current()) { done(ApplyOutcome.Stale); yield break; }
            GameActorWorld.InternalDepth++;
            try { outcome = _sync.SpawnFromSnapshot(snap); }
            catch (Exception e) { Log.Error("snapshot spawn", e); outcome = ApplyOutcome.RuntimeFailure; }
            finally { GameActorWorld.InternalDepth--; }
            ActorReconciler.RepairAllocator(_sync); ActorRuntime.SnapshotAppliedAt = Time.unscaledTimeAsDouble;
            Log.Info($"actor snapshot applied: {snap.Actors.Count} records, {_sync.Table.Count} in table; cleared {clearedTable} replicas and {clearedLocal} save actors; outcome {outcome}");
            Mod.Diagnostics?.Record("actors", "snapshot-applied", $"records={snap.Actors.Count}; table={_sync.Table.Count}; cleared-replicas={clearedTable}; cleared-local={clearedLocal}; block={snap.StartingActorId}; outcome={outcome}");
            done(outcome);
        }

        static bool SceneLoading()
        {
            try { return UnityEngine.SceneManagement.SceneManager.sceneCount > 1 && false; } catch { return false; }
        }
    }
}

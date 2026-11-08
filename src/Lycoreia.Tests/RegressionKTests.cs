// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using Lycoreia.Actors;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.World;

namespace Lycoreia.Tests
{
    static class RegressionKTests
    {
        public static void Register()
        {
            T.Test("currency/initial approve survives join callback; disconnect and next host balance reset revisions", () =>
            {
                var w = new World(); var h = w.StartHost(); var ch = new CurrencyLedger(h); ch.Seed(1, 100);
                var guest = w.AddGuest(); var cg = new CurrencyLedger(guest);
                w.Join(guest); T.True(w.Net.RunUntil(() => guest.Joined, 4000));
                T.Eq(100, cg.Get(1)); T.Eq(100, cg.ExpectedNative(1));
                ch.HostLocalChanged(1, 150); w.Net.Step(300); T.Eq(150, cg.Get(1));
                guest.Disconnect(); w.Net.Step(300); T.Eq(0, cg.Get(1));
                ch.Reset(); ch.Seed(1, 25);
                w.Join(guest); T.True(w.Net.RunUntil(() => guest.Joined, 4000));
                T.Eq(25, cg.Get(1), "old higher revision cannot block a new session's approve");
            });
            T.Test("currency/two guest rewards and spend converge; pending native prediction survives intermediate acknowledgements", () =>
            {
                var w = new World(); var h = w.StartHost(); var ch = new CurrencyLedger(h); ch.Seed(1, 100);
                var a = w.AddGuest("A"); var ca = new CurrencyLedger(a); var b = w.AddGuest("B"); var cb = new CurrencyLedger(b);
                w.Join(a); w.Join(b); T.True(w.Net.RunUntil(() => a.Joined && b.Joined, 4000)); w.Net.Step(200);
                var expectedA = new List<int>(); ca.OnBalanceChanged = (t, n) => expectedA.Add(ca.ExpectedNative(t));
                ca.Request(1, 10); ca.Request(1, -5);
                T.Eq(105, ca.ExpectedNative(1));
                w.Net.Step(300); T.Eq(105, ch.Get(1)); T.Eq(105, ca.Get(1)); T.Eq(105, cb.Get(1));
                T.True(expectedA.TrueForAll(n => n == 105), "the first ACK must not roll back a later local transaction");
                cb.Request(1, 30); w.Net.Step(300); T.Eq(135, ch.Get(1)); T.Eq(135, ca.Get(1)); T.Eq(135, cb.Get(1));
            });
            T.Test("currency/rejection at same revision retracts prediction; stale and negative state cannot overwrite", () =>
            {
                var w = new World(); var h = w.StartHost(); var ch = new CurrencyLedger(h); ch.Seed(1, 50);
                var g = w.AddGuest(); var cg = new CurrencyLedger(g); w.Join(g); T.True(w.Net.RunUntil(() => g.Joined, 4000));
                int corrected = 0; cg.OnBalanceChanged = (t, n) => corrected++;
                cg.Request(1, -500); T.Eq(0, cg.ExpectedNative(1)); w.Net.Step(300);
                T.Eq(50, cg.ExpectedNative(1)); T.True(corrected > 0, "same-revision rejection must correct native state");
                ch.HostLocalChanged(1, 60); w.Net.Step(300);
                h.Node.SendToAll(new CurrencyAdjust { CurrencyType = 1, NewAmount = 1, Revision = 0 });
                h.Node.SendToAll(new CurrencyAdjust { CurrencyType = 1, NewAmount = -10, Revision = 999 }); w.Net.Step(300);
                T.Eq(60, cg.Get(1));
            });
            T.Test("currency/remote income animates once; sales use host price; checkpoints and retries do not repeat notification", () =>
            {
                var w = new World(); var h = w.StartHost(); var ch = new CurrencyLedger(h); ch.Seed(1, 100);
                var g = w.AddGuest(); var cg = new CurrencyLedger(g); int notices = 0;
                cg.OnBalanceChanged = (t, n) => { if (cg.ApplyingNotification) notices++; };
                w.Join(g); T.True(w.Net.RunUntil(() => g.Joined, 4000)); T.Eq(0, notices, "join is a silent snapshot");
                ch.HostLocalChanged(1, 125); w.Net.Step(300); T.Eq(1, notices, "host vase/reward income notifies remote HUD");
                var market = new MarketSystem(h, ch) { SaleCurrency = 1, CurrentHostPrice = _ => 7 };
                var id = Guid.NewGuid();
                g.Node.SendToHost(new PlortSold { RequestId = id, ItemId = 7, Count = 3, Price = 1000 }); w.Net.Step(300);
                T.Eq(146, cg.Get(1)); T.Eq(2, notices);
                g.Node.SendToHost(new PlortSold { RequestId = id, ItemId = 7, Count = 3, Price = 1000 });
                ch.PublishCheckpoint(); ch.PublishCheckpoint(); w.Net.Step(300);
                T.Eq(146, cg.Get(1)); T.Eq(2, notices, "retries and repair checkpoints are silent");
            });
            T.Test("actors/rejoin starting id continues past consumed (tombstoned) ids of the guest block", () =>
            {
                var t = new ActorTable(); string pid = "PLAYER_REJOIN"; var b = ActorIdBlock.For(pid, false);
                T.Eq(b.start, ActorSync.StartingIdFor(t, pid), "empty block starts at its start");
                t.Add(new ActorEntry { Id = b.start + 3 }); t.Add(new ActorEntry { Id = b.start + 9 }); t.Remove(b.start + 9, 42);
                T.True(t.IsTombstoned(b.start + 9));
                T.Eq(b.start + 10, ActorSync.StartingIdFor(t, pid), "a consumed id is never handed out again");
                t.Add(new ActorEntry { Id = 5 }); t.Add(new ActorEntry { Id = b.end + 1 });
                T.Eq(b.start + 10, ActorSync.StartingIdFor(t, pid), "ids of other blocks do not count");
                T.Eq(b.start + 11, ActorIdBlock.Repair(b.start + 2, b, b.start + 10), "the allocator floor moves a rolled-back allocator past it");
            });
            T.Test("animals/sleep catch-up runs only on awake owner and cannot replay old reproduction deadline each frame", () =>
            {
                T.False(AnimalSimulationPolicy.MayReproduce(true, false, false, false), "a replica never breeds");
                T.False(AnimalSimulationPolicy.MayReproduce(true, true, true, false), "a sleeping region never breeds");
                T.False(AnimalSimulationPolicy.MayReproduce(true, true, false, true), "scene load never breeds");
                T.True(AnimalSimulationPolicy.MayReproduce(true, true, false, false));
                T.True(AnimalSimulationPolicy.MayReproduce(false, false, true, true), "offline native behavior is preserved");
                double next = AnimalSimulationPolicy.NextDeadline(20, 10000, 3600);
                T.Eq(13600.0, next, "one catch-up event schedules the next full period from now");
                T.Eq(next, AnimalSimulationPolicy.NextDeadline(next, 10001, 3600), "ordinary future deadline is untouched");
            });
            T.Test("hazards/player kill deferred while loading and in the pre-flag window only", () =>
            {
                T.True(LoadingHazardPolicy.DeferPlayerKill(true, true, 100, double.NegativeInfinity));
                T.True(LoadingHazardPolicy.DeferPlayerKill(true, false, 100, 99.5));
                T.False(LoadingHazardPolicy.DeferPlayerKill(true, false, 100, 97.9));
                T.False(LoadingHazardPolicy.DeferPlayerKill(true, false, 100, double.NegativeInfinity));
                T.False(LoadingHazardPolicy.DeferPlayerKill(false, true, 100, 99.5), "actors are never deferred");
                T.False(LoadingHazardPolicy.DeferPlayerKill(true, false, 100, 101), "clock older than request is not a window");
            });
            T.Test("animals/reproduction cap blocks at the ranch and wild limits", () =>
            {
                T.True(AnimalSimulationPolicy.MayBreedAtPopulation(ChickenCap.RanchWithin30m - 1, true));
                T.False(AnimalSimulationPolicy.MayBreedAtPopulation(ChickenCap.RanchWithin30m, true));
                T.True(AnimalSimulationPolicy.MayBreedAtPopulation(ChickenCap.Within30m - 1, false));
                T.False(AnimalSimulationPolicy.MayBreedAtPopulation(ChickenCap.Within30m, false));
                T.True(AnimalSimulationPolicy.ReproductionCap(true) > AnimalSimulationPolicy.ReproductionCap(false));
            });
        }
    }
}

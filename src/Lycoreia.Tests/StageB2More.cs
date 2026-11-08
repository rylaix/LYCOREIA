// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.Snapshot;
using Lycoreia.World;

namespace Lycoreia.Tests
{
    static partial class StageB2Tests
    {
        public static void RegisterMore()
        {
            T.Test("gordo rewards/ledger: pays at most once; a pending burst pays when the gordo loads; a thrown payout is uncertain unless nothing spawned", () =>
            {
                var l = new GordoRewardLedger();
                T.True(l.TryBegin("g1")); T.False(l.TryBegin("g1"), "already running"); l.Complete("g1"); T.True(l.HasGiven("g1")); T.False(l.TryBegin("g1"), "never re-paid");
                l.MarkPending("g2"); T.True(l.HasPending("g2")); T.True(l.TryBegin("g2")); T.True(l.InProgress("g2")); l.Fail("g2", 0); T.True(l.HasPending("g2"), "nothing spawned: safe retry"); T.False(l.IsUncertain("g2"));
                T.True(l.TryBegin("g2")); l.Fail("g2", null); T.True(l.IsUncertain("g2")); T.False(l.HasPending("g2")); T.False(l.TryBegin("g2"), "uncertain: no retry");
                T.True(l.TryBegin("g3")); l.Fail("g3", 2); T.True(l.IsUncertain("g3"), "something spawned: uncertain");
                l.LoadGiven(new[] { "g4" }); l.MarkPending("g4"); T.False(l.HasPending("g4"), "already rewarded in the save"); T.False(l.TryBegin("g4"));
            });
            T.Test("market/sale counts 1..10,000 for known items register natively once; prices publish only when changed and reach late joiners", () =>
            {
                var (w, h, g) = Rig(1); var snap = new SnapshotHost(h); var ch = new CurrencyLedger(h); var mh = new MarketSystem(h, ch) { SaleCurrency = 1, IsKnownItem = id => id == 7 };
                var mg = new MarketSystem(g[0], new CurrencyLedger(g[0])); var sold = new List<(int, int)>(); mh.OnSold = (i, n, price) => sold.Add((i, n)); int pricesSeen = 0; mg.OnPrices = p => pricesSeen++;
                snap.Register(SnapStep.Prices, ctx => { var m = new MarketPrices(); foreach (var kv in mh.Prices) m.Prices.Add(new IntPair { A = kv.Key, B = kv.Value }); ctx.Send(m); });
                T.True(mh.PublishIfChanged(new Dictionary<int, int> { [7] = 12 })); T.False(mh.PublishIfChanged(new Dictionary<int, int> { [7] = 12 }), "unchanged table not resent"); w.Net.Step(300);
                T.Eq(12, mg.Price(7)); T.Eq(1, pricesSeen);
                g[0].Node.SendToHost(new PlortSold { RequestId = Guid.NewGuid(), ItemId = 7, Count = 3, Price = 1 }); g[0].Node.SendToHost(new PlortSold { RequestId = Guid.NewGuid(), ItemId = 8, Count = 3, Price = 1 });
                g[0].Node.SendToHost(new PlortSold { RequestId = Guid.NewGuid(), ItemId = 7, Count = 10_001, Price = 1 }); g[0].Node.SendToHost(new PlortSold { RequestId = Guid.NewGuid(), ItemId = 7, Count = 0, Price = 1 }); w.Net.Step(400);
                T.Eq(1, sold.Count, "only the valid sale registers"); T.Eq((7, 3), sold[0]); T.Eq(36, ch.Get(1));
                var late = Late(w); var ml = new MarketSystem(late, new CurrencyLedger(late)); JoinLate(w, late); T.Eq(12, ml.Price(7));
            });
            T.Test("messages/restructured owned-manager messages round-trip (prisma, stabilising field, sanctuary, progression, shop request, upgrade kind)", () =>
            {
                var sale = RoundTrip(new PlortSold { RequestId = Guid.Parse("32a467c6-b4a3-4df5-95c8-e839eb54f4d5"), ItemId = 7, Count = 2, Price = 13 });
                T.Eq(Guid.Parse("32a467c6-b4a3-4df5-95c8-e839eb54f4d5"), sale.RequestId); T.Eq(7, sale.ItemId);
                var p = RoundTrip(new PrismaDisruptionState { ForecastedTime = 3, AreaRefreshNeeded = true, Areas = { new PrismaDisruptionState.Area { ReferenceId = "a1", LastCompletionTime = 1, NextChangeTime = 2, ForecastUnlocked = true,
                    Forecast = { new PrismaDisruptionState.Pattern { EndTime = 9, Entries = { new PrismaDisruptionState.Entry { StartTime = 4, Level = 2 } } } } } } });
                T.Eq("a1", p.Areas[0].ReferenceId); T.True(p.AreaRefreshNeeded && p.Areas[0].ForecastUnlocked); T.Eq(2, p.Areas[0].Forecast[0].Entries[0].Level); T.Eq(9.0, p.Areas[0].Forecast[0].EndTime);
                var st = RoundTrip(new StabilizingSpawnerState { IsSnapshot = true, Spawners = { new StabilizingSpawnerState.Spawner { Id = "s", NextTriggerTime = 5, DespawnAtWorldTime = 6, HasSpawned = true } } });
                T.True(st.IsSnapshot && st.Spawners[0].HasSpawned); T.Eq(6.0, st.Spawners[0].DespawnAtWorldTime);
                var sa = RoundTrip(new InitialSanctuary { Rooms = { new SanctuaryRoomRecord { Id = "room", ThemeReferenceId = "theme", PreplacedGadgets = { new SanctuaryRoomRecord.PreplacedGadget { Id = "slot", ActorId = 99 } } } }, OwnedThemeReferenceIds = { "theme" } });
                T.Eq(99L, sa.Rooms[0].PreplacedGadgets[0].ActorId); T.Eq("theme", sa.OwnedThemeReferenceIds[0]);
                var rs = RoundTrip(new SanctuaryRoomState { State = new SanctuaryRoomRecord { Id = "room", ThemeReferenceId = null } }); T.Eq("room", rs.State.Id);
                var th = RoundTrip(new SanctuaryTheme { ThemeReferenceId = "t", IsOwned = true }); T.True(th.IsOwned);
                var pr = RoundTrip(new ProgressionUnlocks { BlueprintIds = { 4 }, Appearances = { new ProgressionUnlocks.AppearanceUnlock { SlimeId = 3, SaveSet = 2 } }, OwnedPalettes = { "pal" } });
                T.Eq(4, pr.BlueprintIds[0]); T.Eq(2, pr.Appearances[0].SaveSet); T.Eq("pal", pr.OwnedPalettes[0]);
                var sr = RoundTrip(new ShopPurchaseRequest { RequestId = Guid.NewGuid(), ShopAssetTag = "shop", ItemAssetGuid = "item", Count = 2 }); T.Eq("item", sr.ItemAssetGuid);
                var up = RoundTrip(new PlayerUpgrade { UpgradeId = 5, Level = -1, Kind = PlayerUpgradeKind.Request, RequestId = Guid.NewGuid() }); T.Eq(PlayerUpgradeKind.Request, up.Kind); T.Eq(-1, up.Level);
                T.True(Lycoreia.Owned.ResourceNodeSyncPolicy.VerifyRules());
            });
            SharedUpgradeTest();
            T.Test("upgrades/request kind goes to the purchase executor; rejections reach the requester only; a pending snapshot entry is held by the ordering rule", () =>
            {
                var (w, h, g) = Rig(1); var snap = new SnapshotHost(h); var wh = new FakeUpgrades(); var wg = new FakeUpgrades();
                var uh = new UpgradeSync(h, wh, snap) { AllIds = () => new[] { 5 } }; var ug = new UpgradeSync(g[0], wg, null);
                int executed = 0; uh.PurchaseHandler = (c, m) => { executed++; h.Node.SendTo(c.Sender, new PlayerUpgrade { UpgradeId = m.UpgradeId, RequestId = m.RequestId, Kind = PlayerUpgradeKind.Rejected }); return true; };
                PlayerUpgrade rejected = null; ug.OnRejected = m => rejected = m;
                var req = Guid.NewGuid(); g[0].Node.SendToHost(new PlayerUpgrade { UpgradeId = 5, Level = 0, RequestId = req, Kind = PlayerUpgradeKind.Request }); w.Net.Step(300);
                T.Eq(1, executed); T.True(rejected != null && rejected.RequestId == req);
                wh.Levels[5] = 2; uh.HostBroadcast(5, 2, req); w.Net.Step(300); T.Eq(2, wg.Levels[5]); T.Eq(1UL, ug.RevisionOf(5));
                uh.SuppressHostLocal = () => true; uh.HostLocalChanged(5); w.Net.Step(200); T.Eq(1UL, uh.RevisionOf(5), "suppressed while a host-run purchase emits its own revision");
                ug.AcceptState = m => false; uh.HostBroadcast(5, 3); w.Net.Step(300); T.Eq(2, wg.Levels[5], "the ordering rule can hold a state");
            });
        }

        static void SharedUpgradeTest()
        {
            T.Test("upgrades/shared: a host-confirmed level reaches every peer including the buyer; a repeated confirm is idempotent; applying never re-broadcasts", () =>
            {
                var (w, h, g) = Rig(2); var wh = new FakeUpgrades(); var w0 = new FakeUpgrades(); var w1 = new FakeUpgrades();
                var uh = new UpgradeSync(h, wh, null); var u0 = new UpgradeSync(g[0], w0, null); var u1 = new UpgradeSync(g[1], w1, null);
                var req = Guid.NewGuid(); wh.Levels[5] = 1; uh.HostBroadcast(5, 1, req); w.Net.Step(300);
                T.Eq(1, w0.Levels[5]); T.Eq(1, w1.Levels[5]);
                var rev = uh.RevisionOf(5); uh.HostBroadcast(5, 1, req); w.Net.Step(300);
                T.Eq(1, w0.Levels[5], "a second confirm of the same level leaves it unchanged"); T.Eq(1, w1.Levels[5]);
                T.Eq(rev + 1, uh.RevisionOf(5));
                var before = uh.RevisionOf(5); w.Net.Step(300); T.Eq(before, uh.RevisionOf(5), "guest applies do not echo back to the host");
            });
        }

        sealed class FakeUpgrades : IUpgradeWorld
        {
            public readonly Dictionary<int, int> Levels = new Dictionary<int, int>();
            public int Level(int id) => Levels.TryGetValue(id, out var l) ? l : -1; public int PriceFor(int id, int level) => 1; public int Currency => 0; public bool TryCharge(int a) => false; public void SetLevel(int id, int level) => Levels[id] = level;
        }
    }
}

// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using Lycoreia.Domain;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.World;

namespace Lycoreia.Tests
{
    static class StageBTests
    {
        static (World w, Session h, List<Session> g) Rig(int guests)
        {
            var w = new World(); var h = w.StartHost(); var list = new List<Session>();
            for (int i = 0; i < guests; i++) { var g = w.AddGuest("G" + i); w.Join(g); T.True(w.Net.RunUntil(() => g.Joined, 4000)); list.Add(g); }
            w.Net.Step(300); return (w, h, list);
        }

        public static void Register()
        {
            T.Test("requests/same GUID is bound to its first sender and payload until expiry", () =>
            {
                long now = 100; var ledger = new RequestLedger<bool>(4, 1000, () => now); var id = Guid.NewGuid();
                long payload = RequestLedger<int>.Hash("purchase", 1);
                ledger.Remember("player-a", id, payload, true);
                T.Eq(LedgerLookup.Replay, ledger.TryGet("player-a", id, payload, out var replayed)); T.True(replayed);
                T.Eq(LedgerLookup.Conflict, ledger.TryGet("player-b", id, payload, out _), "same request id from another sender conflicts");
                T.Eq(LedgerLookup.Conflict, ledger.TryGet("player-a", id, payload + 1, out _), "same sender with different payload conflicts");
                now = 1200; T.Eq(LedgerLookup.Unknown, ledger.TryGet("player-b", id, payload, out _), "expired decision is unknown");
                ledger.Remember("player-b", id, payload, false);
                T.Eq(LedgerLookup.Replay, ledger.TryGet("player-b", id, payload, out var expiredReplay)); T.False(expiredReplay);
            });
            T.Test("inventory/recent records only positive gadget acquisitions", () =>
            {
                T.True(InventoryRecentPolicy.ShouldRecordGadgetObtained(true, 2, 3));
                T.False(InventoryRecentPolicy.ShouldRecordGadgetObtained(true, 3, 2), "spending is not an acquisition");
                T.False(InventoryRecentPolicy.ShouldRecordGadgetObtained(true, 3, 3), "unchanged counts are not new items");
                T.False(InventoryRecentPolicy.ShouldRecordGadgetObtained(false, 0, 1), "ordinary resource counts do not enter the gadget menu");
            });
            T.Test("currency/host-local change publishes once with a revision; guests apply it; equal amounts are not resent", () =>
            {
                var (w, h, g) = Rig(1); var ch = new CurrencyLedger(h); var cg = new CurrencyLedger(g[0]); int applied = 0; cg.OnBalanceChanged = (t, a) => applied++;
                ch.Seed(3, 50); ch.HostLocalChanged(3, 75); w.Net.Step(300);
                T.Eq(75, cg.Get(3)); T.Eq(1, applied); ch.HostLocalChanged(3, 75); w.Net.Step(300); T.Eq(1, applied, "unchanged amount not resent");
                cg.HostLocalChanged(3, 1); T.Eq(75, cg.Get(3), "guests cannot publish");
                g[0].Node.SendToHost(new CurrencyAdjust { CurrencyType = 3, RequestId = Guid.NewGuid(), Delta = -100 }); w.Net.Step(300); T.Eq(75, ch.Get(3), "overspend refused");
            });
            T.Test("market/sale pays in the configured sale currency", () =>
            {
                var (w, h, g) = Rig(1); var ch = new CurrencyLedger(h); var mh = new MarketSystem(h, ch) { SaleCurrency = 9 }; mh.SetPrices(new Dictionary<int, int> { [7] = 10 }); w.Net.Step(200);
                g[0].Node.SendToHost(new PlortSold { RequestId = Guid.NewGuid(), ItemId = 7, Count = 3, Price = 1 }); w.Net.Step(300); T.Eq(30, ch.Get(9)); T.Eq(0, ch.Get(0));
            });
            T.Test("market/sale without a cached host table reads the host price, never the guest claim", () =>
            {
                var (w, h, g) = Rig(1); var ch = new CurrencyLedger(h); var mh = new MarketSystem(h, ch) { SaleCurrency = 9, CurrentHostPrice = _ => 7 };
                g[0].Node.SendToHost(new PlortSold { RequestId = Guid.NewGuid(), ItemId = 7, Count = 3, Price = 100 }); w.Net.Step(300);
                T.Eq(21, ch.Get(9));
            });
            T.Test("market/sale uses live host price and a request retry pays exactly once", () =>
            {
                var (w, h, g) = Rig(2); var ch = new CurrencyLedger(h); int registerCalls = 0, registeredPrice = 0;
                var mh = new MarketSystem(h, ch) { SaleCurrency = 9, CurrentHostPrice = _ => 7, OnSold = (id, count, price) => { registerCalls++; registeredPrice = price; } };
                mh.SetPrices(new Dictionary<int, int> { [7] = 10 }); w.Net.Step(200); var requestId = Guid.NewGuid();
                var sale = new PlortSold { RequestId = requestId, ItemId = 7, Count = 3, Price = 100 };
                g[0].Node.SendToHost(sale); g[0].Node.SendToHost(new PlortSold { RequestId = requestId, ItemId = 7, Count = 3, Price = 100 });
                g[1].Node.SendToHost(new PlortSold { RequestId = requestId, ItemId = 7, Count = 3, Price = 100 }); w.Net.Step(300);
                T.Eq(21, ch.Get(9)); T.Eq(1, registerCalls); T.Eq(7, registeredPrice, "native saturation uses the host's live unit price");
                g[0].Node.SendToHost(new PlortSold { RequestId = requestId, ItemId = 7, Count = 4, Price = 100 }); w.Net.Step(300);
                T.Eq(21, ch.Get(9)); T.Eq(1, registerCalls, "conflicting request-id reuse cannot pay again");
            });
            T.Test("gordo/host-local feeds and one burst reach guests through the state callback", () =>
            {
                var (w, h, g) = Rig(1); var gh = new GordoSync(h); var gg = new GordoSync(g[0]); var seen = new List<(int eaten, bool popped)>(); gg.OnState = (id, st) => seen.Add((st.Eaten, st.Popped));
                gh.HostLocal("g1", 3, 10, 5, false); w.Net.Step(300); T.Eq(3, gg.Gordos["g1"].Eaten);
                gh.HostLocal("g1", 10, 10, 5, true); gh.HostLocal("g1", 10, 10, 5, true); w.Net.Step(300);
                T.True(gg.Gordos["g1"].Popped); T.Eq(1, seen.Count(s => s.popped), "burst once");
                gg.HostLocal("g2", 1, 1, 1, true); T.False(gh.Gordos.ContainsKey("g2"), "guests cannot publish host-local");
            });
            T.Test("gordo/guest feed counts once and the host applies through the callback too", () =>
            {
                var (w, h, g) = Rig(1); var gh = new GordoSync(h); var gg = new GordoSync(g[0]); int hostStates = 0; gh.OnState = (id, st) => hostStates++;
                gh.Seed("g", 5, 1);
                var m = new GordoFeed { Id = "g", RequestId = "r1", FoodActorId = 4, NewFoodCount = 1, RequiredFoodCount = 5, GordoType = 1 };
                g[0].Node.SendToHost(m); g[0].Node.SendToHost(new GordoFeed { Id = "g", RequestId = "r1", FoodActorId = 4, NewFoodCount = 1, RequiredFoodCount = 5, GordoType = 1 }); w.Net.Step(300);
                T.Eq(1, gh.Gordos["g"].Eaten, "the guest's absolute count is only a hint"); T.Eq(1, hostStates); T.Eq(1, gg.Gordos["g"].Eaten);
            });
            T.Test("pending-cache/retry keeps the original age, removes applied and expired entries", () =>
            {
                long now = 0; var p = new PendingCache<string, int>(16, 1000, () => now);
                p.Put("a", 1); p.Put("b", 2);
                now = 600; T.Eq(1, p.Retry((k, v) => k == "b"), "b applied"); T.True(p.Contains("a")); T.False(p.Contains("b"));
                now = 900; T.Eq(0, p.Retry((k, v) => false)); T.True(p.Contains("a"), "not applied, still live");
                now = 1100; T.Eq(0, p.Retry((k, v) => true), "expired entries are never applied"); T.False(p.Contains("a"), "a retry does not refresh the TTL");
            });
            T.Test("pending-cache/contains", () => { var p = new PendingCache<string, int>(); T.False(p.Contains("a")); p.Put("a", 1); T.True(p.Contains("a")); p.TryTake("a", out _); T.False(p.Contains("a")); });
        }
    }
}

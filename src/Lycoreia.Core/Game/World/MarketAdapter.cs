// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.Economy;
using Lycoreia.Net;
using Lycoreia.World;
using UnityEngine;

namespace Lycoreia.Game.World
{
    /// <summary>
    /// Host-authoritative plort prices: the host reads its current prices at most every 0.5 s (unscaled) and publishes the table when it
    /// changed; guests write the received prices into the economy director. A guest's accepted sale is registered natively on the host
    /// (saturation follows) under the applying flag. Not synced: previous-day values (price change arrows may differ on guests).
    /// </summary>
    public static class MarketAdapter
    {
        public const double PublishEvery = 0.5; static double _next; static readonly RateLimitedLog Rl = new RateLimitedLog(null, 10_000);
        static PlortEconomyDirector Director { get { try { return SceneContext.Instance != null ? SceneContext.Instance.PlortEconomyDirector : null; } catch { return null; } } }

        public static void Install(MarketSystem market)
        {
            market.IsKnownItem = id => ActorTypes.TryGetType(id, out _);
            market.CurrentHostPrice = id =>
            {
                try
                {
                    var d = Director;
                    return d != null && ActorTypes.TryGetType(id, out var t) && d.TryGetMarketValues(t, out int current, out _) ? current : -1;
                }
                catch { return -1; }
            };
            market.OnSold = (id, count, unitPrice) =>
            {
                try { var d = Director; if (d != null && ActorTypes.TryGetType(id, out var t)) using (Applying.Enter()) d.RegisterSold(t, count, unitPrice); }
                catch (Exception e) { Rl.Warn("market-sold", "native sale register failed: " + e.Message); }
            };
            market.OnPrices = prices =>
            {
                try
                {
                    var d = Director; var map = d?._currValueMap; if (map == null) return;
                    foreach (var kv in map) { if (kv.Key == null || kv.Value == null) continue; int id = ActorTypes.IdOf(kv.Key); if (id != 0 && prices.TryGetValue(id, out var p)) kv.Value.CurrValue = p; }
                    d.DidUpdateDelegate?.Invoke();
                }
                catch (Exception e) { Rl.Warn("market-apply", "price apply failed: " + e.Message); }
            };
        }

        public static void Pump(MarketSystem market, double now)
        {
            if (!Mod.IsHost || now < _next) return; _next = now + PublishEvery;
            var d = Director; var map = d?._currValueMap; if (map == null) return;
            var prices = new Dictionary<int, int>();
            foreach (var kv in map) { if (kv.Key == null) continue; int id = ActorTypes.IdOf(kv.Key); if (id != 0 && d.TryGetMarketValues(kv.Key, out int cur, out _)) prices[id] = cur; }
            if (prices.Count > 0) market.PublishIfChanged(prices);
        }
    }
}

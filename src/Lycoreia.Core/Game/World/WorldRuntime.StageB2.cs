// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using Lycoreia.Net;
using Lycoreia.Snapshot;

namespace Lycoreia.Game.World
{
    public static partial class WorldRuntime
    {
        static void InitStageB2(Session s, SnapshotHost snap)
        {
            DroneAdapter.Install(s, snap); GardenAdapter.Install(s, snap); LinkAdapter.Install(s, snap); MarketAdapter.Install(Market);
            ResourceNodeAdapter.Install(s, snap); GadgetAdapter.Install(s, snap);
            snap.Register(SnapStep.Prices, ctx => { var m = new Lycoreia.Net.Messages.MarketPrices(); foreach (var kv in _lastPrices()) m.Prices.Add(new Lycoreia.Net.Messages.IntPair { A = kv.Key, B = kv.Value }); if (m.Prices.Count > 0) ctx.Send(m); });
        }
        static long _censusMinute;
        static IEnumerable<KeyValuePair<int, int>> _lastPrices() => Market.Prices;
        static void PumpStageB2(double now)
        {
            long started;
            started = LagProbe.Start();
            try { DroneAdapter.Instance?.Pump(now); } catch (Exception e) { Rl.Warn("pump-drones", "drones pump failed: " + e.Message); }
            finally { LagProbe.End("world-drones", started); }
            started = LagProbe.Start();
            try { MarketAdapter.Pump(Market, now); } catch (Exception e) { Rl.Warn("pump-market", "market pump failed: " + e.Message); }
            finally { LagProbe.End("world-market", started); }
            started = LagProbe.Start();
            try { WeatherAdapter.Pump(Clock, now); } catch (Exception e) { Rl.Warn("pump-weather", "weather pump failed: " + e.Message); }
            finally { LagProbe.End("world-weather", started); }
            started = LagProbe.Start();
            try { LinkAdapter.Instance?.Pump(now); } catch (Exception e) { Rl.Warn("pump-links", "links pump failed: " + e.Message); }
            finally { LagProbe.End("world-links", started); }
            started = LagProbe.Start();
            try { GardenAdapter.Instance?.Pump(now); } catch (Exception e) { Rl.Warn("pump-gardens", "gardens pump failed: " + e.Message); }
            finally { LagProbe.End("world-gardens", started); }
            started = LagProbe.Start();
            try { ResourceNodeAdapter.Pump(); } catch (Exception e) { Rl.Warn("pump-nodes", "nodes pump failed: " + e.Message); }
            finally { LagProbe.End("world-nodes", started); }
            started = LagProbe.Start();
            try { GadgetAdapter.Pump(); } catch (Exception e) { Rl.Warn("pump-gadgets", "gadgets pump failed: " + e.Message); }
            finally { LagProbe.End("world-gadgets", started); }
            // per-seat node and gadget censuses on shared wall-clock minute marks (compare host and guest lines of the same minute)
            long minute = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute;
            if (minute != _censusMinute) { _censusMinute = minute; ResourceNodeAdapter.Census("minute"); GadgetAdapter.Census("minute"); }
        }
    }
}

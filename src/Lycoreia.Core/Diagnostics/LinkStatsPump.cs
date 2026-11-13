// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
#if DEEPLOG
using System;
using System.Collections.Generic;
using Lycoreia.Net;
using UnityEngine;

namespace Lycoreia.Game
{
    /// <summary>
    /// Per-peer link counters as deltas: every 10 s into diagnostics.jsonl ("net"/"link-stats"), every 30 s into latest.log.
    /// Player reports from filtered/VPN paths had no loss figures at all; these give loss (resends per datagram), give-ups,
    /// skipped ordered gaps, duplicates and silence per peer. No endpoints are written (player id only).
    /// </summary>
    sealed class LinkStatsPump : IFramePump
    {
        public string Name => "link-stats";
        double _nextRecord, _nextLog;
        readonly Dictionary<Peer, long[]> _last = new Dictionary<Peer, long[]>();

        public void Pump()
        {
            var node = Mod.Node; if (node == null || !Mod.IsNetworkActive) { _last.Clear(); return; }
            double now = Time.unscaledTimeAsDouble; if (now < _nextRecord) return;
            _nextRecord = now + 10; bool toLog = now >= _nextLog; if (toLog) _nextLog = now + 30;
            long clock = Environment.TickCount64; var seen = new HashSet<Peer>();
            foreach (var p in node.Peers)
            {
                var l = p.Link; if (l == null) continue; seen.Add(p);
                long[] cur = { l.DatagramsSent, l.DatagramsReceived, l.Resent, l.GaveUp, l.Skipped, l.Duplicates, l.BadChecksum, l.Rejected };
                _last.TryGetValue(p, out var prev); prev ??= new long[cur.Length]; _last[p] = cur;
                long sent = cur[0] - prev[0], recv = cur[1] - prev[1], resent = cur[2] - prev[2];
                string who = p.PlayerId ?? (p.IsHost ? "host" : "unregistered");
                string line = $"peer={who}; window=10s; sent={sent}; recv={recv}; resent={resent}; resend-pct={(sent > 0 ? resent * 100 / sent : 0)}; gaveup={cur[3] - prev[3]}; skipped={cur[4] - prev[4]}; dup={cur[5] - prev[5]}; badsum={cur[6] - prev[6]}; rejected={cur[7] - prev[7]}; pending={l.PendingReliable}; partial-bytes={l.PartialBytes}; silent-ms={clock - p.LastHeardMs}; totals-gaveup={cur[3]}; totals-skipped={cur[4]}";
                Mod.Diagnostics?.Record("net", "link-stats", line);
                if (toLog) Log.Info("[net] " + line);
            }
            if (_last.Count > seen.Count) foreach (var k in new List<Peer>(_last.Keys)) if (!seen.Contains(k)) _last.Remove(k);
        }
    }
}
#endif

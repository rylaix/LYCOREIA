// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Lycoreia.Net;

namespace Lycoreia
{
    /// <summary>Learns the address and port the internet sees for one of our UDP sockets (needed for hole punching behind CGNAT).</summary>
    public static class Stun
    {
        public const uint Cookie = 0x2112A442;
        public static readonly string[] DefaultServers = { "stun.l.google.com:19302", "stun1.l.google.com:19302", "stun.cloudflare.com:3478" };

        public static byte[] BindingRequest(byte[] txn)
        {
            var b = new byte[20]; b[0] = 0; b[1] = 1; // type 0x0001, length 0
            b[4] = 0x21; b[5] = 0x12; b[6] = 0xA4; b[7] = 0x42; Buffer.BlockCopy(txn, 0, b, 8, 12); return b;
        }

        /// <summary>True for anything shaped like a STUN message (first two bits 0, magic cookie).</summary>
        public static bool LooksLikeStun(byte[] b, int off, int len) =>
            len >= 20 && (b[off] & 0xC0) == 0 && b[off + 4] == 0x21 && b[off + 5] == 0x12 && b[off + 6] == 0xA4 && b[off + 7] == 0x42;

        public static bool TryTxn(byte[] b, int off, int len, out string txn)
        {
            txn = null; if (!LooksLikeStun(b, off, len)) return false;
            txn = Convert.ToBase64String(b, off + 8, 12); return true;
        }

        /// <summary>Parses a binding success response: XOR-MAPPED-ADDRESS, else MAPPED-ADDRESS (IPv4 and IPv6).</summary>
        public static bool TryParseResponse(byte[] b, int off, int len, out IPEndPoint mapped)
        {
            mapped = null;
            if (!LooksLikeStun(b, off, len) || b[off] != 0x01 || b[off + 1] != 0x01) return false;
            int msgLen = (b[off + 2] << 8) | b[off + 3]; int end = Math.Min(len, 20 + msgLen); int p = 20;
            IPEndPoint plain = null;
            while (p + 4 <= end)
            {
                int type = (b[off + p] << 8) | b[off + p + 1], alen = (b[off + p + 2] << 8) | b[off + p + 3]; int v = p + 4;
                if (v + alen > end) break;
                if ((type == 0x0020 || type == 0x0001) && alen >= 8)
                {
                    int fam = b[off + v + 1]; int port = (b[off + v + 2] << 8) | b[off + v + 3];
                    int alen2 = fam == 1 ? 4 : fam == 2 ? 16 : 0;
                    if (alen2 > 0 && alen >= 4 + alen2)
                    {
                        var ip = new byte[alen2]; Buffer.BlockCopy(b, off + v + 4, ip, 0, alen2);
                        if (type == 0x0020)
                        {
                            port ^= (int)(Cookie >> 16);
                            for (int i = 0; i < alen2; i++) ip[i] ^= i < 4 ? (byte)(Cookie >> (24 - 8 * i)) : b[off + 8 + (i - 4)];
                            mapped = new IPEndPoint(new IPAddress(ip), port); return true;
                        }
                        plain = new IPEndPoint(new IPAddress(ip), port);
                    }
                }
                p = v + ((alen + 3) & ~3);
            }
            mapped = plain; return mapped != null;
        }

        /// <summary>
        /// Asks the servers in turn (each up to 2 tries) through the given socket; returns the first mapped IPv4 endpoint, or null.
        /// Replies are taken out of the socket's receive path by txn id, so the game protocol never sees them.
        /// </summary>
        public static async Task<IPEndPoint> QueryAsync(UdpTransport t, IEnumerable<string> servers = null, int perTryMs = 1200, CancellationToken ct = default)
        {
            foreach (var s in servers ?? DefaultServers)
            {
                IPEndPoint server;
                try
                {
                    int c = s.LastIndexOf(':'); string host = s.Substring(0, c); int port = int.Parse(s.Substring(c + 1));
                    var addrs = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
                    var a = Array.Find(addrs, x => x.AddressFamily == AddressFamily.InterNetwork); if (a == null) continue;
                    server = new IPEndPoint(a, port);
                }
                catch (Exception e) { Log.Debug("stun: resolve " + s + " failed: " + e.Message); continue; }
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    var txn = new byte[12]; System.Random.Shared.NextBytes(txn); string key = Convert.ToBase64String(txn);
                    var tcs = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
                    t.Pending[key] = tcs;
                    try
                    {
                        var req = BindingRequest(txn); t.Send(server, req, 0, req.Length);
                        var done = await Task.WhenAny(tcs.Task, Task.Delay(perTryMs, ct)).ConfigureAwait(false);
                        if (done == tcs.Task && tcs.Task.Result != null && tcs.Task.Result.AddressFamily == AddressFamily.InterNetwork) return tcs.Task.Result;
                    }
                    finally { t.Pending.TryRemove(key, out _); }
                }
            }
            return null;
        }
    }
}

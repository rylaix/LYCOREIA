// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Lycoreia.Net;

namespace Lycoreia.Tests
{
    sealed class FakeSteamTransport : IDatagramTransport
    {
        public event DatagramHandler Received;
        public event Action<System.Net.Sockets.SocketError, string> Faulted;
        public readonly List<EndPoint> Sent = new List<EndPoint>(); public bool Started, Disposed;
        public void Send(EndPoint to, byte[] b, int o, int l) => Sent.Add(to);
        public void Start() => Started = true;
        public void Dispose() => Disposed = true;
        public void Inject(EndPoint from, byte[] d) => Received?.Invoke(from, d, 0, d.Length);
    }

    static class ConnectivityTests
    {
        static IPAddress A(string s) => IPAddress.Parse(s);
        static IPEndPoint E(string s, int p) => new IPEndPoint(A(s), p);

        public static void Register()
        {
            T.Test("joincode/v3 round trip with every part and with single parts", () =>
            {
                var all = new JoinInfo { Public = E("198.51.100.7", 50123), Lan = E("192.168.0.110", 59373), SteamId = 76561198000000001UL, LetIn = true };
                T.True(JoinCode.TryDecode(JoinCode.Encode(all), out var j));
                T.Eq("198.51.100.7:50123", j.Public.ToString()); T.Eq("192.168.0.110:59373", j.Lan.ToString()); T.Eq(76561198000000001UL, j.SteamId); T.True(j.LetIn);
                T.True(JoinCode.TryDecode(JoinCode.Encode(new JoinInfo { SteamId = 42 }).ToLowerInvariant(), out var s)); T.Eq(42UL, s.SteamId); T.True(s.Public == null && s.Lan == null && !s.LetIn);
                T.True(JoinCode.TryDecode(JoinCode.Encode(new JoinInfo { Public = E("2001:db8::5", 1919) }), out var v6)); T.Eq("[2001:db8::5]:1919", v6.Public.ToString());
                T.True(!JoinCode.TryDecode(JoinCode.Encode(new JoinInfo()), out _), "empty code is invalid");
                T.True(!JoinCode.TryDecode("", out _) && !JoinCode.TryDecode("garbage!", out _) && !JoinCode.TryDecode("ZZZZZ", out _));
                // a public "LAN" address is never trusted
                T.True(JoinCode.TryDecode(JoinCode.Encode(new JoinInfo { SteamId = 1, Lan = E("203.0.113.9", 1) }), out var bad)); T.True(bad.Lan == null);
                // Crockford misreadings
                var code = JoinCode.Encode(new JoinInfo { SteamId = 7 }); T.True(JoinCode.TryDecode(code.Replace('0', 'O').Replace('1', 'I'), out var m) && m.SteamId == 7);
            });
            T.Test("joincode/legacy codes (up to rc4.2) still decode", () =>
            {
                T.True(JoinCode.TryDecode(AutoHost.EncodeJoinCode(A("198.51.100.7"), 50000), out var j)); T.Eq("198.51.100.7:50000", j.Public.ToString()); T.True(j.Lan == null && j.SteamId == 0);
                T.True(JoinCode.TryDecode(AutoHost.EncodeJoinCode(A("198.51.100.7"), 50000, A("10.0.0.9")), out var l)); T.Eq("10.0.0.9:50000", l.Lan.ToString());
            });
            T.Test("joincode/let-in codes carry a public IPv4 endpoint only", () =>
            {
                var c = JoinCode.EncodeLetIn(E("93.184.216.34", 61001)); T.True(JoinCode.TryDecodeLetIn(c, out var ep)); T.Eq("93.184.216.34:61001", ep.ToString());
                T.True(!JoinCode.TryDecodeLetIn(JoinCode.EncodeLetIn(E("192.168.1.2", 5)), out _), "private address refused");
                T.True(!JoinCode.TryDecodeLetIn(JoinCode.Encode(new JoinInfo { SteamId = 5 }), out _), "a join code is not a let-in code");
                T.True(!JoinCode.TryDecode(c, out _), "a let-in code is not a join code");
            });
            T.Test("stun/binding request and XOR-MAPPED-ADDRESS parsing", () =>
            {
                var txn = Enumerable.Range(1, 12).Select(i => (byte)i).ToArray(); var req = Stun.BindingRequest(txn);
                T.Eq(20, req.Length); T.Eq(1, (int)req[1]); T.True(Stun.LooksLikeStun(req, 0, req.Length));
                // response: header (0x0101), XOR-MAPPED-ADDRESS 203.0.113.5:54321
                var resp = new byte[32]; resp[0] = 1; resp[1] = 1; resp[3] = 12; resp[4] = 0x21; resp[5] = 0x12; resp[6] = 0xA4; resp[7] = 0x42; Buffer.BlockCopy(txn, 0, resp, 8, 12);
                resp[20] = 0; resp[21] = 0x20; resp[23] = 8; resp[25] = 1; int xport = 54321 ^ 0x2112; resp[26] = (byte)(xport >> 8); resp[27] = (byte)xport;
                var ip = A("203.0.113.5").GetAddressBytes(); byte[] cookie = { 0x21, 0x12, 0xA4, 0x42 }; for (int i = 0; i < 4; i++) resp[28 + i] = (byte)(ip[i] ^ cookie[i]);
                T.True(Stun.TryParseResponse(resp, 0, resp.Length, out var mapped)); T.Eq("203.0.113.5:54321", mapped.ToString());
                T.True(Stun.TryTxn(resp, 0, resp.Length, out var key) && key == Convert.ToBase64String(txn));
                T.True(!Stun.TryParseResponse(req, 0, req.Length, out _), "a request is not a response");
                var frame = new byte[40]; T.True(!Stun.LooksLikeStun(frame, 0, frame.Length));
            });
            T.Test("hostaddress/router mapping, CGNAT via STUN with let-in, Steam and LAN", () =>
            {
                var nat = new FakeNat { Wan = A("198.51.100.7"), LocalAddress = A("192.168.0.110") };
                var ah = new AutoHost(nat, nat, _ => Task.FromResult("198.51.100.7")); int port = ah.PrepareAsync().GetAwaiter().GetResult();
                var j = HostAddress.Compose(ah, null, A("192.168.0.110"), port, 99);
                T.Eq($"198.51.100.7:{port}", j.Public.ToString()); T.True(!j.LetIn); T.Eq(99UL, j.SteamId); T.Eq($"192.168.0.110:{port}", j.Lan.ToString());
                nat = new FakeNat { Wan = A("172.20.4.4") }; ah = new AutoHost(nat, nat, _ => Task.FromResult("93.1.2.3")); port = ah.PrepareAsync().GetAwaiter().GetResult();
                j = HostAddress.Compose(ah, E("93.1.2.3", 40001), A("192.168.0.110"), port, 0);
                T.Eq("93.1.2.3:40001", j.Public.ToString()); T.True(j.LetIn, "double NAT: the guest must be let in");
                j = HostAddress.Compose(null, E("93.1.2.3", 40001), null, 40001, 0); T.True(!j.LetIn, "same port and no router: assume a forwarded port");
                j = HostAddress.Compose(null, E("93.1.2.3", 40002), null, 40001, 0); T.True(j.LetIn, "port rewritten by the NAT");
                j = HostAddress.Compose(null, null, null, 40001, 5); T.True(j.Public == null && j.SteamId == 5 && !j.IsEmpty, "Steam only");
                T.True(HostAddress.Compose(null, null, null, 1, 0).IsEmpty);
            });
            T.Test("composite/routes Steam endpoints to Steam and IP endpoints to UDP, merges receives", () =>
            {
                var net = new SimNet(); var udp = net.Open(1919); var steam = new FakeSteamTransport(); var c = new CompositeTransport(udp, steam);
                var got = new List<EndPoint>(); c.Received += (f, b, o, l) => got.Add(f); c.Start(); T.True(steam.Started);
                c.Send(new SteamEndPoint(77), new byte[] { 1 }, 0, 1); T.Eq(1, steam.Sent.Count);
                var other = net.Open(2000); c.Send(new IPEndPoint(IPAddress.Loopback, 2000), new byte[] { 2 }, 0, 1); T.Eq(1, steam.Sent.Count, "IP send not on Steam");
                steam.Inject(new SteamEndPoint(77), new byte[] { 3 }); T.True(got.Single().Equals(new SteamEndPoint(77)));
                c.Dispose(); T.True(steam.Disposed && udp.Disposed);
                T.True(new SteamEndPoint(76561198000001234UL).ToString().EndsWith("1234") && !new SteamEndPoint(76561198000001234UL).ToString().Contains("76561198"), "Steam ids masked in logs");
            });
            T.Test("session/a guest behind a host NAT gets in once the host lets it in (hole punching)", () =>
            {
                var w = new World(); w.StartHost(); var g = w.AddGuest(); g.DevMode = true;
                var hostEp = new IPEndPoint(IPAddress.Loopback, 1919); var guestEp = new IPEndPoint(IPAddress.Loopback, 7000); bool punched = false;
                w.Net.Blackhole = (from, to) => { if (from.Equals(hostEp) && to.Equals(guestEp)) punched = true; return from.Equals(guestEp) && to.Equals(hostEp) && !punched; };
                g.OpenFor = _ => w.Net.Open(7000);
                string code = null; g.LetInCodeReady += c => code = c; g.DiscoverMapped = _ => Task.FromResult(new IPEndPoint(A("203.0.113.9"), 7000));
                T.True(g.Connect(new[] { new Session.ConnectTarget(hostEp, Session.AckWithinMs, punch: true) }) == null);
                T.True(w.Net.RunUntil(() => code != null, 2000), "let-in code produced"); T.True(JoinCode.TryDecodeLetIn(code, out var lp) && lp.Port == 7000);
                w.Net.Step(40_000); T.True(g.Connecting && g.PunchWaiting && !g.Joined, "still waiting after 40 s (no 10 s timeout)");
                T.True(w.Host.LetIn(guestEp) == null);
                T.True(w.Net.RunUntil(() => g.Joined, 5000), "joined after the host punched"); T.Eq(1, w.Host.Node.GuestCount);
            });
            T.Test("session/targets fall through LAN -> Steam -> public with their own timeouts", () =>
            {
                var w = new World(); w.StartHost(); var g = w.AddGuest(); g.DevMode = true; int port = 7100; var opened = new List<EndPoint>();
                g.OpenFor = ep => { opened.Add(ep); return w.Net.Open(port++); };
                var steamLike = new IPEndPoint(IPAddress.Loopback, 1778);   // stands in for a Steam target that never answers
                var r = g.Connect(new[] { new Session.ConnectTarget(new IPEndPoint(IPAddress.Loopback, 1777), Session.CandidateAckMs), new Session.ConnectTarget(steamLike, Session.SteamAckMs), new Session.ConnectTarget(new IPEndPoint(IPAddress.Loopback, 1919), Session.AckWithinMs) });
                T.True(r == null, r);
                w.Net.Step(Session.CandidateAckMs + 500); T.Eq(2, opened.Count, "second target after the LAN window");
                w.Net.Step(Session.SteamAckMs - 2000); T.Eq(2, opened.Count, "Steam gets its longer window");
                T.True(w.Net.RunUntil(() => g.Joined, 10_000), "joined via the last target"); T.Eq(3, opened.Count);
            });
        }
    }
}

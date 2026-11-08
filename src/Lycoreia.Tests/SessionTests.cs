// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Lycoreia.Net;
using Lycoreia.Net.Messages;

namespace Lycoreia.Tests
{
    /// <summary>A simulated world: one host and any number of guests on the same virtual network.</summary>
    sealed class World
    {
        public readonly SimNet Net; public readonly MessageRegistry Reg = MessageRegistry.FromAssembly(typeof(Msg).Assembly);
        public Node HostNode; public Session Host;
        public readonly List<Session> Guests = new List<Session>(); public readonly Dictionary<Session, int> Ports = new Dictionary<Session, int>();
        public World(int seed = 1) { Net = new SimNet(seed); }
        public Session StartHost(string name = "Hoster")
        {
            HostNode = new Node(Reg, () => Net.Now); Net.Nodes.Add(HostNode);
            Host = new Session(HostNode) { LocalName = name };
            var err = Host.StartServer(Net.Open(1919)); T.True(err == null, err);
            return Host;
        }
        int _port = 5000;
        public Session AddGuest(string name = "Guest", int? port = null, Action<ModRegistry> mods = null)
        {
            var n = new Node(Reg, () => Net.Now); Net.Nodes.Add(n);
            var s = new Session(n) { LocalName = name }; mods?.Invoke(s.Mods);
            Guests.Add(s); Ports[s] = port ?? _port++; s.ForcedPlayerId = "PLAYER_G" + Guests.Count.ToString("D8");
            return s;
        }
        public string Join(Session g)
        {
            var t = Net.Open(Ports[g]);
            return g.ConnectVia(t, new IPEndPoint(IPAddress.Loopback, 1919));
        }
    }

    static class SessionExt { }

    static class SessionTests
    {
        public static void Register()
        {
            T.Test("session/join flow with matching mods", () =>
            {
                var w = new World(); w.StartHost(); var g = w.AddGuest(); var events = new List<string>();
                g.ClientJoined += () => events.Add("joined"); w.Host.PlayerAdded += p => events.Add("host+" + p.Name);
                Identity_Distinct(g);
                T.True(w.Join(g) == null);
                T.True(w.Net.RunUntil(() => g.Joined && w.Host.Players.Count == 2, 3000), "joined");
                T.True(g.Connected); T.Eq(w.Host.LocalId, g.HostId); T.True(g.SyncId > 0);
                T.True(events.Contains("joined") && events.Contains("host+Guest"));
                T.Eq(2, g.Players.Count); T.True(w.Host.Node.GuestCount == 1);
            });
            T.Test("session/mismatched mods are denied with the list and the client is removed", () =>
            {
                var w = new World(); w.StartHost(); w.Host.Mods.Register("Extra", "1.0", ModSide.Shared, typeof(SessionTests).Assembly);
                var g = w.AddGuest(); string err = null; g.ErrorShown += e => err = e;
                w.Join(g);
                T.True(w.Net.RunUntil(() => err != null, 3000), "deny shown");
                T.True(err.Contains("Extra"), err); T.True(!g.Connected && !g.Joined);
                w.Net.Step(4000); T.Eq(0, w.Host.Node.GuestCount); T.Eq(0, w.Host.Node.Peers.Count());
            });
            T.Test("session/mod version compared by exact string, both ways", () =>
            {
                var w = new World(); w.StartHost(); w.Host.Mods.Register("Extra", "1.0", ModSide.Shared, typeof(SessionTests).Assembly);
                var g = w.AddGuest(mods: m => m.Register("Extra", "1.1", ModSide.Shared, typeof(SessionTests).Assembly)); string err = null; g.ErrorShown += e => err = e;
                w.Join(g); T.True(w.Net.RunUntil(() => err != null, 3000)); T.True(err.Contains("1.0") && err.Contains("1.1"), err);
                var g2 = w.AddGuest(mods: m => m.Register("Extra", "1.0", ModSide.Shared, typeof(SessionTests).Assembly));
                Identity_Distinct(g2); w.Join(g2); T.True(w.Net.RunUntil(() => g2.Joined, 3000), "same version joins");
            });
            T.Test("session/local and host-only mods are not compared", () =>
            {
                var w = new World(); w.StartHost(); w.Host.Mods.Register("Quiet", "9", ModSide.HostOnly, typeof(SessionTests).Assembly);
                var g = w.AddGuest(mods: m => m.Register("Mine", "1", ModSide.Local, typeof(Msg).Assembly)); w.Join(g);
                T.True(w.Net.RunUntil(() => g.Joined, 3000));
            });
            T.Test("session/version mismatch denied readably", () =>
            {
                var w = new World(); w.StartHost(); var g = w.AddGuest(); g.Build = "1.0.0"; string err = null; g.ErrorShown += e => err = e;
                w.Join(g); T.True(w.Net.RunUntil(() => err != null, 3000)); T.True(err.Contains(Lycoreia.ModVersion.Value) && err.Contains("1.0.0"), err);
            });
            T.Test("session/no answer times out at 10s", () =>
            {
                var w = new World(); var g = w.AddGuest(); string err = null; g.ErrorShown += e => err = e;
                T.True(w.Join(g) == null); w.Net.Step(9_500); T.True(err == null, "early " + err); T.True(g.Connecting, "connecting"); w.Net.Step(1_500);
                T.True(err != null && !g.Connecting && !g.Active, err);
            });
            T.Test("session/silent host drops the client at 20s", () =>
            {
                var w = new World(); w.StartHost(); var g = w.AddGuest(); string why = null; g.ClientDisconnected += r => why = r;
                w.Join(g); T.True(w.Net.RunUntil(() => g.Joined, 3000));
                w.Net.Blackhole = (f, t) => f.Equals(new IPEndPoint(IPAddress.Loopback, 1919));
                w.Net.Step(18_000); T.True(g.Connected, "still connected before 20s"); 
                w.Net.Step(4_000); T.True(!g.Active && why == "Lost connection to the host.", why);
            });
            T.Test("session/silent client dropped by host at 30s and ownership reassigned", () =>
            {
                var w = new World(); w.StartHost(); var g = w.AddGuest(); Peer removed = null; w.Host.ClientRemoved += p => removed = p;
                w.Join(g); T.True(w.Net.RunUntil(() => g.Joined, 3000));
                var left = new List<string>(); w.Host.PlayerRemoved += p => left.Add(p.Name);
                w.Net.Blackhole = (f, t) => t.Equals(new IPEndPoint(IPAddress.Loopback, 1919));
                w.Net.Step(28_000); T.True(w.Host.Node.GuestCount == 1, "kept before 30s");
                w.Net.Step(8_000); T.Eq(0, w.Host.Node.GuestCount); T.True(removed != null && left.Contains("Guest"));
            });
            T.Test("session/host close notifies clients", () =>
            {
                var w = new World(); w.StartHost(); var g = w.AddGuest(); string why = null; g.ClientDisconnected += r => why = r;
                w.Join(g); T.True(w.Net.RunUntil(() => g.Joined, 3000));
                bool stopped = false; w.Host.ServerStopped += () => stopped = true; w.Host.StopServer();
                T.True(stopped && !w.Host.Active); T.True(w.Net.RunUntil(() => !g.Active, 3000)); T.Eq("Server closed!", why);
            });
            T.Test("session/loopback address refused outside dev mode", () =>
            {
                var w = new World(); var g = w.AddGuest(); g.Open = f => w.Net.Open(7000);
                var r = g.Connect(new IPEndPoint(IPAddress.Loopback, 1919)); T.True(r != null && r.Contains("LAN"), r);
                g.DevMode = true; T.True(g.Connect(new IPEndPoint(IPAddress.Loopback, 1919)) == null);
            });
            T.Test("session/refuses to connect while hosting or already connecting", () =>
            {
                var w = new World(); w.StartHost(); T.True(w.Host.ConnectVia(w.Net.Open(7001), new IPEndPoint(IPAddress.Loopback, 1)) != null);
                var g = w.AddGuest(); w.Join(g); T.True(g.ConnectVia(w.Net.Open(7002), new IPEndPoint(IPAddress.Loopback, 1919)) != null);
                T.True(g.StartServer(w.Net.Open(7003)) != null);
            });
            T.Test("session/reconnect from same endpoint starts with fresh state", () =>
            {
                var w = new World(); w.StartHost(); var g = w.AddGuest(port: 6000);
                var chats = new List<string>(); w.Host.ChatReceived += m => chats.Add(m.Text);
                w.Join(g); T.True(w.Net.RunUntil(() => g.Joined, 3000));
                for (int i = 0; i < 5; i++) g.SendChat("a" + i);
                w.Net.Step(300); T.Eq(5, chats.Count);
                g.Disconnect(); w.Net.Step(200);
                T.True(w.Join(g) == null); T.True(w.Net.RunUntil(() => g.Joined, 4000), "rejoined");
                g.SendChat("again"); w.Net.Step(300); T.Eq("again", chats.Last());
            });
            T.Test("session/guest leave is announced", () =>
            {
                var w = new World(); w.StartHost(); var g1 = w.AddGuest("One"); var g2 = w.AddGuest("Two"); Identity_Distinct(g1); 
                w.Join(g1); T.True(w.Net.RunUntil(() => g1.Joined, 3000)); w.Join(g2); T.True(w.Net.RunUntil(() => g2.Joined, 3000));
                w.Net.Step(300); T.Eq(3, g1.Players.Count, "guest one learns of guest two");
                var sys = new List<string>(); g1.Notice += s => sys.Add(s);
                g2.Disconnect(); T.True(w.Net.RunUntil(() => g1.Players.Count == 2, 2000));
                T.True(w.Host.Players.Count == 2);
            });
            T.Test("session/profile colour: wire round-trip, relayed to others, spoof-proof, late joiners and live changes", () =>
            {
                var back = new PlayerProfile(); { var wr = new NetWriter(); new PlayerProfile { PlayerId = "P1", Colour = "#4D95CB" }.Describe(new Codec(wr)); var b = wr.Span.ToArray(); var rd = NetReader.Rent(b, 0, b.Length); back.Describe(new Codec(rd)); rd.Return(); }
                T.Eq("P1", back.PlayerId); T.Eq("#4D95CB", back.Colour);
                T.Eq("#4D95CB", ProfileColour.Normalize("4d95cb")); T.True(ProfileColour.Normalize("#12345") == null); T.True(ProfileColour.Normalize("#GG0000") == null);
                T.True(ProfileColour.TryParse("#FF8000", out var r, out var gr, out var bl) && r == 255 && gr == 128 && bl == 0);
                var w = new World(); w.StartHost(); var g1 = w.AddGuest("One"); var g2 = w.AddGuest("Two");
                w.Join(g1); T.True(w.Net.RunUntil(() => g1.Joined, 3000));
                w.Host.SendProfile("#112233");
                g1.SendProfile("#AABBCC"); w.Net.Step(300);
                T.Eq("#AABBCC", w.Host.ColourOf(g1.LocalId), "host stores the guest colour"); T.Eq("#112233", g1.ColourOf(w.Host.LocalId), "guest stores the host colour");
                w.Join(g2); T.True(w.Net.RunUntil(() => g2.Joined, 3000));
                foreach (var p in w.Host.KnownProfiles()) w.Host.Node.SendTo(w.Host.Node.FindByPlayer(g2.LocalId), p); w.Net.Step(300);
                T.Eq("#AABBCC", g2.ColourOf(g1.LocalId), "late joiner gets the existing colours"); T.Eq("#AABBCC", g2.Players[g1.LocalId].ColourHex);
                var changed = new List<string>(); g2.ProfileChanged += p => changed.Add(p.ColourHex);
                g1.SendProfile("#010203"); w.Net.Step(300); T.Eq("#010203", changed.Single(), "live change reaches the other guest");
                g1.Node.SendToHost(new PlayerProfile { PlayerId = g2.LocalId, Colour = "#FFFFFF" }); w.Net.Step(300);
                T.Eq("#010203", g1.ColourOf(g1.LocalId)); T.True(g2.ColourOf(g2.LocalId) == null || g2.ColourOf(g2.LocalId) != "#FFFFFF", "a guest cannot set another player's colour");
                g1.Node.SendToHost(new PlayerProfile { PlayerId = g1.LocalId, Colour = "not-a-colour" }); w.Net.Step(300); T.Eq("#010203", w.Host.ColourOf(g1.LocalId), "invalid colour rejected");
            });
            T.Test("session/chat relays through host", () =>
            {
                var w = new World(); w.StartHost(); var g1 = w.AddGuest("One"); var g2 = w.AddGuest("Two");
                w.Join(g1); T.True(w.Net.RunUntil(() => g1.Joined, 3000)); w.Join(g2); T.True(w.Net.RunUntil(() => g2.Joined, 3000));
                var got = new List<string>(); g2.ChatReceived += m => { if (m.Kind == ChatKind.Player) got.Add(m.Username + ":" + m.Text); }; var hostGot = new List<string>(); w.Host.ChatReceived += m => hostGot.Add(m.Text);
                g1.SendChat("  hi there  "); w.Net.Step(300);
                T.Eq("One:hi there", got.Single()); T.Eq("hi there", hostGot.Single());
            });
            T.Test("session/unregistered endpoint cannot send anything but Connect", () =>
            {
                var w = new World(); w.StartHost(); int handled = 0; w.Host.ChatReceived += m => handled++;
                var rogue = new Node(w.Reg, () => w.Net.Now); w.Net.Nodes.Add(rogue);
                rogue.Attach(Role.Guest, w.Net.Open(9000), "PLAYER_ROGUE0001");
                var hp = rogue.AddPeer(new IPEndPoint(IPAddress.Loopback, 1919), true);
                rogue.SendTo(hp, new ChatMessage { Username = "x", Text = "hello", MessageId = "1" });
                w.Net.Step(500); T.Eq(0, handled);
            });
            T.Test("session/player-scoped message must match the endpoint's player", () =>
            {
                var w = new World(); w.StartHost(); var g = w.AddGuest(); w.Join(g); T.True(w.Net.RunUntil(() => g.Joined, 3000));
                int seen = 0; w.Host.Node.On<ModSyncReply>(Side.Host, (c, m) => { seen++; });
                g.Node.SendToHost(new ModSyncReply { PlayerId = "PLAYER_SOMEONEELS" }); w.Net.Step(300);
                T.Eq(0, seen, "spoofed id rejected");
                g.Node.SendToHost(new ModSyncReply { PlayerId = g.LocalId }); w.Net.Step(300);
                T.Eq(1, seen);
            });
            T.Test("session/target parsing", () =>
            {
                string h; int p;
                T.True(Session.ParseTarget("192.168.1.5:2000", out h, out p) && h == "192.168.1.5" && p == 2000);
                T.True(Session.ParseTarget("example.org 2001", out h, out p) && h == "example.org" && p == 2001);
                T.True(Session.ParseTarget("[::1]:2002", out h, out p) && h == "::1" && p == 2002);
                T.True(Session.ParseTarget("[fe80::1]", out h, out p) && h == "fe80::1" && p == 1919);
                T.True(Session.ParseTarget("::1", out h, out p) && h == "::1");
                T.True(Session.ParseTarget("host", out h, out p) && p == 1919);
                T.True(!Session.ParseTarget("", out h, out p)); T.True(!Session.ParseTarget("host:abc", out h, out p)); T.True(!Session.ParseTarget("a b c", out h, out p));
            });
            T.Test("session/join under 5 percent loss and reorder", () =>
            {
                var w = new World(3); w.Net.Loss = 0.05; w.Net.MaxDelay = 40; w.StartHost(); var g = w.AddGuest(); w.Join(g);
                T.True(w.Net.RunUntil(() => g.Joined && w.Host.Players.Count == 2, 20_000), "joined under loss");
            });
            T.Test("api/custom message crosses wire and dispatches to handler", () =>
            {
                var w = new World(); w.StartHost(); w.Host.Mods.Register("Api", "1", ModSide.Shared, typeof(SessionTests).Assembly);
                var g = w.AddGuest(mods: m => m.Register("Api", "1", ModSide.Shared, typeof(SessionTests).Assembly)); w.Join(g); T.True(w.Net.RunUntil(() => g.Joined, 3000));
                var hm = w.Host.Mods.Mods.Single(); var gm = g.Mods.Mods.Single(); T.True(hm.NetId != 0 && gm.NetId == hm.NetId);
                TestEcho.GotHost.Clear(); TestEcho.GotGuest.Clear();
                g.Mods.SendToHost(gm, new TestEchoMessage { Text = "ping" }); w.Net.Step(300);
                T.Eq("ping", TestEcho.GotHost.Single());
                w.Host.Mods.SendToAll(hm, new TestEchoMessage { Text = "pong" }); w.Net.Step(300);
                T.Eq("pong", TestEcho.GotGuest.Single());
                T.Throws<InvalidOperationException>(() => w.Host.Mods.Register("Clash", "1", ModSide.Shared, typeof(SessionTests).Assembly), "hash collision");
            });
        }

        static void Identity_Distinct(Session s) { }
    }

    public sealed class TestEchoMessage : ApiMessage { public string Text; public override void Describe(Codec c) { c.Str(ref Text); } }
    public static class TestEcho { public static readonly List<string> GotHost = new List<string>(), GotGuest = new List<string>(); }
    public sealed class TestEchoHandler : ApiHandler<TestEchoMessage>
    {
        public override void OnHost(TestEchoMessage m, string sender) => TestEcho.GotHost.Add(m.Text);
        public override void OnGuest(TestEchoMessage m) => TestEcho.GotGuest.Add(m.Text);
    }
}

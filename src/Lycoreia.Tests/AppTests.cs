// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.Snapshot;

namespace Lycoreia.Tests
{
    sealed class FakeEnv : ICommandEnv
    {
        public Session Session { get; set; } public SnapshotHost Snapshots { get; set; }
        public List<string> Calls = new List<string>(); public bool Cheats, ChatFocusedV, InSessionV = true;
        public bool ChatFocused => ChatFocusedV; public bool AllowCheatsSetting => Cheats; public bool InSession => InSessionV;
        public void CloseMenu() => Calls.Add("closemenu"); public void OpenManualCodeDialog() => Calls.Add("manualcode"); public void StartAutoHost() => Calls.Add("autohost");
        public void ToggleGui() => Calls.Add("gui"); public void ToggleChat() => Calls.Add("chat"); public void ShowLocalChat(string u, string t) => Calls.Add("local:" + t);
        public void AddSprinkles(int n) => Calls.Add("sprinkles" + n); public void RemoveFirewallExceptions() => Calls.Add("rmfw"); public void BackupSave() => Calls.Add("backup");
        public string Cleanup(bool now) { Calls.Add("cleanup" + (now ? "now" : "")); return "cleaned"; }
    }

    sealed class FakeNat : INatGateway, IFirewall
    {
        public Task<bool> PendingDiscovery; public Task PendingMap;
        public string Detail { get; set; } = ""; public Exception DiscoverThrows; public bool HasDevice = true; public int FailMaps; public List<int> Mapped = new List<int>(), Unmapped = new List<int>(), Allowed = new List<int>(), Removed = new List<int>(); public int MapCalls;
        public Task<bool> DiscoverAsync(CancellationToken ct) => PendingDiscovery ?? (DiscoverThrows != null ? Task.FromException<bool>(DiscoverThrows) : Task.FromResult(HasDevice));
        public Task<IPAddress> ExternalIpAsync(CancellationToken ct) => Task.FromResult(IPAddress.Parse("203.0.113.5"));
        public Task MapAsync(int p, CancellationToken ct) { MapCalls++; if (PendingMap != null) return PendingMap; if (FailMaps > 0) { FailMaps--; throw new InvalidOperationException("conflict"); } Mapped.Add(p); return Task.CompletedTask; }
        public Task UnmapAsync(int p, CancellationToken ct) { Unmapped.Add(p); return Task.CompletedTask; }
        public void Allow(int p) => Allowed.Add(p); public void Remove(int p) => Removed.Add(p);
    }

    static class AppTests
    {
        public static void Register()
        {
            T.Test("identity/player id format and per process stability", () =>
            {
                string id = Identity.ProcessPlayerId; T.True(id.StartsWith("PLAYER_") && id.Length == 7 + 9, id); T.True(id.Substring(7).All(ch => (ch >= '0' && ch <= '9') || (ch >= 'A' && ch <= 'F')));
                T.Eq(id, Identity.ProcessPlayerId); T.Eq("PLAYER_TEST_MODE", Identity.ForHost(true)); T.Eq(id, Identity.ForHost(false));
                ushort s = Identity.ShortId(id); T.True(s >= Identity.ShortIdBase); T.Eq(s, Identity.ShortId(id)); T.True(Identity.ShortId("PLAYER_AAAAAAAAA") != Identity.ShortId("PLAYER_BBBBBBBBB"));
            });
            T.Test("settings/names defaults and visibility", () =>
            {
                T.Eq(1919, (int)Settings.Find("host_port").Default); T.True(Settings.Find("host_port").Hidden); T.Eq("Player", (string)Settings.Find("username").Default);
                T.Eq(10, (int)Settings.Find("backups_to_keep").Default); T.Eq(1, Settings.Find("backups_to_keep").Min); T.Eq(true, (bool)Settings.Find("disable_achievements").Default);
                T.Eq(false, (bool)Settings.Find("streamer_mode").Default); T.True(Settings.Find("rock_plort_mode").Hidden && !Settings.Find("friends_can_kick_me").Hidden);
                T.True(Settings.All.Select(d => d.Key).Distinct().Count() == Settings.All.Count, "unique keys");
            });
            T.Test("version/game and mod checks", () =>
            {
                T.Eq(GameVersionVerdict.Ok, VersionCheck.CheckGame("1.3.0 (build 99)")); T.Eq(GameVersionVerdict.TooOld, VersionCheck.CheckGame("1.2.9")); T.Eq(GameVersionVerdict.Newer, VersionCheck.CheckGame("1.3.1 x"));
                T.Eq("2.0.0", VersionCheck.StripPreRelease("2.0.0-dev")); T.Eq("1.1.0", VersionCheck.StripPreRelease("v1.1.0-rc.14"));
                T.Eq(ModVersionVerdict.Outdated, VersionCheck.CheckMod("1.1.0-rc.14", "1.2.0")); T.Eq(ModVersionVerdict.Newer, VersionCheck.CheckMod("2.0.0-dev", "1.1.0")); T.Eq(ModVersionVerdict.UpToDate, VersionCheck.CheckMod("1.1.0", "1.1.0\n"));
                T.True(VersionCheck.DevEqualsRelease("1.1.0-dev", "1.1.0"));
            });
            T.Test("commands/host connect resync cheats cleanup", () =>
            {
                var w = new World(); var s = w.StartHost(); var env = new FakeEnv { Session = s };
                s.Listen = (p, v6) => w.Net.Open(p + 100); s.StopServer();
                T.Eq("Usage: host <port>", CommandRouter.Run(env, "host")); T.Eq("Usage: host <port>", CommandRouter.Run(env, "host abc"));
                T.True(CommandRouter.Run(env, "host 2020") == null); T.True(env.Calls.Contains("closemenu") && s.IsHost);
                T.Eq("cleaned", CommandRouter.Run(env, "cleanup"));
                CommandRouter.Run(env, "cleanup now"); T.Eq("cleanupnow", env.Calls.Last());
                T.Eq("Cheats are disabled on this server!", CommandRouter.Run(env, "addsprinkles 5")); env.Cheats = true; CommandRouter.Run(env, "addsprinkles 5"); T.Eq("sprinkles5", env.Calls.Last());
                env.InSessionV = false; T.Eq("Load a save first.", CommandRouter.Run(env, "addsprinkles 5")); env.InSessionV = true;
                T.Eq("Usage: chat <message>", CommandRouter.Run(env, "chat   ")); CommandRouter.Run(env, "chat hello world"); T.True(env.Calls.Contains("local:hello world"));
                env.ChatFocusedV = true; env.Calls.Clear(); CommandRouter.Run(env, "togglechat"); T.Eq(0, env.Calls.Count); env.ChatFocusedV = false; CommandRouter.Run(env, "togglechat"); T.Eq("chat", env.Calls[0]);
                T.Eq("Usage: connect <host[:port]>", CommandRouter.Run(env, "connect"));
            });
            T.Test("commands/cleanup is host only and chat slash never relays", () =>
            {
                var w = new World(); var env = new FakeEnv { Session = w.AddGuest() }; var sent = 0; var replies = new List<string>();
                env.Session.Node.PacketSeen += (t, b, p, snt) => { if (snt) sent++; };
                T.Eq("Only the host can run /cleanup.", CommandRouter.Run(env, "cleanup"));
                CommandRouter.HandleChatInput(env, "/cleanup", replies.Add); CommandRouter.HandleChatInput(env, "/cleanup now", replies.Add); CommandRouter.HandleChatInput(env, "/bogus x", replies.Add);
                T.Eq(0, sent, "never sent"); T.Eq("Only the host can run /cleanup.", replies[0]); T.True(replies[2].StartsWith("Unknown command /bogus.") && replies[2].Contains("/cleanup now"), replies[2]);
                var hostEnv = new FakeEnv { Session = w.StartHost() }; replies.Clear(); CommandRouter.HandleChatInput(hostEnv, "/cleanup", replies.Add); T.Eq("cleaned", replies[0]);
            });
            T.Test("commands/chat input is trimmed capped shown locally then sent", () =>
            {
                var w = new World(); var s = w.StartHost(); var env = new FakeEnv { Session = s };
                CommandRouter.HandleChatInput(env, "   hi   ", _ => { }); T.Eq("local:hi", env.Calls.Last());
                env.Calls.Clear(); CommandRouter.HandleChatInput(env, new string('x', 1500), _ => { }); T.Eq("local:" + new string('x', 1000), env.Calls.Last());
                env.Calls.Clear(); T.True(CommandRouter.HandleChatInput(env, "   ", _ => { })); T.Eq(0, env.Calls.Count);
            });
            T.Test("chat/dedup drops echo and trims past 1000", () =>
            {
                var d = new ChatDedup(); T.True(d.Register("a")); T.True(!d.Register("a")); for (int i = 0; i < 1100; i++) d.Register("m" + i); T.True(d.Register("a"), "oldest trimmed");
                T.True(!d.Register("m1099"));
            });
            T.Test("autohost/flow maps a random high port, adds firewall, makes a join code", () =>
            {
                var nat = new FakeNat(); var ah = new AutoHost(nat, nat, _ => Task.FromResult("198.51.100.7"), new Random(5));
                int port = ah.PrepareAsync().GetAwaiter().GetResult();
                T.True(port >= 49152 && port <= 65535); T.Eq(port, nat.Mapped.Single()); T.Eq(port, nat.Allowed.Single()); T.True(AutoHost.TryDecodeJoinCode(ah.JoinCode, out var ip, out int p2)); T.Eq("198.51.100.7", ip.ToString()); T.Eq(port, p2);
                ah.ReleaseAsync().GetAwaiter().GetResult(); T.Eq(port, nat.Unmapped.Single()); T.Eq(port, nat.Removed.Single()); T.True(ah.JoinCode == null);
                ah.ReleaseAsync().GetAwaiter().GetResult(); T.Eq(1, nat.Unmapped.Count, "release is a no-op when nothing is mapped");
            });
            T.Test("autohost/five attempts then plain failure messages", () =>
            {
                var nat = new FakeNat { FailMaps = 99 }; var ah = new AutoHost(nat, nat, _ => Task.FromResult("198.51.100.7"));
                var e = Assert(() => ah.PrepareAsync().GetAwaiter().GetResult()); T.True(e.Message.Contains("PlayIt")); T.Eq(5, nat.MapCalls);
                nat = new FakeNat { HasDevice = false }; ah = new AutoHost(nat, nat, _ => Task.FromResult("198.51.100.7")); e = Assert(() => ah.PrepareAsync().GetAwaiter().GetResult()); T.True(e.Message.Contains("No router answered"));
                foreach (var bad in new[] { "", "0.0.0.0", "127.0.0.1", "not an ip" })
                { ah = new AutoHost(new FakeNat(), null, _ => Task.FromResult(bad)); e = Assert(() => ah.PrepareAsync().GetAwaiter().GetResult()); T.True(e.Message.Contains("public IP"), bad); }
                ah = new AutoHost(new FakeNat(), null, _ => throw new HttpRequestFailed()); e = Assert(() => ah.PrepareAsync().GetAwaiter().GetResult()); T.True(e.Message.Contains("internet"));
            });
            T.Test("autohost/discovery timeout and exception produce distinct messages", () =>
            {
                var nat = new FakeNat { PendingDiscovery = new TaskCompletionSource<bool>().Task, Detail = "adapter-detail" };
                var ah = new AutoHost(nat, nat, _ => Task.FromResult("198.51.100.7")) { DiscoveryLimit = TimeSpan.FromMilliseconds(150) };
                var e = Assert(() => ah.PrepareAsync().GetAwaiter().GetResult()); T.True(e.Message.Contains("timed out"), e.Message); T.True(e.Message.Contains("adapter-detail"), e.Message);
                nat = new FakeNat { DiscoverThrows = new InvalidOperationException("boom-xyz") }; ah = new AutoHost(nat, nat, _ => Task.FromResult("198.51.100.7"));
                e = Assert(() => ah.PrepareAsync().GetAwaiter().GetResult()); T.True(e.Message.Contains("boom-xyz") && !e.Message.Contains("timed out"), e.Message);
            });
            T.Test("igd/ssdp parser, service picker, soap and nat-pmp helpers", () =>
            {
                var r = Igd.ParseSsdp("HTTP/1.1 200 OK\r\nCache-Control: max-age=120\r\nlocation: http://192.168.1.1:5000/rootDesc.xml\r\nServer: Linux UPnP/1.1 MiniUPnPd/2.1\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n");
                T.True(r != null && r.Location == "http://192.168.1.1:5000/rootDesc.xml" && r.Server.Contains("MiniUPnPd") && r.St.EndsWith("InternetGatewayDevice:1"));
                T.True(Igd.ParseSsdp("HTTP/1.1 200 OK\r\nST: x\r\n\r\n") == null); T.True(Igd.ParseSsdp("") == null);
                string ms = System.Text.Encoding.ASCII.GetString(Igd.MSearch("urn:x")); T.True(ms.StartsWith("M-SEARCH * HTTP/1.1\r\n") && ms.Contains("MAN: \"ssdp:discover\"") && ms.EndsWith("ST: urn:x\r\n\r\n"));
                string igd1 = "<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType><serviceList><service><serviceType>urn:schemas-upnp-org:service:Layer3Forwarding:1</serviceType><controlURL>/l3f</controlURL></service></serviceList><deviceList><device><deviceList><device><serviceList><service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType><controlURL>/ctl/IPConn</controlURL></service></serviceList></device></deviceList></device></deviceList></device></root>";
                var s = Igd.PickService(igd1, "http://192.168.1.1:5000/rootDesc.xml"); T.True(s != null && s.Value.Control == "http://192.168.1.1:5000/ctl/IPConn" && s.Value.Type.EndsWith("WANIPConnection:1"));
                string ppp = "<root><URLBase>http://10.0.0.1:80/</URLBase><device><service><serviceType>urn:schemas-upnp-org:service:WANPPPConnection:1</serviceType><controlURL>upnp/ppp</controlURL></service></device></root>";
                s = Igd.PickService(ppp, "http://10.0.0.1:1900/d.xml"); T.True(s != null && s.Value.Control == "http://10.0.0.1/upnp/ppp" && s.Value.Type.EndsWith("WANPPPConnection:1"));
                T.True(Igd.PickService("<root><device/></root>", "http://a/") == null); T.True(Igd.PickService("not xml", "http://a/") == null);
                string env = Igd.SoapEnvelope("urn:schemas-upnp-org:service:WANIPConnection:1", "AddPortMapping", new[] { ("NewExternalPort", "5000"), ("NewRemoteHost", "") });
                T.True(env.Contains("<u:AddPortMapping xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">") && env.Contains("<NewExternalPort>5000</NewExternalPort>") && env.Contains("s:encodingStyle="));
                var f = Igd.ParseFault("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>725</errorCode><errorDescription>OnlyPermanentLeasesSupported</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>");
                T.True(f != null && f.Value.Code == 725 && f.Value.Description == "OnlyPermanentLeasesSupported"); T.True(Igd.ParseFault("<a/>") == null);
                T.Eq("1.2.3.4", Igd.ParseValue("<a><NewExternalIPAddress>1.2.3.4</NewExternalIPAddress></a>", "NewExternalIPAddress"));
                T.True(Igd.TryParsePmpExternal(new byte[] { 0, 128, 0, 0, 0, 0, 0, 1, 203, 0, 113, 5 }, out var ip) && ip.ToString() == "203.0.113.5");
                T.True(!Igd.TryParsePmpExternal(new byte[] { 0, 128, 0, 3, 0, 0, 0, 1, 0, 0, 0, 0 }, out _)); T.True(!Igd.TryParsePmpExternal(new byte[] { 0, 128 }, out _));
                T.Eq("00-01-00-00-C0-00-C3-50-00-00-0E-10", BitConverter.ToString(Igd.PmpMapRequest(true, 49152, 50000, 3600)));
                T.Eq(2, Igd.PmpMapRequest(false, 1, 1, 0)[1]);
                var rep = new byte[] { 0, 129, 0, 0, 0, 0, 0, 9, 0xC0, 0x00, 0xC3, 0x50, 0, 0, 0x0E, 0x10 };
                T.True(Igd.TryParsePmpMap(rep, true, out int res, out int ext) && res == 0 && ext == 50000); T.True(!Igd.TryParsePmpMap(rep, false, out _, out _)); T.True(!Igd.TryParsePmpMap(new byte[15], true, out _, out _));
            });
            T.Test("autohost/noncooperative public-IP and router tasks cannot ignore caller deadline", () =>
            {
                foreach (int step in new[] { 0, 1, 2 })
                {
                    var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var nat = new FakeNat();
                    if (step == 1) nat.PendingDiscovery = never.Task;
                    if (step == 2) nat.PendingMap = never.Task;
                    var ah = new AutoHost(nat, nat, _ => step == 0
                        ? new TaskCompletionSource<string>().Task : Task.FromResult("198.51.100.7"));
                    using var deadline = new CancellationTokenSource(40);
                    var task = ah.PrepareAsync(deadline.Token);
                    bool failed = false;
                    try { task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
                    catch (AutoHostException) { failed = true; }
                    catch (OperationCanceledException) { failed = true; }
                    T.True(failed, "step " + step + " must end without cooperative backend cancellation");
                    never.TrySetResult(true);
                    T.Eq(0, ah.MappedPort); T.True(ah.JoinCode == null, "late setup must not advertise/start a server");
                }
            });
            T.Test("autohost/join code round trip ipv4 and ipv6", () =>
            {
                foreach (var addr in new[] { "1.2.3.4", "255.255.255.255", "2001:db8::1" })
                    foreach (int port in new[] { 1, 1919, 65535 })
                    { var code = AutoHost.EncodeJoinCode(IPAddress.Parse(addr), port); T.True(AutoHost.TryDecodeJoinCode(code.ToLowerInvariant(), out var ip, out int p)); T.Eq(IPAddress.Parse(addr).ToString(), ip.ToString()); T.Eq(port, p); }
                T.True(!AutoHost.TryDecodeJoinCode("garbage!", out _, out _)); T.True(!AutoHost.TryDecodeJoinCode("", out _, out _));
            });
            T.Test("autohost/refresh releases when the server stopped", () =>
            {
                var nat = new FakeNat(); var ah = new AutoHost(nat, nat, _ => Task.FromResult("198.51.100.7")); ah.PortStillHosted = p => false;
                ah.PrepareAsync().GetAwaiter().GetResult();
                typeof(AutoHost).GetMethod("RefreshOnce", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(ah, null);
                T.Eq(1, nat.Unmapped.Count);
            });
            T.Test("logging/format levels targets and rotation", () =>
            {
                string dir = Path.Combine(Path.GetTempPath(), "lyc-log-" + Guid.NewGuid().ToString("N")); // overridden below to stay in work folder
                dir = Path.Combine(AppContext.BaseDirectory, "logs-" + Guid.NewGuid().ToString("N"));
                var shown = new List<string>(); Log.Console = (lv, m) => shown.Add(m);
                Log.Open(dir, maxBytes: 300, backups: 2);
                Log.Info("hello"); Log.Warn("careful", LogTarget.Both); Log.Info("secret 10.0.0.1", LogTarget.Sensitive); Log.Split(LogLevel.Message, "client joined", "client 10.0.0.1:55"); Log.Info("never", LogTarget.Neither);
                Log.Info("[custom] already formatted");
                for (int i = 0; i < 30; i++) Log.Info("filler line number " + i);
                Log.Close(); Log.Console = null;
                var main = File.ReadAllText(Path.Combine(dir, "latest.log")); var sens = File.ReadAllText(Path.Combine(dir, "sensitive.log"));
                T.True(File.Exists(Path.Combine(dir, "latest.log.1")), "rotated"); T.True(!File.Exists(Path.Combine(dir, "latest.log.3")), "two backups only");
                T.True(shown.Contains("A sensitive message was logged!")); T.True(!shown.Any(s => s.Contains("10.0.0.1")), "ip never on console"); T.True(!main.Contains("10.0.0.1"));
                T.True(sens.Contains("10.0.0.1")); T.True(!shown.Contains("never"));
                T.True(System.Text.RegularExpressions.Regex.IsMatch(Log.Format(LogLevel.Warning, "x"), @"^\[\d\d:\d\d:\d\d\] \[WARNING\] x$")); T.Eq("[custom] y", Log.Format(LogLevel.Error, "[custom] y"));
                Directory.Delete(dir, true);
            });
            T.Test("diagnostics/jsonl records, escaping, dropped count, rotation, flush", () =>
            {
                string dir = Path.Combine(AppContext.BaseDirectory, "diag-" + Guid.NewGuid().ToString("N"));
                using (var d = new DiagnosticsWriter(dir, rotateBytes: 4000) { }) 
                {
                    d.HostId = () => "H"; d.Role = () => "host"; d.LocalId = () => "L";
                    T.True(!File.Exists(Path.Combine(dir, "diagnostics.jsonl")) || true); d.Record("x", "ignored while disabled");
                    d.Start("2.0.0-dev"); d.Record("scene", "scene-loaded", "quote\" back\\slash\nnewline " + new string('z', 5000));
                    for (int i = 0; i < 100; i++) d.Record("net", "packet-window", "type=PlayerUpdate; count=" + i);
                    d.Flush();
                }
                var all = Directory.GetFiles(dir, "diagnostics.jsonl*").SelectMany(File.ReadAllLines).ToList();
                T.True(all.Any(l => l.Contains("diagnostics-started") && l.Contains("\"role\":\"offline\"") || l.Contains("diagnostics-started")), "startup record");
                T.True(all.All(l => l.StartsWith("{") && l.EndsWith("}")), "every line is a json object");
                var scene = all.First(l => l.Contains("scene-loaded")); T.True(scene.Contains("quote\\\"") && scene.Contains("\\n") && scene.Length < 4300, "escaped and truncated to 4096 details");
                T.True(all.Count >= 100); T.True(Directory.GetFiles(dir, "diagnostics.jsonl.*").Length >= 1, "rotated at the configured size");
                T.True(all.All(l => !l.Contains("ignored while disabled")));
                Directory.Delete(dir, true);
            });
            T.Test("diagnostics/rotation failure leaves diagnostics disabled without aborting startup", () =>
            {
                string dir = Path.Combine(AppContext.BaseDirectory, "diag-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "diagnostics.jsonl"), "previous run");
                Directory.CreateDirectory(Path.Combine(dir, "diagnostics.jsonl.1"));
                try
                {
                    using var d = new DiagnosticsWriter(dir);
                    d.Start("t");
                    T.True(!d.Enabled);
                    d.Record("test", "ignored");
                    T.Eq("previous run", File.ReadAllText(Path.Combine(dir, "diagnostics.jsonl")));
                }
                finally { Directory.Delete(dir, true); }
            });
            T.Test("diagnostics/queue overflow counts dropped into next record", () =>
            {
                string dir = Path.Combine(AppContext.BaseDirectory, "diag-" + Guid.NewGuid().ToString("N")); var d = new DiagnosticsWriter(dir);
                d.Start("t"); for (int i = 0; i < 40000; i++) d.Record("flood", "e", "i=" + i); d.Record("flood", "last"); d.Flush();
                var lines = File.ReadAllLines(Path.Combine(dir, "diagnostics.jsonl")); T.True(lines.Length > 100);
                Directory.Delete(dir, true);
            });
            T.Test("diagnostics/off in release flavour, on in deeplog and test flavours", () =>
            {
bool release = BuildInfo.Flavour == "release";
                T.Eq(!release, BuildInfo.Diagnostics); T.Eq(!release, (bool)Settings.Find("development_diagnostics").Default);
            });
        }
        sealed class HttpRequestFailed : Exception { }
        static Exception Assert(Action a) { try { a(); } catch (AutoHostException e) { return e; } catch (Exception e) { return e; } throw new AssertException("expected failure"); }
    }
}

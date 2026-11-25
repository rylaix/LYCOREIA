// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lycoreia
{
    /// <summary>Router access (UPnP/NAT-PMP). The real implementation is UpnpGateway; tests use a fake.</summary>
    public interface INatGateway
    {
        Task<bool> DiscoverAsync(CancellationToken ct);                    // false when no UPnP device answers
        Task<IPAddress> ExternalIpAsync(CancellationToken ct);
        Task MapAsync(int udpPort, CancellationToken ct);
        Task UnmapAsync(int udpPort, CancellationToken ct);
        string Detail { get; }                                             // summary of the last discovery or mapping failure
        IPAddress LocalAddress { get; }                                    // this PC's LAN address the mapping forwards to (null if unknown)
    }

    public interface IFirewall { void Allow(int udpPort); void Remove(int udpPort); }

    public enum AutoHostFailure { PublicIp, DiscoveryTimeout, DiscoveryFailed, NoRouter, MapFailed }
    /// <summary>Message: English for logs. Kind + Detail: the menu words it in the player's language on the main thread.</summary>
    public sealed class AutoHostException : Exception
    {
        public readonly AutoHostFailure Kind; public readonly string Detail;
        public AutoHostException(string m, AutoHostFailure kind = AutoHostFailure.MapFailed, string detail = "") : base(m) { Kind = kind; Detail = detail ?? ""; }
    }

    /// <summary>Whether friends on the internet can reach a port mapped on the router.</summary>
    public enum HostReach
    {
        Unknown,        // the router did not report its WAN address
        Public,         // router WAN = the address the internet sees
        CarrierNat,     // router WAN in 100.64.0.0/10: the provider NATs again (CGNAT), the mapping never reaches the internet
        DoubleNat,      // router WAN is a private address: another router/modem sits in front of it
        VpnOrProxy,     // router WAN is public but differs from what this PC's HTTPS traffic shows: VPN/proxy on this PC
    }

    /// <summary>Address classification and masking for logs (no full public addresses in logs).</summary>
    public static class NetAddr
    {
        public static string Kind(IPAddress ip)
        {
            if (ip == null) return "none";
            if (IPAddress.IsLoopback(ip)) return "loopback";
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv4MappedToIPv6) return Kind(ip.MapToIPv4());
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return "private";
                var b6 = ip.GetAddressBytes(); return (b6[0] & 0xFE) == 0xFC ? "private" : "public";
            }
            var b = ip.GetAddressBytes();
            if (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)) return "private";
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return "cgnat";
            if (b[0] == 169 && b[1] == 254) return "link-local";
            if (b[0] == 0 || b[0] >= 224) return "invalid";
            return "public";
        }
        public static bool IsPublic(IPAddress ip) => Kind(ip) == "public";
        /// <summary>True when a and b are in the same IPv4 subnet.</summary>
        public static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
        {
            if (a == null || b == null || mask == null || a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || b.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || mask.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
            var x = a.GetAddressBytes(); var y = b.GetAddressBytes(); var m = mask.GetAddressBytes(); bool any = false;
            for (int i = 0; i < 4; i++) { if ((x[i] & m[i]) != (y[i] & m[i])) return false; any |= m[i] != 0; }
            return any;
        }
        /// <summary>True when one of this PC's network adapters is in the same subnet as ip (that is, we sit behind the same router).</summary>
        public static bool OnLocalSubnet(IPAddress ip)
        {
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (!ip.Equals(ua.Address) && SameSubnet(ip, ua.Address, ua.IPv4Mask)) return true;
                }
            }
            catch (Exception e) { Log.Debug("adapter enumeration failed: " + e.Message); }
            return false;
        }
        /// <summary>"100.72.x.x (cgnat)": enough to diagnose, not enough to identify a player.</summary>
        public static string Describe(IPAddress ip)
        {
            if (ip == null) return "none";
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            string kind = Kind(ip);
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return "ipv6 (" + kind + ")";
            var b = ip.GetAddressBytes();
            return kind == "public" ? b[0] + ".x.x.x (public)" : b[0] + "." + b[1] + ".x.x (" + kind + ")";
        }
    }

    /// <summary>Auto-host flow: public IP, UPnP discovery, random port mapping, firewall exception, join code, 3 minute refresh.</summary>
    public sealed class AutoHost
    {
        public const int MapAttempts = 5, MinPort = 49152, MaxPort = 65535;
        public static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(45);
        public static readonly TimeSpan IpTimeout = TimeSpan.FromSeconds(5), DiscoveryTimeout = TimeSpan.FromSeconds(10), MapTimeout = TimeSpan.FromSeconds(8), ReleaseTimeout = TimeSpan.FromSeconds(8), RefreshEvery = TimeSpan.FromMinutes(3);

        readonly INatGateway _nat; readonly IFirewall _fw; readonly Func<CancellationToken, Task<string>> _publicIp; readonly System.Random _rng;
        public TimeSpan DiscoveryLimit = DiscoveryTimeout;
        string Why() { string d = _nat.Detail; return string.IsNullOrWhiteSpace(d) ? "" : " " + d; }
        public Action<string> StageChanged = stage => { };
        void Stage(string value) { Log.Info("automatic hosting: " + value); try { StageChanged(value); } catch { } }
        public int MappedPort { get; private set; }
        public IPAddress PublicIp { get; private set; }
        /// <summary>WAN address the router reported (null when it would not say).</summary>
        public IPAddress RouterIp { get; private set; }
        /// <summary>This PC's LAN address; also in the join code so a guest behind the same router can skip NAT hairpinning.</summary>
        public IPAddress LanIp { get; private set; }
        public HostReach Reach { get; private set; }
        /// <summary>The Windows firewall rule could not be added (netsh needs administrator rights).</summary>
        public bool FirewallFailed { get; private set; }
        /// <summary>One line for logs and diagnostics, addresses masked.</summary>
        public string Summary { get; private set; } = "";
        public string JoinCode { get; private set; }
        System.Threading.Timer _refresh; readonly object _gate = new object();
        public Func<int, bool> PortStillHosted = p => true;    // set by the session: server running on this port?

        public AutoHost(INatGateway nat, IFirewall fw, Func<CancellationToken, Task<string>> publicIp = null, System.Random rng = null)
        { _nat = nat; _fw = fw; _publicIp = publicIp ?? DefaultPublicIp; _rng = rng ?? new System.Random(); }

        static async Task<string> DefaultPublicIp(CancellationToken ct)
        {
            using var http = new HttpClient { Timeout = IpTimeout };
            return (await http.GetStringAsync("https://api.ipify.org", ct).ConfigureAwait(false)).Trim();
        }

        /// <summary>Runs the whole flow and returns the port to host on. Throws AutoHostException with a player-readable message.</summary>
        public async Task<int> PrepareAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Stage("Releasing previous mapping");
            await ReleaseAsync().WaitAsync(ct).ConfigureAwait(false);   // a previous mapping goes first
            Stage("Finding public address");
            string ipText;
            try { using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(IpTimeout); ipText = await _publicIp(cts.Token).WaitAsync(cts.Token).ConfigureAwait(false); }
            catch (Exception) { throw new AutoHostException("Could not find your public IP address. Check your internet connection.", AutoHostFailure.PublicIp); }
            if (!IPAddress.TryParse(ipText, out var ip) || IPAddress.Any.Equals(ip) || IPAddress.IPv6Any.Equals(ip) || IPAddress.IsLoopback(ip))
                throw new AutoHostException("Could not find your public IP address. Check your internet connection.", AutoHostFailure.PublicIp);

            Stage("Finding router");
            bool found = false; AutoHostException fail = null;
            try { using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(DiscoveryLimit); found = await _nat.DiscoverAsync(cts.Token).WaitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { fail = new AutoHostException("Router discovery timed out." + Why() + " Forward the port by hand or use a tunnel such as PlayIt.", AutoHostFailure.DiscoveryTimeout, _nat.Detail); }
            catch (OperationCanceledException) { ct.ThrowIfCancellationRequested(); }
            catch (Exception e) { fail = new AutoHostException("Router discovery failed: " + e.Message + Why() + " Forward the port by hand or use a tunnel such as PlayIt.", AutoHostFailure.DiscoveryFailed, (e.Message + " " + _nat.Detail).Trim()); }
            if (fail != null) throw fail;
            if (!found) throw new AutoHostException("No router answered UPnP or NAT-PMP." + Why() + " If UPnP is enabled on the router, a firewall (UDP 1900/5351) or a VPN/virtual adapter may be blocking discovery; you can forward the port by hand or use PlayIt.", AutoHostFailure.NoRouter, _nat.Detail);

            ct.ThrowIfCancellationRequested();
            Stage("Opening router port");
            int port = 0;
            using (var overall = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                overall.CancelAfter(MapTimeout);
                for (int i = 0; i < MapAttempts && port == 0; i++)
                {
                    int candidate = _rng.Next(MinPort, MaxPort + 1);
                    try { await _nat.MapAsync(candidate, overall.Token).WaitAsync(overall.Token).ConfigureAwait(false); port = candidate; }
                    catch (Exception e) { Log.Debug("port mapping attempt failed: " + e.Message); if (overall.IsCancellationRequested) break; }
                }
            }
            if (port == 0) throw new AutoHostException("Could not open a port on your router. Forward a UDP port by hand or use a tunnel such as PlayIt." + Why(), AutoHostFailure.MapFailed, _nat.Detail);

            // a mapping "succeeds" on a router that is itself behind the provider's NAT (CGNAT, very common in Russia)
            // or behind a second router; the join code then points at an address that drops every packet. Ask the router what
            // its WAN address is and compare it with what the internet sees.
            ct.ThrowIfCancellationRequested();
            Stage("Checking router address");
            IPAddress wan = null;
            try { using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(IpTimeout); wan = await _nat.ExternalIpAsync(cts.Token).WaitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { Log.Debug("router WAN address unavailable: " + e.Message); }
            var (reach, advertise) = Assess(ip, wan);

            ct.ThrowIfCancellationRequested();
            Stage("Configuring firewall");
            bool fwFailed = false;
            try { _fw?.Allow(port); } catch (Exception e) { fwFailed = true; Log.Warn("firewall exception failed: " + e.Message); }
            ct.ThrowIfCancellationRequested();
            IPAddress lan = null; try { lan = _nat.LocalAddress; } catch { }
            if (lan != null && (lan.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || NetAddr.Kind(lan) != "private")) lan = null;
            string summary = $"port={port}; public={NetAddr.Describe(ip)}; router={NetAddr.Describe(wan)}; lan={(lan == null ? "none" : "yes")}; reach={reach}; firewall={(fwFailed ? "failed" : "ok")}";
            if (reach == HostReach.Public || reach == HostReach.Unknown) Log.Info("automatic hosting: " + summary);
            else Log.Warn("automatic hosting: friends on the internet probably cannot reach this host: " + summary);
            lock (_gate) { MappedPort = port; PublicIp = advertise; RouterIp = wan; LanIp = lan; Reach = reach; FirewallFailed = fwFailed; Summary = summary; JoinCode = EncodeJoinCode(advertise, port, lan); }
            _refresh = new System.Threading.Timer(new TimerCallback(OnRefreshTimer), null, RefreshEvery, RefreshEvery);
            Stage("Starting server socket");
            return port;
        }

        /// <summary>Reachability of a router mapping and the address to put in the join code.</summary>
        public static (HostReach Reach, IPAddress Advertise) Assess(IPAddress seenByInternet, IPAddress routerWan)
        {
            if (routerWan == null || IPAddress.Any.Equals(routerWan)) return (HostReach.Unknown, seenByInternet);
            if (routerWan.IsIPv4MappedToIPv6) routerWan = routerWan.MapToIPv4();
            switch (NetAddr.Kind(routerWan))
            {
                case "cgnat": return (HostReach.CarrierNat, seenByInternet);
                case "public": break;
                default: return (HostReach.DoubleNat, seenByInternet);
            }
            // The mapping lives on the router: its public WAN address is the one that forwards to us.
            return routerWan.Equals(seenByInternet) ? (HostReach.Public, seenByInternet) : (HostReach.VpnOrProxy, routerWan);
        }

        void OnRefreshTimer(object state) => RefreshOnce();
        void RefreshOnce()
        {
            int port; lock (_gate) port = MappedPort;
            if (port == 0) return;
            try
            {
                if (!PortStillHosted(port)) { ReleaseAsync().GetAwaiter().GetResult(); return; }
                using var cts = new CancellationTokenSource(MapTimeout);
                _nat.MapAsync(port, cts.Token).WaitAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch (Exception e) { Log.Warn("could not refresh the port mapping: " + e.Message); }
        }

        /// <summary>Removes the mapping, firewall exception and timer. A no-op when nothing is mapped. Failures only warn.</summary>
        public async Task ReleaseAsync()
        {
            int port; lock (_gate) { port = MappedPort; MappedPort = 0; JoinCode = null; LanIp = null; Reach = HostReach.Unknown; FirewallFailed = false; RouterIp = null; Summary = ""; }
            var t = _refresh; _refresh = null; t?.Dispose();
            if (port == 0) return;
            try { using var cts = new CancellationTokenSource(ReleaseTimeout); await _nat.UnmapAsync(port, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false); }
            catch (Exception e) { Log.Warn("could not remove the port mapping: " + e.Message); }
            try { _fw?.Remove(port); } catch (Exception e) { Log.Warn("could not remove the firewall exception: " + e.Message); }
        }

        /// <summary>Synchronous release for quit: blocks up to the release timeout so the process cannot kill a background task.</summary>
        public void ReleaseBlocking()
        {
            try { ReleaseAsync().Wait(ReleaseTimeout + TimeSpan.FromSeconds(1)); } catch { }
        }

        // ---- join code: 4 or 16 address bytes plus a 2 byte port, then optionally the host's 4 byte LAN IPv4, Crockford base 32, grouped for reading.
        // Decoders before the LAN address was added read only the public part and ignore the trailing LAN bytes.
        const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        public static string EncodeJoinCode(IPAddress ip, int port, IPAddress lan = null)
        {
            var addr = ip.GetAddressBytes(); var lanBytes = lan != null && lan.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? lan.GetAddressBytes() : Array.Empty<byte>();
            var data = new byte[1 + addr.Length + 2 + lanBytes.Length];
            data[0] = (byte)(addr.Length == 4 ? 4 : 6); Buffer.BlockCopy(addr, 0, data, 1, addr.Length); data[1 + addr.Length] = (byte)(port >> 8); data[2 + addr.Length] = (byte)port;
            Buffer.BlockCopy(lanBytes, 0, data, 3 + addr.Length, lanBytes.Length);
            var sb = new StringBuilder(); int bits = 0, acc = 0;
            foreach (byte b in data) { acc = (acc << 8) | b; bits += 8; while (bits >= 5) { sb.Append(Alphabet[(acc >> (bits - 5)) & 31]); bits -= 5; } }
            if (bits > 0) sb.Append(Alphabet[(acc << (5 - bits)) & 31]);
            string s = sb.ToString(); return string.Join("-", Enumerable.Range(0, (s.Length + 4) / 5).Select(i => s.Substring(i * 5, Math.Min(5, s.Length - i * 5))));
        }
        public static bool TryDecodeJoinCode(string code, out IPAddress ip, out int port) => TryDecodeJoinCode(code, out ip, out port, out _);
        public static bool TryDecodeJoinCode(string code, out IPAddress ip, out int port, out IPAddress lan)
        {
            ip = null; port = 0; lan = null; if (string.IsNullOrWhiteSpace(code)) return false;
            var clean = code.Replace("-", "").Replace(" ", "").ToUpperInvariant(); var bytes = new System.Collections.Generic.List<byte>(); int bits = 0, acc = 0;
            foreach (char ch in clean) { int v = Alphabet.IndexOf(ch); if (v < 0) return false; acc = (acc << 5) | v; bits += 5; if (bits >= 8) { bytes.Add((byte)((acc >> (bits - 8)) & 255)); bits -= 8; } }
            if (bytes.Count < 7) return false; int len = bytes[0] == 4 ? 4 : bytes[0] == 6 ? 16 : -1;
            if (len < 0 || bytes.Count < 1 + len + 2) return false;
            ip = new IPAddress(bytes.GetRange(1, len).ToArray()); port = (bytes[1 + len] << 8) | bytes[2 + len];
            if (bytes.Count >= 3 + len + 4) { var l = new IPAddress(bytes.GetRange(3 + len, 4).ToArray()); if (NetAddr.Kind(l) == "private") lan = l; }
            return port > 0;
        }
    }
}

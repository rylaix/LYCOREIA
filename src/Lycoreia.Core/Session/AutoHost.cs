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
    }

    public interface IFirewall { void Allow(int udpPort); void Remove(int udpPort); }

    public sealed class AutoHostException : Exception { public AutoHostException(string m) : base(m) { } }

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
            catch (Exception) { throw new AutoHostException("Could not find your public IP address. Check your internet connection."); }
            if (!IPAddress.TryParse(ipText, out var ip) || IPAddress.Any.Equals(ip) || IPAddress.IPv6Any.Equals(ip) || IPAddress.IsLoopback(ip))
                throw new AutoHostException("Could not find your public IP address. Check your internet connection.");

            Stage("Finding router");
            bool found = false; AutoHostException fail = null;
            try { using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(DiscoveryLimit); found = await _nat.DiscoverAsync(cts.Token).WaitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { fail = new AutoHostException("Router discovery timed out." + Why() + " Forward the port by hand or use a tunnel such as PlayIt."); }
            catch (OperationCanceledException) { ct.ThrowIfCancellationRequested(); }
            catch (Exception e) { fail = new AutoHostException("Router discovery failed: " + e.Message + Why() + " Forward the port by hand or use a tunnel such as PlayIt."); }
            if (fail != null) throw fail;
            if (!found) throw new AutoHostException("No router answered UPnP or NAT-PMP." + Why() + " If UPnP is enabled on the router, a firewall (UDP 1900/5351) or a VPN/virtual adapter may be blocking discovery; you can forward the port by hand or use PlayIt.");

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
            if (port == 0) throw new AutoHostException("Could not open a port on your router. Forward a UDP port by hand or use a tunnel such as PlayIt." + Why());

            ct.ThrowIfCancellationRequested();
            Stage("Configuring firewall");
            try { _fw?.Allow(port); } catch (Exception e) { Log.Warn("firewall exception failed: " + e.Message); }
            ct.ThrowIfCancellationRequested();
            lock (_gate) { MappedPort = port; PublicIp = ip; JoinCode = EncodeJoinCode(ip, port); }
            _refresh = new System.Threading.Timer(new TimerCallback(OnRefreshTimer), null, RefreshEvery, RefreshEvery);
            Stage("Starting server socket");
            return port;
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
            int port; lock (_gate) { port = MappedPort; MappedPort = 0; JoinCode = null; }
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

        // ---- join code: 4 or 16 address bytes plus a 2 byte port, Crockford base 32, grouped for reading
        const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        public static string EncodeJoinCode(IPAddress ip, int port)
        {
            var addr = ip.GetAddressBytes(); var data = new byte[1 + addr.Length + 2];
            data[0] = (byte)(addr.Length == 4 ? 4 : 6); Buffer.BlockCopy(addr, 0, data, 1, addr.Length); data[data.Length - 2] = (byte)(port >> 8); data[data.Length - 1] = (byte)port;
            var sb = new StringBuilder(); int bits = 0, acc = 0;
            foreach (byte b in data) { acc = (acc << 8) | b; bits += 8; while (bits >= 5) { sb.Append(Alphabet[(acc >> (bits - 5)) & 31]); bits -= 5; } }
            if (bits > 0) sb.Append(Alphabet[(acc << (5 - bits)) & 31]);
            string s = sb.ToString(); return string.Join("-", Enumerable.Range(0, (s.Length + 4) / 5).Select(i => s.Substring(i * 5, Math.Min(5, s.Length - i * 5))));
        }
        public static bool TryDecodeJoinCode(string code, out IPAddress ip, out int port)
        {
            ip = null; port = 0; if (string.IsNullOrWhiteSpace(code)) return false;
            var clean = code.Replace("-", "").Replace(" ", "").ToUpperInvariant(); var bytes = new System.Collections.Generic.List<byte>(); int bits = 0, acc = 0;
            foreach (char ch in clean) { int v = Alphabet.IndexOf(ch); if (v < 0) return false; acc = (acc << 5) | v; bits += 5; if (bits >= 8) { bytes.Add((byte)((acc >> (bits - 8)) & 255)); bits -= 8; } }
            if (bytes.Count < 7) return false; int len = bytes[0] == 4 ? 4 : bytes[0] == 6 ? 16 : -1;
            if (len < 0 || bytes.Count < 1 + len + 2) return false;
            ip = new IPAddress(bytes.GetRange(1, len).ToArray()); port = (bytes[1 + len] << 8) | bytes[2 + len]; return port > 0;
        }
    }
}

// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Lycoreia
{
    /// <summary>One SSDP answer.</summary>
    internal sealed class SsdpReply { public string Location, St, Server; }

    /// <summary>Pure packet and XML helpers for UPnP IGD and NAT-PMP.</summary>
    internal static class Igd
    {
        public static readonly string[] SearchTargets = {
            "urn:schemas-upnp-org:device:InternetGatewayDevice:1", "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
            "urn:schemas-upnp-org:service:WANIPConnection:1", "urn:schemas-upnp-org:service:WANIPConnection:2", "urn:schemas-upnp-org:service:WANPPPConnection:1" };

        public static byte[] MSearch(string st) => Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: " + st + "\r\n\r\n");

        /// <summary>Parses an SSDP response or NOTIFY; null when it has no LOCATION header.</summary>
        public static SsdpReply ParseSsdp(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var r = new SsdpReply(); var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':'); if (c <= 0) continue;
                string k = lines[i].Substring(0, c).Trim(), v = lines[i].Substring(c + 1).Trim();
                if (k.Equals("LOCATION", StringComparison.OrdinalIgnoreCase)) r.Location = v;
                else if (k.Equals("ST", StringComparison.OrdinalIgnoreCase) || (r.St == null && k.Equals("NT", StringComparison.OrdinalIgnoreCase))) r.St = v;
                else if (k.Equals("SERVER", StringComparison.OrdinalIgnoreCase)) r.Server = v;
            }
            return string.IsNullOrEmpty(r.Location) ? null : r;
        }

        static string Child(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim();

        /// <summary>Picks the WAN connection service out of a device description. Prefers WANIPConnection:2, :1, then any WANIPConnection, then WANPPPConnection:1.</summary>
        public static (string Control, string Type)? PickService(string xml, string locationUrl)
        {
            XDocument doc; try { doc = XDocument.Parse(xml); } catch (Exception) { return null; }
            string baseUrl = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "URLBase")?.Value.Trim();
            var services = doc.Descendants().Where(x => x.Name.LocalName == "service").Select(s => (Type: Child(s, "serviceType"), Ctl: Child(s, "controlURL"))).Where(s => !string.IsNullOrEmpty(s.Type) && !string.IsNullOrEmpty(s.Ctl)).ToList();
            const string ip = "urn:schemas-upnp-org:service:WANIPConnection:", ppp = "urn:schemas-upnp-org:service:WANPPPConnection:1";
            var pick = services.Where(s => s.Type == ip + "2").Concat(services.Where(s => s.Type == ip + "1")).Concat(services.Where(s => s.Type.StartsWith(ip, StringComparison.Ordinal))).Concat(services.Where(s => s.Type == ppp)).Cast<(string Type, string Ctl)?>().FirstOrDefault();
            if (pick == null) return null;
            try { var b = new Uri(string.IsNullOrEmpty(baseUrl) ? locationUrl : baseUrl); return (new Uri(b, pick.Value.Ctl).ToString(), pick.Value.Type); }
            catch (Exception) { return null; }
        }

        // ---- SOAP
        public static string SoapEnvelope(string serviceType, string action, IEnumerable<(string, string)> args)
        {
            XNamespace s = "http://schemas.xmlsoap.org/soap/envelope/", u = serviceType;
            var call = new XElement(u + action, new XAttribute(XNamespace.Xmlns + "u", serviceType), args.Select(a => new XElement(a.Item1, a.Item2)));
            var env = new XElement(s + "Envelope", new XAttribute(XNamespace.Xmlns + "s", s.NamespaceName), new XAttribute(s + "encodingStyle", "http://schemas.xmlsoap.org/soap/encoding/"), new XElement(s + "Body", call));
            return "<?xml version=\"1.0\"?>" + env.ToString(SaveOptions.DisableFormatting);
        }
        public static (int Code, string Description)? ParseFault(string xml)
        {
            try
            {
                var d = XDocument.Parse(xml); var code = d.Descendants().FirstOrDefault(x => x.Name.LocalName == "errorCode");
                if (code == null) return d.Descendants().Any(x => x.Name.LocalName == "Fault") ? (0, d.Descendants().FirstOrDefault(x => x.Name.LocalName == "faultstring")?.Value ?? "fault") : ((int, string)?)null;
                int.TryParse(code.Value.Trim(), out int n); return (n, d.Descendants().FirstOrDefault(x => x.Name.LocalName == "errorDescription")?.Value.Trim() ?? "");
            }
            catch (Exception) { return null; }
        }
        public static string ParseValue(string xml, string name) { try { return XDocument.Parse(xml).Descendants().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim(); } catch (Exception) { return null; } }

        // ---- NAT-PMP (RFC 6886), big endian
        public static readonly byte[] PmpExternalRequest = { 0, 0 };
        public static bool TryParsePmpExternal(byte[] d, out IPAddress ip)
        {
            ip = null; if (d == null || d.Length != 12 || d[0] != 0 || d[1] != 128 || d[2] != 0 || d[3] != 0) return false;
            ip = new IPAddress(new[] { d[8], d[9], d[10], d[11] }); return true;
        }
        public static byte[] PmpMapRequest(bool udp, int internalPort, int externalPort, uint lifetime) => new byte[] {
            0, (byte)(udp ? 1 : 2), 0, 0, (byte)(internalPort >> 8), (byte)internalPort, (byte)(externalPort >> 8), (byte)externalPort,
            (byte)(lifetime >> 24), (byte)(lifetime >> 16), (byte)(lifetime >> 8), (byte)lifetime };
        /// <summary>Parses a 16 byte map reply. False when malformed or the opcode does not match; result carries the NAT-PMP result code.</summary>
        public static bool TryParsePmpMap(byte[] d, bool udp, out int result, out int externalPort)
        {
            result = -1; externalPort = 0; if (d == null || d.Length != 16 || d[0] != 0 || d[1] != (udp ? 129 : 130)) return false;
            result = (d[2] << 8) | d[3]; externalPort = (d[10] << 8) | d[11]; return true;
        }
    }

    /// <summary>Own UPnP IGD (SSDP, device description, SOAP) with a NAT-PMP fallback.</summary>
    public sealed class UpnpGateway : INatGateway
    {
        public static Action<string, string> Trace;
        public string Detail { get; private set; } = "";
        static void Tr(string ev, string d) { try { Trace?.Invoke(ev, d); } catch (Exception) { } }

        sealed class Local { public IPAddress Ip; public string Name; public List<IPAddress> Gateways = new List<IPAddress>(); public override string ToString() => Name + " " + Ip + " (gw " + (Gateways.Count == 0 ? "none" : string.Join("/", Gateways)) + ")"; }

        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        static readonly IPEndPoint Multicast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        string _control, _type; IPAddress _local, _pmpGateway, _pmpLocal, _pmpExternal;

        static List<Local> Adapters()
        {
            var list = new List<Local>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    var props = ni.GetIPProperties();
                    var gws = props.GatewayAddresses.Select(g => g.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(a)).ToList();
                    foreach (var ua in props.UnicastAddresses)
                    {
                        var a = ua.Address; if (a.AddressFamily != AddressFamily.InterNetwork) continue;
                        var b = a.GetAddressBytes(); if ((b[0] == 169 && b[1] == 254) || list.Any(x => x.Ip.Equals(a))) continue;
                        var l = new Local { Ip = a, Name = ni.Name }; l.Gateways.AddRange(gws);
                        if (l.Gateways.Count == 0 && b[3] != 1) l.Gateways.Add(new IPAddress(new byte[] { b[0], b[1], b[2], 1 }));
                        list.Add(l);
                    }
                }
            }
            catch (Exception e) { Tr("upnp-adapters", "enumeration failed: " + e.Message); }
            if (list.Count == 0) list.Add(new Local { Ip = IPAddress.Any, Name = "any" });
            return list;
        }

        static IPAddress RouteLocal(IPAddress to)
        {
            try { using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); s.Connect(new IPEndPoint(to, 9)); return ((IPEndPoint)s.LocalEndPoint).Address; }
            catch (Exception) { return IPAddress.Any; }
        }

        public async Task<bool> DiscoverAsync(CancellationToken ct)
        {
            _control = null; _pmpGateway = null; Detail = "";
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct); budget.CancelAfter(TimeSpan.FromSeconds(6));
            var locals = Adapters(); string adapters = string.Join(", ", locals.Select(l => l.ToString()));
            Tr("upnp-adapters", locals.Count + " adapter(s): " + adapters);
            try
            {
                var pmp = PmpProbe(locals, budget.Token);
                var found = await Ssdp(locals, budget.Token).ConfigureAwait(false);
                foreach (var kv in found)
                {
                    budget.Token.ThrowIfCancellationRequested();
                    try
                    {
                        string xml = await Http.GetStringAsync(kv.Key, budget.Token).ConfigureAwait(false);
                        var svc = Igd.PickService(xml, kv.Key);
                        if (svc == null) { Tr("upnp-device", "no WAN connection service in " + kv.Key); continue; }
                        _control = svc.Value.Control; _type = svc.Value.Type;
                        _local = IPAddress.Any.Equals(kv.Value.Item1) ? RouteLocal(new Uri(kv.Key).HostToIp()) : kv.Value.Item1;
                        Tr("upnp-device", "control=" + _control + " service=" + _type + " local=" + _local); return true;
                    }
                    catch (Exception e) when (!budget.IsCancellationRequested) { Tr("upnp-device", "description failed " + kv.Key + ": " + e.Message); }
                }
                var hit = await pmp.ConfigureAwait(false);
                if (hit.Item1 != null) { _pmpGateway = hit.Item1; _pmpLocal = hit.Item2; _pmpExternal = hit.Item3; return true; }
                Detail = "no UPnP/NAT-PMP answer on " + locals.Count + " adapter(s): " + adapters + "; searched multicast and direct to gateway" + (found.Count > 0 ? "; " + found.Count + " device description(s) had no usable WAN service" : "");
                Tr("upnp-none", Detail); return false;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Detail = "discovery budget used up on " + locals.Count + " adapter(s): " + adapters; Tr("upnp-none", Detail); return false;
            }
        }

        async Task<ConcurrentDictionary<string, (IPAddress, SsdpReply)>> Ssdp(List<Local> locals, CancellationToken budget)
        {
            var found = new ConcurrentDictionary<string, (IPAddress, SsdpReply)>();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(budget); cts.CancelAfter(TimeSpan.FromSeconds(3));
            var socks = new List<UdpClient>(); var tasks = new List<Task>();
            try
            {
                foreach (var l in locals)
                {
                    UdpClient u;
                    try { u = new UdpClient(new IPEndPoint(l.Ip, 0)); } catch (Exception e) { Tr("upnp-adapters", "bind " + l.Ip + " failed: " + e.Message); continue; }
                    socks.Add(u);
                    try { if (!IPAddress.Any.Equals(l.Ip)) u.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, l.Ip.GetAddressBytes()); u.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2); } catch (Exception) { }
                    tasks.Add(Listen(u, l, found, cts)); tasks.Add(SendRounds(u, l, cts.Token));
                }
                try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch (Exception) { }
            }
            finally { foreach (var s in socks) s.Dispose(); }
            return found;
        }

        static async Task SendRounds(UdpClient u, Local l, CancellationToken ct)
        {
            try
            {
                for (int round = 0; round < 2; round++)
                {
                    if (round > 0) await Task.Delay(250, ct).ConfigureAwait(false);
                    foreach (var st in Igd.SearchTargets)
                    {
                        var pkt = Igd.MSearch(st);
                        foreach (var to in new[] { Multicast }.Concat(l.Gateways.Select(g => new IPEndPoint(g, 1900))))
                            try { await u.SendAsync(pkt, pkt.Length, to).ConfigureAwait(false); } catch (Exception e) when (e is SocketException || e is ObjectDisposedException) { }
                    }
                }
            }
            catch (Exception) { }
        }

        static async Task Listen(UdpClient u, Local l, ConcurrentDictionary<string, (IPAddress, SsdpReply)> found, CancellationTokenSource cts)
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var res = await u.ReceiveAsync(cts.Token).ConfigureAwait(false);
                    var r = Igd.ParseSsdp(Encoding.ASCII.GetString(res.Buffer));
                    if (r == null) continue;
                    Tr("upnp-reply", "from=" + res.RemoteEndPoint + " st=" + r.St + " location=" + r.Location + " server=" + r.Server);
                    if (found.TryAdd(r.Location, (l.Ip, r)) && found.Count == 1) cts.CancelAfter(500);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { try { await Task.Delay(20, cts.Token).ConfigureAwait(false); } catch (Exception) { return; } }
                catch (Exception) { return; }
            }
        }

        // ---- NAT-PMP
        static async Task<byte[]> PmpExchange(IPAddress local, IPAddress gateway, byte[] req, int expect, CancellationToken ct)
        {
            using var u = new UdpClient(new IPEndPoint(local, 0));
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(1000);
                try
                {
                    await u.SendAsync(req, req.Length, new IPEndPoint(gateway, 5351)).ConfigureAwait(false);
                    while (true) { var r = await u.ReceiveAsync(cts.Token).ConfigureAwait(false); if (r.Buffer.Length == expect && r.RemoteEndPoint.Address.Equals(gateway)) return r.Buffer; }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                catch (SocketException) { }
            }
            return null;
        }

        static async Task<(IPAddress, IPAddress, IPAddress)> PmpProbe(List<Local> locals, CancellationToken ct)
        {
            var probes = locals.SelectMany(l => l.Gateways.Select(g => (l, g))).ToList();
            var tasks = probes.Select(async p =>
            {
                try
                {
                    var d = await PmpExchange(p.l.Ip, p.g, Igd.PmpExternalRequest, 12, ct).ConfigureAwait(false);
                    if (d != null && Igd.TryParsePmpExternal(d, out var ext)) { Tr("natpmp-reply", "gateway=" + p.g + " external=" + ext); return (p.g, p.l.Ip, ext); }
                }
                catch (Exception) { }
                return ((IPAddress)null, (IPAddress)null, (IPAddress)null);
            }).ToList();
            foreach (var t in tasks) { var r = await t.ConfigureAwait(false); if (r.Item1 != null) return r; }
            return (null, null, null);
        }

        // ---- SOAP
        async Task<string> Soap(string action, params (string, string)[] args)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _control) { Content = new StringContent(Igd.SoapEnvelope(_type, action, args), Encoding.UTF8, "text/xml") };
            req.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("text/xml; charset=\"utf-8\"");
            req.Headers.TryAddWithoutValidation("SOAPAction", "\"" + _type + "#" + action + "\"");
            using var resp = await Http.SendAsync(req, ct0).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return body;
            var f = Igd.ParseFault(body);
            throw new UpnpFault(f?.Code ?? (int)resp.StatusCode, f?.Description ?? resp.ReasonPhrase);
        }
        CancellationToken ct0;
        sealed class UpnpFault : Exception { public int Code; public UpnpFault(int code, string d) : base("router refused (UPnP error " + code + (string.IsNullOrEmpty(d) ? "" : " " + d) + ")") { Code = code; } }

        public async Task<IPAddress> ExternalIpAsync(CancellationToken ct)
        {
            try
            {
                if (_pmpGateway != null)
                {
                    var d = await PmpExchange(_pmpLocal, _pmpGateway, Igd.PmpExternalRequest, 12, ct).ConfigureAwait(false);
                    if (d != null && Igd.TryParsePmpExternal(d, out var ip)) return ip;
                    return _pmpExternal;
                }
                if (_control == null) throw new InvalidOperationException("no router discovered");
                ct0 = ct; string x = await Soap("GetExternalIPAddress").ConfigureAwait(false);
                return IPAddress.TryParse(Igd.ParseValue(x, "NewExternalIPAddress"), out var a) ? a : throw new InvalidOperationException("router returned no external address");
            }
            catch (Exception e) when (!(e is OperationCanceledException)) { Detail = "external address: " + e.Message; Tr("upnp-error", Detail); throw; }
        }

        public async Task MapAsync(int port, CancellationToken ct) => await MapCore(port, 3600, ct).ConfigureAwait(false);
        public async Task UnmapAsync(int port, CancellationToken ct)
        {
            try
            {
                if (_pmpGateway != null) { await MapCore(port, 0, ct).ConfigureAwait(false); return; }
                if (_control == null) return;
                ct0 = ct; await Soap("DeletePortMapping", ("NewRemoteHost", ""), ("NewExternalPort", port.ToString()), ("NewProtocol", "UDP")).ConfigureAwait(false);
            }
            catch (Exception e) when (!(e is OperationCanceledException)) { Detail = "unmap: " + e.Message; Tr("upnp-error", Detail); throw; }
        }

        async Task MapCore(int port, int lease, CancellationToken ct)
        {
            try
            {
                if (_pmpGateway != null)
                {
                    var d = await PmpExchange(_pmpLocal, _pmpGateway, Igd.PmpMapRequest(true, port, lease == 0 ? 0 : port, (uint)lease), 16, ct).ConfigureAwait(false);
                    if (d == null) throw new InvalidOperationException("NAT-PMP gateway did not answer the mapping request");
                    if (!Igd.TryParsePmpMap(d, true, out int result, out int ext)) throw new InvalidOperationException("malformed NAT-PMP reply");
                    if (result != 0) throw new InvalidOperationException("NAT-PMP refused the mapping (result " + result + ")");
                    if (lease != 0 && ext != port) throw new InvalidOperationException("NAT-PMP gave external port " + ext + " instead of " + port);
                    return;
                }
                if (_control == null) throw new InvalidOperationException("no router discovered");
                ct0 = ct;
                for (int l = lease; ; l = 0)
                {
                    try
                    {
                        await Soap("AddPortMapping", ("NewRemoteHost", ""), ("NewExternalPort", port.ToString()), ("NewProtocol", "UDP"), ("NewInternalPort", port.ToString()), ("NewInternalClient", _local.ToString()),
                            ("NewEnabled", "1"), ("NewPortMappingDescription", "Lycoreia"), ("NewLeaseDuration", l.ToString())).ConfigureAwait(false);
                        return;
                    }
                    catch (UpnpFault f) when (f.Code == 725 && l != 0) { Tr("upnp-error", "725 only permanent leases, retrying with 0"); }
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException)) { Detail = "port " + port + ": " + e.Message; Tr("upnp-error", Detail); throw; }
        }
    }

    static class UriExt { public static IPAddress HostToIp(this Uri u) => IPAddress.TryParse(u.Host, out var a) ? a : IPAddress.Any; }
}

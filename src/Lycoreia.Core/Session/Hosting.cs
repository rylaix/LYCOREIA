// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Lycoreia.Net;

namespace Lycoreia
{
    public static class HostAddress
    {
        /// <summary>
        /// Router mapping when it reaches the internet; otherwise the STUN-mapped endpoint with the let-in flag (the host's NAT only
        /// lets in guests it has sent to first). LAN and Steam id when known.
        /// </summary>
        public static JoinInfo Compose(AutoHost upnp, IPEndPoint stun, IPAddress lan, int localPort, ulong steamId)
        {
            var j = new JoinInfo { SteamId = steamId };
            bool routerReaches = upnp != null && upnp.MappedPort != 0 && upnp.Reach != HostReach.CarrierNat && upnp.Reach != HostReach.DoubleNat;
            if (routerReaches) j.Public = new IPEndPoint(upnp.PublicIp, upnp.MappedPort);
            else if (stun != null && NetAddr.IsPublic(stun.Address))
            {
                j.Public = stun;
                // A mapped port equal to ours usually means a manual port forward or a full-cone NAT: no let-in needed.
                // Router mapped but useless (CGNAT/double NAT), or the NAT rewrote our port: the host must punch towards each guest.
                j.LetIn = (upnp != null && upnp.MappedPort != 0) || stun.Port != localPort;
            }
            else if (upnp != null && upnp.MappedPort != 0) j.Public = new IPEndPoint(upnp.PublicIp, upnp.MappedPort);
            if (lan != null && NetAddr.Kind(lan) == "private" && lan.AddressFamily == AddressFamily.InterNetwork) j.Lan = new IPEndPoint(lan, localPort);
            return j;
        }

        /// <summary>The LAN address this PC uses towards the internet (no packet is sent).</summary>
        public static IPAddress PrimaryLan()
        {
            try { using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); s.Connect(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53)); var a = ((IPEndPoint)s.LocalEndPoint).Address; return NetAddr.Kind(a) == "private" ? a : null; }
            catch (Exception) { return null; }
        }
    }

    /// <summary>Builds and refreshes the host's join code. Created by the composition root; driven by the menu layer.</summary>
    public sealed class HostAdvertiser
    {
        public const int KeepAliveMs = 20_000;
        public string Code { get; private set; } = "";
        /// <summary>Steam-only code for friends' rich presence (no public address in it).</summary>
        public string PresenceCode { get; private set; } = "";
        public bool LetIn { get; private set; }
        public bool Pending { get; private set; }
        public bool ViaSteam { get; private set; }
        public string Summary { get; private set; } = "";
        public event Action Changed;
        /// <summary>Runs an action on the main thread (Node.Post).</summary>
        public Action<Action> Post = a => a();
        public Func<UdpTransport, Task<IPEndPoint>> StunQuery = u => Stun.QueryAsync(u);
        int _gen; Timer _keep; IPEndPoint _lastStun;

        public void Begin(AutoHost upnp, UdpTransport udp, int localPort, ulong steamId, IPAddress lan)
        {
            int gen = Interlocked.Increment(ref _gen); Stop(keepGen: true);
            Pending = true; ViaSteam = steamId != 0; Code = ""; LetIn = false;
            bool needStun = udp != null && !(upnp != null && upnp.MappedPort != 0 && upnp.Reach != HostReach.CarrierNat && upnp.Reach != HostReach.DoubleNat);
            Task.Run(async () =>
            {
                IPEndPoint stun = null;
                if (needStun) { try { stun = await StunQuery(udp).ConfigureAwait(false); } catch (Exception e) { Log.Warn("stun failed: " + e.Message); } }
                Post(() => { if (gen == _gen) Apply(upnp, stun, lan, localPort, steamId, udp, gen); });
            });
        }

        void Apply(AutoHost upnp, IPEndPoint stun, IPAddress lan, int localPort, ulong steamId, UdpTransport udp, int gen)
        {
            var j = HostAddress.Compose(upnp, stun, lan, localPort, steamId);
            Code = j.IsEmpty ? "" : JoinCode.Encode(j); LetIn = j.LetIn; Pending = false; _lastStun = stun;
            PresenceCode = steamId == 0 ? "" : JoinCode.Encode(new JoinInfo { SteamId = steamId, Lan = j.Lan });
            Summary = $"public={(j.Public == null ? "none" : NetAddr.Describe(j.Public.Address) + ":" + j.Public.Port)}; via={(upnp?.MappedPort > 0 ? "router" : stun != null ? "stun" : "none")}; let-in={j.LetIn}; lan={(j.Lan != null ? "yes" : "no")}; steam={(steamId != 0 ? "yes" : "no")}; stun={(stun == null ? "none" : NetAddr.Describe(stun.Address) + ":" + stun.Port + (stun.Port == localPort ? " (same port)" : " (port changed by NAT)"))}";
            Log.Info("join code: " + Summary);
            if (LetIn && udp != null) _keep = new Timer(_ => KeepAlive(upnp, lan, localPort, steamId, udp, gen), null, KeepAliveMs, KeepAliveMs);
            try { Changed?.Invoke(); } catch (Exception e) { Log.Error("advertiser subscriber", e); }
        }

        // The CGNAT mapping dies after ~30-120 s without traffic; a STUN request every 20 s keeps it and tells us if the port moved.
        void KeepAlive(AutoHost upnp, IPAddress lan, int localPort, ulong steamId, UdpTransport udp, int gen)
        {
            if (gen != _gen) return;
            IPEndPoint now;
            try { now = StunQuery(udp).GetAwaiter().GetResult(); } catch { return; }
            if (now == null || now.Equals(_lastStun)) return;
            Log.Warn($"the NAT moved this host's public port ({_lastStun?.Port} -> {now.Port}); the join code changed");
            Post(() => { if (gen == _gen) { _keep?.Dispose(); _keep = null; Apply(upnp, now, lan, localPort, steamId, udp, gen); } });
        }

        public void Stop(bool keepGen = false)
        {
            if (!keepGen) Interlocked.Increment(ref _gen);
            _keep?.Dispose(); _keep = null; Code = ""; PresenceCode = ""; LetIn = false; Pending = false; ViaSteam = false; _lastStun = null;
        }
    }
}

// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Lycoreia.Net
{
    public delegate void DatagramHandler(EndPoint from, byte[] buf, int offset, int length);

    /// <summary>Raw datagram pipe. The real implementation is a UDP socket; tests use an in-memory network.</summary>
    public interface IDatagramTransport : IDisposable
    {
        event DatagramHandler Received;
        event Action<SocketError, string> Faulted;
        void Send(EndPoint to, byte[] buf, int offset, int length);
        void Start();
    }

    public sealed class UdpTransport : IDatagramTransport
    {
        public const int BufferSize = 2 * 1024 * 1024;
        readonly Socket _s; readonly Thread _rx; volatile bool _stop; readonly bool _v6;
        public event DatagramHandler Received;
        public event Action<SocketError, string> Faulted;
        public EndPoint LocalEndPoint => _s.LocalEndPoint;

        /// <summary>Server: bind a port. Dual-stack on the IPv6 wildcard when ipv6 is requested, else IPv4.</summary>
        public static UdpTransport Listen(int port, bool ipv6)
        {
            var s = new Socket(ipv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (ipv6) s.DualMode = true;
            Configure(s);
            s.Bind(new IPEndPoint(ipv6 ? IPAddress.IPv6Any : IPAddress.Any, port));
            return new UdpTransport(s, ipv6);
        }
        /// <summary>Client: unbound socket of the target's family.</summary>
        public static UdpTransport Open(AddressFamily family)
        {
            if (family == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6) throw new NotSupportedException("IPv6 is not supported on this machine");
            var s = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            Configure(s);
            s.Bind(new IPEndPoint(family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
            return new UdpTransport(s, family == AddressFamily.InterNetworkV6);
        }
        static void Configure(Socket s) { s.ReceiveBufferSize = BufferSize; s.SendBufferSize = BufferSize; s.ReceiveTimeout = 0; }

        UdpTransport(Socket s, bool v6) { _s = s; _v6 = v6; _rx = new Thread(RxLoop) { IsBackground = true, Name = "Lycoreia-rx" }; }
        public void Start() { DisableConnReset(); _rx.Start(); }

        void DisableConnReset()
        {
            try { _s.IOControl((IOControlCode)(-1744830452), new byte[] { 0 }, null); } catch { } // SIO_UDP_CONNRESET off (windows only)
        }

        public void Send(EndPoint to, byte[] buf, int off, int len)
        {
            if (_stop) return;
            try
            {
                if (_v6 && to is IPEndPoint ip && ip.AddressFamily == AddressFamily.InterNetwork) to = new IPEndPoint(ip.Address.MapToIPv6(), ip.Port);
                _s.SendTo(buf, off, len, SocketFlags.None, to);
            }
            catch (SocketException e) { if (!_stop) Faulted?.Invoke(e.SocketErrorCode, e.Message); }
            catch (ObjectDisposedException) { }
        }

        void RxLoop()
        {
            var buf = new byte[65536];
            EndPoint any = new IPEndPoint(_v6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
            while (!_stop)
            {
                try
                {
                    EndPoint from = any;
                    int n = _s.ReceiveFrom(buf, ref from);
                    if (from is IPEndPoint ip && ip.Address.IsIPv4MappedToIPv6) from = new IPEndPoint(ip.Address.MapToIPv4(), ip.Port);
                    Received?.Invoke(from, buf, 0, n);
                }
                catch (SocketException e)
                {
                    if (_stop) return;
                    int code = (int)e.SocketErrorCode;
                    if (code == 10054) { Faulted?.Invoke(e.SocketErrorCode, "no server is listening"); continue; } // ICMP unreachable
                    Faulted?.Invoke(e.SocketErrorCode, e.Message); if (code == 10004) return;
                }
                catch (ObjectDisposedException) { return; }
                catch (Exception e) { Log.Error("receive loop", e); }
            }
        }

        public void Dispose()
        {
            _stop = true;
            try { _s.Close(); } catch { }
        }
    }
}

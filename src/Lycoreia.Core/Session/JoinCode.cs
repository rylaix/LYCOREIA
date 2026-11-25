// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace Lycoreia
{
    /// <summary>Everything a guest needs to reach a host. Any part may be missing.</summary>
    public sealed class JoinInfo
    {
        /// <summary>Address friends on the internet use (router mapping or, behind CGNAT, the STUN-mapped port).</summary>
        public IPEndPoint Public;
        /// <summary>Host's address inside its home network (friends behind the same router).</summary>
        public IPEndPoint Lan;
        /// <summary>Host's Steam id: Steam relays the traffic, no router setup at all. 0 = none.</summary>
        public ulong SteamId;
        /// <summary>The host's NAT drops unsolicited packets: the guest must send the host a let-in code (UDP hole punching).</summary>
        public bool LetIn;
        public bool IsEmpty => Public == null && Lan == null && SteamId == 0;
    }

    /// <summary>
    /// Text codes players paste. Crockford base 32, grouped by 5.
    /// v3 (first byte 0x33): flags byte, then tagged fields: 1 public v4 (ip4 port2), 2 public v6 (ip16 port2), 3 LAN v4 (ip4 port2), 4 Steam id (8).
    /// Legacy (first byte 4/6, up to rc4.2): public ip + port [+ LAN ip4 using the same port]. Let-in codes: 0x34, ip4, port2.
    /// </summary>
    public static class JoinCode
    {
        const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        public const byte V3 = 0x33, LetInType = 0x34;
        const byte FlagLetIn = 1, TagPublic4 = 1, TagPublic6 = 2, TagLan4 = 3, TagSteam = 4;

        public static string Encode(JoinInfo j)
        {
            var d = new List<byte> { V3, (byte)(j.LetIn ? FlagLetIn : 0) };
            if (j.Public != null)
            {
                var a = j.Public.Address.IsIPv4MappedToIPv6 ? j.Public.Address.MapToIPv4() : j.Public.Address;
                d.Add(a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? TagPublic4 : TagPublic6); d.AddRange(a.GetAddressBytes()); Port(d, j.Public.Port);
            }
            if (j.Lan != null && j.Lan.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) { d.Add(TagLan4); d.AddRange(j.Lan.Address.GetAddressBytes()); Port(d, j.Lan.Port); }
            if (j.SteamId != 0) { d.Add(TagSteam); for (int i = 7; i >= 0; i--) d.Add((byte)(j.SteamId >> (8 * i))); }
            return ToText(d.ToArray());
        }

        public static bool TryDecode(string code, out JoinInfo j)
        {
            j = null; var b = FromText(code); if (b == null || b.Count < 2) return false;
            if (b[0] == 4 || b[0] == 6) return TryDecodeLegacy(b, out j);
            if (b[0] != V3) return false;
            var r = new JoinInfo { LetIn = (b[1] & FlagLetIn) != 0 }; int i = 2;
            while (i < b.Count)
            {
                byte tag = b[i++];
                int need = tag switch { TagPublic4 => 6, TagPublic6 => 18, TagLan4 => 6, TagSteam => 8, _ => -1 };
                if (need < 0) break;                              // unknown tag or trailing pad bits: stop, keep what we have
                if (i + need > b.Count) return false;
                switch (tag)
                {
                    case TagPublic4: r.Public = Ep(b, i, 4); break;
                    case TagPublic6: r.Public = Ep(b, i, 16); break;
                    case TagLan4: { var e = Ep(b, i, 4); if (NetAddr.Kind(e.Address) == "private") r.Lan = e; break; }
                    case TagSteam: { ulong s = 0; for (int k = 0; k < 8; k++) s = (s << 8) | b[i + k]; r.SteamId = s; break; }
                }
                i += need;
            }
            if (r.Public != null && r.Public.Port == 0) r.Public = null;
            if (r.IsEmpty) return false;
            j = r; return true;
        }

        static bool TryDecodeLegacy(List<byte> b, out JoinInfo j)
        {
            j = null; int len = b[0] == 4 ? 4 : 16;
            if (b.Count < 1 + len + 2) return false;
            int port = (b[1 + len] << 8) | b[2 + len]; if (port <= 0) return false;
            j = new JoinInfo { Public = new IPEndPoint(new IPAddress(b.GetRange(1, len).ToArray()), port) };
            if (b.Count >= 3 + len + 4) { var l = new IPAddress(b.GetRange(3 + len, 4).ToArray()); if (NetAddr.Kind(l) == "private") j.Lan = new IPEndPoint(l, port); }
            return true;
        }

        public static string EncodeLetIn(IPEndPoint ep)
        {
            var a = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
            if (a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) throw new ArgumentException("IPv4 only");
            var d = new List<byte> { LetInType }; d.AddRange(a.GetAddressBytes()); Port(d, ep.Port); return ToText(d.ToArray());
        }
        public static bool TryDecodeLetIn(string code, out IPEndPoint ep)
        {
            ep = null; var b = FromText(code); if (b == null || b.Count < 7 || b[0] != LetInType) return false;
            var e = Ep(b, 1, 4); if (e.Port == 0 || !NetAddr.IsPublic(e.Address)) return false;
            ep = e; return true;
        }

        static void Port(List<byte> d, int port) { d.Add((byte)(port >> 8)); d.Add((byte)port); }
        static IPEndPoint Ep(List<byte> b, int i, int len) => new IPEndPoint(new IPAddress(b.GetRange(i, len).ToArray()), (b[i + len] << 8) | b[i + len + 1]);

        static string ToText(byte[] data)
        {
            var sb = new StringBuilder(); int bits = 0, acc = 0;
            foreach (byte x in data) { acc = (acc << 8) | x; bits += 8; while (bits >= 5) { sb.Append(Alphabet[(acc >> (bits - 5)) & 31]); bits -= 5; } }
            if (bits > 0) sb.Append(Alphabet[(acc << (5 - bits)) & 31]);
            string s = sb.ToString(); return string.Join("-", Enumerable.Range(0, (s.Length + 4) / 5).Select(i => s.Substring(i * 5, Math.Min(5, s.Length - i * 5))));
        }
        static List<byte> FromText(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            var clean = code.Replace("-", "").Replace(" ", "").Trim().ToUpperInvariant()
                .Replace('O', '0').Replace('I', '1').Replace('L', '1');   // Crockford: common misreadings
            var bytes = new List<byte>(); int bits = 0, acc = 0;
            foreach (char ch in clean) { int v = Alphabet.IndexOf(ch); if (v < 0) return null; acc = ((acc << 5) | v) & 0xFFFF; bits += 5; if (bits >= 8) { bytes.Add((byte)((acc >> (bits - 8)) & 255)); bits -= 8; } }
            return bytes;
        }
    }
}

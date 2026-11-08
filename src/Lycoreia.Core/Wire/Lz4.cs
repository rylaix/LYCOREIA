// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;

namespace Lycoreia.Net
{
    /// <summary>Minimal LZ4 block compressor/decompressor (greedy, single hash table). Not a frame format.</summary>
    public static class Lz4
    {
        const int MinMatch = 4, HashBits = 13;
        [ThreadStatic] static int[] _table;

        public static int MaxCompressedSize(int n) => n + n / 255 + 16;

        /// <summary>Returns compressed length, or -1 if dst is too small.</summary>
        public static int Compress(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            int n = src.Length, ip = 0, op = 0, anchor = 0;
            var table = _table ??= new int[1 << HashBits];
            Array.Clear(table, 0, table.Length);
            if (n >= 13)
            {
                int limit = n - 12; // last match must start before this
                while (ip < limit)
                {
                    uint seq = BitConverter.ToUInt32(src.Slice(ip, 4));
                    int h = (int)((seq * 2654435761u) >> (32 - HashBits));
                    int cand = table[h] - 1; table[h] = ip + 1;
                    if (cand >= 0 && ip - cand < 65536 && BitConverter.ToUInt32(src.Slice(cand, 4)) == seq)
                    {
                        int mlen = MinMatch;
                        int maxLen = n - 5 - ip;
                        while (mlen < maxLen && src[cand + mlen] == src[ip + mlen]) mlen++;
                        int lit = ip - anchor;
                        if (!Emit(dst, ref op, src, anchor, lit, ip - cand, mlen)) return -1;
                        ip += mlen; anchor = ip;
                    }
                    else ip++;
                }
            }
            // final literals
            int rest = n - anchor;
            if (op + 1 + rest + rest / 255 + 1 > dst.Length) return -1;
            int tok = Math.Min(rest, 15) << 4;
            dst[op++] = (byte)tok;
            if (rest >= 15) { int r = rest - 15; while (r >= 255) { dst[op++] = 255; r -= 255; } dst[op++] = (byte)r; }
            src.Slice(anchor, rest).CopyTo(dst.Slice(op)); op += rest;
            return op;
        }

        static bool Emit(Span<byte> dst, ref int op, ReadOnlySpan<byte> src, int anchor, int lit, int offset, int mlen)
        {
            if (op + 1 + lit + lit / 255 + 1 + 2 + (mlen / 255) + 2 > dst.Length) return false;
            int ml = mlen - MinMatch;
            dst[op++] = (byte)((Math.Min(lit, 15) << 4) | Math.Min(ml, 15));
            if (lit >= 15) { int r = lit - 15; while (r >= 255) { dst[op++] = 255; r -= 255; } dst[op++] = (byte)r; }
            src.Slice(anchor, lit).CopyTo(dst.Slice(op)); op += lit;
            dst[op++] = (byte)offset; dst[op++] = (byte)(offset >> 8);
            if (ml >= 15) { int r = ml - 15; while (r >= 255) { dst[op++] = 255; r -= 255; } dst[op++] = (byte)r; }
            return true;
        }

        /// <summary>Decompresses into dst, which must be exactly the original size. Throws WireException on bad data.</summary>
        public static void Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            int ip = 0, op = 0;
            while (true)
            {
                if (ip >= src.Length) throw new WireException("lz4 truncated");
                int tok = src[ip++];
                int lit = tok >> 4;
                if (lit == 15) { int b; do { if (ip >= src.Length) throw new WireException("lz4 truncated"); b = src[ip++]; lit += b; if (lit > dst.Length) throw new WireException("lz4 overflow"); } while (b == 255); }
                if (lit > src.Length - ip || lit > dst.Length - op) throw new WireException("lz4 literal overflow");
                src.Slice(ip, lit).CopyTo(dst.Slice(op)); ip += lit; op += lit;
                if (ip == src.Length) break;
                if (ip + 2 > src.Length) throw new WireException("lz4 truncated");
                int off = src[ip] | (src[ip + 1] << 8); ip += 2;
                if (off == 0 || off > op) throw new WireException("lz4 bad offset");
                int ml = tok & 15;
                if (ml == 15) { int b; do { if (ip >= src.Length) throw new WireException("lz4 truncated"); b = src[ip++]; ml += b; if (ml > dst.Length) throw new WireException("lz4 overflow"); } while (b == 255); }
                ml += MinMatch;
                if (ml > dst.Length - op) throw new WireException("lz4 match overflow");
                for (int i = 0; i < ml; i++) { dst[op] = dst[op - off]; op++; }
            }
            if (op != dst.Length) throw new WireException("lz4 size mismatch");
        }
    }

    public static class Crc32
    {
        static readonly uint[] T = Build();
        static uint[] Build() { var t = new uint[256]; for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[i] = c; } return t; }
        public static uint Compute(ReadOnlySpan<byte> data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte b in data) c = T[(c ^ b) & 0xFF] ^ (c >> 8);
            return ~c;
        }
    }
}

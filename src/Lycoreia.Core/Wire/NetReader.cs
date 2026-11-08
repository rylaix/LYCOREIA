// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;

namespace Lycoreia.Net
{
    /// <summary>Bounds-checked reader over a byte slice. Every failure is a WireException.</summary>
    public sealed class NetReader
    {
        static readonly ConcurrentBag<NetReader> Bag = new ConcurrentBag<NetReader>();
        public static NetReader Rent(byte[] data, int offset, int length) { if (!Bag.TryTake(out var r)) r = new NetReader(); r.Init(data, offset, length); return r; }
        public void Return() { _data = null; if (Bag.Count < 64) Bag.Add(this); }

        byte[] _data; int _pos, _end; int _flags, _flagLeft;
        public void Init(byte[] d, int off, int len) { _data = d; _pos = off; _end = off + len; _flagLeft = 0; }
        public int Remaining => _end - _pos;
        public bool AtEnd => _pos >= _end;

        ReadOnlySpan<byte> Take(int n)
        {
            _flagLeft = 0;
            if (n < 0 || n > _end - _pos) throw new WireException("read past end");
            var s = new ReadOnlySpan<byte>(_data, _pos, n); _pos += n; return s;
        }
        public bool Flag()
        {
            if (_flagLeft == 0)
            {
                if (_pos >= _end) throw new WireException("read past end");
                _flags = _data[_pos++]; _flagLeft = 8;
            }
            bool v = (_flags & 1) != 0; _flags >>= 1; _flagLeft--; return v;
        }
        public byte U8() => Take(1)[0];
        public bool Bool() { byte b = U8(); if (b > 1) throw new WireException("bad bool"); return b == 1; }
        public sbyte I8() => (sbyte)U8();
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public short I16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        public float F32() => BitConverter.Int32BitsToSingle(I32());
        public double F64() => BitConverter.Int64BitsToDouble(I64());
        public float F16() => (float)BitConverter.UInt16BitsToHalf(U16());

        /// <summary>Varint of at most maxBytes bytes; longer or non-terminating encodings are rejected.</summary>
        public ulong VarU(int maxBytes = 10)
        {
            _flagLeft = 0;
            ulong v = 0; int shift = 0;
            for (int i = 0; i < maxBytes; i++)
            {
                if (_pos >= _end) throw new WireException("read past end");
                byte b = _data[_pos++];
                if (i == 9 && b > 1) throw new WireException("varint overflow");
                v |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    if (i > 0 && b == 0) throw new WireException("overlong varint");
                    return v;
                }
                shift += 7;
            }
            throw new WireException("varint too long");
        }
        public long Var() { ulong u = VarU(10); return (long)(u >> 1) ^ -(long)(u & 1); }
        public uint VarU32() { ulong u = VarU(5); if (u > uint.MaxValue) throw new WireException("varint exceeds 32 bits"); return (uint)u; }
        public int Var32() { uint u = VarU32(); return (int)(u >> 1) ^ -(int)(u & 1); }

        public string Str(int max = 4096)
        {
            ulong n = VarU(5);
            if (n == 0) return string.Empty;
            if (n > (ulong)max) throw new WireException($"string length {n} exceeds limit {max}");
            var bytes = Take((int)n);
            return StringCache.Get(bytes);
        }
        public Guid Guid() => new Guid(Take(16));
        /// <summary>Reads a collection count, rejecting counts that cannot fit in the remaining bytes.</summary>
        public int Count(int minItemBytes = 1)
        {
            ulong n = VarU(5);
            if (minItemBytes < 1) minItemBytes = 1;
            if (n > (ulong)(Remaining / minItemBytes)) throw new WireException($"count {n} exceeds remaining {Remaining} bytes");
            return (int)n;
        }
        public ReadOnlySpan<byte> Raw(int n) => Take(n);
        public Vec2 V2() => new Vec2(F32(), F32());
        public Vec3 V3() => new Vec3(F32(), F32(), F32());
        public Vec4 V4() => new Vec4(F32(), F32(), F32(), F32());
        public Quat Q() => new Quat(F32(), F32(), F32(), F32());
        public Col ColF() => new Col(F32(), F32(), F32(), F32());
        public Col Col8() => new Col(U8() / 255f, U8() / 255f, U8() / 255f, U8() / 255f);
        public Quat QCompact()
        {
            int big = U8(); if (big > 3) throw new WireException("bad quaternion index");
            float a = 0, b = 0, c = 0, d = 0, sum = 0;
            for (int i = 0; i < 4; i++)
            {
                if (i == big) continue;
                float v = I16() / 32767f * 0.70710678f; sum += v * v;
                if (i == 0) a = v; else if (i == 1) b = v; else if (i == 2) c = v; else d = v;
            }
            float m = MathF.Sqrt(MathF.Max(0, 1 - sum));
            if (big == 0) a = m; else if (big == 1) b = m; else if (big == 2) c = m; else d = m;
            return new Quat(a, b, c, d);
        }
    }

    /// <summary>Small thread-local cache so repeated short strings (player ids, reference ids) do not allocate on read.</summary>
    static class StringCache
    {
        [ThreadStatic] static string[] _slots;
        public static string Get(ReadOnlySpan<byte> utf8)
        {
            if (utf8.Length > 64) return Decode(utf8);
            _slots ??= new string[512];
            uint h = 2166136261;
            foreach (byte b in utf8) h = (h ^ b) * 16777619;
            ref string slot = ref _slots[h & 511];
            if (slot != null && Matches(slot, utf8)) return slot;
            return slot = Decode(utf8);
        }
        static bool Matches(string s, ReadOnlySpan<byte> u)
        {
            if (s.Length > u.Length) return false;
            for (int i = 0; i < s.Length; i++) if (s[i] != u[i] || u[i] >= 0x80) return false;
            return s.Length == u.Length;
        }
        static readonly UTF8Encoding Strict = new UTF8Encoding(false, true);
        static string Decode(ReadOnlySpan<byte> u)
        {
            try { return Strict.GetString(u); }
            catch (ArgumentException) { throw new WireException("invalid UTF-8"); }
        }
    }
}

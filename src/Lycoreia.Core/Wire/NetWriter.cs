// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;

namespace Lycoreia.Net
{
    /// <summary>Thrown by readers on malformed input. Caught by the dispatcher, never by game code.</summary>
    public sealed class WireException : Exception { public WireException(string m) : base(m) { } }

    /// <summary>Growable pooled output buffer with little-endian primitives, zig-zag varints and packed flags.</summary>
    public sealed class NetWriter
    {
        static readonly ConcurrentBag<NetWriter> Bag = new ConcurrentBag<NetWriter>();
        public static NetWriter Rent() { if (!Bag.TryTake(out var w)) w = new NetWriter(); w.Reset(); return w; }
        public void Return() { if (Bag.Count < 64) Bag.Add(this); }

        byte[] _buf = new byte[2048];
        int _len;
        int _flagPos = -1, _flagBits;
        public int Length => _len;
        public byte[] Buffer => _buf;
        public ReadOnlySpan<byte> Span => new ReadOnlySpan<byte>(_buf, 0, _len);

        public void Reset() { _len = 0; _flagPos = -1; _flagBits = 0; }

        void Ensure(int n)
        {
            if (_len + n <= _buf.Length) return;
            int cap = Math.Max(_buf.Length * 2, _len + n);
            Array.Resize(ref _buf, cap);
        }
        Span<byte> Take(int n) { EndFlags(); Ensure(n); var s = new Span<byte>(_buf, _len, n); _len += n; return s; }

        /// <summary>Ends the current packed-flag group. Any non-flag write does this implicitly.</summary>
        public void EndFlags() { _flagPos = -1; _flagBits = 0; }

        public void Flag(bool v)
        {
            if (_flagPos < 0 || _flagBits == 8)
            {
                Ensure(1); _flagPos = _len; _buf[_len++] = 0; _flagBits = 0;
            }
            if (v) _buf[_flagPos] |= (byte)(1 << _flagBits);
            _flagBits++;
        }
        public void U8(byte v) { Take(1)[0] = v; }
        public void Bool(bool v) { Take(1)[0] = v ? (byte)1 : (byte)0; }
        public void I8(sbyte v) { Take(1)[0] = (byte)v; }
        public void U16(ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), v);
        public void I16(short v) => BinaryPrimitives.WriteInt16LittleEndian(Take(2), v);
        public void U32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), v);
        public void I32(int v) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), v);
        public void U64(ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(Take(8), v);
        public void I64(long v) => BinaryPrimitives.WriteInt64LittleEndian(Take(8), v);
        public void F32(float v) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), BitConverter.SingleToInt32Bits(v));
        public void F64(double v) => BinaryPrimitives.WriteInt64LittleEndian(Take(8), BitConverter.DoubleToInt64Bits(v));
        public void F16(float v) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), BitConverter.HalfToUInt16Bits((Half)v));

        public void VarU(ulong v)
        {
            EndFlags(); Ensure(10);
            while (v >= 0x80) { _buf[_len++] = (byte)(v | 0x80); v >>= 7; }
            _buf[_len++] = (byte)v;
        }
        public void Var(long v) => VarU(((ulong)v << 1) ^ (ulong)(v >> 63));
        public void Var32(int v) => VarU(((uint)v << 1) ^ (uint)(v >> 31));

        public void Raw(ReadOnlySpan<byte> src) { src.CopyTo(Take(src.Length)); }

        public void Str(string s, int max = 4096)
        {
            if (string.IsNullOrEmpty(s)) { VarU(0); return; }
            int n = Encoding.UTF8.GetByteCount(s);
            if (n > max) throw new WireException($"string of {n} bytes exceeds limit {max}");
            VarU((ulong)n);
            Encoding.UTF8.GetBytes(s, Take(n));
        }
        public void Guid(Guid g) { g.TryWriteBytes(Take(16)); }
        public void Count(int n) => VarU((ulong)n);
        public void V2(Vec2 v) { F32(v.X); F32(v.Y); }
        public void V3(Vec3 v) { F32(v.X); F32(v.Y); F32(v.Z); }
        public void V4(Vec4 v) { F32(v.X); F32(v.Y); F32(v.Z); F32(v.W); }
        public void Q(Quat q) { F32(q.X); F32(q.Y); F32(q.Z); F32(q.W); }
        public void ColF(Col c) { F32(c.R); F32(c.G); F32(c.B); F32(c.A); }
        public void Col8(Col c) { U8(To8(c.R)); U8(To8(c.G)); U8(To8(c.B)); U8(To8(c.A)); }
        static byte To8(float f) => (byte)Math.Clamp((int)MathF.Round(f * 255f), 0, 255);

        /// <summary>Smallest-three quaternion: one index byte plus three 16-bit components.</summary>
        public void QCompact(Quat q)
        {
            float a = q.X, b = q.Y, c = q.Z, d = q.W;
            float n = MathF.Sqrt(a * a + b * b + c * c + d * d);
            if (n < 1e-6f) { a = b = c = 0; d = 1; } else { a /= n; b /= n; c /= n; d /= n; }
            int big = 0; float bv = MathF.Abs(a);
            if (MathF.Abs(b) > bv) { big = 1; bv = MathF.Abs(b); }
            if (MathF.Abs(c) > bv) { big = 2; bv = MathF.Abs(c); }
            if (MathF.Abs(d) > bv) { big = 3; bv = MathF.Abs(d); }
            float sign = (big == 0 ? a : big == 1 ? b : big == 2 ? c : d) < 0 ? -1f : 1f;
            U8((byte)big);
            if (big != 0) I16(Quant(a * sign));
            if (big != 1) I16(Quant(b * sign));
            if (big != 2) I16(Quant(c * sign));
            if (big != 3) I16(Quant(d * sign));
        }
        const float QMax = 0.70710678f;
        static short Quant(float v) => (short)Math.Clamp((int)MathF.Round(v / QMax * 32767f), -32767, 32767);
    }
}

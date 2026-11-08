// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Text;
using Lycoreia.Net;

namespace Lycoreia.Tests
{
    static class WireTests
    {
        static NetReader RR(NetWriter w) => NetReader.Rent(w.Buffer, 0, w.Length);

        public static void Register()
        {
            T.Test("wire/numbers round-trip", () =>
            {
                var w = new NetWriter();
                w.U8(255); w.I8(-5); w.U16(65535); w.I16(-32768); w.U32(uint.MaxValue); w.I32(int.MinValue); w.U64(ulong.MaxValue); w.I64(long.MinValue);
                w.F32(1.5f); w.F64(-2.25); w.F16(0.5f); w.Bool(true); w.Guid(Guid.Parse("11111111-2222-3333-4444-555555555555"));
                var r = RR(w);
                T.Eq((byte)255, r.U8()); T.Eq((sbyte)-5, r.I8()); T.Eq((ushort)65535, r.U16()); T.Eq((short)-32768, r.I16());
                T.Eq(uint.MaxValue, r.U32()); T.Eq(int.MinValue, r.I32()); T.Eq(ulong.MaxValue, r.U64()); T.Eq(long.MinValue, r.I64());
                T.Eq(1.5f, r.F32()); T.Eq(-2.25, r.F64()); T.Eq(0.5f, r.F16()); T.True(r.Bool());
                T.Eq(Guid.Parse("11111111-2222-3333-4444-555555555555"), r.Guid()); T.True(r.AtEnd);
            });
            T.Test("wire/little endian layout", () =>
            {
                var w = new NetWriter(); w.U32(0x01020304);
                T.Eq((byte)4, w.Buffer[0]); T.Eq((byte)1, w.Buffer[3]);
            });
            T.Test("wire/varint edge cases", () =>
            {
                foreach (long v in new long[] { 0, 1, -1, 63, -64, 64, 127, 128, 16383, 16384, int.MaxValue, int.MinValue, long.MaxValue, long.MinValue })
                { var w = new NetWriter(); w.Var(v); T.Eq(v, RR(w).Var(), "signed " + v); }
                foreach (ulong v in new ulong[] { 0, 127, 128, ulong.MaxValue })
                { var w = new NetWriter(); w.VarU(v); T.Eq(v, RR(w).VarU(), "unsigned " + v); }
                var w2 = new NetWriter(); w2.Var(-1); T.Eq(1, w2.Length); // small negatives stay small
                var w3 = new NetWriter(); w3.Var32(int.MinValue); T.Eq(int.MinValue, RR(w3).Var32());
            });
            T.Test("wire/varint overlong and truncated rejected", () =>
            {
                T.Throws<WireException>(() => NetReader.Rent(new byte[] { 0x80, 0x00 }, 0, 2).VarU(), "overlong");
                T.Throws<WireException>(() => NetReader.Rent(new byte[] { 0x80 }, 0, 1).VarU(), "truncated");
                T.Throws<WireException>(() => NetReader.Rent(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F }, 0, 6).VarU32(), "too big for 32");
                T.Throws<WireException>(() => NetReader.Rent(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F }, 0, 10).VarU(), "overflow 64");
                var ok = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 };
                T.Eq(ulong.MaxValue, NetReader.Rent(ok, 0, 10).VarU());
            });
            T.Test("wire/packed flags share bytes", () =>
            {
                var w = new NetWriter();
                for (int i = 0; i < 10; i++) w.Flag(i % 3 == 0);
                T.Eq(2, w.Length);
                w.U8(7); w.Flag(true); w.Flag(false);
                T.Eq(4, w.Length);
                var r = RR(w);
                for (int i = 0; i < 10; i++) T.Eq(i % 3 == 0, r.Flag(), "flag " + i);
                T.Eq((byte)7, r.U8()); T.True(r.Flag()); T.True(!r.Flag()); T.True(r.AtEnd);
            });
            T.Test("wire/explicit end flags", () =>
            {
                var w = new NetWriter(); w.Flag(true); w.EndFlags(); w.Flag(true);
                T.Eq(2, w.Length);
            });
            T.Test("wire/strings", () =>
            {
                var w = new NetWriter(); w.Str("héllo wörld ✓"); w.Str(null); w.Str("");
                var r = RR(w); T.Eq("héllo wörld ✓", r.Str()); T.Eq("", r.Str()); T.Eq("", r.Str()); T.True(r.AtEnd);
            });
            T.Test("wire/string limits", () =>
            {
                var w = new NetWriter(); w.Str(new string('a', 4096));
                T.Eq(new string('a', 4096), RR(w).Str());
                T.Throws<WireException>(() => new NetWriter().Str(new string('a', 4097)), "write over default");
                var big = new NetWriter(); big.VarU(5000); big.Raw(new byte[5000]);
                T.Throws<WireException>(() => RR(big).Str(), "read over default");
                T.Throws<WireException>(() => RR(w).Str(100), "per-field max");
                var bad = new NetWriter(); bad.VarU(2); bad.U8(0xC3); bad.U8(0x28);
                T.Throws<WireException>(() => RR(bad).Str(), "invalid utf-8");
                var trunc = new NetWriter(); trunc.VarU(10); trunc.U8(1);
                T.Throws<WireException>(() => RR(trunc).Str(), "truncated string");
            });
            T.Test("wire/vectors quaternions colours", () =>
            {
                var w = new NetWriter();
                w.V2(new Vec2(1, 2)); w.V3(new Vec3(1, 2, 3)); w.V4(new Vec4(1, 2, 3, 4)); w.Q(new Quat(.1f, .2f, .3f, .9f));
                w.ColF(new Col(.1f, .2f, .3f, 1)); w.Col8(new Col(1, 0.5f, 0, 1));
                var r = RR(w);
                T.Eq(2f, r.V2().Y); T.Eq(3f, r.V3().Z); T.Eq(4f, r.V4().W); T.Eq(.9f, r.Q().W); T.Eq(.2f, r.ColF().G);
                var c = r.Col8(); T.True(Math.Abs(c.G - 0.5f) < 0.01f); T.Eq(1f, c.R);
            });
            T.Test("wire/compact quaternion", () =>
            {
                var rng = new Random(3);
                for (int i = 0; i < 500; i++)
                {
                    float x = (float)rng.NextDouble() * 2 - 1, y = (float)rng.NextDouble() * 2 - 1, z = (float)rng.NextDouble() * 2 - 1, wv = (float)rng.NextDouble() * 2 - 1;
                    float n = MathF.Sqrt(x * x + y * y + z * z + wv * wv); x /= n; y /= n; z /= n; wv /= n;
                    var w = new NetWriter(); w.QCompact(new Quat(x, y, z, wv)); T.Eq(7, w.Length);
                    var q = RR(w).QCompact();
                    float dot = Math.Abs(q.X * x + q.Y * y + q.Z * z + q.W * wv);
                    T.True(dot > 0.9995f, "dot " + dot);
                }
                var id = new NetWriter(); id.QCompact(Quat.Identity); var qi = RR(id).QCompact(); T.True(Math.Abs(qi.W) > 0.999f);
            });
            T.Test("wire/collection count attacks", () =>
            {
                var w = new NetWriter(); w.VarU(100000); w.U8(1);
                T.Throws<WireException>(() => RR(w).Count(), "count over remaining");
                var d = new NetWriter(); d.VarU(1_000_000_000);
                var c = new Codec(RR(d)); List<int> l = null;
                T.Throws<WireException>(() => c.List(ref l, Items.Var32), "list attack");
                var d2 = new NetWriter(); d2.VarU(20); d2.Raw(new byte[39]);
                T.Throws<WireException>(() => RR(d2).Count(2), "min item size");
            });
            T.Test("wire/list dict optional enum round-trip", () =>
            {
                var w = new NetWriter(); var c = new Codec(w);
                var list = new List<string> { "a", "bb", "" }; var dict = new Dictionary<int, long> { [1] = -5, [300] = 1L << 40 };
                var val = Vec3Of(1, 2, 3); bool has = true; var none = Vec3Of(0, 0, 0); bool hasNone = false; var mode = Delivery.ReliableOrdered;
                c.List(ref list, Items.Str); c.Dict(ref dict, Items.Var32, Items.Var64); c.Opt(ref val, ref has, Items.V3); c.Opt(ref none, ref hasNone, Items.V3); c.Enum(ref mode);
                var rc = new Codec(RR(w)); List<string> l2 = null; Dictionary<int, long> d2 = null; Vec3 v2 = default; bool h2 = false, h3 = true; Vec3 n2 = Vec3Of(9, 9, 9); Delivery m2 = 0;
                rc.List(ref l2, Items.Str); rc.Dict(ref d2, Items.Var32, Items.Var64); rc.Opt(ref v2, ref h2, Items.V3); rc.Opt(ref n2, ref h3, Items.V3); rc.Enum(ref m2);
                T.Eq(3, l2.Count); T.Eq("bb", l2[1]); T.Eq(1L << 40, d2[300]); T.True(h2); T.Eq(3f, v2.Z); T.True(!h3); T.Eq(0f, n2.X); T.Eq(Delivery.ReliableOrdered, m2);
            });
            T.Test("lz4/round trip and incompressible", () =>
            {
                var rng = new Random(5);
                foreach (int n in new[] { 0, 1, 12, 13, 100, 1000, 70000 })
                {
                    var src = new byte[n]; for (int i = 0; i < n; i++) src[i] = (byte)(rng.Next(4) * (i % 7)); // compressible
                    var dst = new byte[Lz4.MaxCompressedSize(n)]; int len = Lz4.Compress(src, dst);
                    T.True(len > 0, "compress " + n);
                    var back = new byte[n]; Lz4.Decompress(new ReadOnlySpan<byte>(dst, 0, len), back); T.SeqEq(src, back, "lz4 " + n);
                }
                var noise = new byte[5000]; rng.NextBytes(noise); var d2 = new byte[Lz4.MaxCompressedSize(5000)];
                int l2 = Lz4.Compress(noise, d2); var b2 = new byte[5000]; Lz4.Decompress(new ReadOnlySpan<byte>(d2, 0, l2), b2); T.SeqEq(noise, b2);
            });
            T.Test("lz4/corrupt input rejected", () =>
            {
                T.Throws<WireException>(() => Lz4.Decompress(new byte[] { 0x0F, 0x01, 0x00 }, new byte[10]), "offset beyond output");
                T.Throws<WireException>(() => Lz4.Decompress(new byte[] { 0xF0 }, new byte[10]), "truncated");
                T.Throws<WireException>(() => Lz4.Decompress(new byte[] { 0x50, 1, 2, 3, 4, 5 }, new byte[3]), "literal overflow");
            });
            T.Test("frame/header round trip and checksum", () =>
            {
                var buf = new byte[1300]; var payload = new byte[] { 1, 2, 3, 4, 5 };
                int len = Frame.WriteData(buf, 3, Frame.FReliable | Frame.FOrdered | Frame.FSplit, 777, 321, 54321, 99, 4, 9, payload);
                T.Eq(Frame.ParseResult.Ok, Frame.Parse(buf, 0, len, out var h));
                T.Eq((ushort)321, h.Type); T.Eq((ushort)54321, h.MsgId); T.Eq(99u, h.Seq); T.Eq((ushort)4, h.Part); T.Eq((ushort)9, h.Count);
                T.Eq((byte)3, h.Lane); T.Eq((ushort)777, h.Epoch); T.True(h.Reliable && h.Ordered && h.Split && !h.Compressed); T.Eq(5, h.PayloadLength);
                for (int i = 0; i < len; i++) { var c = (byte[])buf.Clone(); c[i] ^= 0x10; var res = Frame.Parse(c, 0, len, out _); T.True(res != Frame.ParseResult.Ok, "corruption at " + i + " accepted"); }
                T.Eq(Frame.ParseResult.TooShort, Frame.Parse(buf, 0, 3, out _));
                var bad = (byte[])buf.Clone(); bad[0] = 0; T.Eq(Frame.ParseResult.BadMagic, Frame.Parse(bad, 0, len, out _));
            });
        }
        static Vec3 Vec3Of(float x, float y, float z) => new Vec3(x, y, z);
    }
}

// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;

namespace Lycoreia.Net
{
    /// <summary>A composite value that describes its own layout once, for both directions.</summary>
    public interface IWire { void Describe(Codec c); }

    public delegate void ItemFn<T>(Codec c, ref T item);

    /// <summary>
    /// Symmetric field visitor. A message lists its fields once in Describe; the same call writes or reads
    /// depending on the mode, so layouts cannot drift apart and there is no per-message serialiser.
    /// </summary>
    public sealed class Codec
    {
        public NetWriter W; public NetReader R;
        public bool Reading => R != null;
        public Codec() { }
        public Codec(NetWriter w) { W = w; }
        public Codec(NetReader r) { R = r; }

        public void Flag(ref bool v) { if (R != null) v = R.Flag(); else W.Flag(v); }
        public void Bool(ref bool v) { if (R != null) v = R.Bool(); else W.Bool(v); }
        public void U8(ref byte v) { if (R != null) v = R.U8(); else W.U8(v); }
        public void I8(ref sbyte v) { if (R != null) v = R.I8(); else W.I8(v); }
        public void U16(ref ushort v) { if (R != null) v = R.U16(); else W.U16(v); }
        public void I32(ref int v) { if (R != null) v = R.I32(); else W.I32(v); }
        public void U32(ref uint v) { if (R != null) v = R.U32(); else W.U32(v); }
        public void I64(ref long v) { if (R != null) v = R.I64(); else W.I64(v); }
        public void U64(ref ulong v) { if (R != null) v = R.U64(); else W.U64(v); }
        public void F32(ref float v) { if (R != null) v = R.F32(); else W.F32(v); }
        public void F16(ref float v) { if (R != null) v = R.F16(); else W.F16(v); }
        public void F64(ref double v) { if (R != null) v = R.F64(); else W.F64(v); }
        /// <summary>Zig-zag varint, 32-bit.</summary>
        public void Var(ref int v) { if (R != null) v = R.Var32(); else W.Var32(v); }
        public void Var(ref long v) { if (R != null) v = R.Var(); else W.Var(v); }
        public void VarU(ref uint v) { if (R != null) v = R.VarU32(); else W.VarU(v); }
        public void VarU(ref ulong v) { if (R != null) v = R.VarU(); else W.VarU(v); }
        public void Str(ref string v, int max = 4096) { if (R != null) v = R.Str(max); else W.Str(v, max); }
        public void Guid(ref Guid v) { if (R != null) v = R.Guid(); else W.Guid(v); }
        public void V2(ref Vec2 v) { if (R != null) v = R.V2(); else W.V2(v); }
        public void V3(ref Vec3 v) { if (R != null) v = R.V3(); else W.V3(v); }
        public void V4(ref Vec4 v) { if (R != null) v = R.V4(); else W.V4(v); }
        public void Q(ref Quat v) { if (R != null) v = R.Q(); else W.Q(v); }
        public void QCompact(ref Quat v) { if (R != null) v = R.QCompact(); else W.QCompact(v); }
        public void ColF(ref Col v) { if (R != null) v = R.ColF(); else W.ColF(v); }
        public void Col8(ref Col v) { if (R != null) v = R.Col8(); else W.Col8(v); }
        public void EndFlags() { W?.EndFlags(); }
        /// <summary>Length-prefixed opaque bytes (for game-defined payloads composed with another Codec).</summary>
        public void Blob(ref byte[] v, int max = 1 << 20)
        {
            if (R != null) { int n = R.Count(); if (n > max) throw new WireException("blob over limit"); v = R.Raw(n).ToArray(); }
            else { v ??= System.Array.Empty<byte>(); if (v.Length > max) throw new WireException("blob over limit"); W.Count(v.Length); W.Raw(v); }
        }
        public void Byte(ref byte v) => U8(ref v);

        /// <summary>Enum as zig-zag varint of its underlying value.</summary>
        public void Enum<T>(ref T v) where T : struct, Enum
        {
            if (R != null) v = FromLong<T>(R.Var()); else W.Var(ToLong(v));
        }
        public void EnumByte<T>(ref T v) where T : struct, Enum
        {
            if (R != null) v = FromLong<T>(R.U8()); else W.U8((byte)ToLong(v));
        }
        static long ToLong<T>(T v) where T : struct, Enum => Convert.ToInt64(v);
        static T FromLong<T>(long l) where T : struct, Enum => (T)Enum_ToObject(typeof(T), l);
        static object Enum_ToObject(Type t, long l) => System.Enum.ToObject(t, l);

        /// <summary>Optional value: a presence flag, then the value if present.</summary>
        public void Opt<T>(ref T v, ref bool has, ItemFn<T> fn)
        {
            Flag(ref has);
            if (has) fn(this, ref v); else if (R != null) v = default;
        }

        public void Wire<T>(ref T v) where T : class, IWire, new()
        {
            if (R != null) { v ??= new T(); }
            v.Describe(this);
        }
        public void WireS<T>(ref T v) where T : struct, IWire { v.Describe(this); }

        public void List<T>(ref List<T> list, ItemFn<T> fn, int minItemBytes = 1)
        {
            if (R != null)
            {
                int n = R.Count(minItemBytes);
                if (list == null) list = new List<T>(Math.Min(n, 1024)); else list.Clear();
                for (int i = 0; i < n; i++) { T t = default; fn(this, ref t); list.Add(t); }
            }
            else
            {
                int n = list?.Count ?? 0; W.Count(n);
                for (int i = 0; i < n; i++) { T t = list[i]; fn(this, ref t); }
            }
        }
        public void Dict<K, V>(ref Dictionary<K, V> d, ItemFn<K> kf, ItemFn<V> vf, int minItemBytes = 2)
        {
            if (R != null)
            {
                int n = R.Count(minItemBytes);
                if (d == null) d = new Dictionary<K, V>(Math.Min(n, 1024)); else d.Clear();
                for (int i = 0; i < n; i++) { K k = default; V v = default; kf(this, ref k); vf(this, ref v); d[k] = v; }
            }
            else
            {
                int n = d?.Count ?? 0; W.Count(n);
                if (d != null) foreach (var kv in d) { K k = kv.Key; V v = kv.Value; kf(this, ref k); vf(this, ref v); }
            }
        }
    }

    /// <summary>Ready-made item functions for List/Dict/Opt.</summary>
    public static class Items
    {
        public static readonly ItemFn<int> Var32 = (Codec c, ref int v) => c.Var(ref v);
        public static readonly ItemFn<long> Var64 = (Codec c, ref long v) => c.Var(ref v);
        public static readonly ItemFn<uint> VarU32 = (Codec c, ref uint v) => c.VarU(ref v);
        public static readonly ItemFn<string> Str = (Codec c, ref string v) => c.Str(ref v);
        public static readonly ItemFn<byte> U8 = (Codec c, ref byte v) => c.U8(ref v);
        public static readonly ItemFn<float> F32 = (Codec c, ref float v) => c.F32(ref v);
        public static readonly ItemFn<double> F64 = (Codec c, ref double v) => c.F64(ref v);
        public static readonly ItemFn<Vec3> V3 = (Codec c, ref Vec3 v) => c.V3(ref v);
        public static readonly ItemFn<Guid> Guid = (Codec c, ref Guid v) => c.Guid(ref v);
        public static ItemFn<T> Of<T>() where T : struct, IWire => (Codec c, ref T v) => v.Describe(c);
        public static ItemFn<T> OfClass<T>() where T : class, IWire, new() => (Codec c, ref T v) => c.Wire(ref v);
    }
}

// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
namespace Lycoreia.Net
{
    /// <summary>Unity-free value types used on the wire. Conversions to UnityEngine types live in the game layer.</summary>
    public struct Vec2 { public float X, Y; public Vec2(float x, float y) { X = x; Y = y; } }
    public struct Vec3
    {
        public float X, Y, Z;
        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static readonly Vec3 Zero = new Vec3(0, 0, 0);
        public override string ToString() => $"({X:0.##},{Y:0.##},{Z:0.##})";
    }
    public struct Vec4 { public float X, Y, Z, W; public Vec4(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; } }
    public struct Quat
    {
        public float X, Y, Z, W;
        public Quat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        public static readonly Quat Identity = new Quat(0, 0, 0, 1);
    }
    /// <summary>Float colour (RGBA).</summary>
    public struct Col { public float R, G, B, A; public Col(float r, float g, float b, float a) { R = r; G = g; B = b; A = a; } }
}

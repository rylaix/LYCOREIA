// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using Lycoreia.Net;

namespace Lycoreia.Players
{
    /// <summary>
    /// Remote avatar smoothing: the target interpolates the last two snapshots over one update period and extrapolates unclamped;
    /// the shown pose blends towards it at about 15 per second (unscaled time); angles take the shortest arc; more than 3 m off snaps.
    /// </summary>
    public sealed class AvatarSmoother
    {
        public const float BlendRate = 15f, SnapDistance = 3f; public const double DefaultPeriod = 0.05, MaxExtrapolate = 1.0;
        Vec3 _p0, _p1, _shown; float _yaw0, _yaw1, _shownYaw, _pitch0, _pitch1, _shownPitch; double _t0, _t1; int _count; double _period = DefaultPeriod;
        public bool HasState => _count > 0;
        public Vec3 Position => _shown; public float Yaw => _shownYaw; public float Pitch => _shownPitch;

        public void Push(double now, Vec3 pos, float yaw, float pitch)
        {
            if (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y) || !float.IsFinite(pos.Z) || !float.IsFinite(yaw) || !float.IsFinite(pitch)) return;
            if (_count == 0) { _p0 = _p1 = _shown = pos; _yaw0 = _yaw1 = _shownYaw = yaw; _pitch0 = _pitch1 = _shownPitch = pitch; _t0 = _t1 = now; _count = 1; return; }
            double gap = now - _t1; if (gap > 1e-4) _period = Math.Clamp(gap, 0.01, 0.5);
            _p0 = _p1; _yaw0 = _yaw1; _pitch0 = _pitch1; _t0 = _t1;
            _p1 = pos; _yaw1 = yaw; _pitch1 = pitch; _t1 = now; _count++;
        }

        /// <summary>The target pose at <paramref name="now"/>: one period behind the newest snapshot, extrapolated past it (bounded to 1 s of motion).</summary>
        public (Vec3 pos, float yaw, float pitch) Target(double now)
        {
            if (_count < 2) return (_p1, _yaw1, _pitch1);
            double s = Math.Min((now - _t1) / _period, MaxExtrapolate / _period) + 1.0;       // 0 at the older snapshot, 1 at the newest, >1 extrapolates
            float f = (float)s;
            var p = new Vec3(_p0.X + (_p1.X - _p0.X) * f, _p0.Y + (_p1.Y - _p0.Y) * f, _p0.Z + (_p1.Z - _p0.Z) * f);
            // position may extrapolate (bounded to 1 s); a turn never does past the newest sample: extrapolating a 30-degree turn for 20
            // periods during a short packet gap spun the whole avatar round
            return (p, _yaw0 + DeltaAngle(_yaw0, _yaw1) * Math.Clamp(f, 0, 1), _pitch0 + (_pitch1 - _pitch0) * Math.Clamp(f, 0, 1));
        }

        public void Step(double now, float dt)
        {
            if (_count == 0) return; var (p, y, pi) = Target(now);
            float dx = p.X - _shown.X, dy = p.Y - _shown.Y, dz = p.Z - _shown.Z;
            if (dx * dx + dy * dy + dz * dz > SnapDistance * SnapDistance) { _shown = p; _shownYaw = y; _shownPitch = pi; return; }
            float k = 1f - MathF.Exp(-BlendRate * Math.Max(0f, dt));
            _shown = new Vec3(_shown.X + dx * k, _shown.Y + dy * k, _shown.Z + dz * k);
            _shownYaw = Normalize(_shownYaw + DeltaAngle(_shownYaw, y) * k); _shownPitch += (pi - _shownPitch) * k;
        }

        /// <summary>Shortest signed arc from a to b in degrees (-180..180].</summary>
        public static float DeltaAngle(float a, float b) { float d = (b - a) % 360f; if (d > 180f) d -= 360f; if (d <= -180f) d += 360f; return d; }
        public static float Normalize(float a) { a %= 360f; return a < 0 ? a + 360f : a; }
    }

    public static class AvatarRules
    {
        /// <summary>Avatar, marker and FX show only in the same scene group; an id below 0 means unknown, so hide.</summary>
        public static bool Visible(int peerScene, int localScene) => peerScene >= 0 && localScene >= 0 && peerScene == localScene;

        public const float NearDistance = 10f, GrowUntil = 80f, PeakAt = 120f, MinWorldScale = 0.55f, MaxScreenFactor = 1.7f;
        /// <summary>
        /// World scale of a nametag at a distance: smaller up close; from 10 m it keeps its on-screen size, growing to about 1.35x at 80 m and
        /// at most 1.7x; it shrinks again past 120 m. The minimum keeps it from hiding the body up close.
        /// </summary>
        public static float NametagScale(float distance)
        {
            if (!float.IsFinite(distance) || distance <= 0) return MinWorldScale;
            if (distance < NearDistance) return Math.Max(MinWorldScale, distance / NearDistance);
            float screen;
            if (distance <= GrowUntil) screen = 1f + 0.35f * (distance - NearDistance) / (GrowUntil - NearDistance);
            else if (distance <= PeakAt) screen = 1.35f + (MaxScreenFactor - 1.35f) * (distance - GrowUntil) / (PeakAt - GrowUntil);
            else screen = MaxScreenFactor * Math.Max(0.35f, 1f - (distance - PeakAt) / 240f);
            return screen * distance / NearDistance;
        }
        /// <summary>The on-screen factor implied by a world scale at a distance (for tests and diagnostics).</summary>
        public static float ScreenFactor(float distance) => NametagScale(distance) * NearDistance / Math.Max(distance, 0.001f);
    }
}

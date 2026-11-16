// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;

namespace Lycoreia.Coop
{
    public static class CoopWheelLayout
    {
        /// <summary>Visual gap between neighbouring wedges, degrees.</summary>
        public const float GapDegrees = 3f;

        /// <summary>Angle of wedge i's middle, degrees clockwise from the top (the same convention as CoopWheelRules.Pick).</summary>
        public static float CenterAngle(int i, int n) => n <= 0 ? 0f : i * 360f / n;

        /// <summary>Image.fillAmount (Radial360) of one wedge.</summary>
        public static float FillAmount(int n, float gap = GapDegrees) => n <= 1 ? 1f : Math.Max(0.02f, (360f / n - gap) / 360f);

        /// <summary>
        /// RectTransform z rotation (degrees, counter-clockwise positive) for a Radial360 image whose fill origin is the top and which fills clockwise,
        /// so that the filled part is centred on <see cref="CenterAngle"/>.
        /// </summary>
        public static float ImageRotationZ(int i, int n, float gap = GapDegrees)
        {
            if (n <= 1) return 0f;
            float start = CenterAngle(i, n) - (360f / n - gap) / 2f;
            return -start;
        }

        /// <summary>Position on a circle of radius r (x right, y up) for wedge i.</summary>
        public static (float x, float y) Position(int i, int n, float r)
        {
            double a = CenterAngle(i, n) * Math.PI / 180.0;
            return ((float)(Math.Sin(a) * r), (float)(Math.Cos(a) * r));
        }

        /// <summary>Which side the outer label of wedge i sits on: 1 = right of the ring (left aligned), -1 = left (right aligned), 0 = above or below (centred).</summary>
        public static int LabelSide(int i, int n)
        {
            double s = Math.Sin(CenterAngle(i, n) * Math.PI / 180.0);
            return s > 0.3 ? 1 : s < -0.3 ? -1 : 0;
        }

        /// <summary>Screen rectangle (x0, y0, x1, y1; y up) of the outer label of wedge i for a box of width w and height h, matching LabelSide's pivots.</summary>
        public static (float x0, float y0, float x1, float y1) LabelRect(int i, int n, float r, float w, float h)
        {
            var (x, y) = Position(i, n, r); int side = LabelSide(i, n);
            float x0 = side > 0 ? x : side < 0 ? x - w : x - w / 2f;
            float y0 = side != 0 ? y - h / 2f : (y >= 0 ? y : y - h);
            return (x0, y0, x0 + w, y0 + h);
        }

        /// <summary>True when any two outer labels of an n-wedge wheel overlap.</summary>
        public static bool LabelsOverlap(int n, float r, float w, float h)
        {
            for (int a = 0; a < n; a++)
                for (int b = a + 1; b < n; b++)
                {
                    var p = LabelRect(a, n, r, w, h); var q = LabelRect(b, n, r, w, h);
                    if (p.x0 < q.x1 && q.x0 < p.x1 && p.y0 < q.y1 && q.y0 < p.y1) return true;
                }
            return false;
        }

        /// <summary>Wedge a gamepad stick points at (stick components -1..1, y up; 0 = top, clockwise); -1 inside the dead zone.</summary>
        public static int PickStick(float x, float y, int count, float deadZone = 0.45f)
            => CoopWheelRules.Pick(x * 100f, y * 100f, count, deadZone * 100f);

        /// <summary>Icon key of a wheel action (resolved to a native sprite, else a generated one, by the Unity side).</summary>
        public static string IconKey(WheelAction a)
        {
            switch (a)
            {
                case WheelAction.PinJob: return "job";
                case WheelAction.WriteJob: return "write";
                case WheelAction.Note: return "note";
                case WheelAction.WriteNote: return "write";
                case WheelAction.RemoveNote: return "remove";
                case WheelAction.ClaimJob: return "claim";
                case WheelAction.FinishJob: return "done";
                case WheelAction.UnclaimJob: return "remove";
                case WheelAction.RemoveJob: return "remove";
                default: return "ping";
            }
        }

        /// <summary>
        /// Gamepad buttons the wheel may be held on, best first. Gameplay needs the right stick for aiming, so the hold button sits where the thumb is free:
        /// the d-pad first (most ergonomic with the right stick), then Back/View, then the stick clicks, then Start.
        /// </summary>
        public static readonly string[] PadCandidates = { "dpad/down", "dpad/up", "dpad/left", "dpad/right", "select", "rightStickPress", "leftStickPress", "start" };

        /// <summary>First candidate that no gameplay action uses; null when all are taken. Bound paths look like "&lt;Gamepad&gt;/dpad/down" or "dpad".</summary>
        public static string PickPadButton(IEnumerable<string> boundGamepadPaths)
        {
            var bound = new List<string>();
            foreach (var p in boundGamepadPaths ?? new string[0])
            {
                if (string.IsNullOrEmpty(p)) continue;
                var s = p.Trim(); int slash = s.IndexOf('/');
                if (s.StartsWith("<", StringComparison.Ordinal) && slash > 0) s = s.Substring(slash + 1);
                bound.Add(s.ToLowerInvariant());
            }
            foreach (var c in PadCandidates)
            {
                var lc = c.ToLowerInvariant(); bool taken = false;
                foreach (var b in bound)
                    if (b == lc || lc.StartsWith(b + "/", StringComparison.Ordinal) || b.StartsWith(lc + "/", StringComparison.Ordinal)) { taken = true; break; }
                if (!taken) return c;
            }
            return null;
        }
    }
}

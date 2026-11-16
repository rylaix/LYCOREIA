// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using Lycoreia.Coop;
using Lycoreia.Text;

namespace Lycoreia.Tests
{
    static class CoopWheelPolishTests
    {
        public static void Register()
        {
            T.Test("wheel layout/fill+rotation: wedges tile the ring, centred on their angle", () =>
            {
                T.True(Math.Abs(CoopWheelLayout.FillAmount(8) - (45f - 3f) / 360f) < 1e-5, "fill of 8 wedges");
                T.True(CoopWheelLayout.FillAmount(1) == 1f, "single wedge is the full ring");
                T.True(Math.Abs(CoopWheelLayout.ImageRotationZ(0, 4) - 43.5f) < 1e-4, "wedge 0 of 4 starts 43.5 degrees counter-clockwise of the top (z rotation positive = ccw)");
                T.True(Math.Abs(CoopWheelLayout.ImageRotationZ(1, 4) - (-46.5f)) < 1e-4, "wedge 1 of 4 starts at 46.5 degrees clockwise");
                for (int n = 2; n <= 8; n++)
                    for (int i = 0; i < n; i++)
                    {
                        float start = -CoopWheelLayout.ImageRotationZ(i, n), mid = start + CoopWheelLayout.FillAmount(n) * 360f / 2f;
                        T.True(Math.Abs(mid - CoopWheelLayout.CenterAngle(i, n)) < 1e-3, "filled arc centred on wedge " + i + "/" + n);
                    }
            });
            T.Test("wheel layout/position+pick agree: aiming at a wedge position picks it", () =>
            {
                for (int n = 2; n <= 8; n++)
                    for (int i = 0; i < n; i++)
                    {
                        var (x, y) = CoopWheelLayout.Position(i, n, 200f);
                        T.Eq(i, CoopWheelRules.Pick(x, y, n), "mouse pick " + i + "/" + n);
                        T.Eq(i, CoopWheelLayout.PickStick(x / 200f, y / 200f, n), "stick pick " + i + "/" + n);
                    }
                T.Eq(-1, CoopWheelLayout.PickStick(0.1f, 0.2f, 6), "stick inside the dead zone cancels");
                T.Eq(-1, CoopWheelLayout.PickStick(0f, 0f, 6), "centred stick cancels");
            });
            T.Test("wheel layout/label side and icon keys", () =>
            {
                T.Eq(0, CoopWheelLayout.LabelSide(0, 8), "top is centred");
                T.Eq(1, CoopWheelLayout.LabelSide(2, 8), "right is on the right");
                T.Eq(-1, CoopWheelLayout.LabelSide(6, 8), "left is on the left");
                foreach (WheelAction a in Enum.GetValues(typeof(WheelAction))) T.True(!string.IsNullOrEmpty(CoopWheelLayout.IconKey(a)), "icon key for " + a);
                T.Eq("claim", CoopWheelLayout.IconKey(WheelAction.ClaimJob), "claim icon");
            });
            T.Test("wheel layout/gamepad hold button avoids what gameplay binds", () =>
            {
                T.Eq("dpad/down", CoopWheelLayout.PickPadButton(new string[0]), "nothing bound: d-pad down");
                T.Eq("dpad/up", CoopWheelLayout.PickPadButton(new[] { "<Gamepad>/dpad/down" }), "down bound: next");
                T.Eq("select", CoopWheelLayout.PickPadButton(new[] { "<Gamepad>/dpad" }), "whole d-pad bound: skip all four directions");
                T.Eq("select", CoopWheelLayout.PickPadButton(new[] { "<Gamepad>/dpad/down", "<Gamepad>/dpad/up", "<Gamepad>/dpad/left", "<Gamepad>/dpad/right" }), "all directions bound");
                T.Eq("start", CoopWheelLayout.PickPadButton(new[] { "<Gamepad>/dpad", "<Gamepad>/select", "<Gamepad>/rightStickPress", "<Gamepad>/leftStickPress" }), "only start left");
                T.True(CoopWheelLayout.PickPadButton(new[] { "<Gamepad>/dpad", "<Gamepad>/select", "<Gamepad>/rightStickPress", "<Gamepad>/leftStickPress", "<Gamepad>/start" }) == null, "everything bound: none");
            });
            T.Test("wheel text: entries and presets translate, player text and English stay", () =>
            {
                T.Eq("Задание: накормить этот загон", WheelText.Label("ru", "Job: Feed this corral"), "preset job");
                T.Eq("Заметка: занято", WheelText.Label("ru", "Note: Reserved"), "preset note");
                T.Eq("Задание: написать своё...", WheelText.Label("ru", "Job: write your own..."), "write entry");
                T.Eq("Берусь: my own text", WheelText.Label("ru", "I'll do: my own text"), "player text untouched");
                T.Eq("Убрать заметку", WheelText.Label("ru", "Remove the note"), "whole entry");
                T.Eq("Job: Feed this corral", WheelText.Label("en", "Job: Feed this corral"), "English unchanged");
                T.Eq("Nothing known", WheelText.Label("ru", "Nothing known"), "unknown passes through");
            });
        }
    }
}

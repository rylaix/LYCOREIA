// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
// 2.1.0-rc3 playtest fixes: boost-fx message (wire, relay, rate limit, authority) and native cue name scoring with the real names from the logs.
using System.Collections.Generic;
using System.Linq;
using Lycoreia.Net;
using Lycoreia.Net.Messages;
using Lycoreia.Owned.Components.UI;
using Lycoreia.Owned.Fx;
using Lycoreia.Players;

namespace Lycoreia.Tests
{
    static class BoostFxAndCueTests
    {
        sealed class Seat : IBoostFxWorld { public List<BoostFx> Played = new List<BoostFx>(); public void PlayBoostFx(BoostFx fx) => Played.Add(fx); }

        static string Best(string[] names, string[] want, string[] avoid, string[] prefer = null)
            => names.OrderByDescending(n => FxNames.Score(n, want, avoid, prefer)).First();

        public static void Register()
        {
            T.Test("boostfx/wire round trip", () =>
            {
                var reg = MessageRegistry.FromAssembly(typeof(Mod).Assembly);
                var info = reg.Get<BoostFx>();
                T.Eq((ushort)414, info.Id); T.True(info.Mode == Delivery.Unreliable && info.PlayerScoped);
                var m = new BoostFx { JumperId = "PLAYER_A", StoodOnId = "PLAYER_B", Position = new Vec3(1.5f, -2f, 300f), Strength = 11.25f };
                var w = new NetWriter(); m.Describe(new Codec(w)); var bytes = w.Span.ToArray();
                var back = new BoostFx(); var rd = NetReader.Rent(bytes, 0, bytes.Length); back.Describe(new Codec(rd)); T.True(rd.AtEnd);
                T.Eq("PLAYER_A", back.JumperId); T.Eq("PLAYER_B", back.StoodOnId); T.Eq(300f, back.Position.Z); T.Eq(11.25f, back.Strength);
            });
            T.Test("boostfx/rules reject non-finite, huge and over-long", () =>
            {
                T.True(BoostFxRules.IsSane(new BoostFx { JumperId = "A", StoodOnId = "", Position = Vec3.Zero, Strength = 10f }));
                T.False(BoostFxRules.IsSane(new BoostFx { JumperId = "A", Position = new Vec3(float.NaN, 0, 0), Strength = 10f }));
                T.False(BoostFxRules.IsSane(new BoostFx { JumperId = "A", Position = Vec3.Zero, Strength = 1000f }));
                T.False(BoostFxRules.IsSane(new BoostFx { JumperId = "A", Position = Vec3.Zero, Strength = float.PositiveInfinity }));
                T.False(BoostFxRules.IsSane(new BoostFx { JumperId = new string('x', 65), Position = Vec3.Zero, Strength = 1f }));
                T.False(BoostFxRules.IsSane(new BoostFx { JumperId = "", Position = Vec3.Zero, Strength = 1f }));
            });
            T.Test("boostfx/guest -> host plays and relays to the other guest, forged jumper id is replaced, flood dropped", () =>
            {
                var w = new World(); var h = w.StartHost(); var hs = new Seat(); var hsync = new BoostFxSync(h, hs, () => w.Net.Now / 1000.0);
                var a = w.AddGuest("A"); var b = w.AddGuest("B"); var sa = new Seat(); var sb = new Seat();
                var asy = new BoostFxSync(a, sa, () => w.Net.Now / 1000.0); var bsy = new BoostFxSync(b, sb, () => w.Net.Now / 1000.0);
                w.Join(a); T.True(w.Net.RunUntil(() => a.Joined, 4000)); w.Join(b); T.True(w.Net.RunUntil(() => b.Joined, 4000)); w.Net.Step(300);
                asy.Publish(h.LocalId, new Vec3(1, 2, 3), 12f); w.Net.Step(200);
                T.Eq(1, hs.Played.Count); T.Eq(1, sb.Played.Count); T.Eq(0, sa.Played.Count, "the sender does not play its own message twice");
                T.Eq(a.LocalId, hs.Played[0].JumperId); T.Eq(h.LocalId, sb.Played[0].StoodOnId); T.Eq(12f, sb.Played[0].Strength);
                a.Node.SendToHost(new BoostFx { JumperId = b.LocalId, StoodOnId = "", Position = Vec3.Zero, Strength = 5f }); w.Net.Step(200);
                T.Eq(1, hs.Played.Count, "a forged jumper id (or a flood inside 0.2 s) is dropped");
                w.Net.Step(400); asy.Publish("", new Vec3(1, 2, 3), 12f); w.Net.Step(200);
                T.Eq(2, hs.Played.Count); T.Eq(2, sb.Played.Count);
                T.True(hsync.Played == 2 && hsync.Received >= 2 && bsy.Played == 2 && asy.Sent == 2, "counters for the diagnostics");
            });
            T.Test("boostfx/host boosts are broadcast to every guest", () =>
            {
                var w = new World(); var h = w.StartHost(); var hsync = new BoostFxSync(h, new Seat(), () => w.Net.Now / 1000.0);
                var a = w.AddGuest("A"); var sa = new Seat(); new BoostFxSync(a, sa, () => w.Net.Now / 1000.0); w.Join(a); T.True(w.Net.RunUntil(() => a.Joined, 4000)); w.Net.Step(300);
                hsync.Publish(a.LocalId, new Vec3(4, 5, 6), 9f); w.Net.Step(200);
                T.Eq(1, sa.Played.Count); T.Eq(h.LocalId, sa.Played[0].JumperId); T.Eq(a.LocalId, sa.Played[0].StoodOnId);
            });
            T.Test("fx names/word split on the real cue names", () =>
            {
                T.Eq("env,fields,lapping,01", string.Join(",", FxNames.Words("envFieldsLapping01")));
                T.Eq("vo,twin,jump", string.Join(",", FxNames.Words("VOTwinJump")));
                T.Eq("resource,detector,ping,medium", string.Join(",", FxNames.Words("ResourceDetector_PingMedium")));
                T.Eq("fx,smoke,dust,medium", string.Join(",", FxNames.Words("FX_SmokeDust_Medium")));
            });
            var pingWant = new[] { "ping", "waypoint", "beacon", "marker", "notification", "newitem" };
            var pingAvoid = new[] { "pause", "lab", "gadget", "slime", "gordo", "tarr", "plort", "vac", "ambient", "music", "lapping" };
            var pingNames = new[] { "envFieldsLapping01", "ResourceDetector_PingFar", "ResourceDetector_PingClose", "ResourceDetector_PingMedium", "gadgetLinkedCannonPing" };
            T.Test("fx names/ping: ping never matches inside lapping, ResourceDetector_PingMedium wins", () =>
            {
                T.Eq(-1, FxNames.Score("envFieldsLapping01", pingWant, new string[0]), "no word-internal match");
                T.Eq("ResourceDetector_PingMedium", Best(pingNames, pingWant, pingAvoid, new[] { "ResourceDetector_PingMedium", "ResourceDetector_PingClose" }));
                T.True(FxNames.Score("ResourceDetector_PingClose", pingWant, pingAvoid) > FxNames.Score("gadgetLinkedCannonPing", pingWant, pingAvoid), "even without the prefer list");
            });
            var jumpWant = new[] { "jump", "jetpack", "hop", "boost", "launch" };
            var jumpAvoid = new[] { "land", "slime", "batty", "flutter", "angler", "twin", "ui" };
            var jumpNames = new[] { "VOTwinJump", "BattyJump", "FlutterJump", "VOAnglerJump", "VOFlutterJump", "PlayerJumpGlass", "PlayerJumpCloud" };
            T.Test("fx names/jump: player jump cue beats slime voices and slime jumps, with or without the prefer list", () =>
            {
                T.Eq("PlayerJumpCloud", Best(jumpNames, jumpWant, jumpAvoid, new[] { "PlayerJumpCloud" }));
                T.True(Best(jumpNames, jumpWant, jumpAvoid).StartsWith("PlayerJump"), "scoring alone picks a Player cue");
                T.True(FxNames.Score("VOTwinJump", jumpWant, jumpAvoid) < FxNames.Score("PlayerJumpGlass", jumpWant, jumpAvoid) - 50);
            });
            T.Test("fx names/puff: smoke dust over the radiant slime jump vfx", () =>
            {
                var want = new[] { "smokedust", "jetpack", "dust", "puff", "smoke", "jump", "land" };
                var avoid = new[] { "slime", "radiant", "door", "impact" };
                var names = new[] { "RadiantJumpVFX", "FX_SmokeDust", "FX_DoorDust_Impact", "FX_SmokeDust_Medium", "FX_SmokeDust_Big" };
                T.True(Best(names, want, avoid).StartsWith("FX_SmokeDust"));
                T.Eq("FX_SmokeDust_Medium", Best(names, want, avoid, new[] { "FX_SmokeDust_Medium", "FX_SmokeDust" }));
                T.True(FxNames.Score("RadiantJumpVFX", want, avoid) < FxNames.Score("FX_SmokeDust_Big", want, avoid));
            });
            T.Test("ui cues/Success and Fail resolve from the real 8 UIAudioTable keys", () =>
            {
                var keys = new[] { "InputHintHover", "ButtonHover", "ButtonFocused", "ButtonClick", "OpenScreen", "CloseScreen", "OpenPopup", "ClosePopup" };
                T.Eq("OpenPopup", UiCueScoring.BestKey(8, keys)); T.Eq("ClosePopup", UiCueScoring.BestKey(9, keys));
                T.Eq("ButtonHover", UiCueScoring.BestKey(0, keys), "hover is unchanged"); T.Eq("OpenScreen", UiCueScoring.BestKey(2, keys));
                T.Eq("CloseScreen", UiCueScoring.BestKey(3, keys));
            });
        }
    }
}

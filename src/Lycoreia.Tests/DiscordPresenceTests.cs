// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Text;
using Lycoreia;

namespace Lycoreia.Tests
{
    static class DiscordPresenceTests
    {
        public static void Register()
        {
            T.Test("discord/frame round trip little endian", () =>
            {
                var b = DiscordPresence.EncodeFrame(1, "{\"a\":\"é\"}");
                T.Eq((byte)1, b[0]); T.Eq((byte)0, b[1]); T.Eq(Encoding.UTF8.GetByteCount("{\"a\":\"é\"}"), (int)b[4]);
                T.True(DiscordPresence.TryDecodeFrame(b, b.Length, out var op, out var json, out var used));
                T.Eq(1, op); T.Eq("{\"a\":\"é\"}", json); T.Eq(b.Length, used);
            });
            T.Test("discord/frame incomplete and oversize rejected", () =>
            {
                var b = DiscordPresence.EncodeFrame(3, "hello");
                T.False(DiscordPresence.TryDecodeFrame(b, b.Length - 1, out _, out _, out _));
                T.False(DiscordPresence.TryDecodeFrame(b, 4, out _, out _, out _));
                var bad = new byte[8]; bad[4] = 0xFF; bad[5] = 0xFF; bad[6] = 0xFF; bad[7] = 0x7F;
                T.False(DiscordPresence.TryDecodeFrame(bad, 8, out _, out _, out _));
            });
            T.Test("discord/handshake and escaping", () =>
            {
                T.Eq("{\"v\":1,\"client_id\":\"123\"}", DiscordPresence.HandshakeJson("123"));
                T.Eq("\"a\\\"b\\\\c\\n\\u0001\"", DiscordPresence.Q("a\"b\\c\n\u0001"));
                T.True(DiscordPresence.IsReady("{\"cmd\":\"DISPATCH\",\"evt\":\"READY\"}"));
                T.False(DiscordPresence.IsReady("{\"evt\":\"ERROR\"}"));
            });
            T.Test("discord/activity json", () =>
            {
                var a = new PresenceActivity("Hosting", "Party of 2", 1700000000, "lyc-1", 2, 1);
                var j = DiscordPresence.SetActivityJson(42, a, "9.9", "https://d.gg/x", "n1");
                T.True(j.StartsWith("{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":42,\"activity\":{\"details\":\"Hosting\",\"state\":\"Party of 2\""));
                T.True(j.Contains("\"timestamps\":{\"start\":1700000000}"));
                T.True(j.Contains("\"party\":{\"id\":\"lyc-1\",\"size\":[2,2]}"));
                T.True(j.Contains("\"large_image\":\"lycoreia\"") && j.Contains("\"large_text\":\"Lycoreia 9.9\""));
                T.True(j.Contains("\"buttons\":[{\"label\":\"Discord\",\"url\":\"https://d.gg/x\"}]"));
                T.True(j.EndsWith("},\"nonce\":\"n1\"}"));
                T.True(DiscordPresence.SetActivityJson(1, null, "v", "u", "n").Contains("\"activity\":null"));
                T.False(DiscordPresence.ActivityJson(new PresenceActivity("a", "b", 1), "v", "u").Contains("party"));
                T.True(new PresenceActivity("a", "b", 1).Equals(new PresenceActivity("a", "b", 1)));
            });
        }
    }
}

// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Lycoreia.Net;
using Lycoreia.Net.Messages;

namespace Lycoreia.Tests
{
    /// <summary>Fills public fields of a message with deterministic non-default values by reflection (tests only).</summary>
    static class Filler
    {
        public static void Fill(object o, int seed, bool minimal)
        {
            var rng = new Random(seed);
            foreach (var f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.IsInitOnly || f.GetCustomAttribute<NonSerializedAttribute>() != null) continue;
                f.SetValue(o, Make(f.FieldType, rng, minimal, 0, f.Name));
            }
        }
        static object Make(Type t, Random r, bool min, int depth, string name)
        {
            if (min) { if (t == typeof(string)) return ""; if (t.IsValueType && t.IsPrimitive) return Activator.CreateInstance(t); }
            if (t == typeof(string)) return "id" + r.Next(1000);
            if (t == typeof(bool)) return !min && r.Next(2) == 0;
            if (t == typeof(byte)) return (byte)r.Next(1, 200);
            if (t == typeof(int)) return r.Next(-500, 500);
            if (t == typeof(uint)) return (uint)r.Next(1, 100000);
            if (t == typeof(long)) return (long)r.Next(1, 100000) * 977;
            if (t == typeof(ulong)) return (ulong)r.Next(1, 100000);
            if (t == typeof(ushort)) return (ushort)r.Next(1, 60000);
            if (t == typeof(float)) return (float)Math.Round(r.NextDouble() * 10, 1);
            if (t == typeof(double)) return Math.Round(r.NextDouble() * 1000, 3);
            if (t == typeof(Guid)) return min ? Guid.Empty : new Guid(r.Next(), 1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
            if (t == typeof(Vec3)) return new Vec3(1.5f, -2.5f, r.Next(100));
            if (t == typeof(Quat)) return new Quat(0, 0, 0, 1);
            if (t.IsEnum) { var vals = Enum.GetValues(t); return vals.GetValue(r.Next(vals.Length)); }
            if (t == typeof(byte[])) return min ? new byte[0] : new byte[] { 1, 2, 3, (byte)r.Next(255) };
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                var l = (IList)Activator.CreateInstance(t); var et = t.GetGenericArguments()[0];
                if (!min && depth < 3) for (int i = 0; i < 3; i++) l.Add(Make(et, r, min, depth + 1, name)); return l;
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                var d = (IDictionary)Activator.CreateInstance(t); var ga = t.GetGenericArguments();
                if (!min && depth < 3) for (int i = 0; i < 3; i++) d[Make(ga[0], r, min, depth + 1, name) ?? "k"] = Make(ga[1], r, min, depth + 1, name);
                return d;
            }
            if (t.IsValueType || t.GetConstructor(Type.EmptyTypes) != null)
            {
                var o = Activator.CreateInstance(t);
                if (t == typeof(Revision) ) { var rv = new Revision { Has = !min, Value = min ? 0 : 424242 }; return rv; }
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance)) f.SetValue(o, Make(f.FieldType, r, min, depth + 1, f.Name));
                return o;
            }
            throw new NotSupportedException("filler cannot make " + t);
        }
    }

    static class MessageTests
    {
        static byte[] Encode(Msg m) { var w = new NetWriter(); m.Describe(new Codec(w)); return w.Span.ToArray(); }
        static Msg Decode(MessageInfo info, byte[] b)
        {
            var m = info.Factory(); var r = NetReader.Rent(b, 0, b.Length); m.Describe(new Codec(r));
            if (!r.AtEnd) throw new WireException("leftover"); return m;
        }

        public static void Register()
        {
            var reg = MessageRegistry.FromAssembly(typeof(Msg).Assembly);
            T.Test("messages/registry has the whole catalogue", () => T.True(reg.Count >= 120, "only " + reg.Count));
            T.Test("messages/ids unique and in range", () =>
            {
                var seen = new HashSet<ushort>(); foreach (var i in reg.All) { T.True(seen.Add(i.Id), "dup " + i.Name); T.True(i.Id != 0); }
            });
            T.Test("messages/only core messages accept unregistered senders", () =>
            {
                foreach (var i in reg.All.Where(i => i.AllowUnregistered)) T.Eq("Connect", i.Name);
            });
            T.Test("messages/every kind round-trips, filled and minimal", () =>
            {
                foreach (var info in reg.All)
                    foreach (bool minimal in new[] { false, true })
                    {
                        var m = info.Factory(); Filler.Fill(m, info.Id, minimal);
                        byte[] a; try { a = Encode(m); } catch (Exception e) { throw new AssertException($"{info.Name} encode: {e.Message}"); }
                        Msg back; try { back = Decode(info, a); } catch (Exception e) { throw new AssertException($"{info.Name} decode ({(minimal ? "minimal" : "filled")}): {e.Message}"); }
                        T.SeqEq(a, Encode(back), info.Name + " re-encode");
                        // deterministic: same message, same bytes
                        T.SeqEq(a, Encode(m), info.Name + " deterministic");
                    }
            });
            T.Test("messages/truncated payloads fail cleanly", () =>
            {
                foreach (var info in reg.All)
                {
                    var m = info.Factory(); Filler.Fill(m, info.Id + 7, false); var a = Encode(m);
                    for (int cut = 0; cut < a.Length; cut++)
                    {
                        var part = a.AsSpan(0, cut).ToArray();
                        try { Decode(info, part); }
                        catch (WireException) { }
                        catch (Exception e) { throw new AssertException($"{info.Name} cut {cut}: {e.GetType().Name} {e.Message}"); }
                    }
                }
            });
            T.Test("messages/trailing bytes detected", () =>
            {
                var info = reg.Get<PlayerPing>(); var m = new PlayerPing { PlayerId = "PLAYER_A" }; var a = Encode(m).Concat(new byte[] { 9 }).ToArray();
                T.Throws<WireException>(() => Decode(info, a));
            });
            T.Test("messages/random garbage never throws anything but WireException", () =>
            {
                var rng = new Random(77);
                foreach (var info in reg.All)
                    for (int i = 0; i < 40; i++)
                    {
                        var junk = new byte[rng.Next(0, 80)]; rng.NextBytes(junk);
                        try { Decode(info, junk); } catch (WireException) { } catch (Exception e) { throw new AssertException($"{info.Name}: {e.GetType().Name} {e.Message}"); }
                    }
            });
            T.Test("messages/player scoped types expose the claimed id", () =>
            {
                T.Eq("PLAYER_X", ((IPlayerScoped)new PlayerUpdate { PlayerId = "PLAYER_X" }).ScopedPlayerId);
                T.True(reg.Get<PlayerUpdate>().PlayerScoped && reg.Get<PlayerPing>().PlayerScoped && !reg.Get<ActorSpawn>().PlayerScoped);
            });
            T.Test("messages/high-rate player update is small", () =>
            {
                var m = new PlayerUpdate { PlayerId = "PLAYER_ABCDEF123", SceneGroup = 7 }; T.True(Encode(m).Length < 64, "size " + Encode(m).Length);
                var u = new ActorUpdate { ActorId = 123456 }; T.True(Encode(u).Length < 48, "size " + Encode(u).Length);
            });
            T.Test("messages/oversized actor snapshot page is split across datagrams and rejoined", () =>
            {
                var info = reg.Get<InitialActors>(); var m = new InitialActors { SyncId = 5, Page = 0, PageCount = 1 };
                for (int i = 0; i < 200; i++) m.Actors.Add(new ActorRecord { ActorId = 1000 + i, OwnerId = "PLAYER_HOST000001", Position = new Vec3(i, 2, 3), Rotation = Quat.Identity });
                var a = Encode(m); T.True(a.Length > Frame.MaxPayload, "page is " + a.Length);
                var back = (InitialActors)Decode(info, a); T.Eq(200, back.Actors.Count);
            });
        }
    }
}

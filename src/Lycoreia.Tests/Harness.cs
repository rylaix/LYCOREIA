// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Lycoreia.Tests
{
    public sealed class AssertException : Exception { public AssertException(string m) : base(m) { } }

    public static class T
    {
        public static readonly List<(string name, Action body)> All = new List<(string, Action)>();
        public static void Test(string name, Action body) => All.Add((name, body));
        public static void True(bool c, string msg = "expected true") { if (!c) throw new AssertException(msg); }
        public static void False(bool c, string msg = "expected false") { if (c) throw new AssertException(msg); }
        public static void Eq<A>(A expected, A actual, string what = "")
        {
            if (!EqualityComparer<A>.Default.Equals(expected, actual)) throw new AssertException($"{what} expected <{expected}> but was <{actual}>");
        }
        public static void Throws<E>(Action a, string what = "") where E : Exception
        {
            try { a(); } catch (E) { return; } catch (Exception e) { throw new AssertException($"{what} expected {typeof(E).Name} but got {e.GetType().Name}"); }
            throw new AssertException($"{what} expected {typeof(E).Name}, nothing thrown");
        }
        public static void SeqEq(byte[] a, byte[] b, string what = "")
        {
            if (a.Length != b.Length) throw new AssertException($"{what} length {a.Length} vs {b.Length}");
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) throw new AssertException($"{what} differs at {i}");
        }
    }

    public static class Runner
    {
        public static int Main(string[] args)
        {
            string filter = args.Length > 0 ? args[0] : null;
            Log.Console = null;
            Suites.Register();
            int pass = 0, fail = 0; var failures = new List<string>();
            foreach (var (name, body) in T.All)
            {
                if (filter != null && !name.Contains(filter)) continue;
                var sw = Stopwatch.StartNew();
                try { body(); pass++; if (filter != null) Console.WriteLine($"  ok   {name} ({sw.ElapsedMilliseconds} ms)"); }
                catch (Exception e) { fail++; failures.Add(name); Console.WriteLine($"  FAIL {name}: {e.Message}"); if (!(e is AssertException)) Console.WriteLine(e.StackTrace); }
            }
            Console.WriteLine($"TESTS: {pass} passed, {fail} failed");
            return fail == 0 ? 0 : 1;
        }
    }
}

namespace Lycoreia.Tests
{
    /// <summary>Runs a static Verify* method of the owned rules when the core was built with the test flavour; skipped (true) otherwise.</summary>
    static class Ver
    {
        public static bool Run(Type t, string name)
        {
            var m = t.GetMethod(name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (m == null) return true;
            return (bool)m.Invoke(null, null);
        }
    }
}

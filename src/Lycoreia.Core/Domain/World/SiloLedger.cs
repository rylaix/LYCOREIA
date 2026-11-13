// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;

namespace Lycoreia.World
{
    /// <summary>One storage slot: actor type id (0 = empty) and count.</summary>
    public struct SiloSlot : IEquatable<SiloSlot>
    {
        public int Type, Count;
        public SiloSlot(int type, int count) { Type = count > 0 ? type : 0; Count = type != 0 ? Math.Max(0, count) : 0; }
        public bool Empty => Type == 0 || Count <= 0;
        public bool Equals(SiloSlot o) => (Empty && o.Empty) || (Type == o.Type && Count == o.Count);
        public override string ToString() => Empty ? "-" : Type + "x" + Count;
    }

    /// <summary>A guest's change of one slot, relative to the host state it last applied.</summary>
    public struct SiloChange { public int Slot, Type, Delta; }

    public static class SiloLedger
    {
        /// <summary>Guest: what changed locally since the last host state (a type swap is a removal plus an addition).</summary>
        public static List<SiloChange> Diff(IReadOnlyList<SiloSlot> baseline, IReadOnlyList<SiloSlot> current)
        {
            var list = new List<SiloChange>(); int n = Math.Min(baseline.Count, current.Count);
            for (int i = 0; i < n; i++)
            {
                var b = baseline[i]; var c = current[i]; if (b.Equals(c)) continue;
                if (!b.Empty && !c.Empty && b.Type == c.Type) { list.Add(new SiloChange { Slot = i, Type = c.Type, Delta = c.Count - b.Count }); continue; }
                if (!b.Empty) list.Add(new SiloChange { Slot = i, Type = b.Type, Delta = -b.Count });
                if (!c.Empty) list.Add(new SiloChange { Slot = i, Type = c.Type, Delta = c.Count });
            }
            return list;
        }

        /// <summary>Host: applies a guest change to its own slot; an addition needs an empty slot or the same type, a removal the same type. Never below 0 or above max.</summary>
        public static SiloSlot Apply(SiloSlot slot, SiloChange ch, int maxCount)
        {
            if (ch.Type == 0 || ch.Delta == 0) return slot;
            if (ch.Delta > 0)
            {
                if (!slot.Empty && slot.Type != ch.Type) return slot;
                int have = slot.Empty ? 0 : slot.Count; int max = maxCount > 0 ? maxCount : int.MaxValue;
                return new SiloSlot(ch.Type, (int)Math.Min((long)have + ch.Delta, max));
            }
            if (slot.Empty || slot.Type != ch.Type) return slot;
            int left = slot.Count + ch.Delta; return left > 0 ? new SiloSlot(slot.Type, left) : default;
        }

        public static bool SameState(IReadOnlyList<SiloSlot> a, IReadOnlyList<SiloSlot> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (!a[i].Equals(b[i])) return false;
            return true;
        }

        /// <summary>Storage key: plot id + the plot's ammo set key.</summary>
        public static string Key(string plot, string set) => (plot ?? "") + "|" + (set ?? "");
    }
}

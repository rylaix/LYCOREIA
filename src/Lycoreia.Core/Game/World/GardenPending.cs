// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;

namespace Lycoreia.World
{
    public enum GardenStep { InSync, Plant, ClearThenPlant, Clear, Defer }

    public sealed class GardenPending
    {
        /// <summary>Seconds a plot must stay active before a pending crop is applied (the wake-up frame builds the plot).</summary>
        public const double SettleSeconds = 0.5;
        readonly Dictionary<string, double> _since = new Dictionary<string, double>(StringComparer.Ordinal);   // plot -> active since (-1 = still inactive)
        public int Count => _since.Count;
        public bool Has(string plot) => plot != null && _since.ContainsKey(plot);
        public void Add(string plot) { if (!string.IsNullOrEmpty(plot) && !_since.ContainsKey(plot)) _since[plot] = -1; }
        public void Remove(string plot) { if (plot != null) _since.Remove(plot); }
        public void Clear() => _since.Clear();

        /// <summary>What to do for a garden whose wanted crop is <paramref name="wanted"/> (0 = empty) given what the plot shows.</summary>
        public static GardenStep Decide(int wanted, bool plotActive, int attachedCrop, bool hasAttached)
        {
            if (!plotActive) return GardenStep.Defer;
            if (wanted > 0)
            {
                if (attachedCrop == wanted) return GardenStep.InSync;
                return hasAttached || attachedCrop > 0 ? GardenStep.ClearThenPlant : GardenStep.Plant;
            }
            return hasAttached || attachedCrop > 0 ? GardenStep.Clear : GardenStep.InSync;
        }

        /// <summary>Plots whose pending crop is due: active for <see cref="SettleSeconds"/>. Plots that went inactive again start over.</summary>
        public List<string> TakeDue(double now, Func<string, bool> isActive)
        {
            var due = new List<string>();
            foreach (var id in new List<string>(_since.Keys))
            {
                if (!isActive(id)) { _since[id] = -1; continue; }
                if (_since[id] < 0) { _since[id] = now; continue; }
                if (now - _since[id] >= SettleSeconds) due.Add(id);
            }
            foreach (var id in due) _since.Remove(id);
            return due;
        }
    }
}

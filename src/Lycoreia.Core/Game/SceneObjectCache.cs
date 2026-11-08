// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Lycoreia.Game
{
    static class SceneObjectCache
    {
        static readonly Dictionary<(Type, bool), object> Objects = new();
        public static void Invalidate() { Objects.Clear(); Avatars.AnimatorBindings.Clear(); }
        public static void Invalidate<T>() where T : UnityEngine.Object
        { Objects.Remove((typeof(T), false)); Objects.Remove((typeof(T), true)); }
        public static T[] All<T>(bool inactive = false) where T : UnityEngine.Object
        {
            var key = (typeof(T), inactive);
            if (!Objects.TryGetValue(key, out var cached))
            {
                T[] found = UnityEngine.Object.FindObjectsOfType<T>(inactive);
                Objects[key] = cached = found;
            }
            return (T[])cached;
        }
    }
}

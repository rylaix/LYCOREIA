// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Lycoreia.Game.Avatars
{
    static class AnimatorBindings
    {
        internal readonly struct Binding
        {
            public readonly string name; public readonly int nameHash; public readonly AnimatorControllerParameterType type;
            public Binding(string n, int h, AnimatorControllerParameterType t) { name = n; nameHash = h; type = t; }
        }
        static readonly Dictionary<IntPtr, Binding[]> Cache = new();
        public static void Clear() => Cache.Clear();
        public static Binding[] For(Animator animator)
        {
            var controller = animator?.runtimeAnimatorController;
            if (controller == null) return Array.Empty<Binding>();
            if (Cache.TryGetValue(controller.Pointer, out var found)) return found;
            var list = new List<Binding>();
            foreach (var parameter in animator.parameters) list.Add(new Binding(parameter.name, parameter.nameHash, parameter.type));
            if (Cache.Count > 128) Cache.Clear();
            return Cache[controller.Pointer] = list.ToArray();
        }
    }
}

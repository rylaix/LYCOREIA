// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher;
using Il2CppMonomiPark.SlimeRancher.Regions;
using Lycoreia.Actors;
using UnityEngine;

namespace Lycoreia.Game
{
    /// <summary>Owner/replica body mode that survives region hibernation (see <see cref="BodyModeRule"/>).</summary>
    public static class BodyMode
    {
        /// <summary>Sets the body's kinematic flag, or the hibernation cache the game will restore on wake.</summary>
        public static void Set(GameObject go, Rigidbody body, bool kinematic)
        {
            if (go == null) return;
            RegionMember rm = null; bool hib = false;
            try { rm = go.GetComponent<RegionMember>(); hib = rm != null && rm._hibernating; } catch { }
            var (writeBody, writeCache) = BodyModeRule.Plan(rm != null, hib);
            if (writeCache) { try { rm._hibernatingIsKinematic = kinematic; } catch { } }
            if (writeBody && body != null) body.isKinematic = kinematic;
        }

        public static bool IsHibernating(GameObject go)
        {
            try { var rm = go != null ? go.GetComponent<RegionMember>() : null; return rm != null && rm._hibernating; } catch { return false; }
        }

        /// <summary>The game holds this body kinematic on purpose: captive or held in a vacuum, or hanging on a plant joint.</summary>
        public static bool NativeHold(GameObject go)
        {
            if (go == null) return false;
            try { var v = go.GetComponent<Vacuumable>(); if (v != null && (v._held || v._captiveToJoint != null)) return true; } catch { }
            try { var rc = go.GetComponent<ResourceCycle>(); if (rc != null && rc._joint != null && rc._joint.Joint != null) return true; } catch { }
            return false;
        }
    }
}

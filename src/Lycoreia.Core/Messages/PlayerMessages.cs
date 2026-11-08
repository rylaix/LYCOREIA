// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;

namespace Lycoreia.Net.Messages
{
    /// <summary>Per-peer pose and animator inputs; unreliable, own lane, relayed unchanged by the host.</summary>
    [Msg(20, Direction.Both, Delivery.Unreliable, Lane.Player)]
    public sealed class PlayerUpdate : Msg, IPlayerScoped
    {
        public string PlayerId; public Vec3 Position; public float BodyYaw; public int Airborne; public float LookYaw;
        public float MoveX, MoveZ, SpeedX, SpeedZ, LookPitch; public bool Moving, Sprinting; public int SceneGroup;
        public string ScopedPlayerId => PlayerId;
        public override void Describe(Codec c)
        {
            c.Str(ref PlayerId, 64); c.V3(ref Position); c.F32(ref BodyYaw); c.Var(ref Airborne); c.F32(ref LookYaw);
            c.F32(ref MoveX); c.F32(ref MoveZ); c.F32(ref SpeedX); c.F32(ref SpeedZ); c.F32(ref LookPitch);
            c.Flag(ref Moving); c.Flag(ref Sprinting); c.Var(ref SceneGroup);
        }
    }

    public enum PlayerFxKind { VacReject, VacHold, VacAccept, Shoot, ShootEmpty, WaterSplash, SlotChange, RunStart, RunEnd, ShootSound, Shockwave }

    [Msg(21, Direction.Both, Delivery.Unreliable, Lane.Fx)]
    public sealed class PlayerFx : Msg, IPlayerScoped
    {
        public PlayerFxKind Kind; public Vec3 Position; public string PlayerId;
        public string ScopedPlayerId => PlayerId;
        public override void Describe(Codec c) { c.Enum(ref Kind); c.V3(ref Position); c.Str(ref PlayerId, 64); }
        public bool NeedsReliable => Kind == PlayerFxKind.VacHold || Kind == PlayerFxKind.RunStart || Kind == PlayerFxKind.RunEnd || Kind == PlayerFxKind.VacAccept;
    }

    [Msg(22, Direction.Both, Delivery.Unreliable, Lane.Fx)]
    public sealed class MovementSound : Msg
    {
        public Vec3 Position; public string Cue;
        public override void Describe(Codec c) { c.V3(ref Position); c.Str(ref Cue, 128); }
    }

    [Msg(23, Direction.Both, Delivery.Unreliable, Lane.Player)]
    public sealed class GadgetGhost : Msg, IPlayerScoped
    {
        public bool Enabled, Valid; public string PlayerId; public Vec3 Position; public Quat Rotation, LocalRotation; public int Gadget;
        public string ScopedPlayerId => PlayerId;
        public override void Describe(Codec c) { c.Flag(ref Enabled); c.Flag(ref Valid); c.Str(ref PlayerId, 64); c.V3(ref Position); c.QCompact(ref Rotation); c.QCompact(ref LocalRotation); c.Var(ref Gadget); }
    }

    [Msg(24, Direction.Both)]
    public sealed class PlayerUpgrade : Msg
    {
        public int UpgradeId; public int Level; public ulong Revision; public System.Guid RequestId;
        /// <summary>State (host's canonical level), Request (a guest's purchase intent at its current level) or Rejected (to the requester).</summary>
        public PlayerUpgradeKind Kind;
        public override void Describe(Codec c) { c.Var(ref UpgradeId); c.Var(ref Level); c.VarU(ref Revision); c.Guid(ref RequestId); c.Enum(ref Kind); }
    }
    public enum PlayerUpgradeKind : byte { State, Request, Rejected }

    [Msg(25, Direction.HostToGuest)]
    public sealed class InitialUpgrades : Msg
    {
        public Dictionary<int, int> Levels = new Dictionary<int, int>(); public Dictionary<int, ulong> Revisions = new Dictionary<int, ulong>();
        public override void Describe(Codec c) { c.Dict(ref Levels, Items.Var32, Items.Var32); c.Dict(ref Revisions, Items.Var32, (Codec cc, ref ulong v) => cc.VarU(ref v)); }
    }

    [Msg(26, Direction.Both)]
    public sealed class PlayerKick : Msg
    {
        public string KickerId, TargetId; public Vec3 Impulse; public long TargetActorId; public Vec3 LargoImpulse;
        public override void Describe(Codec c) { c.Str(ref KickerId, 64); c.Str(ref TargetId, 64); c.V3(ref Impulse); c.Var(ref TargetActorId); c.V3(ref LargoImpulse); }
    }

    [Msg(27, Direction.Both)]
    public sealed class PlayerPing : Msg, IPlayerScoped
    {
        public string PlayerId; public Vec3 Position, Normal; public int SceneGroup;
        public string ScopedPlayerId => PlayerId;
        public override void Describe(Codec c) { c.Str(ref PlayerId, 64); c.V3(ref Position); c.V3(ref Normal); c.Var(ref SceneGroup); }
    }

    [Msg(28, Direction.Both)]
    public sealed class PlayerVacPalette : Msg, IPlayerScoped
    {
        public string PlayerId, PaletteId;
        public string ScopedPlayerId => PlayerId;
        public override void Describe(Codec c) { c.Str(ref PlayerId, 64); c.Str(ref PaletteId, 128); }
    }

    [Msg(29, Direction.Both, Delivery.Unreliable, Lane.Fx)]
    public sealed class VacSuctionPose : Msg, IPlayerScoped
    {
        public string PlayerId; public uint Sequence; public Vec3 Position; public Quat Rotation; public bool Holding;
        public string ScopedPlayerId => PlayerId;
        public override void Describe(Codec c) { c.Str(ref PlayerId, 64); c.VarU(ref Sequence); c.V3(ref Position); c.QCompact(ref Rotation); c.Flag(ref Holding); }
    }

    /// <summary>A player's cosmetic profile (name colour as "#RRGGBB"); sent on join and whenever it changes, the host relays it stamped with the sender.</summary>
    [Msg(30, Direction.Both)]
    public sealed class PlayerProfile : Msg, IPlayerScoped
    {
        public string PlayerId, Colour;
        public string ScopedPlayerId => PlayerId;
        public override void Describe(Codec c) { c.Str(ref PlayerId, 64); c.Str(ref Colour, 16); }
    }

    /// <summary>Name colours travel as "#RRGGBB"; anything else is rejected.</summary>
    public static class ProfileColour
    {
        /// <summary>"#RRGGBB" (upper case) for "RRGGBB" or "#RRGGBB"; null when invalid.</summary>
        public static string Normalize(string value)
        {
            if (value == null) return null; value = value.Trim(); if (value.StartsWith("#")) value = value.Substring(1);
            if (value.Length != 6) return null;
            foreach (char ch in value) if (!Uri.IsHexDigit(ch)) return null;
            return "#" + value.ToUpperInvariant();
        }
        public static bool TryParse(string value, out byte r, out byte g, out byte b)
        {
            r = g = b = 255; var n = Normalize(value); if (n == null) return false;
            r = Convert.ToByte(n.Substring(1, 2), 16); g = Convert.ToByte(n.Substring(3, 2), 16); b = Convert.ToByte(n.Substring(5, 2), 16); return true;
        }
    }
}

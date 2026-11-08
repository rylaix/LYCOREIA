// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;

namespace Lycoreia.Net.Messages
{
    /// <summary>Optional trailing world revision shared by actor messages.</summary>
    public struct Revision : IWire
    {
        public bool Has; public long Value;
        public void Describe(Codec c) { c.Flag(ref Has); if (Has) c.Var(ref Value); else if (c.Reading) Value = 0; }
    }

    public enum SpawnKind : byte { Normal, Ranch, VacShot, Gadget, Saved }

    /// <summary>Slime-only extras of a spawn.</summary>
    public struct SlimeExtras : IWire
    {
        public float EmoHunger, EmoAgitation, EmoFear, EmoSocial; public bool Sleeping; public int AppearanceA, AppearanceB; public int Radiancy;
        public void Describe(Codec c) { c.F16(ref EmoHunger); c.F16(ref EmoAgitation); c.F16(ref EmoFear); c.F16(ref EmoSocial); c.Flag(ref Sleeping); c.Var(ref AppearanceA); c.Var(ref AppearanceB); c.Var(ref Radiancy); }
    }

    [Msg(50, Direction.Both, Delivery.ReliableOrdered, Lane.ActorCritical)]
    public sealed class ActorSpawn : Msg
    {
        public long ActorId; public Vec3 Position; public Quat Rotation; public int TypeId; public int SceneGroup; public SpawnKind Kind;
        public string OwnerId; public bool HasSlime; public SlimeExtras Slime; public bool HasMaterial; public int MaterialIndex; public bool HasCharge; public double ChargeTime;
        public Revision Rev;
        public override void Describe(Codec c)
        {
            c.Var(ref ActorId); c.V3(ref Position); c.QCompact(ref Rotation); c.Var(ref TypeId); c.Var(ref SceneGroup); c.EnumByte(ref Kind); c.Str(ref OwnerId, 64);
            c.Flag(ref HasSlime); c.Flag(ref HasMaterial); c.Flag(ref HasCharge);
            if (HasSlime) c.WireS(ref Slime); if (HasMaterial) c.Var(ref MaterialIndex); if (HasCharge) c.F64(ref ChargeTime);
            c.WireS(ref Rev);
        }
    }

    [Msg(51, Direction.Both, Delivery.ReliableOrdered, Lane.ActorCritical)]
    public sealed class ActorDestroy : Msg
    {
        public long ActorId; public string OwnerToken; public Revision Rev;
        public override void Describe(Codec c) { c.Var(ref ActorId); c.Str(ref OwnerToken, 64); c.WireS(ref Rev); }
    }

    /// <summary>Transform sample from the owner; high rate, compact rotation.</summary>
    [Msg(52, Direction.Both, Delivery.Unreliable, Lane.ActorUpdate)]
    public sealed class ActorUpdate : Msg
    {
        public long ActorId; public Vec3 Position; public Quat Rotation; public Vec3 Velocity; public uint Epoch; public Revision Rev;
        /// <summary>Full-precision rotation (used by consume-pull samples, whose length is validated); otherwise the compact form.</summary>
        public bool FullRotation;
        /// <summary>Owner samples only: the owner's own vacuum holds or is pulling the actor (nobody may take it meanwhile).</summary>
        public bool Held;
        public override void Describe(Codec c) { c.Flag(ref FullRotation); c.Flag(ref Held); c.Var(ref ActorId); c.V3(ref Position); if (FullRotation) c.Q(ref Rotation); else c.QCompact(ref Rotation); c.V3(ref Velocity); c.VarU(ref Epoch); c.WireS(ref Rev); }
    }

    public enum ActorStateKind : byte { Slime, Produce, Plort }

    [Msg(53, Direction.Both, Delivery.ReliableOrdered, Lane.ActorCritical)]
    public sealed class ActorState : Msg
    {
        public long ActorId; public ActorStateKind Kind;
        public float Hunger, Agitation, Fear, Social; public bool Sleeping;      // Slime
        public double Progress; public int CycleState; public float Scale; public bool Attached;   // Produce (Attached: on its plant's joint at the owner)
        public bool Invulnerable; public float InvulnerablePeriod;               // Plort
        public Revision Rev;
        public override void Describe(Codec c)
        {
            c.Var(ref ActorId); c.EnumByte(ref Kind);
            switch (Kind)
            {
                case ActorStateKind.Slime: c.F16(ref Hunger); c.F16(ref Agitation); c.F16(ref Fear); c.F16(ref Social); c.Bool(ref Sleeping); break;
                case ActorStateKind.Produce: c.F64(ref Progress); c.Var(ref CycleState); c.F32(ref Scale); c.Bool(ref Attached); break;
                case ActorStateKind.Plort: c.Bool(ref Invulnerable); c.F32(ref InvulnerablePeriod); break;
            }
            c.WireS(ref Rev);
        }
    }

    /// <summary>Ownership message with modes: plain transfer, interest claim, and the capture protocol.</summary>
    [Msg(54, Direction.Both, Delivery.ReliableOrdered, Lane.ActorCritical)]
    public sealed class ActorTransfer : Msg
    {
        public long ActorId; public string NewOwner, ExpectedPreviousOwner; public bool HasExpected; public uint Epoch; public Revision Rev;
        public bool Interest, CaptureIntent, CaptureApproved, CaptureCommit, ConsumesActor; public Guid CaptureRequestId; public float InterestDistance;
        public override void Describe(Codec c)
        {
            c.Var(ref ActorId); c.Str(ref NewOwner, 64); c.Str(ref ExpectedPreviousOwner, 64); c.VarU(ref Epoch); c.WireS(ref Rev);
            c.Flag(ref HasExpected); c.Flag(ref Interest); c.Flag(ref CaptureIntent); c.Flag(ref CaptureApproved); c.Flag(ref CaptureCommit); c.Flag(ref ConsumesActor);
            if (CaptureIntent || CaptureApproved || CaptureCommit) c.Guid(ref CaptureRequestId);
            if (Interest) c.F32(ref InterestDistance);
        }
    }

    [Msg(55, Direction.Both, Delivery.ReliableOrdered, Lane.ActorCritical)]
    public sealed class ActorUnload : Msg
    {
        public long ActorId; public string SenderId; public Revision Rev;
        public override void Describe(Codec c) { c.Var(ref ActorId); c.Str(ref SenderId, 64); c.WireS(ref Rev); }
    }

    [Msg(56, Direction.GuestToHost, Delivery.ReliableOrdered, Lane.ActorCritical)]
    public sealed class CaptureAbort : Msg
    {
        public long ActorId; public Guid RequestId;
        public override void Describe(Codec c) { c.Var(ref ActorId); c.Guid(ref RequestId); }
    }

    public struct TypeEntry : IWire { public int Id; public string ReferenceId; public void Describe(Codec c) { c.Var(ref Id); c.Str(ref ReferenceId, 256); } }

    [Msg(57, Direction.HostToGuest)]
    public sealed class ActorTypeRegistry : Msg
    {
        public List<TypeEntry> Types = new List<TypeEntry>();
        public override void Describe(Codec c) { c.List(ref Types, Items.Of<TypeEntry>()); }
    }

    /// <summary>One actor in a snapshot page: spawn data plus owner and epoch.</summary>
    public struct ActorRecord : IWire
    {
        public long ActorId; public Vec3 Position; public Quat Rotation; public int TypeId; public int SceneGroup; public string OwnerId; public uint Epoch;
        public bool HasSlime; public SlimeExtras Slime; public bool HasMaterial; public int MaterialIndex; public bool HasCharge; public double ChargeTime;
        public void Describe(Codec c)
        {
            c.Var(ref ActorId); c.V3(ref Position); c.QCompact(ref Rotation); c.Var(ref TypeId); c.Var(ref SceneGroup); c.Str(ref OwnerId, 64); c.VarU(ref Epoch);
            c.Flag(ref HasSlime); c.Flag(ref HasMaterial); c.Flag(ref HasCharge);
            if (HasSlime) c.WireS(ref Slime); if (HasMaterial) c.Var(ref MaterialIndex); if (HasCharge) c.F64(ref ChargeTime);
        }
    }

    /// <summary>A page of the actor snapshot. All pages of one snapshot share SyncId; the first page carries the header fields.</summary>
    [Msg(58, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)]
    public sealed class InitialActors : Msg
    {
        public long SyncId; public int Page, PageCount; public long StartingActorId; public double WorldTime; public Revision WorldRevision;
        public List<ActorRecord> Actors = new List<ActorRecord>();
        public override void Describe(Codec c)
        {
            c.Var(ref SyncId); c.Var(ref Page); c.Var(ref PageCount);
            if (Page == 0) { c.Var(ref StartingActorId); c.F64(ref WorldTime); c.WireS(ref WorldRevision); }
            c.List(ref Actors, Items.Of<ActorRecord>(), 20);
        }
    }

    [Msg(59, Direction.Both, Delivery.ReliableOrdered, Lane.ActorCritical)]
    public sealed class ResourceAttach : Msg
    {
        public long ActorId; public string PlotId; public int Joint; public Vec3 SpawnerId; public double NextSpawnTime; public float StoredWater;
        public bool NextSpawnRipens, WasPreviouslyPlanted; public Revision Rev;
        public override void Describe(Codec c)
        {
            c.Var(ref ActorId); c.Str(ref PlotId, 256); c.Var(ref Joint); c.V3(ref SpawnerId); c.F64(ref NextSpawnTime); c.F32(ref StoredWater);
            c.Flag(ref NextSpawnRipens); c.Flag(ref WasPreviouslyPlanted); c.WireS(ref Rev);
        }
    }

    // ---- slime behaviour
    [Msg(60, Direction.Both)]
    public sealed class DervishCyclone : Msg
    {
        public long ActorId; public bool Active; public int Size; public Vec3 FloatDir; public long Rev;
        public override void Describe(Codec c) { c.Var(ref ActorId); c.Flag(ref Active); c.Var(ref Size); c.V3(ref FloatDir); c.Var(ref Rev); }
    }
    [Msg(61, Direction.Both)]
    public sealed class SloomberYawn : Msg
    {
        public long ActorId; public bool Active, ForceClear; public long Rev;
        public override void Describe(Codec c) { c.Var(ref ActorId); c.Flag(ref Active); c.Flag(ref ForceClear); c.Var(ref Rev); }
    }
    [Msg(62, Direction.Both)]
    public sealed class TangleVineGrapple : Msg
    {
        public long ActorId, TargetId; public bool Active; public long Rev;
        public override void Describe(Codec c) { c.Var(ref ActorId); c.Var(ref TargetId); c.Flag(ref Active); c.Var(ref Rev); }
    }
    [Msg(63, Direction.Both)]
    public sealed class StalkConsumableTarget : Msg
    {
        public long ActorId, TargetId, Rev;
        public override void Describe(Codec c) { c.Var(ref ActorId); c.Var(ref TargetId); c.Var(ref Rev); }
    }
    public enum SlimeBehaviorKind : byte { Dervish, Sloomber, Tangle, Stalk }
    public struct SlimeBehaviorEntry : IWire
    {
        public SlimeBehaviorKind Kind; public long ActorId, TargetId, Rev; public bool Active, ForceClear; public int Size; public Vec3 FloatDir;
        public void Describe(Codec c) { c.EnumByte(ref Kind); c.Var(ref ActorId); c.Var(ref TargetId); c.Var(ref Rev); c.Bool(ref Active); c.Bool(ref ForceClear); c.Var(ref Size); c.V3(ref FloatDir); }
    }
    [Msg(64, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)]
    public sealed class InitialSlimeBehavior : Msg
    {
        public List<SlimeBehaviorEntry> Entries = new List<SlimeBehaviorEntry>();
        public override void Describe(Codec c) { c.List(ref Entries, Items.Of<SlimeBehaviorEntry>(), 12); }
    }
}

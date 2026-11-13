// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;

namespace Lycoreia.Net.Messages
{
    // ---------- small reusable entry shapes
    public struct IdState : IWire { public string Id; public int State; public void Describe(Codec c) { c.Str(ref Id, 256); c.Var(ref State); } }
    public struct IdFlag : IWire { public string Id; public bool Flag; public void Describe(Codec c) { c.Str(ref Id, 256); c.Flag(ref Flag); } }
    public struct IdTime : IWire { public string Id; public double Time; public void Describe(Codec c) { c.Str(ref Id, 256); c.F64(ref Time); } }
    public struct IdCount : IWire { public string Id; public int Count; public void Describe(Codec c) { c.Str(ref Id, 256); c.Var(ref Count); } }
    public struct IntPair : IWire { public int A, B; public void Describe(Codec c) { c.Var(ref A); c.Var(ref B); } }
    public struct LongPair : IWire { public long A, B; public void Describe(Codec c) { c.Var(ref A); c.Var(ref B); } }
    public struct StrPair : IWire { public string A, B; public void Describe(Codec c) { c.Str(ref A, 256); c.Str(ref B, 256); } }

    /// <summary>Base for simple "initial list" snapshot messages.</summary>
    public abstract class ListMsg<T> : Msg where T : struct, IWire
    {
        public List<T> Items = new List<T>();
        public override void Describe(Codec c) { c.List(ref Items, Net.Items.Of<T>()); }
    }

    // ---------- time, weather, fx (100-119)
    [Msg(100, Direction.HostToGuest, Delivery.Unreliable, Lane.World)]
    public sealed class WorldTime : Msg { public long Revision; public double Time; public override void Describe(Codec c) { c.Var(ref Revision); c.F64(ref Time); } }
    [Msg(101, Direction.Both)]
    public sealed class FastForward : Msg { public double Time; public override void Describe(Codec c) { c.F64(ref Time); } }
    [Msg(102, Direction.Both, Delivery.Unreliable, Lane.Fx)]
    public sealed class WorldFx : Msg { public int Kind; public Vec3 Position; public override void Describe(Codec c) { c.Var(ref Kind); c.V3(ref Position); } }
    [Msg(103, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.World)]
    public sealed class WeatherUpdate : Msg { public long Revision; public byte[] Data = Array.Empty<byte>(); public override void Describe(Codec c) { c.Var(ref Revision); c.Blob(ref Data); } }
    [Msg(104, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)]
    public sealed class InitialWeather : Msg { public long Revision; public byte[] Data = Array.Empty<byte>(); public override void Describe(Codec c) { c.Var(ref Revision); c.Blob(ref Data); } }
    /// <summary>One forecast entry of a zone (the payload of the weather blobs, encoded with <see cref="WeatherForecast"/>).</summary>
    public struct WeatherEntry : IWire
    {
        public string Pattern, State; public double Start, End; public bool EndKnown, Started;
        public void Describe(Codec c) { c.Str(ref Pattern, 128); c.Str(ref State, 128); c.F64(ref Start); c.F64(ref End); c.Flag(ref EndKnown); c.Flag(ref Started); }
    }
    public struct ZoneForecast : IWire { public string Zone; public List<WeatherEntry> Entries; public void Describe(Codec c) { c.Str(ref Zone, 256); c.List(ref Entries, Items.Of<WeatherEntry>()); } }
    /// <summary>The whole host forecast (all zones) carried as the weather blob.</summary>
    public sealed class WeatherForecast : IWire { public List<ZoneForecast> Zones = new List<ZoneForecast>(); public void Describe(Codec c) { c.List(ref Zones, Items.Of<ZoneForecast>()); } }
    [Msg(105, Direction.Both)]
    public sealed class LightningStrike : Msg { public Vec3 Position; public override void Describe(Codec c) { c.V3(ref Position); } }
    [Msg(106, Direction.HostToGuest)]
    public sealed class TornadoSpawn : Msg
    {
        public int TornadoId; public bool Despawn; public Vec3 Position; public Quat Rotation;
        public override void Describe(Codec c) { c.Var(ref TornadoId); c.Flag(ref Despawn); c.V3(ref Position); c.QCompact(ref Rotation); }
    }

    // ---------- land plots, gardens (120-139)
    [Msg(120, Direction.Both)] public sealed class NewLandPlot : Msg { public string PlotId; public int Type; public override void Describe(Codec c) { c.Str(ref PlotId, 256); c.Var(ref Type); } }
    [Msg(121, Direction.Both)] public sealed class LandPlotUpgrade : Msg { public string PlotId; public int Upgrade; public override void Describe(Codec c) { c.Str(ref PlotId, 256); c.Var(ref Upgrade); } }
    public struct PlotRecord : IWire
    {
        /// <summary>Crop: the garden's crop type id (0 empty); CropRevision: the garden's revision stream position the crop belongs to.</summary>
        public string Id; public int Type; public List<int> Upgrades; public int Crop; public long CropRevision;
        public void Describe(Codec c) { c.Str(ref Id, 256); c.Var(ref Type); c.List(ref Upgrades, Items.Var32); c.Var(ref Crop); c.Var(ref CropRevision); }
    }
    [Msg(122, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)] public sealed class InitialLandPlots : ListMsg<PlotRecord> { }
    [Msg(123, Direction.Both)]
    public sealed class GardenPlant : Msg
    {
        public string PlotId, GardenId; public int ActorType; public long Revision;
        public override void Describe(Codec c) { c.Str(ref PlotId, 256); c.Str(ref GardenId, 256); c.Var(ref ActorType); c.Var(ref Revision); }
    }
    [Msg(124, Direction.Both)]
    public sealed class GardenUpdate : Msg
    {
        public string GardenId; public double NextSpawnTime; public float StoredWater; public bool NextSpawnRipens; public long Revision;
        public override void Describe(Codec c) { c.Str(ref GardenId, 256); c.F64(ref NextSpawnTime); c.F32(ref StoredWater); c.Flag(ref NextSpawnRipens); c.Var(ref Revision); }
    }
    [Msg(125, Direction.Both)]
    public sealed class GardenOwnership : Msg
    {
        public string GardenId, OwnerId; public bool HasFinalState;
        public override void Describe(Codec c) { c.Str(ref GardenId, 256); c.Str(ref OwnerId, 64); c.Flag(ref HasFinalState); }
    }
    [Msg(126, Direction.Both)] public sealed class AutoFeederSpeed : Msg { public string Id; public int Speed; public override void Describe(Codec c) { c.Str(ref Id, 256); c.Var(ref Speed); } }
    [Msg(127, Direction.Both)] public sealed class AutoFeederDispense : Msg { public string Id; public double NextTime; public override void Describe(Codec c) { c.Str(ref Id, 256); c.F64(ref NextTime); } }
    [Msg(128, Direction.Both)] public sealed class PlortCollection : Msg { public string Id; public double EndTime; public override void Describe(Codec c) { c.Str(ref Id, 256); c.F64(ref EndTime); } }
    [Msg(129, Direction.Both)]
    public sealed class SiloSlotSelect : Msg
    {
        public int Side, Index; public string Id;
        public override void Describe(Codec c) { c.Var(ref Side); c.Var(ref Index); c.Str(ref Id, 256); }
    }

    // ---------- gordos (140-149)
    [Msg(140, Direction.Both)]
    public sealed class GordoFeed : Msg
    {
        public string Id, RequestId; public long FoodActorId; public int NewFoodCount, RequiredFoodCount, GordoType, FoodType;
        public override void Describe(Codec c) { c.Str(ref Id, 256); c.Str(ref RequestId, 64); c.Var(ref FoodActorId); c.Var(ref NewFoodCount); c.Var(ref RequiredFoodCount); c.Var(ref GordoType); c.Var(ref FoodType); }
    }
    [Msg(141, Direction.Both)]
    public sealed class GordoBurst : Msg
    {
        public string Id, RewardAuthorityId; public int EatenCount, RequiredFoodCount, GordoType;
        public override void Describe(Codec c) { c.Str(ref Id, 256); c.Var(ref EatenCount); c.Var(ref RequiredFoodCount); c.Var(ref GordoType); c.Str(ref RewardAuthorityId, 64); }
    }
    public struct GordoRecord : IWire
    {
        public string Id; public int Eaten, Required, Type; public bool WasSeen, Popped; public List<int> ActiveRewards;
        public void Describe(Codec c) { c.Str(ref Id, 256); c.Var(ref Eaten); c.Var(ref Required); c.Var(ref Type); c.Flag(ref WasSeen); c.Flag(ref Popped); c.List(ref ActiveRewards, Items.Var32); }
    }
    [Msg(142, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)] public sealed class InitialGordos : ListMsg<GordoRecord> { }
    [Msg(143, Direction.Both)]
    public sealed class GordoSeen : Msg
    {
        public string Id; public int EatenCount, RequiredFoodCount, GordoType;
        public override void Describe(Codec c) { c.Str(ref Id, 256); c.Var(ref EatenCount); c.Var(ref RequiredFoodCount); c.Var(ref GordoType); }
    }
    [Msg(144, Direction.Both)]
    public sealed class GordoReward : Msg
    {
        public string GordoId; public int PrefabIndex, RewardOrdinal; public Vec3 Position; public Quat Rotation;
        public override void Describe(Codec c) { c.Str(ref GordoId, 256); c.Var(ref PrefabIndex); c.Var(ref RewardOrdinal); c.V3(ref Position); c.QCompact(ref Rotation); }
    }

    // ---------- switches, doors, geysers, map, pedia, nodes (150-189)
    [Msg(150, Direction.Both)] public sealed class SwitchActivate : Msg { public string Id; public int State; public bool Immediate; public override void Describe(Codec c) { c.Str(ref Id, 256); c.Var(ref State); c.Flag(ref Immediate); } }
    [Msg(151, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)] public sealed class InitialSwitches : ListMsg<IdState> { }
    [Msg(152, Direction.Both, Delivery.ReliableOrdered, Lane.Fx)] public sealed class GeyserTrigger : Msg { public string ObjectPath; public float Duration; public override void Describe(Codec c) { c.Str(ref ObjectPath, 512); c.F32(ref Duration); } }
    [Msg(153, Direction.Both)] public sealed class MapUnlock : Msg { public string NodeId; public override void Describe(Codec c) { c.Str(ref NodeId, 256); } }
    [Msg(154, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)] public sealed class InitialMap : Msg { public List<string> Nodes = new List<string>(); public override void Describe(Codec c) { c.List(ref Nodes, Items.Str); } }
    [Msg(155, Direction.Both)] public sealed class AccessDoor : Msg { public string Id; public int State; public override void Describe(Codec c) { c.Str(ref Id, 256); c.Var(ref State); } }
    [Msg(156, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)] public sealed class InitialAccessDoors : ListMsg<IdState> { }
    [Msg(157, Direction.Both)] public sealed class PediaUnlock : Msg { public string Id; public bool Popup; public override void Describe(Codec c) { c.Str(ref Id, 256); c.Flag(ref Popup); } }
    [Msg(158, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)] public sealed class InitialPedia : Msg { public List<string> Entries = new List<string>(); public override void Describe(Codec c) { c.List(ref Entries, Items.Str); } }
    public struct NodePlacement : IWire
    {
        public string Id, Definition; public bool HasNode; public int State, VariantIndex; public double DespawnAtWorldTime; public List<int> TypeIds;
        public void Describe(Codec c) { c.Str(ref Id, 256); c.Flag(ref HasNode); c.Var(ref State); c.Str(ref Definition, 256); c.Var(ref VariantIndex); c.F64(ref DespawnAtWorldTime); c.List(ref TypeIds, Items.Var32); }
    }
    /// <summary>One resource node spawner's state. <c>Emptied</c>: a harvest report (state change only) instead of a placement.</summary>
    [Msg(159, Direction.Both)] public sealed class NodeChange : Msg { public NodePlacement P; public bool Emptied; public override void Describe(Codec c) { P.Describe(c); c.Flag(ref Emptied); } }
    [Msg(160, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)] public sealed class InitialResourceNodes : ListMsg<IdState> { }
    [Msg(161, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)] public sealed class ResourceNodePlacements : ListMsg<NodePlacement> { }
    [Msg(162, Direction.Both)] public sealed class ConversationPlayed : Msg { public string Name; public override void Describe(Codec c) { c.Str(ref Name, 256); } }
    [Msg(163, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)] public sealed class InitialConversations : Msg { public List<string> Names = new List<string>(); public override void Describe(Codec c) { c.List(ref Names, Items.Str); } }
    [Msg(164, Direction.Both)] public sealed class EventRaised : Msg { public string EventKey, DataKey; public int Count; public override void Describe(Codec c) { c.Str(ref EventKey, 256); c.Str(ref DataKey, 256); c.Var(ref Count); } }
    public struct EventRecord : IWire { public string EventKey, DataKey; public int Count; public void Describe(Codec c) { c.Str(ref EventKey, 256); c.Str(ref DataKey, 256); c.Var(ref Count); } }
    [Msg(165, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)] public sealed class InitialEvents : ListMsg<EventRecord> { }

    // ---------- treasure pods, ammo, refinery, components (190-229)
    public struct PodAppearance : IWire { public string SlimeDefinition; public int SaveSet; public void Describe(Codec c) { c.Str(ref SlimeDefinition, 256); c.Var(ref SaveSet); } }
    /// <summary>The rewards a pod is configured with (reference ids). Receivers grant blueprints/appearances model-only; component counts come from their own snapshot.</summary>
    public struct PodRewards : IWire
    {
        public List<string> Blueprints, Components; public List<PodAppearance> Appearances;
        public void Describe(Codec c) { c.List(ref Blueprints, Items.Str); c.List(ref Components, Items.Str); c.List(ref Appearances, Items.Of<PodAppearance>()); }
    }
    [Msg(190, Direction.Both)]
    public sealed class PodOpen : Msg
    {
        /// <summary>Guest to host: an open request (RequestId), a delegated-activation report (ClientActivated, Rewards) or decline (ClientActivated + Rejected).
        /// Host to guest: the committed open state (Rewards), a rejection (Rejected) or an activation grant (ClientActivated).</summary>
        public int PodId; public uint RequestId; public bool Rejected, ClientActivated; public PodRewards Rewards;
        public override void Describe(Codec c) { c.Var(ref PodId); c.VarU(ref RequestId); c.Flag(ref Rejected); c.Flag(ref ClientActivated); Rewards.Describe(c); }
    }
    [Msg(191, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)]
    public sealed class InitialTreasurePods : Msg
    {
        public Dictionary<int, int> Pods = new Dictionary<int, int>(); public Dictionary<int, PodRewards> Rewards = new Dictionary<int, PodRewards>();
        public override void Describe(Codec c) { c.Dict(ref Pods, Items.Var32, Items.Var32); c.Dict(ref Rewards, Items.Var32, Items.Of<PodRewards>()); }
    }
    [Msg(192, Direction.Both)]
    public sealed class AmmoAdd : Msg
    {
        public int Identifiable, Count; public bool Radiant; public string StorageId;
        public override void Describe(Codec c) { c.Var(ref Identifiable); c.Var(ref Count); c.Flag(ref Radiant); c.Str(ref StorageId, 256); }
    }
    [Msg(193, Direction.Both)]
    public sealed class AmmoAddToSlot : Msg
    {
        public int Identifiable, Slot, Count; public bool Radiant; public string StorageId;
        public override void Describe(Codec c) { c.Var(ref Identifiable); c.Var(ref Slot); c.Var(ref Count); c.Flag(ref Radiant); c.Str(ref StorageId, 256); }
    }
    [Msg(194, Direction.Both)]
    public sealed class AmmoDecrement : Msg
    {
        public int Slot, Count; public string StorageId;
        public override void Describe(Codec c) { c.Var(ref Slot); c.Var(ref Count); c.Str(ref StorageId, 256); }
    }
    [Msg(195, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)]
    public sealed class AmmoStorageSnapshot : Msg { public string StorageId; public byte[] Data = Array.Empty<byte>(); public override void Describe(Codec c) { c.Str(ref StorageId, 256); c.Blob(ref Data); } }
    [Msg(196, Direction.Both, Delivery.ReliableOrdered, Lane.Economy)]
    public sealed class RefineryUpdate : Msg
    {
        public Guid RequestId; public int Delta, ItemCount, ItemId; public long Revision;
        public override void Describe(Codec c) { c.Guid(ref RequestId); c.Var(ref Delta); c.Var(ref ItemCount); c.Var(ref ItemId); c.Var(ref Revision); }
    }
    [Msg(197, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)]
    public sealed class InitialRefinery : Msg
    {
        public long Revision; public Dictionary<int, int> Items = new Dictionary<int, int>();
        public override void Describe(Codec c) { c.Var(ref Revision); c.Dict(ref Items, Net.Items.Var32, Net.Items.Var32); }
    }
    [Msg(198, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)]
    public sealed class InitialComponents : Msg { public Dictionary<string, int> Items = new Dictionary<string, int>(); public override void Describe(Codec c) { c.Dict(ref Items, Net.Items.Str, Net.Items.Var32); } }
    [Msg(199, Direction.Both)] public sealed class ComponentAdd : Msg { public string ComponentId; public int Count; public override void Describe(Codec c) { c.Str(ref ComponentId, 256); c.Var(ref Count); } }

    // ---------- puzzles, depositors, prisma (230-249)
    [Msg(230, Direction.Both)] public sealed class PuzzleSlot : Msg { public string Id; public bool Filled; public override void Describe(Codec c) { c.Str(ref Id, 256); c.Flag(ref Filled); } }
    [Msg(231, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)] public sealed class InitialPuzzleSlots : ListMsg<IdFlag> { }
    [Msg(232, Direction.Both)] public sealed class PlortDepositor : Msg { public string Id; public int Amount; public override void Describe(Codec c) { c.Str(ref Id, 256); c.Var(ref Amount); } }
    [Msg(233, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)] public sealed class InitialPlortDepositors : ListMsg<IdCount> { }
    [Msg(234, Direction.Both)] public sealed class PrismaBarrier : Msg { public string Id; public double ActivationTime; public override void Describe(Codec c) { c.Str(ref Id, 256); c.F64(ref ActivationTime); } }
    [Msg(235, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)] public sealed class InitialPrismaBarriers : ListMsg<IdTime> { }
    /// <summary>The whole prisma disruption model: areas -> forecast patterns -> entries (the guest swaps the full replacement in at once).</summary>
    [Msg(236, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.World)]
    public sealed class PrismaDisruptionState : Msg
    {
        public double ForecastedTime; public bool AreaRefreshNeeded; public List<Area> Areas = new List<Area>();
        public sealed class Entry : IWire { public double StartTime; public int Level; public void Describe(Codec c) { c.F64(ref StartTime); c.Var(ref Level); } }
        public sealed class Pattern : IWire { public double EndTime; public List<Entry> Entries = new List<Entry>(); public void Describe(Codec c) { c.F64(ref EndTime); c.List(ref Entries, Items.OfClass<Entry>()); } }
        public sealed class Area : IWire
        {
            public string ReferenceId; public double LastCompletionTime, NextChangeTime; public bool ForecastUnlocked; public List<Pattern> Forecast = new List<Pattern>();
            public void Describe(Codec c) { c.Str(ref ReferenceId, 256); c.F64(ref LastCompletionTime); c.F64(ref NextChangeTime); c.Flag(ref ForecastUnlocked); c.List(ref Forecast, Items.OfClass<Pattern>()); }
        }
        public override void Describe(Codec c) { c.F64(ref ForecastedTime); c.Flag(ref AreaRefreshNeeded); c.List(ref Areas, Items.OfClass<Area>()); }
    }
    /// <summary>Stabilising field spawners (host broadcast of changed spawners, or the full set as the join snapshot).</summary>
    [Msg(237, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.World)]
    public sealed class StabilizingSpawnerState : Msg
    {
        public bool IsSnapshot; public List<Spawner> Spawners = new List<Spawner>();
        public sealed class Spawner : IWire
        {
            public string Id; public double NextTriggerTime, DespawnAtWorldTime; public bool HasSpawned;
            public void Describe(Codec c) { c.Str(ref Id, 256); c.F64(ref NextTriggerTime); c.F64(ref DespawnAtWorldTime); c.Flag(ref HasSpawned); }
        }
        public override void Describe(Codec c) { c.Flag(ref IsSnapshot); c.List(ref Spawners, Items.OfClass<Spawner>()); }
    }

    // ---------- drones (250-279)
    [Msg(250, Direction.Both)]
    public sealed class DroneTask : Msg
    {
        public long ActorId; public int TargetIdent; public byte Target, Sink, Source;
        /// <summary>A non-owner's UI assignment: the host routes it to the lease owner, who applies and republishes it as owner.</summary>
        public bool Intent;
        public override void Describe(Codec c) { c.Var(ref ActorId); c.Var(ref TargetIdent); c.U8(ref Target); c.U8(ref Sink); c.U8(ref Source); c.Flag(ref Intent); }
    }
    [Msg(251, Direction.Both)]
    public sealed class DroneBattery : Msg
    {
        /// <summary>Owner: Charge is the absolute energy. Intent (fuel added on a non-owner): Charge is the amount added.</summary>
        public long ActorId; public float Charge, DepletedPerHour; public bool Intent;
        public override void Describe(Codec c) { c.Var(ref ActorId); c.F32(ref Charge); c.F32(ref DepletedPerHour); c.Flag(ref Intent); }
    }
    [Msg(252, Direction.Both, Delivery.Unreliable, Lane.ActorUpdate)]
    public sealed class DroneUpdate : Msg
    {
        public long StationId; public Vec3 Position; public Quat Rotation; public bool AtStation; public int AnimatorState; public byte Kind; public int Hash; public byte ParamType; public float Value;
        public bool HasAmmo; public byte[] Ammo = Array.Empty<byte>();
        public override void Describe(Codec c)
        {
            c.Var(ref StationId); c.V3(ref Position); c.QCompact(ref Rotation); c.Flag(ref AtStation); c.Flag(ref HasAmmo); c.Var(ref AnimatorState); c.U8(ref Kind); c.Var(ref Hash); c.U8(ref ParamType); c.F32(ref Value);
            if (HasAmmo) c.Blob(ref Ammo);
        }
    }
    [Msg(253, Direction.Both)] public sealed class DroneOwnership : Msg { public long StationId; public string ClaimerId, PreviousOwnerId; public override void Describe(Codec c) { c.Var(ref StationId); c.Str(ref ClaimerId, 64); c.Str(ref PreviousOwnerId, 64); } }
    [Msg(254, Direction.Both)] public sealed class DroneCloud : Msg
    {
        /// <summary>Guest to host: IsDelta, Amount is a signed change. Host to guests: Amount is the canonical absolute amount.</summary>
        public int TypeId, Amount; public bool IsDelta;
        public override void Describe(Codec c) { c.Var(ref TypeId); c.Var(ref Amount); c.Flag(ref IsDelta); }
    }
    [Msg(255, Direction.Both)]
    public sealed class DroneCloudExtraction : Msg { public long StationId; public bool Extracting; public int TypeId, Count; public override void Describe(Codec c) { c.Var(ref StationId); c.Flag(ref Extracting); c.Var(ref TypeId); c.Var(ref Count); } }
    [Msg(256, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)] public sealed class InitialDroneCloud : Msg { public Dictionary<int, int> Amounts = new Dictionary<int, int>(); public override void Describe(Codec c) { c.Dict(ref Amounts, Items.Var32, Items.Var32); } }
    public enum DroneEventKind { Deposit, Withdraw, Arrive, Leave }
    [Msg(257, Direction.Both)]
    public sealed class DroneEvent : Msg { public DroneEventKind Kind; public long StationId; public int Scene; public double WorldTime; public int TypeId, Count; public override void Describe(Codec c) { c.Enum(ref Kind); c.Var(ref StationId); c.Var(ref Scene); c.F64(ref WorldTime); c.Var(ref TypeId); c.Var(ref Count); } }
    [Msg(258, Direction.Both)] public sealed class DroneAnimation : Msg { public long StationId; public int Animation; public override void Describe(Codec c) { c.Var(ref StationId); c.Var(ref Animation); } }
    public struct DroneResourceGroup : IWire { public int Scene, TypeId, Count; public void Describe(Codec c) { c.Var(ref Scene); c.Var(ref TypeId); c.Var(ref Count); } }
    [Msg(259, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)] public sealed class InitialDroneResources : ListMsg<DroneResourceGroup> { }
    [Msg(260, Direction.Both)]
    public sealed class DroneResourceUpdate : Msg
    {
        public bool Valid; public string Id; public Vec3 Location, OffsetLocation; public int Count, TypeId;
        public override void Describe(Codec c) { c.Flag(ref Valid); c.Str(ref Id, 256); c.V3(ref Location); c.V3(ref OffsetLocation); c.Var(ref Count); c.Var(ref TypeId); }
    }

    // ---------- gadgets and teleporters (280-299)
    [Msg(280, Direction.Both)] public sealed class TeleporterFx : Msg { public string ObjectPath; public bool IsArrival; public override void Describe(Codec c) { c.Str(ref ObjectPath, 512); c.Flag(ref IsArrival); } }
    [Msg(281, Direction.Both)] public sealed class GadgetLinking : Msg { public long GadgetId, PartnerId; public override void Describe(Codec c) { c.Var(ref GadgetId); c.Var(ref PartnerId); } }
    [Msg(282, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)] public sealed class InitialGadgetLinks : ListMsg<LongPair> { }
    [Msg(283, Direction.Both)] public sealed class TeleporterLink : Msg { public string SourceNodeId, DestinationNodeId; public int DestinationSceneGroup; public override void Describe(Codec c) { c.Str(ref SourceNodeId, 256); c.Str(ref DestinationNodeId, 256); c.Var(ref DestinationSceneGroup); } }
    public struct TeleporterRecord : IWire { public string Source, Destination; public int SceneGroup; public void Describe(Codec c) { c.Str(ref Source, 256); c.Str(ref Destination, 256); c.Var(ref SceneGroup); } }
    [Msg(284, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)] public sealed class InitialTeleporterLinks : ListMsg<TeleporterRecord> { }
    [Msg(285, Direction.Both)] public sealed class TeleporterUnlink : Msg { public string SourceNodeId, DestinationNodeId; public override void Describe(Codec c) { c.Str(ref SourceNodeId, 256); c.Str(ref DestinationNodeId, 256); } }
    [Msg(286, Direction.Both)] public sealed class CannonFire : Msg { public long GadgetId; public double NextFireTime; public override void Describe(Codec c) { c.Var(ref GadgetId); c.F64(ref NextFireTime); } }
    /// <summary>A placed gadget or decoration (pre-placed ones are matched by type and position, their ids differ per save).</summary>
    public struct GadgetRecord : IWire
    {
        public long Id; public int TypeId, Scene; public Vec3 Position, Euler; public bool PrePlaced;
        public void Describe(Codec c) { c.Var(ref Id); c.Var(ref TypeId); c.Var(ref Scene); c.V3(ref Position); c.V3(ref Euler); c.Flag(ref PrePlaced); }
    }
    [Msg(287, Direction.Both)] public sealed class GadgetPlaced : Msg { public GadgetRecord G; public override void Describe(Codec c) { G.Describe(c); } }
    [Msg(288, Direction.Both)] public sealed class GadgetRemoved : Msg { public GadgetRecord G; public override void Describe(Codec c) { G.Describe(c); } }
    [Msg(289, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)] public sealed class InitialGadgets : ListMsg<GadgetRecord> { }

    // ---------- economy, progression, sanctuary, shop (400-449)
    [Msg(400, Direction.Both, Delivery.ReliableOrdered, Lane.Economy)]
    public sealed class CurrencyAdjust : Msg
    {
        public int CurrencyType; public Guid RequestId; public ulong Revision; public int NewAmount, Delta, Before, After, Requested, SourceIdent; public bool ShowNotification;
        public override void Describe(Codec c)
        {
            c.Var(ref CurrencyType); c.Guid(ref RequestId); c.VarU(ref Revision); c.Var(ref NewAmount); c.Var(ref Delta); c.Var(ref Before); c.Var(ref After); c.Var(ref Requested); c.Var(ref SourceIdent); c.Flag(ref ShowNotification);
        }
    }
    [Msg(401, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Economy)]
    public sealed class MarketPrices : Msg { public List<IntPair> Prices = new List<IntPair>(); public override void Describe(Codec c) { c.List(ref Prices, Items.Of<IntPair>()); } }
    [Msg(402, Direction.GuestToHost, Delivery.ReliableOrdered, Lane.Economy)]
    public sealed class PlortSold : Msg { public Guid RequestId; public int ItemId, Count, Price; public override void Describe(Codec c) { c.Guid(ref RequestId); c.Var(ref ItemId); c.Var(ref Count); c.Var(ref Price); } }
    /// <summary>Unlock-only union of blueprints (type ids), slime appearances (slime type id + save set) and owned palettes (reference ids).</summary>
    [Msg(403, Direction.Both, Delivery.ReliableOrdered, Lane.Economy)]
    public sealed class ProgressionUnlocks : Msg
    {
        public List<int> BlueprintIds = new List<int>(); public List<AppearanceUnlock> Appearances = new List<AppearanceUnlock>(); public List<string> OwnedPalettes = new List<string>();
        public sealed class AppearanceUnlock : IWire { public int SlimeId, SaveSet; public void Describe(Codec c) { c.Var(ref SlimeId); c.Var(ref SaveSet); } }
        public override void Describe(Codec c) { c.List(ref BlueprintIds, Items.Var32); c.List(ref Appearances, Items.OfClass<AppearanceUnlock>()); c.List(ref OwnedPalettes, Items.Str); }
    }
    /// <summary>One sanctuary room: theme (null/empty = vanilla) and its preplaced gadgets.</summary>
    public sealed class SanctuaryRoomRecord : IWire
    {
        public string Id, ThemeReferenceId; public List<PreplacedGadget> PreplacedGadgets = new List<PreplacedGadget>();
        public sealed class PreplacedGadget : IWire { public string Id; public long ActorId; public void Describe(Codec c) { c.Str(ref Id, 256); c.Var(ref ActorId); } }
        public void Describe(Codec c) { c.Str(ref Id, 256); c.Str(ref ThemeReferenceId, 256); c.List(ref PreplacedGadgets, Items.OfClass<PreplacedGadget>()); }
    }
    [Msg(404, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Snapshot)]
    public sealed class InitialSanctuary : Msg
    {
        public List<SanctuaryRoomRecord> Rooms = new List<SanctuaryRoomRecord>(); public List<string> OwnedThemeReferenceIds = new List<string>();
        public override void Describe(Codec c) { c.List(ref Rooms, Items.OfClass<SanctuaryRoomRecord>()); c.List(ref OwnedThemeReferenceIds, Items.Str); }
    }
    [Msg(405, Direction.Both, Delivery.ReliableOrdered, Lane.World)] public sealed class SanctuaryRoomState : Msg { public SanctuaryRoomRecord State = new SanctuaryRoomRecord(); public override void Describe(Codec c) { c.Wire(ref State); } }
    [Msg(406, Direction.Both, Delivery.ReliableOrdered, Lane.World)] public sealed class SanctuaryTheme : Msg { public string ThemeReferenceId; public bool IsOwned; public override void Describe(Codec c) { c.Str(ref ThemeReferenceId, 256); c.Flag(ref IsOwned); } }
    [Msg(410, Direction.GuestToHost, Delivery.ReliableOrdered, Lane.Economy)]
    public sealed class ShopPurchaseRequest : Msg { public Guid RequestId; public string ShopAssetTag, ItemAssetGuid; public int Count; public override void Describe(Codec c) { c.Guid(ref RequestId); c.Str(ref ShopAssetTag, 256); c.Str(ref ItemAssetGuid, 64); c.Var(ref Count); } }
    [Msg(411, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Economy)]
    public sealed class ShopStateSnapshot : Msg { public Guid SessionId; public ulong Revision; public byte[] State = Array.Empty<byte>(); public override void Describe(Codec c) { c.Guid(ref SessionId); c.VarU(ref Revision); c.Blob(ref State, 4 << 20); } }
    [Msg(412, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.Economy)]
    public sealed class ShopPurchaseResult : Msg { public Guid RequestId; public bool Accepted; public override void Describe(Codec c) { c.Guid(ref RequestId); c.Flag(ref Accepted); } }
    // Spec 02: host progression owns external teleporter activation, independently of destination links.
    [Msg(413, Direction.HostToGuest, Delivery.ReliableOrdered, Lane.World)]
    public sealed class TeleporterActivation : Msg
    {
        public string NodeId; public bool Waiting;
        public override void Describe(Codec c) { c.Str(ref NodeId, 256); c.Flag(ref Waiting); }
    }
}

namespace Lycoreia.Net.Messages
{
    /// <summary>2.1: one plot storage slot on the wire.</summary>
    public struct SiloSlotWire : IWire
    {
        public int Type, Count;
        public void Describe(Codec c) { c.Var(ref Type); c.Var(ref Count); }
    }

    /// <summary>2.1: the host's whole content of one plot storage (silo, corral collector, feeder); absolute, the guest overwrites its copy.</summary>
    [Msg(66, Direction.HostToGuest, Delivery.ReliableOrdered)]
    public sealed class SiloState : Msg
    {
        public string Plot, Set; public List<SiloSlotWire> Slots = new List<SiloSlotWire>();
        public override void Describe(Codec c) { c.Str(ref Plot, 256); c.Str(ref Set, 256); c.List(ref Slots, Items.Of<SiloSlotWire>(), 2); }
    }

    /// <summary>2.1: a guest changed one slot locally (vacuumed into or out of a silo); the host applies it and answers with SiloState.</summary>
    [Msg(67, Direction.GuestToHost, Delivery.ReliableOrdered)]
    public sealed class SiloDelta : Msg
    {
        public string Plot, Set; public int Slot, Type, Delta;
        public override void Describe(Codec c) { c.Str(ref Plot, 256); c.Str(ref Set, 256); c.Var(ref Slot); c.Var(ref Type); c.Var(ref Delta); }
    }
}

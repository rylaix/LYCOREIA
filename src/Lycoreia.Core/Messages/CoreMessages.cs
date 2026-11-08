// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System.Collections.Generic;

namespace Lycoreia.Net.Messages
{
    /// <summary>Id ranges: 1-19 session core, 20-49 players, 50-99 actors, 100-399 world, 400-499 economy and progression, 900+ api, 1000+ test.</summary>
    public static class Ids
    {
        public const ushort Connect = 1, ModSyncRequest = 2, ModSyncReply = 3, Approve = 4, Deny = 5, Close = 6, Heartbeat = 7, HeartbeatAck = 8,
            Resync = 9, PlayerJoin = 10, PlayerJoined = 11, PlayerLeave = 12, PlayerLeft = 13, Chat = 14, ApiCall = 900;
    }

    public struct PlayerEntry : IWire
    {
        public string Id, Name;
        public void Describe(Codec c) { c.Str(ref Id, 64); c.Str(ref Name, 128); }
    }
    public struct ModEntry : IWire
    {
        public uint Id; public string Name, Version;
        public void Describe(Codec c) { c.VarU(ref Id); c.Str(ref Name, 128); c.Str(ref Version, 64); }
    }
    public struct ModNetId : IWire
    {
        public uint ModId; public byte NetId;
        public void Describe(Codec c) { c.VarU(ref ModId); c.U8(ref NetId); }
    }

    [Msg(Ids.Connect, Direction.GuestToHost, AllowUnregistered = true)]
    public sealed class Connect : Msg
    {
        public string PlayerId, Name, Build;
        public override void Describe(Codec c) { c.Str(ref PlayerId, 64); c.Str(ref Name, 128); c.Str(ref Build, 64); }
    }

    [Msg(Ids.ModSyncRequest, Direction.HostToGuest)]
    public sealed class ModSyncRequest : Msg { public override void Describe(Codec c) { } }

    [Msg(Ids.ModSyncReply, Direction.GuestToHost)]
    public sealed class ModSyncReply : Msg, IPlayerScoped
    {
        public string PlayerId; public List<ModEntry> Mods = new List<ModEntry>();
        public string ScopedPlayerId => PlayerId;
        public override void Describe(Codec c) { c.Str(ref PlayerId, 64); c.List(ref Mods, Items.Of<ModEntry>()); }
    }

    [Msg(Ids.Approve, Direction.HostToGuest)]
    public sealed class Approve : Msg
    {
        public bool InitialJoin, AllowCheats;
        public string YourId, HostId; public long SyncId;
        public List<PlayerEntry> Players = new List<PlayerEntry>();
        public Dictionary<int, int> Currencies = new Dictionary<int, int>();
        public Dictionary<int, ulong> CurrencyRevisions = new Dictionary<int, ulong>();
        public List<ModNetId> Mods = new List<ModNetId>();
        public override void Describe(Codec c)
        {
            c.Flag(ref InitialJoin); c.Flag(ref AllowCheats);
            c.Str(ref YourId, 64); c.Str(ref HostId, 64); c.Var(ref SyncId);
            c.List(ref Players, Items.Of<PlayerEntry>());
            c.Dict(ref Currencies, Items.Var32, Items.Var32);
            c.Dict(ref CurrencyRevisions, Items.Var32, (Codec cc, ref ulong v) => cc.VarU(ref v));
            c.List(ref Mods, Items.Of<ModNetId>());
        }
    }

    [Msg(Ids.Deny, Direction.HostToGuest)]
    public sealed class Deny : Msg { public string Reason; public override void Describe(Codec c) { c.Str(ref Reason, 1024); } }

    [Msg(Ids.Close, Direction.HostToGuest)]
    public sealed class Close : Msg { public override void Describe(Codec c) { } }

    [Msg(Ids.Heartbeat, Direction.GuestToHost, Delivery.Unreliable)]
    public sealed class Heartbeat : Msg { public override void Describe(Codec c) { } }

    [Msg(Ids.HeartbeatAck, Direction.HostToGuest, Delivery.Unreliable)]
    public sealed class HeartbeatAck : Msg { public override void Describe(Codec c) { } }

    [Msg(Ids.Resync, Direction.GuestToHost)]
    public sealed class ResyncRequest : Msg { public bool Automatic; public override void Describe(Codec c) { c.Bool(ref Automatic); } }

    [Msg(Ids.PlayerJoin, Direction.GuestToHost)]
    public sealed class PlayerJoin : Msg { public override void Describe(Codec c) { } }

    [Msg(Ids.PlayerJoined, Direction.HostToGuest)]
    public sealed class PlayerJoined : Msg { public PlayerEntry Player; public override void Describe(Codec c) { c.WireS(ref Player); } }

    [Msg(Ids.PlayerLeave, Direction.GuestToHost)]
    public sealed class PlayerLeave : Msg { public override void Describe(Codec c) { } }

    [Msg(Ids.PlayerLeft, Direction.HostToGuest)]
    public sealed class PlayerLeft : Msg { public string PlayerId; public override void Describe(Codec c) { c.Str(ref PlayerId, 64); } }

    public enum ChatKind : byte { Player = 0, System = 1 }

    [Msg(Ids.Chat, Direction.Both, Delivery.ReliableOrdered, Lane.Chat)]
    public sealed class ChatMessage : Msg
    {
        public string Username, Text, MessageId; public ChatKind Kind;
        public override void Describe(Codec c) { c.Str(ref Username, 128); c.Str(ref Text, 4096); c.Str(ref MessageId, 64); c.EnumByte(ref Kind); }
    }
}

// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Lycoreia.Net;
using Lycoreia.Net.Messages;

namespace Lycoreia
{
    /// <summary>Where a registered mod runs. None means shared (compared in mod sync) for backward habit; see ModSide.</summary>
    public enum ModSide { None = 0, Local = 1, HostOnly = 2, Shared = 3 }

    /// <summary>Base for custom messages of other mods: write the fields once in Describe.</summary>
    public abstract class ApiMessage : IWire { public abstract void Describe(Codec c); }

    /// <summary>Handler base: one method per receiving side. Subclass once per message type.</summary>
    public abstract class ApiHandler<T> : IApiHandler where T : ApiMessage, new()
    {
        public virtual void OnGuest(T message) { }
        public virtual void OnHost(T message, string senderId) { }
        Type IApiHandler.MessageType => typeof(T);
        void IApiHandler.Invoke(bool onHost, ApiMessage m, string sender) { if (onHost) OnHost((T)m, sender); else OnGuest((T)m); }
    }
    public interface IApiHandler { Type MessageType { get; } void Invoke(bool onHost, ApiMessage m, string sender); }

    public sealed class ModHandle
    {
        public uint Id; public string Name, Version; public ModSide Side; public byte NetId;
        internal Assembly Assembly;
        internal readonly Dictionary<ushort, Type> Types = new Dictionary<ushort, Type>();
        internal readonly Dictionary<Type, ushort> TypeIds = new Dictionary<Type, ushort>();
        internal readonly Dictionary<Type, IApiHandler> Handlers = new Dictionary<Type, IApiHandler>();
        public bool Shared => Side == ModSide.Shared || Side == ModSide.None;
    }

    /// <summary>Public mod API: registration, custom serialisers and custom messages wrapped with the mod id.</summary>
    public sealed class ModRegistry
    {
        readonly Dictionary<uint, ModHandle> _byId = new Dictionary<uint, ModHandle>();
        readonly Dictionary<byte, ModHandle> _byNet = new Dictionary<byte, ModHandle>();
        readonly Dictionary<Type, Delegate> _serializers = new Dictionary<Type, Delegate>();
        public IEnumerable<ModHandle> Mods => _byId.Values;
        public Node Node; public Session Session;

        public static uint HashId(string s) { uint h = 2166136261; foreach (char c in s) h = (h ^ c) * 16777619; return h; }

        /// <summary>Registers a mod and scans its assembly for message and handler types. Throws on id collision.</summary>
        public ModHandle Register(string name, string version, ModSide side, Assembly asm)
        {
            uint id = HashId(asm.GetName().Name);
            if (_byId.TryGetValue(id, out var ex)) throw new InvalidOperationException($"mod id collision between {ex.Name} and {name}");
            var h = new ModHandle { Id = id, Name = name, Version = version, Side = side, Assembly = asm };
            foreach (var t in SafeTypes(asm).Where(t => !t.IsAbstract && typeof(ApiMessage).IsAssignableFrom(t)).OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                ushort tid = (ushort)(h.Types.Count + 1); h.Types[tid] = t; h.TypeIds[t] = tid;
            }
            foreach (var t in SafeTypes(asm).Where(t => !t.IsAbstract && typeof(IApiHandler).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null))
            { var ih = (IApiHandler)Activator.CreateInstance(t); h.Handlers[ih.MessageType] = ih; }
            _byId[id] = h; AssignNetId(h);
            return h;
        }
        static Type[] SafeTypes(Assembly a) { try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(x => x != null).ToArray(); } }
        void AssignNetId(ModHandle h) { byte n = 1; while (_byNet.ContainsKey(n)) n++; h.NetId = n; _byNet[n] = h; }

        /// <summary>Host: assigns small network ids to the registered mods (deterministic order) and returns the map for Approve.</summary>
        public List<ModNetId> BuildNetIds()
        {
            _byNet.Clear(); byte n = 1; var list = new List<ModNetId>();
            foreach (var m in _byId.Values.OrderBy(m => m.Id)) { m.NetId = n; _byNet[n] = m; list.Add(new ModNetId { ModId = m.Id, NetId = n }); n++; }
            return list;
        }
        public void ApplyNetIds(List<ModNetId> ids)
        {
            _byNet.Clear(); foreach (var m in _byId.Values) m.NetId = 0;
            foreach (var e in ids) if (_byId.TryGetValue(e.ModId, out var m)) { m.NetId = e.NetId; _byNet[e.NetId] = m; }
        }
        public void ClearNetIds() { _byNet.Clear(); foreach (var m in _byId.Values) m.NetId = 0; }

        public void RegisterSerializer<T>(ItemFn<T> fn) => _serializers[typeof(T)] = fn;
        public bool TryGetSerializer<T>(out ItemFn<T> fn) { if (_serializers.TryGetValue(typeof(T), out var d)) { fn = (ItemFn<T>)d; return true; } fn = null; return false; }

        // ---- mod sync
        public List<ModEntry> LocalSharedList() => _byId.Values.Where(m => m.Shared).Select(m => new ModEntry { Id = m.Id, Name = m.Name ?? $"Unknown Mod ({m.Id})", Version = m.Version ?? "Unknown Version" }).ToList();

        /// <summary>Compares both ways. Returns an empty list when equal, otherwise readable differences.</summary>
        public List<string> Compare(List<ModEntry> remote)
        {
            var diffs = new List<string>(); var mine = LocalSharedList();
            foreach (var m in mine)
            {
                var r = remote.FirstOrDefault(x => x.Id == m.Id);
                if (r.Name == null && r.Id == 0) diffs.Add($"client is missing {m.Name} {m.Version}");
                else if (r.Version != m.Version) diffs.Add($"{m.Name}: host has {m.Version}, client has {r.Version}");
            }
            foreach (var r in remote) if (!mine.Any(x => x.Id == r.Id)) diffs.Add($"host does not have {r.Name} {r.Version}");
            return diffs;
        }

        // ---- custom messages
        ApiCall Wrap(ModHandle mod, ApiMessage m)
        {
            if (mod.NetId == 0) { Log.Warn($"mod {mod.Name} has no network id yet (not connected); message not sent"); return null; }
            if (!mod.TypeIds.TryGetValue(m.GetType(), out var tid)) { Log.Warn($"{m.GetType().Name} is not a registered message of {mod.Name}"); return null; }
            var w = NetWriter.Rent();
            try { m.Describe(new Codec(w)); return new ApiCall { NetId = mod.NetId, TypeId = tid, Payload = w.Span.ToArray() }; }
            finally { w.Return(); }
        }
        public void SendToHost(ModHandle mod, ApiMessage m, Delivery mode = Delivery.ReliableOrdered, Lane lane = Lane.Api) { var c = Wrap(mod, m); if (c != null) Node.SendToHost(c, mode, lane); }
        public void SendToAll(ModHandle mod, ApiMessage m, Delivery mode = Delivery.ReliableOrdered, Lane lane = Lane.Api) { var c = Wrap(mod, m); if (c != null) Node.SendToAll(c, mode, lane); }
        public void SendToAllExcept(ModHandle mod, ApiMessage m, string exceptPlayerId, Delivery mode = Delivery.ReliableOrdered, Lane lane = Lane.Api)
        { var c = Wrap(mod, m); if (c != null) Node.SendToAllExcept(c, Node.FindByPlayer(exceptPlayerId), mode, lane); }
        public void SendToPlayer(ModHandle mod, ApiMessage m, string playerId, Delivery mode = Delivery.ReliableOrdered, Lane lane = Lane.Api)
        { var c = Wrap(mod, m); if (c != null) Node.SendTo(Node.FindByPlayer(playerId), c, mode, lane); }
        /// <summary>Host to everyone, guest to the host.</summary>
        public void SendAuto(ModHandle mod, ApiMessage m, Delivery mode = Delivery.ReliableOrdered, Lane lane = Lane.Api) { var c = Wrap(mod, m); if (c != null) Node.SendAuto(c, mode, lane); }

        internal Verdict Dispatch(in Ctx ctx, ApiCall call)
        {
            if (!_byNet.TryGetValue(call.NetId, out var mod)) { Log.Warn($"api message for unknown mod net id {call.NetId}"); return Verdict.Reject; }
            if (!mod.Types.TryGetValue(call.TypeId, out var t)) { Log.Warn($"api message of unknown type {call.TypeId} for {mod.Name}"); return Verdict.Reject; }
            if (!mod.Handlers.TryGetValue(t, out var handler)) return Verdict.Reject;
            var msg = (ApiMessage)Activator.CreateInstance(t);
            var r = NetReader.Rent(call.Payload, 0, call.Payload.Length);
            try { msg.Describe(new Codec(r)); if (!r.AtEnd) throw new WireException("leftover"); }
            catch (WireException e) { Log.Warn($"bad api payload for {mod.Name}/{t.Name}: {e.Message}"); return Verdict.Reject; }
            finally { r.Return(); }
            handler.Invoke(ctx.OnHost, msg, ctx.SenderId);
            return Verdict.Accept;
        }
    }

    [Msg(Ids900.ApiCall, Direction.Both, Delivery.ReliableOrdered, Lane.Api)]
    public sealed class ApiCall : Msg
    {
        public byte NetId; public ushort TypeId; public byte[] Payload = System.Array.Empty<byte>();
        public override void Describe(Codec c)
        {
            c.U8(ref NetId); c.U16(ref TypeId);
            if (c.Reading) { int n = c.R.Count(); Payload = c.R.Raw(n).ToArray(); } else { c.W.Count(Payload.Length); c.W.Raw(Payload); }
        }
    }
    static class Ids900 { public const ushort ApiCall = Net.Messages.Ids.ApiCall; }
}

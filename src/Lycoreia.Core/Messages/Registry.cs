// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Lycoreia.Net
{
    /// <summary>A network message: one class, one Describe method, one attribute for the delivery rules.</summary>
    public abstract class Msg : IWire
    {
        public abstract void Describe(Codec c);
        /// <summary>Set by the dispatcher on received messages; the sender's registered player id (host side) or host id (guest side).</summary>
        [NonSerialized] public string From;
    }

    /// <summary>Messages carrying a claimed player id. The dispatcher rejects the message when it differs from the endpoint's player.</summary>
    public interface IPlayerScoped { string ScopedPlayerId { get; } }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class MsgAttribute : Attribute
    {
        public readonly ushort Id; public readonly Direction Dir; public readonly Delivery Mode; public readonly Lane Lane;
        /// <summary>True only for the first message from an unregistered endpoint.</summary>
        public bool AllowUnregistered;
        public MsgAttribute(ushort id, Direction dir, Delivery mode = Delivery.ReliableOrdered, Lane lane = Lane.Important) { Id = id; Dir = dir; Mode = mode; Lane = lane; }
    }

    public sealed class MessageInfo
    {
        public ushort Id; public Type Type; public string Name; public Direction Dir; public Delivery Mode; public Lane Lane; public bool AllowUnregistered;
        public Func<Msg> Factory; public bool PlayerScoped;
        readonly Stack<Msg> _pool = new Stack<Msg>();
        public Msg Rent() { lock (_pool) if (_pool.Count > 0) return _pool.Pop(); return Factory(); }
        public void Recycle(Msg m) { lock (_pool) if (_pool.Count < 256) _pool.Push(m); }
    }

    /// <summary>Central id table built from [Msg] attributes. Ids are never reused; duplicates fail loudly.</summary>
    public sealed class MessageRegistry
    {
        readonly Dictionary<ushort, MessageInfo> _byId = new Dictionary<ushort, MessageInfo>();
        readonly Dictionary<Type, MessageInfo> _byType = new Dictionary<Type, MessageInfo>();
        public IEnumerable<MessageInfo> All => _byId.Values;
        public int Count => _byId.Count;

        public static MessageRegistry FromAssembly(Assembly asm)
        {
            var r = new MessageRegistry(); r.Scan(asm); return r;
        }
        public void Scan(Assembly asm)
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); }
            foreach (var t in types)
            {
                if (t.IsAbstract || !typeof(Msg).IsAssignableFrom(t)) continue;      // never inspect attributes of unrelated (game hook) types
                var a = t.GetCustomAttribute<MsgAttribute>(false);
                if (a == null) continue;
                Add(t, a);
            }
        }
        public void Add(Type t, MsgAttribute a)
        {
            if (_byId.TryGetValue(a.Id, out var dup)) throw new InvalidOperationException($"message id {a.Id} used by {dup.Name} and {t.Name}");
            var info = new MessageInfo
            {
                Id = a.Id, Type = t, Name = t.Name, Dir = a.Dir, Mode = a.Mode, Lane = a.Lane, AllowUnregistered = a.AllowUnregistered,
                Factory = CompileFactory(t), PlayerScoped = typeof(IPlayerScoped).IsAssignableFrom(t)
            };
            _byId[a.Id] = info; _byType[t] = info;
        }
        static Func<Msg> CompileFactory(Type t)
        {
            var ctor = t.GetConstructor(Type.EmptyTypes) ?? throw new InvalidOperationException(t.Name + " needs a public parameterless constructor");
            var body = System.Linq.Expressions.Expression.New(ctor);
            return System.Linq.Expressions.Expression.Lambda<Func<Msg>>(body).Compile();
        }
        public MessageInfo Get(ushort id) => _byId.TryGetValue(id, out var i) ? i : null;
        public MessageInfo Get(Type t) => _byType.TryGetValue(t, out var i) ? i : throw new InvalidOperationException(t.Name + " is not registered");
        public MessageInfo Get<T>() where T : Msg => Get(typeof(T));
    }
}

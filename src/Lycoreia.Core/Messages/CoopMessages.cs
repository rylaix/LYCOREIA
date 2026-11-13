// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System.Collections.Generic;

namespace Lycoreia.Net.Messages
{
    /// <summary>A player's board operation (job, note, expedition, stat report). Guests send it to the host; the host applies it.</summary>
    [Msg(68, Direction.GuestToHost, Delivery.ReliableOrdered)]
    public sealed class CoopOpMsg : Msg
    {
        public byte Kind; public int Id, Arg, Scene; public string Text = "", Anchor = ""; public bool HasPosition; public Vec3 Position;
        public override void Describe(Codec c)
        {
            c.Byte(ref Kind); c.Var(ref Id); c.Var(ref Arg); c.Var(ref Scene); c.Str(ref Text, 256); c.Str(ref Anchor, 256); c.Flag(ref HasPosition);
            if (HasPosition) c.V3(ref Position);
        }
    }

    public struct CoopJobWire : IWire
    {
        public int Id, Scene; public string Text, CreatedBy, ClaimedBy, Anchor; public bool Done, HasPosition; public Vec3 Position;
        public void Describe(Codec c)
        {
            c.Var(ref Id); c.Var(ref Scene); c.Str(ref Text, 256); c.Str(ref CreatedBy, 64); c.Str(ref ClaimedBy, 64); c.Str(ref Anchor, 256);
            c.Flag(ref Done); c.Flag(ref HasPosition); if (HasPosition) c.V3(ref Position);
        }
    }

    public struct CoopNoteWire : IWire
    {
        public int Id, Scene; public string Anchor, Text, Author; public Vec3 Position;
        public void Describe(Codec c) { c.Var(ref Id); c.Var(ref Scene); c.Str(ref Anchor, 256); c.Str(ref Text, 256); c.Str(ref Author, 64); c.V3(ref Position); }
    }

    public struct CoopStatWire : IWire
    {
        public string Player; public byte Stat; public long Value;
        public void Describe(Codec c) { c.Str(ref Player, 64); c.Byte(ref Stat); c.Var(ref Value); }
    }

    /// <summary>The host's whole board (small: at most 45 jobs, 64 notes). Sent on every change and in the join snapshot.</summary>
    [Msg(69, Direction.HostToGuest, Delivery.ReliableOrdered)]
    public sealed class CoopBoardMsg : Msg
    {
        public List<CoopJobWire> Jobs = new List<CoopJobWire>(); public List<CoopNoteWire> Notes = new List<CoopNoteWire>(); public List<CoopStatWire> Stats = new List<CoopStatWire>();
        public bool HasExpedition; public int ExpDef, ExpTarget, ExpProgress, ExpReward; public byte ExpOutcome; public long ExpStarted, ExpEnds; public string ExpStartedBy = "";
        public List<CoopStatWire> ExpByPlayer = new List<CoopStatWire>();
        /// <summary>Events since the previous board (shown as chat system lines); empty in the join snapshot.</summary>
        public List<string> Notices = new List<string>();
        /// <summary>Expedition history (Stat = outcome, Value = expedition id): guests need it for the same offers and levels.</summary>
        public List<CoopStatWire> History = new List<CoopStatWire>();
        public override void Describe(Codec c)
        {
            c.List(ref Jobs, Items.Of<CoopJobWire>(), 6); c.List(ref Notes, Items.Of<CoopNoteWire>(), 8); c.List(ref Stats, Items.Of<CoopStatWire>(), 3);
            c.Flag(ref HasExpedition);
            if (HasExpedition)
            {
                c.Var(ref ExpDef); c.Var(ref ExpTarget); c.Var(ref ExpProgress); c.Var(ref ExpReward); c.Byte(ref ExpOutcome); c.Var(ref ExpStarted); c.Var(ref ExpEnds);
                c.Str(ref ExpStartedBy, 64); c.List(ref ExpByPlayer, Items.Of<CoopStatWire>(), 3);
            }
            c.List(ref Notices, (Codec cc, ref string v) => cc.Str(ref v, 512), 1); c.List(ref History, Items.Of<CoopStatWire>(), 3);
        }
    }
}

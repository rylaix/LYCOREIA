// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Buffers.Binary;

namespace Lycoreia.Net
{
    public enum Delivery : byte { Unreliable = 0, Reliable = 1, ReliableOrdered = 2 }

    /// <summary>Independent ordering lanes. A slow lane never blocks another.</summary>
    public enum Lane : byte
    {
        Important = 0, Acknowledge = 1, Player = 2, ActorCritical = 3, ActorUpdate = 4, World = 5, Economy = 6, Snapshot = 7,
        Fx = 8, Api = 9, Chat = 10
    }

    public enum Direction : byte { HostToGuest = 1, GuestToHost = 2, Both = 3 }

    /// <summary>Datagram constants and header layout.</summary>
    public static class Frame
    {
        public const int MaxPayload = 1200;          // payload bytes per datagram, the one tunable constant
        public const int MaxParts = 65535;
        public const byte Magic = 0xC2;
        public const byte ProtocolVersion = 2;
        public const int CompressMinBytes = 128;
        public const int MaxMessageBytes = 32 * 1024 * 1024; // decompressed / assembled cap
        public const int MaxHeader = 1 + 1 + 1 + 2 + 2 + 2 + 4 + 4;

        // byte1: version (low nibble) | kind (high nibble)
        public const byte KindData = 0, KindAck = 1;
        // byte2 flags
        public const byte FReliable = 1, FOrdered = 2, FCompressed = 4, FSplit = 8; // high nibble = lane

        public struct Header
        {
            public byte Kind, Flags, Lane; public ushort Epoch, Type, MsgId; public uint Seq; public ushort Part, Count;
            public int PayloadOffset, PayloadLength;
            public bool Reliable => (Flags & FReliable) != 0;
            public bool Ordered => (Flags & FOrdered) != 0;
            public bool Compressed => (Flags & FCompressed) != 0;
            public bool Split => (Flags & FSplit) != 0;
        }

        /// <summary>Writes a data datagram. Returns total length.</summary>
        public static int WriteData(byte[] buf, byte lane, byte flags, ushort epoch, ushort type, ushort msgId, uint seq,
            ushort part, ushort count, ReadOnlySpan<byte> payload)
        {
            int p = 0;
            buf[p++] = Magic; buf[p++] = (byte)(ProtocolVersion | (KindData << 4));
            buf[p++] = (byte)(flags | (lane << 4));
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(p), epoch); p += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(p), type); p += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(p), msgId); p += 2;
            if ((flags & FOrdered) != 0) { BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(p), seq); p += 4; }
            if ((flags & FSplit) != 0)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(p), part); p += 2;
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(p), count); p += 2;
            }
            payload.CopyTo(buf.AsSpan(p)); p += payload.Length;
            return Seal(buf, p);
        }

        public static int WriteAckHeader(byte[] buf, ushort epoch) { buf[0] = Magic; buf[1] = (byte)(ProtocolVersion | (KindAck << 4)); buf[2] = (byte)((byte)Lane.Acknowledge << 4); BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(3), epoch); return 5; }

        /// <summary>Appends the 32-bit checksum over everything written so far.</summary>
        public static int Seal(byte[] buf, int len)
        {
            uint crc = Crc32.Compute(new ReadOnlySpan<byte>(buf, 0, len));
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(len), crc);
            return len + 4;
        }

        public enum ParseResult { Ok, TooShort, BadMagic, BadVersion, BadChecksum, Malformed }

        public static ParseResult Parse(byte[] buf, int off, int len, out Header h)
        {
            h = default;
            if (len < 5 + 4) return ParseResult.TooShort;
            if (buf[off] != Magic) return ParseResult.BadMagic;
            if ((buf[off + 1] & 15) != ProtocolVersion) return ParseResult.BadVersion;
            uint want = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + len - 4));
            if (Crc32.Compute(new ReadOnlySpan<byte>(buf, off, len - 4)) != want) return ParseResult.BadChecksum;
            h.Kind = (byte)(buf[off + 1] >> 4); h.Flags = (byte)(buf[off + 2] & 15); h.Lane = (byte)(buf[off + 2] >> 4);
            int p = off + 3, end = off + len - 4;
            h.Epoch = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(p)); p += 2;
            if (h.Kind == KindAck) { h.PayloadOffset = p; h.PayloadLength = end - p; return ParseResult.Ok; }
            if (h.Kind != KindData) return ParseResult.Malformed;
            if (end - p < 4) return ParseResult.Malformed;
            h.Type = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(p)); p += 2;
            h.MsgId = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(p)); p += 2;
            if (h.Ordered) { if (end - p < 4) return ParseResult.Malformed; h.Seq = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(p)); p += 4; }
            if (h.Split)
            {
                if (end - p < 4) return ParseResult.Malformed;
                h.Part = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(p)); p += 2;
                h.Count = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(p)); p += 2;
                if (h.Count < 2 || h.Part >= h.Count) return ParseResult.Malformed;
            }
            else { h.Part = 0; h.Count = 1; }
            h.PayloadOffset = p; h.PayloadLength = end - p;
            return ParseResult.Ok;
        }
    }
}

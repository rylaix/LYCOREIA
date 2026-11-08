// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Security.Cryptography;

namespace Lycoreia
{
    public static class Identity
    {
        public const string Prefix = "PLAYER_";
        public const string DevHostId = "PLAYER_TEST_MODE";
        public const ushort ShortIdBase = 12345;
        /// <summary>Size of the actor id block allocated to each player; ids below it belong to the host.</summary>
        public const long ActorIdOffset = 100_000_000L;
        static string _cached;
        static readonly object Gate = new object();

        /// <summary>Generated once per process: PLAYER_ plus nine uppercase hex characters from 16 random bytes.</summary>
        public static string ProcessPlayerId
        {
            get
            {
                lock (Gate)
                {
                    if (_cached != null) return _cached;
                    var b = new byte[16]; RandomNumberGenerator.Fill(b);
                    return _cached = Prefix + Convert.ToHexString(b).Substring(0, 9);
                }
            }
        }
        public static string ForHost(bool devMode) => devMode ? DevHostId : ProcessPlayerId;

        /// <summary>Short numeric id derived from the characters after the prefix, starting at 12345.</summary>
        public static ushort ShortId(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return ShortIdBase;
            string tail = playerId.StartsWith(Prefix, StringComparison.Ordinal) ? playerId.Substring(Prefix.Length) : playerId;
            uint h = 2166136261;
            foreach (char ch in tail) h = (h ^ ch) * 16777619;
            return (ushort)(ShortIdBase + (h % (ushort.MaxValue - ShortIdBase)));
        }
    }
}

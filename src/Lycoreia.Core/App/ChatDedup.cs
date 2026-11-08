// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System.Collections.Generic;

namespace Lycoreia
{
    /// <summary>Message id set: register the local copy before sending, drop the echo. Trimmed past 1000 entries.</summary>
    public sealed class ChatDedup
    {
        public const int Limit = 1000;
        readonly HashSet<string> _set = new HashSet<string>(); readonly Queue<string> _order = new Queue<string>();
        /// <summary>True when the id is new (the line should be shown).</summary>
        public bool Register(string id)
        {
            if (string.IsNullOrEmpty(id)) return true;
            lock (_set)
            {
                if (!_set.Add(id)) return false;
                _order.Enqueue(id);
                while (_order.Count > Limit) _set.Remove(_order.Dequeue());
                return true;
            }
        }
    }
}

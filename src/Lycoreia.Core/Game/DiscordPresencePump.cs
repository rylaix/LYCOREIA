// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using UnityEngine;

namespace Lycoreia.Game
{
    /// <summary>Computes the presence about once a second and posts it to the background client.</summary>
    sealed class DiscordPresencePump : IFramePump
    {
        public static readonly DiscordPresence Client = new DiscordPresence(BuildInfo.DiscordAppId, ModVersion.Value, BuildInfo.Discord);
        public string Name => "discord";
        float _next; string _kind; long _start;

        public void Pump()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            bool en = Prefs.Get<bool>(Settings.DiscordPresence);
            if (!en) { Client.Set(null, false); _kind = null; return; }
            string kind, details, state = null, party = null; int n = 0;
            if (!GameState.Ready) { kind = "menu"; details = "In the main menu"; }
            else if (Mod.IsHost) { kind = "host"; details = "Hosting a co-op ranch"; }
            else if (Mod.IsGuest && Mod.IsNetworkActive) { kind = "guest"; details = "On a friend's ranch"; }
            else { kind = "solo"; details = "Ranching solo"; }
            if (kind == "host" || kind == "guest")
            {
                var s = Mod.Session; n = Math.Max(1, s?.Players?.Count ?? 1); state = n == 1 ? "Waiting for friends" : "Party of " + n;
                string hid = s?.HostId ?? "";
                if (hid.Length > 0) { uint h = 2166136261; foreach (char c in hid) h = (h ^ c) * 16777619; party = "lyc-" + h.ToString("x8"); }
            }
            if (kind != _kind) { _kind = kind; _start = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); }
            Client.Set(new PresenceActivity(details, state ?? " ", _start, party, n, 0), true);   // sessions have no player cap: party size without a maximum
        }
    }
}

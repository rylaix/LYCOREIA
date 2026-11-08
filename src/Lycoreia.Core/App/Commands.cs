// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Linq;
using Lycoreia.Snapshot;

namespace Lycoreia
{
    /// <summary>Everything the commands need from the game; implemented by the entry layer, faked in tests.</summary>
    public interface ICommandEnv
    {
        Session Session { get; }
        SnapshotHost Snapshots { get; }
        void CloseMenu();
        void OpenManualCodeDialog();
        void StartAutoHost();
        void ToggleGui();
        void ToggleChat();
        bool ChatFocused { get; }
        void ShowLocalChat(string username, string text);
        bool AllowCheatsSetting { get; }
        void AddSprinkles(int count);
        void RemoveFirewallExceptions();
        void BackupSave();
        string Cleanup(bool now);
        bool InSession { get; }
    }

    /// <summary>Console commands and chat slash commands as pure routing. Returns the line to show, or null.</summary>
    public static class CommandRouter
    {
        public const string SlashHint = "Commands: /cleanup, /cleanup now";

        public static string Run(ICommandEnv env, string line)
        {
            line = (line ?? "").Trim(); if (line.Length == 0) return null;
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries); string name = parts[0].ToLowerInvariant(); var args = parts.Skip(1).ToArray();
            switch (name)
            {
                case "host":
                    if (args.Length == 0 || !int.TryParse(args[0], out int port) || port < 1 || port > 65535) return "Usage: host <port>";
                    env.CloseMenu(); return env.Session.StartServer(port, ipv6: true);
                case "autohost": env.StartAutoHost(); return null;
                case "manualcode": env.OpenManualCodeDialog(); return null;
                case "chat":
                    {
                        string text = string.Join(" ", args).Trim(); if (text.Length == 0) return "Usage: chat <message>";
                        env.ShowLocalChat(env.Session.LocalName, text); env.Session.SendChat(text); return null;
                    }
                case "connect": return args.Length == 0 ? "Usage: connect <host[:port]>" : env.Session.Connect(string.Join(" ", args));
                case "resync":
                    if (env.Session.IsHost) return env.Snapshots?.ResyncAll();
                    if (env.Session.IsGuest) { env.Session.Node.SendToHost(new Net.Messages.ResyncRequest()); return "Resync requested..."; }
                    return "Not in a session.";
                case "addsprinkles":
                    if (!env.InSession) return "Load a save first.";
                    if (!env.AllowCheatsSetting) return "Cheats are disabled on this server!";
                    if (args.Length == 0 || !int.TryParse(args[0], out int n) || n <= 0) return "Usage: addsprinkles <amount>";
                    env.AddSprinkles(n); return null;
                case "removeexceptions": env.RemoveFirewallExceptions(); return "Removed the firewall exceptions added by Lycoreia.";
                case "togglegui": env.ToggleGui(); return null;
                case "togglechat": if (!env.ChatFocused) env.ToggleChat(); return null;
                case "backupsave": env.BackupSave(); return null;
                case "cleanup": return env.Session.IsHost ? env.Cleanup(args.Length > 0 && args[0].Equals("now", StringComparison.OrdinalIgnoreCase)) : "Only the host can run /cleanup.";
                default: return $"Unknown command {name}.";
            }
        }

        /// <summary>
        /// Chat box input: a leading slash runs locally and is never sent; anything else is trimmed, capped, shown locally and sent.
        /// Returns true when the text was consumed locally.
        /// </summary>
        public static bool HandleChatInput(ICommandEnv env, string input, Action<string> reply, int cap = 1000)
        {
            input = (input ?? "").Trim(); if (input.Length == 0) return true;
            if (input.StartsWith("/"))
            {
                var cmd = input.Substring(1).Trim().ToLowerInvariant();
                if (cmd == "cleanup" || cmd == "cleanup now") reply(env.Session.IsHost ? env.Cleanup(cmd.EndsWith("now")) : "Only the host can run /cleanup.");
                else reply($"Unknown command {input.Split(' ')[0]}. {SlashHint}");
                return true;
            }
            if (input.Length > cap) input = input.Substring(0, cap);
            env.ShowLocalChat(env.Session.LocalName, input); env.Session.SendChat(input);
            return true;
        }
    }
}

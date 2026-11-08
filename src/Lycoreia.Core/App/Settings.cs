// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System.Collections.Generic;

namespace Lycoreia
{
    public sealed class SettingDef
    {
        public string Key, Display; public object Default; public bool Hidden; public int Min;
        public SettingDef(string key, object def, string display = null, bool hidden = false, int min = int.MinValue) { Key = key; Default = def; Display = display; Hidden = hidden; Min = min; }
    }

    /// <summary>The setting table: names, defaults and visibility in one place. The game layer creates the entries from it.</summary>
    public static class Settings
    {
        public const string Username = "username", UsernameColor = "username_color", AllowCheats = "allow_cheats", RecentPort = "recent_port", RecentIp = "recent_ip",
            HostPort = "host_port", JoinGame = "coop_join_game", FirewallExceptions = "firewall_exceptions", UiFirstRun = "ui_first_run",
            StreamerMode = "streamer_mode", DisableAchievements = "disable_achievements", FriendsCanVac = "friends_can_vac_me", FriendsCanKick = "friends_can_kick_me",
            PacketSizeLogging = "packet_size_logging", PacketAckLogging = "packet_ack_logging", DevDiagnostics = "development_diagnostics",
            AutoBackups = "auto_save_backups", DeleteOldBackups = "delete_old_backups", BackupsToKeep = "backups_to_keep", SkipHostConnectBackups = "skip_host_connect_backups",
            RockPlortMode = "rock_plort_mode", DiscordPresence = "discord_presence";

        public static IReadOnlyList<SettingDef> All { get; } = new List<SettingDef>
        {
            // hidden
            new SettingDef(Username, "Player", hidden: true), new SettingDef(UsernameColor, "#FFFFFF", hidden: true), new SettingDef(AllowCheats, false, hidden: true),
            new SettingDef(RecentPort, 1919, hidden: true), new SettingDef(RecentIp, "", hidden: true), new SettingDef(HostPort, 1919, hidden: true),
            new SettingDef(JoinGame, "", hidden: true), new SettingDef(FirewallExceptions, "", hidden: true), new SettingDef(UiFirstRun, true, hidden: true),
            new SettingDef(RockPlortMode, false, hidden: true),
            // visible
            new SettingDef(StreamerMode, false, "Streamer Mode"), new SettingDef(DisableAchievements, true, "Disable Achievements"),
            new SettingDef(FriendsCanVac, true, "Friends Can Vac Me"), new SettingDef(FriendsCanKick, true, "Friends Can Kick Me"),
            new SettingDef(PacketSizeLogging, false, "Packet Size Logging"), new SettingDef(PacketAckLogging, true, "Packet Acknowledge Logging"),
            new SettingDef(DevDiagnostics, BuildInfo.Diagnostics
#if LYC_TEST
                || true
#endif
                , "Development Diagnostics (bounded logs)"),
            new SettingDef(AutoBackups, true, "Automatic Save Backups"), new SettingDef(DeleteOldBackups, false, "Delete Old Backups"),
            new SettingDef(DiscordPresence, true, "Discord Presence"),
            new SettingDef(BackupsToKeep, 10, "Backups To Keep", min: 1), new SettingDef(SkipHostConnectBackups, false, "Skip Host/Connect Backups"),
        };

        public static SettingDef Find(string key) { foreach (var d in All) if (d.Key == key) return d; return null; }
    }
}

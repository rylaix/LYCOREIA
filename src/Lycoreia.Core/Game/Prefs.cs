// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using MelonLoader;

namespace Lycoreia.Game
{
    /// <summary>MelonPreferences category created from the Settings table (names, defaults and visibility live in Settings.cs).</summary>
    public static class Prefs
    {
        static MelonPreferences_Category _cat;
        public static void Create()
        {
            if (_cat != null) return;
            _cat = MelonPreferences.CreateCategory(BuildInfo.Name, BuildInfo.Name);
            foreach (var d in Settings.All)
            {
                switch (d.Default)
                {
                    case bool b: _cat.CreateEntry(d.Key, b, d.Display, null, d.Hidden); break;
                    case int i: _cat.CreateEntry(d.Key, i, d.Display, null, d.Hidden); break;
                    case string s: _cat.CreateEntry(d.Key, s, d.Display, null, d.Hidden); break;
                }
            }
        }
        public static T Get<T>(string key) { try { return _cat.GetEntry<T>(key).Value; } catch { return (T)Settings.Find(key)?.Default; } }
        public static void Set<T>(string key, T value) { try { _cat.GetEntry<T>(key).Value = value; MelonPreferences.Save(); } catch (Exception e) { Log.Warn("could not save setting " + key + ": " + e.Message); } }

        public static string Username => Get<string>(Settings.Username);
        public static bool AllowCheats => Get<bool>(Settings.AllowCheats);
        public static bool FriendsCanVacMe => Get<bool>(Settings.FriendsCanVac);
        public static bool FriendsCanKickMe => Get<bool>(Settings.FriendsCanKick);
        public static bool StreamerMode => Get<bool>(Settings.StreamerMode);
    }
}

// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Lycoreia.Game
{
    public sealed class UpdateChecker : IFramePump
    {
        public string Name => "update-check";
        static volatile UpdateStatus _result; static bool _started, _published, _toldInWorld;

        /// <summary>Starts the background check once (setting "Check For Updates").</summary>
        public static void Start()
        {
            if (_started) return; _started = true;
            bool enabled = true; try { enabled = Prefs.Get<bool>(Settings.UpdateCheck); } catch { }
            if (!enabled) { Log.Info("update check disabled in the settings"); return; }
            Task.Run(async () =>
            {
                try { _result = await CheckAsync(); }
                catch (Exception e) { _result = new UpdateStatus { Kind = UpdateKind.Unknown, Installed = BuildInfo.Version, Message = "update check failed: " + e.Message }; }
            });
        }

        static async Task<UpdateStatus> CheckAsync()
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Lycoreia-update-check/" + BuildInfo.Version);
            var gitHub = Fetch(http, async () =>
                (await Fetch(http, async () => UpdateCheck.ParseGitHubRelease(await http.GetStringAsync(UpdateCheck.GitHubReleaseApi))))   // API rate limit -> fallback
                ?? UpdateCheck.ParsePlain(await http.GetStringAsync(BuildInfo.LatestVersionUrl)));
            var nexus = Fetch(http, async () =>
            {
                using var body = new StringContent(UpdateCheck.NexusQuery, Encoding.UTF8, "application/json");
                using var r = await http.PostAsync(UpdateCheck.NexusGraphQl, body);
                return r.IsSuccessStatusCode ? UpdateCheck.ParseNexus(await r.Content.ReadAsStringAsync()) : null;
            });
            var list = new List<PublishedVersion>
            {
                new PublishedVersion("GitHub", await gitHub, BuildInfo.Releases),
                new PublishedVersion("Nexus Mods", await nexus, BuildInfo.Nexus),
            };
            return UpdateCheck.Evaluate(BuildInfo.Version, list);
        }

        static async Task<string> Fetch(HttpClient http, Func<Task<string>> read) { try { return await read(); } catch { return null; } }

        /// <summary>Main thread: publish the result once (log + menu), and tell the player once in chat after entering a world.</summary>
        public void Pump()
        {
            var r = _result; if (r == null) return;
            if (!_published)
            {
                _published = true;
                Mod.Update = r;
                if (r.Kind == UpdateKind.UpdateAvailable || r.Kind == UpdateKind.AheadOfPublic) Log.Warn(r.Message);
                else Log.Info($"update check: {r.Kind} (installed {r.Installed}, public {r.Newest ?? "unknown"}){(r.Message != null ? "; " + r.Message : "")}");
            }
            if (!_toldInWorld && GameState.Ready && !GameState.Loading && (r.Kind == UpdateKind.UpdateAvailable || r.Kind == UpdateKind.AheadOfPublic))
            {
                _toldInWorld = true;
                try { Mod.Session?.RaiseNotice(r.Message); } catch { }
            }
        }
    }
}

// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Lycoreia.Net;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

[assembly: MelonLoader.MelonInfo(typeof(Lycoreia.Game.LycoreiaMod), "Lycoreia", Lycoreia.ModVersion.Value, "Rylaix")]
[assembly: MelonLoader.MelonGame("MonomiPark", "SlimeRancher2")]
[assembly: AssemblyDescription(Lycoreia.BuildInfo.Description)]
[assembly: AssemblyMetadata("display_version", Lycoreia.BuildInfo.Version)]
[assembly: AssemblyMetadata("source_code", Lycoreia.BuildInfo.SourceCode)]
[assembly: AssemblyMetadata("nexus", Lycoreia.BuildInfo.Nexus)]
[assembly: AssemblyMetadata("discord", Lycoreia.BuildInfo.Discord)]

namespace Lycoreia.Game
{
    /// <summary>Marks a static class whose static Register() the expansion calls once during late init (feature modules register pumps and events).</summary>
    [AttributeUsage(AttributeTargets.Class)] public sealed class FeatureRegistrationAttribute : Attribute { }

    /// <summary>One feature manager pumped from the main-thread dispatcher each frame. A failing item is logged and never stops the rest.</summary>
    public interface IFramePump { string Name { get; } void Pump(); }

    sealed class ArbiterPump : IFramePump
    {
        public string Name => "arbiter";
        public void Pump() { if (Mod.IsHost && Mod.Actors != null) Mod.Actors.Arbiter.Tick(Time.unscaledTime); }
    }

    /// <summary>The MelonLoader mod: identity (assembly attributes), init order, scene hooks, pump order and quit order.</summary>
    public sealed class LycoreiaMod : MelonLoader.MelonMod
    {
        public static readonly List<IFramePump> Pumps = new List<IFramePump>();
        /// <summary>Pumps that must run after the animators (LateUpdate), e.g. procedural rigs.</summary>
        public static readonly List<IFramePump> LatePumps = new List<IFramePump>();
        static Thread _tick; static volatile bool _stop;
        static bool _gameContextReady;
        static bool _firstUpdate;
        static bool _initStarted;   

        public override void OnInitializeMelon()
        {
            if (_initStarted) return;      // one-shot even if a step below throws (a second run would start a second tick thread and re-register features)
            _initStarted = true;
            Prefs.Create();
            Log.Console = (lv, m) => { switch (lv) { case LogLevel.Warning: MelonLoader.MelonLogger.Warning(m); break; case LogLevel.Error: MelonLoader.MelonLogger.Error(m); break; default: MelonLoader.MelonLogger.Msg(m); break; } };
            Mod.Init(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, diagnostics: Prefs.Get<bool>(Settings.DevDiagnostics));
            Log.PacketSizes = Prefs.Get<bool>(Settings.PacketSizeLogging);
            Mod.Session.LocalName = Prefs.Username; Mod.Session.AllowCheats = Prefs.AllowCheats;
            CheckGameVersion();
            Mod.Session.ServerStarted += () => Mod.Session.AllowCheats = Prefs.AllowCheats;     // cheats follow the local setting when the server starts
            _stop = false;
            _tick = new Thread(() => { while (!_stop) { try { Mod.Node.Tick(); } catch (Exception e) { Log.Error("network tick", e); } Thread.Sleep(5); } }) { IsBackground = true, Name = "Lycoreia-tick" };
            _tick.Start();
            Type[] all; try { all = typeof(LycoreiaMod).Assembly.GetTypes(); } catch (ReflectionTypeLoadException e) { all = Array.FindAll(e.Types, x => x != null); }
            foreach (var t in all)
            {
                if (t.GetCustomAttribute<FeatureRegistrationAttribute>() == null) continue;
                try { t.GetMethod("Register", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(null, null); }
                catch (Exception e) { Log.Error("feature registration " + t.Name, e); }
            }
            Pumps.Add(new DiscordPresencePump()); Watchdog.Start();                  // 2.1: names the step of a frozen main thread
            Pumps.Add(new UpdateChecker()); UpdateChecker.Start();                 // GitHub + Nexus Mods: update available / ahead of the public release
#if DEEPLOG
            Pumps.Add(new LinkStatsPump());                 // per-peer loss/resend/give-up figures for lossy-link reports
#endif
            Log.Info($"Lycoreia {BuildInfo.Version} ({BuildInfo.Flavour}) ready; {Mod.Registry.Count} message types");
        }


        static void CheckGameVersion()
        {
            string game = "", unity = "";
            try { game = Application.version ?? ""; unity = Application.unityVersion ?? ""; } catch (Exception e) { Log.Warn("game version unavailable: " + e.Message); }
            Mod.GameVersion = game;
            Mod.Session.Build = VersionCheck.BuildTag(ModVersion.Value, game);
            var verdict = VersionCheck.CheckGame(game);
            Log.Info($"game version {game} (Unity {unity}); Lycoreia is built for {BuildInfo.RequiredGameVersion}: {verdict}");
            if (verdict == GameVersionVerdict.TooOld)
            {
                // Older game builds are not supported (owner decision 2026-11-09): no compatibility paths, multiplayer is blocked.
                Mod.GameUnsupported = true;
                Mod.GameVersionWarning = $"Your Slime Rancher 2 is version {VersionCheck.FirstWord(game)}. Lycoreia {BuildInfo.Version} only supports Slime Rancher 2 {BuildInfo.RequiredGameVersion}. Update the game through Steam to play multiplayer.";
                Mod.Session.BlockedReason = Mod.GameVersionWarning;
            }
            else if (verdict == GameVersionVerdict.Newer)
                Mod.GameVersionWarning = $"Your Slime Rancher 2 ({VersionCheck.FirstWord(game)}) is newer than the version Lycoreia {BuildInfo.Version} was tested with ({BuildInfo.RequiredGameVersion}). Check for a Lycoreia update.";
            if (Mod.GameVersionWarning != null) Log.Warn(Mod.GameVersionWarning);
        }

        public override void OnLateInitializeMelon()
        {
            Log.Info("Unity late initialization complete; main-thread dispatcher ready");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (!Mod.Initialised) return;
            SceneObjectCache.Invalidate();
            Log.Info("scene loaded: " + sceneName);
            try { Lycoreia.Owned.Components.UI.CoopWheel.Cancel(); Lycoreia.Owned.Components.UI.InputGate.ReleaseAll(); } catch { }   // 2.1: never leave game input disabled across a scene change
            Mod.Diagnostics?.Record("scene", "scene-loaded", $"buildIndex={buildIndex}; scene={sceneName}");
            if (sceneName == "MainMenuEnvironment") Avatars.AvatarTemplate.BeginCapture();
            if (WorldExit.ShouldEndSession(Mod.IsNetworkActive, sceneName))
            {
                // issue4: the host's world is gone; never keep serving snapshots or actor state from the main menu
                Mod.Diagnostics?.Record("session", "world-exit", $"scene={sceneName}; host={Mod.IsHost}");
                if (Mod.IsHost) { Log.Info("left the world while hosting: closing the server"); Mod.Session.StopServer(); }
                else if (Mod.IsGuest) { Log.Info("left the world: disconnecting from the host"); Mod.Session.Disconnect(null, true); }
            }
        }

        /// <summary>Main menu after a loaded world ends the session (WorldExitTracker).</summary>
        internal static readonly WorldExitTracker WorldExit = new WorldExitTracker();

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (!Mod.Initialised) return;
            SceneObjectCache.Invalidate();
            Log.Info("scene initialized: " + sceneName);
            Mod.Diagnostics?.Record("scene", "scene-initialized", $"buildIndex={buildIndex}; scene={sceneName}");
            if (sceneName == "MainMenuEnvironment")
            {
                Avatars.AvatarTemplate.BeginCapture();      // the menu model may only activate a few frames later: retried from OnUpdate
                Lycoreia.Owned.Components.UI.MultiplayerUI.Initialize();
            }
        }

#if LYC_TEST
        static bool _introEnded;
        static float _nextIntroCheck;
        static void TrySkipTestIntro()
        {
            if (_introEnded || Environment.GetEnvironmentVariable("LYCOREIA_TEST_SKIP_INTRO") != "1"
                || Time.unscaledTime < _nextIntroCheck) return;
            _nextIntroCheck = Time.unscaledTime + 1f;
            foreach (var root in Resources.FindObjectsOfTypeAll<Il2CppMonomiPark.SlimeRancher.UI.IntroSequence.IntroSequenceUIRoot>())
            {
                if (!root || !root.gameObject.activeInHierarchy) continue;
                root.EndSequence();
                _introEnded = true;
                Log.Info("test harness: intro sequence ended");
                break;
            }
        }
#endif

        /// <summary>Raised once the actor, world and avatar layers exist (owned features register their snapshot steps and handlers here).</summary>
        public static event Action GameLayersReady;

        internal static void AfterGameContext(GameContext gameContext)
        {
            _gameContextReady = true;
            Lycoreia.Owned.Components.UI.MultiplayerUI.RegisterMenuButtons();
            if (Mod.Initialised && Mod.Actors == null)
            {
                var world = new GameActorWorld();
                Mod.Actors = new Lycoreia.Actors.ActorSync(Mod.Session, Mod.Snapshots, world, () => Time.unscaledTimeAsDouble, Mod.Guest.Buffer);
                Mod.Snapshots.SendTypeRegistry = p => Mod.Node.SendTo(p, ActorTypes.BuildRegistry());
                Mod.Node.On<Lycoreia.Net.Messages.ActorTypeRegistry>(Side.Guest, (c, m) => ActorTypes.Apply(m));
                Mod.Actors.RegisterSnapshotWriter(0);
                Mod.Actors.Trace = (ev, d) => Mod.Diagnostics?.Record("actors", ev, d);
                Mod.Actors.ExecuteKill = GameActorWorld.ExecuteKillRequest;                                                    // a non-owner's Tarr kill runs on the owner
                ActorReconciler.Install(Mod.Session); Mod.Session.ServerStarted += ActorReconciler.EnsureHostTypes;
                Coop.CoopRuntime.Install(Mod.Session); Pumps.Add(new Coop.CoopRuntime());                                     // 2.1 co-op board
                Hooks.RingtailStatues.Install(Mod.Session);                                                                    // owner-only Ringtail stone form (issue3)
                Mod.Guest.Applier = new GameSnapshotApplier(Mod.Actors);
                Pumps.Add(new ArbiterPump()); Pumps.Add(new ActorRuntime()); Pumps.Add(new ActorReconciler());
                World.WorldRuntime.Init(Mod.Session, Mod.Snapshots); Pumps.Add(new World.WorldRuntime.Pump());
                var avatars = Avatars.RemoteAvatars.Install(Mod.Session); Pumps.Add(avatars); LatePumps.Add(avatars.Late); Pumps.Add(new Avatars.AvatarBodies());   // 2.1 player bodies + boost jump   // rigs pose bones after the animator
#if DEEPLOG
                Pumps.Add(new Hooks.PauseTrace.Poll());                                                                         // installs the pause hooks on the first in-game frame
                Pumps.Add(new Avatars.AvatarProbe());                                                                           // animator facts for the avatar mapping ([anim-probe] lines)
#endif
                try { GameLayersReady?.Invoke(); } catch (Exception e) { Log.Error("owned feature registration", e); }
                Mod.Session.ServerStarted += () => { Hooks.SpawnGate.Reset(); GameCommands.AutoBackup("host"); };
                Mod.Session.ClientJoined += () => { Hooks.SpawnGate.Reset(); GameCommands.AutoBackup("connect"); };
                Mod.Session.ServerStopped += ActorTypes.Clear; Mod.Session.ClientDisconnected += _ => ActorTypes.Clear();
            }
            Mod.Diagnostics?.Record("scene", "game-context-ready", $"types={ActorTypes.Count}");
        }

        public override void OnUpdate()
        {
            if (!Mod.Initialised) return;
            LagProbe.Frame(); Watchdog.Frame();
            if (!_firstUpdate) { _firstUpdate = true; Log.Info("main-thread update started"); }
#if LYC_TEST
            try { TrySkipTestIntro(); } catch (Exception e) { Log.Error("test intro skip", e); }
#endif
            long started = LagProbe.Start(); Watchdog.Step("dispatcher (message handlers)");
            try { Mod.Node.Pump(); } catch (Exception e) { Log.Error("dispatcher", e); }
            finally { LagProbe.End("dispatcher", started); }
            Hooks.NetworkMenuInput.Pump();
            try { WorldExit.Observe(Mod.IsNetworkActive, GameState.Ready && !GameState.Loading); } catch { }
            started = LagProbe.Start();
            try { Avatars.AvatarTemplate.PumpCapture(); } catch (Exception e) { Log.Error("avatar template", e); }
            finally { LagProbe.End("avatar-template", started); }
            foreach (var p in Pumps)
            {
                if (_disabledPumps.Contains(p)) continue;
                started = LagProbe.Start(); Watchdog.Step(p.Name);
                try { p.Pump(); } catch (Exception e) { PumpFailed(p, "pump", e); }
                finally { LagProbe.End(p.Name, started); }
            }
        }

        static readonly HashSet<IFramePump> _disabledPumps = new HashSet<IFramePump>();

        /// <summary>A pump that references a game type or member this game build lacks fails identically every frame
        /// (issue 2026-11-09: 1120 PaletteDirector TypeLoadExceptions in one minute): log it once and switch it off.</summary>
        static void PumpFailed(IFramePump p, string kind, Exception e)
        {
            if (e is TypeLoadException || e is MissingMemberException || e is TypeInitializationException && e.InnerException is TypeLoadException)
            {
                _disabledPumps.Add(p);
                Log.Error($"{kind} {p.Name} disabled: this game build lacks something it needs ({e.Message})");
                return;
            }
            Log.Error(kind + " " + p.Name, e);
        }

        public override void OnLateUpdate()
        {
            if (!Mod.Initialised) return;
            foreach (var p in LatePumps)
            {
                long started = LagProbe.Start();
                if (_disabledPumps.Contains(p)) continue;
                Watchdog.Step("late-" + p.Name);
                try { p.Pump(); } catch (Exception e) { PumpFailed(p, "late pump", e); }
                finally { if (started != 0) LagProbe.End("late-" + p.Name, started); }
            }
            Watchdog.Step("between frames");
        }

        public override void OnApplicationQuit()
        {
            _stop = true;
            Mod.Quit(DiscordPresencePump.Client.Shutdown);
        }
    }
    /// <summary>Runs the game-context step once per GameContext instance (replaces the former framework callback).</summary>
    [HarmonyPatch(typeof(GameContext), nameof(GameContext.Start))]
    static class GameContextStartPatch
    {
        static IntPtr _last;
        [HarmonyPostfix]
        static void Postfix(GameContext __instance)
        {
            try
            {
                if (__instance == null || __instance.Pointer == IntPtr.Zero || __instance.Pointer == _last) return;
                _last = __instance.Pointer;
                LycoreiaMod.AfterGameContext(__instance);
            }
            catch (Exception e) { Log.Error("game context step", e); }
        }
    }
}
